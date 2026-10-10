using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using BatCave.Universal.Topology;

namespace BatCave.WindowsAgent;

internal sealed class LanBridgePeer : IDisposable
{
    public const int DefaultPort = 47870;
    public const int SecurePort = 47871;

    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _connectionLock = new();
    private readonly string _deviceId;
    private readonly DeviceIdentityStore? _identity;
    private readonly Func<ApprovedPeer[]>? _approvedPeers;
    private readonly SemaphoreSlim _handshakeSlots = new(4);
    private ApprovedPeer? _authenticatedPeer;
    private long _sessionGeneration;
    public long SessionGeneration => Interlocked.Read(ref _sessionGeneration);
    public bool IsSecure => _identity is not null;

    private TcpListener? _listener;
    private TcpClient? _client;
    private StreamWriter? _writer;
    private OutboundQueue? _outbound;
    private string? _peerDeviceId;
    private Task<bool> _lastSend = Task.FromResult(true);

    private sealed record OutboundMessage(string Type, string Json, int Bytes, TaskCompletionSource<bool> Completion);
    private sealed class OutboundQueue
    {
        public readonly Channel<OutboundMessage> Channel = System.Threading.Channels.Channel.CreateBounded<OutboundMessage>(
            new BoundedChannelOptions(4096) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false });
        public int Bytes;
    }
    private string? _peerLabel;
    private int? _listenPort;
    private VirtualDesktopBounds _localBounds;
    private bool _disposed;

    public event Action<string, ShortcutInputPayload>? ShortcutInputReceived;
    public event Action<string, ClipboardTextPayload>? ClipboardTextReceived;
    public event Action<string>? PeerConnected;
    public event Action<string>? PeerDisconnected;
    public event Action<DeviceHelloPayload>? PeerHello;
    public event Action<string, long, string, JsonElement>? InputMessageReceived;

    public LanBridgePeer(DeviceIdentityStore? identity = null, Func<ApprovedPeer[]>? approvedPeers = null)
    {
        _identity = identity;
        _approvedPeers = approvedPeers;
        _deviceId = identity?.DeviceId ?? $"windows:{Environment.MachineName}";
        if (identity is not null && approvedPeers is null) { throw new ArgumentException("Secure mode requires an approval store."); }
    }

    public string LocalDeviceId => _deviceId;

    public bool HasPeer
    {
        get
        {
            lock (_connectionLock)
            {
                return _writer is not null && _peerDeviceId is not null;
            }
        }
    }

    public string? CurrentPeerDeviceId
    {
        get { lock (_connectionLock) { return _peerDeviceId; } }
    }

    public string? CurrentPeerLabel
    {
        get
        {
            lock (_connectionLock)
            {
                return _peerLabel;
            }
        }
    }

    public void UpdateLocalBounds(VirtualDesktopBounds bounds)
    {
        _localBounds = bounds;
    }

    public void PrintState()
    {
        var listening = _listenPort is null ? "OFF" : $"ON:{_listenPort}";
        var peer = _peerLabel ?? "none";
        Console.WriteLine($"LAN state: listening={listening} peer={peer} deviceId={_deviceId}");
    }

    public Task StartListeningAsync(int port = DefaultPort)
    {
        ThrowIfDisposed();

        if (_listener is not null)
        {
            Console.WriteLine($"LAN listener is already active on port {_listenPort}.");
            return Task.CompletedTask;
        }

        // Plaintext exists only for local regression/development. Never expose
        // an unauthenticated input endpoint to the LAN, even through CLI options.
        var address = IsSecure ? IPAddress.Any : IPAddress.Loopback;
        _listener = new TcpListener(address, port);
        _listener.Start();
        _listenPort = port;

        Console.WriteLine($"LAN listening on tcp://{address}:{port}");
        _ = AcceptLoopAsync(_listener, port);
        return Task.CompletedTask;
    }

    public async Task ConnectAsync(string host, int port = DefaultPort)
    {
        ThrowIfDisposed();

        if (IsSecure) { throw new InvalidOperationException("Use the approved secure connection method."); }
        IPAddress target;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) { target = IPAddress.Loopback; }
        else if (!IPAddress.TryParse(host, out target!) || !IPAddress.IsLoopback(target))
            throw new InvalidOperationException("Plaintext development connections are restricted to loopback.");

        Console.WriteLine($"LAN connecting to {host}:{port}...");
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(target, port).WaitAsync(TimeSpan.FromSeconds(5), _shutdown.Token).ConfigureAwait(false);
            ReplaceConnection(client, client.GetStream(), $"{host}:{port}", null);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task ConnectSecureAsync(string host, ApprovedPeer peer, int port = SecurePort,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_identity is null || !_approvedPeers!().Contains(peer) || peer.DeviceId == _deviceId)
            throw new InvalidOperationException("Approve and select the other computer before connecting.");
        if (host.Length > 253 || Uri.CheckHostName(host) == UriHostNameType.Unknown || port is < 1 or > 65535)
            throw new ArgumentException("Enter the other computer's local address or hostname, without a URL or port.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var client = new TcpClient();
        Stream? authenticatedStream = null;
        try
        {
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            authenticatedStream = await SecurePeerTransport.AuthenticateClientAsync(client, _identity.Certificate, peer, timeout.Token).ConfigureAwait(false);
            lock (_connectionLock)
            {
                timeout.Token.ThrowIfCancellationRequested();
                // Selection or approval may have changed during authentication.
                if (!_approvedPeers!().Contains(peer)) throw new InvalidOperationException("Computer approval changed.");
                ReplaceConnection(client, authenticatedStream, $"{host}:{port}", peer);
            }
        }
        catch { authenticatedStream?.Dispose(); client.Dispose(); throw; }
    }

    public void PublishEdgeHandoff(string edge, int virtualX, int virtualY, int deltaX, int deltaY, VirtualDesktopBounds bounds)
    {
        var payload = new EdgeHandoffPayload(
            edge.ToLowerInvariant(),
            virtualX,
            virtualY,
            deltaX,
            deltaY,
            ScreenBounds.From(bounds));

        _ = SendMessageSafelyAsync("cursor.edgeHandoff", payload);
    }

    public void PublishCursorHandoff(TopologyHandoff handoff)
    {
        var payload = new CursorHandoffPayload(
            handoff.LinkId,
            handoff.SegmentPosition,
            handoff.SourceDisplayId,
            handoff.SourceEdge,
            handoff.SourcePosition,
            handoff.SourceX,
            handoff.SourceY,
            handoff.TargetDeviceId,
            handoff.TargetDisplayId,
            handoff.TargetEdge,
            handoff.TargetPosition,
            handoff.TargetX,
            handoff.TargetY);

        _ = SendMessageSafelyAsync("cursor.handoff", payload);
    }

    public void PublishMouseDelta(int deltaX, int deltaY)
    {
        _ = SendMessageSafelyAsync("input.mouseDelta", new MouseDeltaPayload(deltaX, deltaY));
    }

    public void PublishMouseButton(string button, bool isDown)
    {
        _ = SendMessageSafelyAsync("input.mouseButton", new MouseButtonPayload(button, isDown));
    }

    public void PublishMouseWheel(int verticalDelta, int horizontalDelta)
    {
        _ = SendMessageSafelyAsync("input.mouseWheel", new MouseWheelPayload(verticalDelta, horizontalDelta));
    }

    public void PublishKeyInput(ushort virtualKey, ushort scanCode, bool isKeyUp, bool isExtended, string? text)
    {
        _ = SendMessageSafelyAsync("input.key", new KeyInputPayload(virtualKey, scanCode, isKeyUp, isExtended, text));
    }

    public void PublishShortcutInput(string action)
    {
        _ = SendMessageSafelyAsync("input.shortcut", new ShortcutInputPayload(action));
    }

    public void PublishClipboardText(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > BoundedFrameReader.MaximumClipboardBytes)
        {
            Console.WriteLine("Clipboard is larger than 64 KiB; sharing skipped.");
            return;
        }
        _ = SendMessageSafelyAsync("clipboard.text", new ClipboardTextPayload("text/plain", text));
    }

    public void PublishControlTestStart(TimeSpan duration)
    {
        _ = SendMessageSafelyAsync("control.testStart", new ControlTestStartPayload((int)Math.Round(duration.TotalSeconds)));
    }

    public void PublishControlTestStop(string reason)
    {
        _ = SendMessageSafelyAsync("control.testStop", new ControlTestStopPayload(reason));
    }

    public async Task SendDeviceHelloAsync()
    {
        var payload = new DeviceHelloPayload(
            _deviceId,
            Environment.MachineName,
            "Windows",
            "BatCave.WindowsAgent",
            ScreenBounds.From(_localBounds));

        await SendMessageSafelyAsync("device.hello", payload).ConfigureAwait(false);
    }

    private async Task AcceptLoopAsync(TcpListener listener, int port)
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                if (_identity is null)
                    ReplaceConnection(client, client.GetStream(), GetRemoteEndpoint(client), null);
                else if (_handshakeSlots.Wait(0))
                    _ = AcceptSecureConnectionAsync(client);
                else
                    client.Dispose();

            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"LAN accept failed: {ex.Message}");
            }
        }
    }

    private async Task AcceptSecureConnectionAsync(TcpClient client)
    {
        try
        {
            var (stream, peer) = await SecurePeerTransport.AuthenticateServerAsync(client, _identity!.Certificate,
                _approvedPeers!(), _shutdown.Token).ConfigureAwait(false);
            // Approval can be revoked while authentication is in progress.
            if (!_approvedPeers().Contains(peer)) { stream.Dispose(); client.Dispose(); return; }
            ReplaceConnection(client, stream, GetRemoteEndpoint(client), peer);
        }
        catch (Exception ex)
        {
            client.Dispose();
            Console.WriteLine($"Secure connection rejected: {ex.GetType().Name}");
        }
        finally { _handshakeSlots.Release(); }
    }

    public void DisconnectUnapprovedPeer()
    {
        lock (_connectionLock)
        {
            if (_client is not null && _authenticatedPeer is not null && !_approvedPeers!().Contains(_authenticatedPeer))
                ReportDisconnectedIfCurrent(_client, _peerLabel ?? "peer");
        }
    }

    public void Disconnect()
    {
        lock (_connectionLock)
        {
            if (_client is not null) { ReportDisconnectedIfCurrent(_client, _peerLabel ?? "peer"); }
        }
    }

    private void ReplaceConnection(TcpClient client, Stream stream, string peerLabel, ApprovedPeer? authenticatedPeer)
    {
        client.NoDelay = true;
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 5);
        client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 1);
        client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 5);
        var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: false) { AutoFlush = true };
        var outbound = new OutboundQueue();
        lock (_connectionLock)
        {
            if (_disposed)
            {
                client.Dispose();
                return;
            }
            // End the previous session before exposing its replacement. Queued input
            // belongs to one socket and must never spill into the new session.
            if (_client is { } previous)
            {
                ReportDisconnectedIfCurrent(previous, _peerLabel ?? "peer");
            }
            var hello = new DeviceHelloPayload(_deviceId, Environment.MachineName, "Windows",
                "BatCave.WindowsAgent", ScreenBounds.From(_localBounds));
            var helloJson = JsonSerializer.Serialize(BatCaveEnvelope.Create(_deviceId, "device.hello", hello), _jsonOptions);
            var helloBytes = Encoding.UTF8.GetByteCount(helloJson) + 1;
            outbound.Bytes = helloBytes;
            outbound.Channel.Writer.TryWrite(new OutboundMessage("device.hello", helloJson, helloBytes,
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)));
            _client = client;
            _writer = writer;
            _outbound = outbound;
            _peerLabel = peerLabel;
            _peerDeviceId = null;
            _authenticatedPeer = authenticatedPeer;
            Interlocked.Increment(ref _sessionGeneration);
        }
        Console.WriteLine($"LAN TCP connected: {peerLabel}; waiting for device.hello");
        _ = SendLoopAsync(client, writer, outbound, peerLabel);
        _ = ReceiveLoopAsync(client, stream, peerLabel);
        _ = CheckHelloTimeoutAsync(client, peerLabel);
    }

    private async Task CheckHelloTimeoutAsync(TcpClient client, string peerLabel)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(5), _shutdown.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        lock (_connectionLock)
        {
            if (ReferenceEquals(_client, client) && _peerDeviceId is null)
            {
                Console.WriteLine($"LAN handshake timed out: {peerLabel}");
                ReportDisconnectedIfCurrent(client, peerLabel);
            }
        }
    }

    private async Task SendLoopAsync(TcpClient client, StreamWriter writer,
        OutboundQueue outbound, string peerLabel)
    {
        try
        {
            await foreach (var message in outbound.Channel.Reader.ReadAllAsync())
            {
                try
                {
                    await writer.WriteLineAsync(message.Json).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    message.Completion.TrySetResult(true);
                    if (ShouldLogMessage(message.Type)) { Console.WriteLine($"LAN TX {message.Type} to {peerLabel}"); }
                }
                catch
                {
                    message.Completion.TrySetResult(false);
                    throw;
                }
                finally { Interlocked.Add(ref outbound.Bytes, -message.Bytes); }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"LAN send failed to {peerLabel}: {ex.Message}");
            ReportDisconnectedIfCurrent(client, peerLabel);
        }
        finally
        {
            while (outbound.Channel.Reader.TryRead(out var pending)) { pending.Completion.TrySetResult(false); }
            writer.Dispose();
        }
    }

    private async Task ReceiveLoopAsync(TcpClient client, Stream stream, string peerLabel)
    {
        try
        {
            var reader = new BoundedFrameReader(stream);
            while (!_shutdown.IsCancellationRequested)
            {
                var line = await reader.ReadAsync(_shutdown.Token).ConfigureAwait(false);
                if (line is null)
                {
                    ReportDisconnectedIfCurrent(client, peerLabel);
                    return;
                }

                lock (_connectionLock)
                {
                    if (!ReferenceEquals(_client, client)) { return; }
                    LogReceivedMessage(line);
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
            ReportDisconnectedIfCurrent(client, peerLabel);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"LAN receive failed from {peerLabel}: {ex.Message}");
            ReportDisconnectedIfCurrent(client, peerLabel);
        }
    }

    private void ReportDisconnectedIfCurrent(TcpClient client, string peerLabel)
    {
        if (!ClearConnectionIfCurrent(client))
        {
            return;
        }

        Console.WriteLine($"LAN peer disconnected: {peerLabel}");
        Console.WriteLine($"APPSTATUS disconnected peer={peerLabel}");
        PeerDisconnected?.Invoke(peerLabel);
    }

    private bool ClearConnectionIfCurrent(TcpClient client)
    {
        lock (_connectionLock)
        {
            if (!ReferenceEquals(_client, client))
            {
                return false;
            }

            _outbound?.Channel.Writer.TryComplete();
            _outbound = null;
            _writer = null;
            _peerDeviceId = null;
            _authenticatedPeer = null;
            Interlocked.Increment(ref _sessionGeneration);
            _client?.Close();
            _client = null;
            _peerLabel = null;
            return true;
        }
    }

    private Task<bool> SendMessageSafelyAsync(string type, object payload)
    {
        lock (_connectionLock)
        {
            if (_outbound is null || _client is null) { return Task.FromResult(false); }
            var json = JsonSerializer.Serialize(BatCaveEnvelope.Create(_deviceId, type, payload), _jsonOptions);
            var bytes = Encoding.UTF8.GetByteCount(json) + 1;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var queue = _outbound;
            var reserved = Interlocked.Add(ref queue.Bytes, bytes);
            if (bytes - 1 > BoundedFrameReader.MaximumFrameBytes || reserved > BoundedFrameReader.MaximumQueuedBytes
                || !queue.Channel.Writer.TryWrite(new OutboundMessage(type, json, bytes, completion)))
            {
                Interlocked.Add(ref queue.Bytes, -bytes);
                // Fail closed instead of dropping a key-up or accumulating stale motion.
                Console.WriteLine("LAN send queue full; ending peer session");
                ReportDisconnectedIfCurrent(_client, _peerLabel ?? "peer");
                completion.TrySetResult(false);
            }
            _lastSend = completion.Task;
            return completion.Task;
        }
    }

    public async Task FlushAsync()
    {
        Task<bool> pending;
        lock (_connectionLock) { pending = _lastSend; }
        try { await pending.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { Console.WriteLine("LAN shutdown flush timed out"); }
    }

    private void LogReceivedMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var protocol = root.TryGetProperty("protocol", out var protocolElement)
                ? protocolElement.GetString()
                : "unknown";
            var type = root.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : "unknown";
            var source = root.TryGetProperty("sourceDeviceId", out var sourceElement)
                ? sourceElement.GetString()
                : "unknown";

            if (!string.Equals(protocol, BatCaveProtocol.Name, StringComparison.Ordinal))
            {
                if (_identity is not null) throw new InvalidDataException("Unsupported authenticated protocol.");
                Console.WriteLine($"LAN RX unsupported protocol={protocol} type={type} from {source}");
                return;
            }

            if (!root.TryGetProperty("payload", out var payload))
            {
                if (_identity is not null) throw new InvalidDataException("Missing authenticated payload.");
                Console.WriteLine($"LAN RX {type} from {source} missing payload");
                return;
            }

            if (payload.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(source)
                || string.IsNullOrWhiteSpace(type) || type.Length > 64 || source.Length > 128
                || source.Any(char.IsControl) || type.Any(char.IsControl))
            {
                if (_identity is not null) throw new InvalidDataException("Invalid authenticated envelope.");
                return;
            }
            if (type == "device.hello")
            {
                var helloId = ReadString(payload, "deviceId");
                if (helloId != source || helloId == _deviceId || helloId == "missing")
                {
                    if (_identity is not null) throw new InvalidDataException("Invalid hello identity.");
                    return;
                }
                if (_authenticatedPeer is not null && helloId != _authenticatedPeer.DeviceId)
                {
                    throw new InvalidDataException("Hello device ID does not match its approved certificate.");
                }
                if (_identity is not null && _peerDeviceId is not null)
                    throw new InvalidDataException("Unexpected repeated hello.");
                if (_peerDeviceId is null)
                {
                    var hello = payload.Deserialize<DeviceHelloPayload>(_jsonOptions);
                    if (_identity is not null && (hello?.VirtualDesktopBounds is not { } bounds
                        || bounds.Width <= 0 || bounds.Height <= 0 || bounds.Width > 100000 || bounds.Height > 100000
                        || bounds.Left is < -1_000_000 or > 1_000_000 || bounds.Top is < -1_000_000 or > 1_000_000
                        // Existing Mac agents advertise an exclusive maximum;
                        // Windows/Linux advertise the last pixel. Accept both.
                        || (long)bounds.Right - bounds.Left - bounds.Width is < -1 or > 0
                        || (long)bounds.Bottom - bounds.Top - bounds.Height is < -1 or > 0))
                        throw new InvalidDataException("Invalid authenticated screen bounds.");
                    _peerDeviceId = helloId;
                    if (hello is not null) { PeerHello?.Invoke(hello); }
                    Console.WriteLine($"LAN peer connected: {_peerLabel} device={helloId}");
                    Console.WriteLine($"APPSTATUS connected peer={_peerLabel}");
                    PeerConnected?.Invoke(_peerLabel ?? "peer");
                }
            }
            if (!string.Equals(source, _peerDeviceId, StringComparison.Ordinal))
            {
                if (_identity is not null) { throw new InvalidDataException("Unexpected source identity."); }
                return;
            }

            if (ShouldLogMessage(type))
            {
                Console.WriteLine($"LAN RX {type} from {source}: {DescribePayload(type, payload)}");
            }

            if (string.Equals(type, "clipboard.text", StringComparison.Ordinal))
            {
                DispatchClipboardText(source ?? "unknown", payload);
            }
            else if (string.Equals(type, "input.shortcut", StringComparison.Ordinal))
            {
                DispatchShortcutInput(source ?? "unknown", payload);
            }
            if (_identity is not null && type is "cursor.handoff" or "input.mouseDelta" or "input.mouseButton"
                or "input.mouseWheel" or "input.key" or "input.shortcut" or "control.testStop")
                InputMessageReceived?.Invoke(source!, SessionGeneration, type!, payload);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            if (_identity is not null) throw new InvalidDataException("Invalid authenticated message.", ex);
            Console.WriteLine($"LAN RX invalid json bytes={Encoding.UTF8.GetByteCount(json)}");
        }
    }

    private void DispatchShortcutInput(string sourceDeviceId, JsonElement payload)
    {
        try
        {
            var shortcut = payload.Deserialize<ShortcutInputPayload>(_jsonOptions);
            if (shortcut is null)
            {
                Console.WriteLine($"LAN RX input.shortcut from {sourceDeviceId} has empty payload");
                return;
            }

            ShortcutInputReceived?.Invoke(sourceDeviceId, shortcut);
        }
        catch (JsonException)
        {
            if (_identity is not null) throw new InvalidDataException("Invalid authenticated shortcut.");
            Console.WriteLine("LAN RX invalid shortcut");
        }
    }

    private void DispatchClipboardText(string sourceDeviceId, JsonElement payload)
    {
        try
        {
            var clipboard = payload.Deserialize<ClipboardTextPayload>(_jsonOptions);
            if (clipboard is null)
            {
                Console.WriteLine($"LAN RX clipboard.text from {sourceDeviceId} has empty payload");
                return;
            }

            if (clipboard.Format != "text/plain" || clipboard.Text is null
                || Encoding.UTF8.GetByteCount(clipboard.Text) > BoundedFrameReader.MaximumClipboardBytes)
                throw new InvalidDataException("Clipboard exceeds its allowed type or size.");

            ClipboardTextReceived?.Invoke(sourceDeviceId, clipboard);
        }
        catch (JsonException)
        {
            if (_identity is not null) throw new InvalidDataException("Invalid authenticated clipboard.");
            Console.WriteLine("LAN RX invalid clipboard");
        }
    }

    private static bool ShouldLogMessage(string? type)
    {
        return type is not "input.mouseDelta" and not "input.key";
    }

    private static string DescribePayload(string? type, JsonElement payload)
    {
        return type switch
        {
            "device.hello" => DescribeDeviceHello(payload),
            "cursor.edgeHandoff" => DescribeEdgeHandoff(payload),
            "cursor.handoff" => DescribeCursorHandoff(payload),
            "input.mouseDelta" => DescribeMouseDelta(payload),
            "input.mouseButton" => DescribeMouseButton(payload),
            "input.mouseWheel" => DescribeMouseWheel(payload),
            "input.key" => DescribeKeyInput(payload),
            "input.shortcut" => DescribeShortcutInput(payload),
            "clipboard.text" => DescribeClipboardText(payload),
            "control.testStart" => DescribeControlTestStart(payload),
            "control.testStop" => DescribeControlTestStop(payload),
            "file.offer" => DescribeFileOffer(payload),
            _ => $"unhandled payload bytes={Encoding.UTF8.GetByteCount(payload.GetRawText())}"
        };
    }

    private static string DescribeDeviceHello(JsonElement payload)
    {
        var deviceId = ReadString(payload, "deviceId");
        var deviceName = ReadString(payload, "deviceName");
        var operatingSystem = ReadString(payload, "operatingSystem");
        var agentName = ReadString(payload, "agentName");
        var bounds = payload.TryGetProperty("virtualDesktopBounds", out var boundsElement)
            ? DescribeBounds(boundsElement)
            : "bounds=missing";

        return $"deviceId={deviceId} name={deviceName} os={operatingSystem} agent={agentName} {bounds}";
    }

    private static string DescribeEdgeHandoff(JsonElement payload)
    {
        var edge = ReadString(payload, "edge");
        var x = ReadInt(payload, "virtualX");
        var y = ReadInt(payload, "virtualY");
        var dx = ReadInt(payload, "deltaX");
        var dy = ReadInt(payload, "deltaY");

        return $"edge={edge} virtual=({x},{y}) delta=({dx},{dy})";
    }

    private static string DescribeCursorHandoff(JsonElement payload)
    {
        var linkId = ReadString(payload, "linkId");
        var targetDeviceId = ReadString(payload, "targetDeviceId");
        var targetDisplayId = ReadString(payload, "targetDisplayId");
        var targetEdge = ReadString(payload, "targetEdge");
        var targetPosition = ReadDouble(payload, "targetPosition");
        var targetX = ReadInt(payload, "targetX");
        var targetY = ReadInt(payload, "targetY");
        var segmentPosition = ReadDouble(payload, "segmentPosition");

        return $"link={linkId} segment={segmentPosition:0.000} target={targetDeviceId}/{targetDisplayId} {targetEdge} {targetPosition:0.000} point=({targetX},{targetY})";
    }

    private static string DescribeMouseDelta(JsonElement payload)
    {
        var dx = ReadInt(payload, "deltaX");
        var dy = ReadInt(payload, "deltaY");
        return $"delta=({dx},{dy})";
    }

    private static string DescribeMouseButton(JsonElement payload)
    {
        var button = ReadString(payload, "button");
        var isDown = ReadBool(payload, "isDown");
        return $"button={button} down={isDown}";
    }

    private static string DescribeMouseWheel(JsonElement payload)
    {
        var vertical = ReadInt(payload, "verticalDelta");
        var horizontal = ReadInt(payload, "horizontalDelta");
        return $"wheel=({horizontal},{vertical})";
    }

    private static string DescribeKeyInput(JsonElement payload)
    {
        var virtualKey = ReadInt(payload, "virtualKey");
        var scanCode = ReadInt(payload, "scanCode");
        var isKeyUp = ReadBool(payload, "isKeyUp");
        var isExtended = ReadBool(payload, "isExtended");
        return $"up={isKeyUp} extended={isExtended}";
    }

    private static string DescribeShortcutInput(JsonElement payload)
    {
        return $"action={ReadString(payload, "action")}";
    }

    private static string DescribeClipboardText(JsonElement payload)
    {
        var format = ReadString(payload, "format");
        var text = ReadString(payload, "text");
        return $"format={format} chars={(text == "missing" ? 0 : text.Length)}";
    }

    private static string DescribeControlTestStart(JsonElement payload)
    {
        return $"duration={ReadInt(payload, "durationSeconds")}s";
    }

    private static string DescribeControlTestStop(JsonElement payload)
    {
        return $"reason={ReadString(payload, "reason")}";
    }

    private static string DescribeFileOffer(JsonElement payload)
    {
        var transferId = ReadString(payload, "transferId");
        var fileName = ReadString(payload, "fileName");
        var byteLength = ReadLong(payload, "byteLength");

        return $"transferId={transferId} fileName={fileName} byteLength={byteLength}";
    }

    private static string DescribeBounds(JsonElement bounds)
    {
        var left = ReadInt(bounds, "left");
        var top = ReadInt(bounds, "top");
        var width = ReadInt(bounds, "width");
        var height = ReadInt(bounds, "height");
        var right = ReadInt(bounds, "right");
        var bottom = ReadInt(bounds, "bottom");

        return $"bounds=left={left},top={top},width={width},height={height},right={right},bottom={bottom}";
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.ToString()
            : "missing";
    }

    private static int ReadInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : 0;
    }

    private static double ReadDouble(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetDouble(out var value)
            ? value
            : 0;
    }

    private static long ReadLong(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value)
            ? value
            : 0;
    }

    private static bool ReadBool(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.True;
    }

    private static string EscapeForLog(string text)
    {
        return text
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
    }

    private static string GetRemoteEndpoint(TcpClient client)
    {
        return client.Client.RemoteEndPoint?.ToString() ?? "unknown";
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LanBridgePeer));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        _listener?.Stop();

        lock (_connectionLock)
        {
            _outbound?.Channel.Writer.TryComplete();
            _outbound = null;
            _writer = null;
            _peerDeviceId = null;
            _client?.Close();
            _client = null;
            _peerLabel = null;
        }
    }
}
