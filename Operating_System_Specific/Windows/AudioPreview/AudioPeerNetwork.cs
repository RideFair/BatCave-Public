using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Channels;
using BatCave.Audio;
using BatCave.WindowsAgent;

namespace BatCave.AudioPreview;

// Callbacks are serialized with connection registration. Handlers must only enqueue
// work; they must not wait for native audio, the UI, or another network callback.
internal sealed class AudioPeerNetwork : IAudioPeerLink, IDisposable
{
    public const string Protocol = "batcave.audio.v1";
    public const int Port = 47873;
    private const int MaxControlBytes = 16384;
    private const int MaxConnections = 8;
    private readonly object _gate = new();
    private readonly string _id;
    private readonly X509Certificate2 _certificate;
    private readonly Func<ApprovedPeer[]> _approvals;
    private readonly Func<AudioDeviceInventory> _inventory;
    private readonly Dictionary<string, Connection> _peers = new(StringComparer.Ordinal);
    private readonly Dictionary<long, Admission> _pending = new();
    private readonly HashSet<Task> _workers = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _admissions = new(4, 4);
    private long _pendingId;
    private bool _disposed;
    private TcpListener? _listener;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { MaxDepth = 12 };
    private sealed record Hello(string Protocol, string Type, string DeviceId, Guid SessionId, AudioDeviceInventory Inventory);
    private sealed record QueuedFrame(byte Kind, byte[] Data, long Created, long AudioEpoch = 0);

    private sealed class Admission
    {
        private readonly object _lifetime = new();
        private bool _finished;
        public required long Id;
        public required TcpClient Socket;
        public required CancellationTokenSource Stop;
        public bool HoldsSlot = true; // accessed only under the network gate
        public void Cancel() { lock (_lifetime) { if (_finished) return; Stop.Cancel(); Socket.Dispose(); } }
        public void ReleaseResources() { lock (_lifetime) { if (_finished) return; _finished = true; Stop.Dispose(); } }
    }

    private sealed class Connection : IDisposable
    {
        private int _closed;
        public required TcpClient Socket;
        public required SslStream Stream;
        public required AudioPeerSession Peer;
        public required Guid LocalSession;
        public required CancellationTokenSource Stop;
        public Guid OutgoingStream;
        public bool SendingPaused;
        public long AudioEpoch;
        public readonly HashSet<Guid> RetiredStreams = new();
        public readonly Channel<QueuedFrame> Control = Channel.CreateBounded<QueuedFrame>(new BoundedChannelOptions(32) { SingleReader = true });
        public readonly Channel<QueuedFrame> Audio = Channel.CreateBounded<QueuedFrame>(new BoundedChannelOptions(8) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
        public readonly SemaphoreSlim Ready = new(0, 1);
        public bool IsStopped => Volatile.Read(ref _closed) != 0;
        public void Signal() { try { if (Ready.CurrentCount == 0) Ready.Release(); } catch (ObjectDisposedException) { } catch (SemaphoreFullException) { } }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            Stop.Cancel(); Socket.Dispose(); Stream.Dispose();
            Control.Writer.TryComplete(); Audio.Writer.TryComplete(); Signal();
        }
        // Only Run's finally calls this, after both its reader and writer have exited.
        public void ReleaseResources() { Stop.Dispose(); Ready.Dispose(); }
    }

    public event Action<AudioPeerSession, AudioDeviceInventory>? InventoryReceived;
    public event Action<AudioPeerSession, string, JsonElement>? ControlReceived;
    public event Action<AudioPeerSession, AudioPacket>? PacketReceived;
    public event Action<string>? PeerDisconnected;
    public event Action<string>? Status;
    public AudioPeerNetwork(string id, X509Certificate2 certificate, Func<ApprovedPeer[]> approvals, Func<AudioDeviceInventory> inventory)
    { _id = id; _certificate = certificate; _approvals = approvals; _inventory = inventory; }
    public AudioPeerSession? Session(string id)
    { lock (_gate) return _peers.TryGetValue(id, out var connection) && !connection.IsStopped ? connection.Peer : null; }
    public string[] ConnectedIds
    { get { lock (_gate) return _peers.Where(p => !p.Value.IsStopped).Select(p => p.Key).ToArray(); } }
    public int LocalPort
    { get { lock (_gate) return _listener?.LocalEndpoint is IPEndPoint endpoint ? endpoint.Port : 0; } }
    internal int PendingCount { get { lock (_gate) return _pending.Count; } }

    public void Listen(int port = Port, IPAddress? bindAddress = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_listener is not null) return;
            var listener = new TcpListener(bindAddress ?? IPAddress.Any, port);
            listener.Start(8); _listener = listener; Track(AcceptLoop(listener));
        }
    }
    private void Track(Task worker)
    {
        lock (_gate) _workers.Add(worker);
        _ = worker.ContinueWith(completed => { lock (_gate) _workers.Remove(completed); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private Admission? Admit(TcpClient socket)
    {
        lock (_gate)
        {
            if (_disposed || !_admissions.Wait(0)) { socket.Dispose(); return null; }
            var admission = new Admission { Id = ++_pendingId, Socket = socket,
                Stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token) };
            _pending.Add(admission.Id, admission);
            return admission;
        }
    }
    private void ReleaseAdmission(Admission admission)
    {
        lock (_gate)
        {
            if (!admission.HoldsSlot) return;
            admission.HoldsSlot = false; _pending.Remove(admission.Id); _admissions.Release();
        }
    }
    private async Task AcceptLoop(TcpListener listener)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient? socket = null;
            try
            {
                socket = await listener.AcceptTcpClientAsync(_stop.Token);
                var admission = Admit(socket);
                if (admission is not null) Track(Accept(admission));
            }
            catch (Exception) when (_stop.IsCancellationRequested) { break; }
            catch { socket?.Dispose(); ReportStatus("Audio listener could not accept a connection."); }
        }
    }
    private async Task Accept(Admission admission)
    {
        SslStream? stream = null;
        try
        {
            var authenticated = await SecurePeerTransport.AuthenticateServerAsync(admission.Socket, _certificate, _approvals(), admission.Stop.Token);
            stream = authenticated.Stream;
            await Run(admission, stream, authenticated.Peer.DeviceId);
        }
        catch (Exception) { ReportStatus("An audio connection was rejected or ended."); }
        finally { stream?.Dispose(); admission.Socket.Dispose(); ReleaseAdmission(admission); admission.ReleaseResources(); }
    }
    public async Task Connect(string host, ApprovedPeer expected, int port = Port)
    {
        Admission admission;
        var established = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_approvals().Any(peer => peer.DeviceId == expected.DeviceId
                && string.Equals(peer.CertificateSha256, expected.CertificateSha256, StringComparison.OrdinalIgnoreCase)))
                throw new AuthenticationException("An approved audio peer is required.");
            admission = Admit(new TcpClient { NoDelay = true }) ?? throw new InvalidOperationException("Audio connection admission limit reached.");
            Track(RunOutbound(admission, host, port, expected, established));
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await established.Task.WaitAsync(timeout.Token); }
        catch { admission.Cancel(); throw; }
    }
    private async Task RunOutbound(Admission admission, string host, int port, ApprovedPeer expected, TaskCompletionSource established)
    {
        SslStream? stream = null;
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(admission.Stop.Token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await admission.Socket.ConnectAsync(host, port, timeout.Token);
                stream = await SecurePeerTransport.AuthenticateClientAsync(admission.Socket, _certificate, expected, timeout.Token);
            }
            await Run(admission, stream, expected.DeviceId, established);
        }
        catch (Exception ex) { established.TrySetException(ex); ReportStatus("The audio connection ended."); }
        finally { stream?.Dispose(); admission.Socket.Dispose(); ReleaseAdmission(admission); admission.ReleaseResources(); }
    }
    private void RequireCurrentApproval(string id, X509Certificate? certificate)
    {
        if (!_approvals().Any(peer => peer.DeviceId == id && SecurePeerTransport.MatchesApprovedPeer(certificate, peer)))
            throw new AuthenticationException("The audio peer is no longer approved.");
    }
    private void Register(Admission admission, Connection connection, AudioDeviceInventory inventory)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            admission.Stop.Token.ThrowIfCancellationRequested();
            RequireCurrentApproval(connection.Peer.DeviceId, connection.Stream.RemoteCertificate);
            if (_peers.TryGetValue(connection.Peer.DeviceId, out var previous))
            {
                _peers.Remove(connection.Peer.DeviceId); previous.Dispose();
                // The locally observed transport change revokes consent even when the
                // remote hello reuses its old SessionId. This precedes the new inventory.
                NotifyDisconnected(connection.Peer.DeviceId);
            }
            else if (_peers.Count >= MaxConnections) throw new InvalidDataException("Audio connection limit reached.");
            _peers.Add(connection.Peer.DeviceId, connection);
            ReleaseAdmission(admission);
            InventoryReceived?.Invoke(connection.Peer, inventory);
        }
    }
    private bool Deliver(Connection connection, Action callback)
    {
        lock (_gate)
        {
            if (_disposed || connection.IsStopped || !_peers.TryGetValue(connection.Peer.DeviceId, out var current)
                || !ReferenceEquals(current, connection)) return false;
            RequireCurrentApproval(connection.Peer.DeviceId, connection.Stream.RemoteCertificate);
            callback(); return true;
        }
    }
    private void Remove(Connection connection)
    {
        lock (_gate)
        {
            if (!_peers.TryGetValue(connection.Peer.DeviceId, out var current) || !ReferenceEquals(current, connection)) return;
            _peers.Remove(connection.Peer.DeviceId);
            NotifyDisconnected(connection.Peer.DeviceId);
        }
    }
    private async Task Run(Admission admission, SslStream stream, string expectedId, TaskCompletionSource? established = null)
    {
        admission.Socket.NoDelay = true;
        var localSession = Guid.NewGuid(); Hello hello;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(admission.Stop.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var localInventory = _inventory(); ValidateInventory(new(_id, localSession), localInventory);
            await WriteFrame(stream, 1, JsonSerializer.SerializeToUtf8Bytes(new Hello(Protocol, "hello", _id, localSession, localInventory), _json), timeout.Token);
            var first = await ReadFrame(stream, timeout.Token);
            if (first.Kind != 1) throw new InvalidDataException("Audio hello required.");
            hello = JsonSerializer.Deserialize<Hello>(first.Data, _json) ?? throw new InvalidDataException("Audio hello required.");
            if (hello.Protocol != Protocol || hello.Type != "hello" || hello.DeviceId != expectedId || hello.DeviceId == _id || hello.SessionId == Guid.Empty)
                throw new InvalidDataException("Audio hello does not match the approved certificate.");
            ValidateInventory(new(expectedId, hello.SessionId), hello.Inventory);
        }
        var connection = new Connection { Socket = admission.Socket, Stream = stream, Peer = new(expectedId, hello.SessionId), LocalSession = localSession,
            Stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token) };
        Task? writer = null;
        try
        {
            Register(admission, connection, hello.Inventory);
            writer = Writer(connection); established?.TrySetResult();
            var window = Stopwatch.GetTimestamp(); var audioCount = 0; var controls = 0;
            while (!connection.IsStopped)
            {
                var frame = await ReadFrame(stream, connection.Stop.Token);
                if (Stopwatch.GetElapsedTime(window).TotalSeconds >= 1) { window = Stopwatch.GetTimestamp(); audioCount = controls = 0; }
                if (frame.Kind == 2)
                {
                    if (++audioCount > 250 || !AudioPacketCodec.TryDecode(frame.Data, out var packet) || packet is null
                        || packet.OriginDeviceId != connection.Peer.DeviceId || packet.DeviceSessionId != connection.Peer.SessionId || packet.VisitedDeviceIds.Count != 1)
                        throw new InvalidDataException("Invalid audio source frame.");
                    if (!Deliver(connection, () => PacketReceived?.Invoke(connection.Peer, packet))) break;
                }
                else
                {
                    if (++controls > 100) throw new InvalidDataException("Excessive audio control frames.");
                    using var document = JsonDocument.Parse(frame.Data, new JsonDocumentOptions { MaxDepth = 12 });
                    var root = document.RootElement;
                    var type = root.GetProperty("type").GetString() ?? throw new InvalidDataException("Missing audio message type.");
                    var payload = root.GetProperty("payload");
                    if (type == "inventory")
                    {
                        var inventory = payload.Deserialize<AudioDeviceInventory>(_json) ?? throw new InvalidDataException("Missing audio inventory.");
                        ValidateInventory(connection.Peer, inventory);
                        if (!Deliver(connection, () => InventoryReceived?.Invoke(connection.Peer, inventory))) break;
                    }
                    else if (type is "subscribe" or "offer" or "accept" or "reject" or "stop")
                    {
                        if (!Deliver(connection, () =>
                        {
                            if (type == "stop") UpdateSendingState(connection, type, payload);
                            ControlReceived?.Invoke(connection.Peer, type, payload.Clone());
                        })) break;
                    }
                    else throw new InvalidDataException("Unknown audio control message.");
                }
            }
        }
        finally
        {
            connection.Dispose();
            if (writer is not null) try { await writer; } catch { }
            try { Remove(connection); } finally { connection.ReleaseResources(); }
        }
    }
    private static void ValidateInventory(AudioPeerSession peer, AudioDeviceInventory inventory)
    { using var validator = new AudioRoutingSession(); validator.SetAuthenticatedInventory(peer, inventory); }
    public bool SendControl(string destination, string type, object payload)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(new { type, payload }, _json);
        using var control = (type is "stop" or "offer") && data.Length <= MaxControlBytes ? JsonDocument.Parse(data) : null;
        lock (_gate)
        {
            if (_disposed || !_peers.TryGetValue(destination, out var connection) || connection.IsStopped) return false;
            try { RequireCurrentApproval(destination, connection.Stream.RemoteCertificate); }
            catch { connection.Dispose(); return false; }
            if (control is not null && !UpdateSendingState(connection, type, control.RootElement.GetProperty("payload"))) return false;
            if (data.Length > MaxControlBytes || !connection.Control.Writer.TryWrite(new(1, data, Stopwatch.GetTimestamp())))
            { connection.Dispose(); return false; }
            connection.Signal(); return true;
        }
    }
    public bool SendAudio(string destination, Guid streamId, ulong sequence, float[] samples)
    {
        lock (_gate)
        {
            if (_disposed || !_peers.TryGetValue(destination, out var connection) || connection.IsStopped) return false;
            try { RequireCurrentApproval(destination, connection.Stream.RemoteCertificate); }
            catch { connection.Dispose(); return false; }
            if (connection.SendingPaused || connection.RetiredStreams.Contains(streamId)
                || connection.OutgoingStream != Guid.Empty && connection.OutgoingStream != streamId) return false;
            var data = AudioPacketCodec.Encode(new(_id, connection.LocalSession, streamId, sequence, new[] { _id }, samples));
            connection.OutgoingStream = streamId;
            var queued = connection.Audio.Writer.TryWrite(new(2, data, Stopwatch.GetTimestamp(), connection.AudioEpoch)); connection.Signal(); return queued;
        }
    }
    private static bool UpdateSendingState(Connection connection, string type, JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("streamId", out var value)
            || value.ValueKind != JsonValueKind.String || !value.TryGetGuid(out var stream)) return false;
        if (type == "offer")
        {
            if (stream == Guid.Empty || connection.RetiredStreams.Contains(stream)) return false;
            if (connection.OutgoingStream != Guid.Empty && connection.OutgoingStream != stream)
            {
                if (connection.RetiredStreams.Count >= AudioLimits.MaxStreamsPerPeerSession) { connection.Dispose(); return false; }
                connection.RetiredStreams.Add(connection.OutgoingStream);
                Interlocked.Increment(ref connection.AudioEpoch);
            }
            connection.OutgoingStream = stream; connection.SendingPaused = false; return true;
        }
        if (stream != Guid.Empty && stream != connection.OutgoingStream) return true;
        if (connection.OutgoingStream != Guid.Empty)
        {
            if (connection.RetiredStreams.Count >= AudioLimits.MaxStreamsPerPeerSession) { connection.Dispose(); return false; }
            connection.RetiredStreams.Add(connection.OutgoingStream);
        }
        connection.OutgoingStream = Guid.Empty; connection.SendingPaused = true;
        Interlocked.Increment(ref connection.AudioEpoch); return true;
    }
    public void RefreshInventory() { var inventory = _inventory(); foreach (var id in ConnectedIds) SendControl(id, "inventory", inventory); }
    private static async Task Writer(Connection connection)
    {
        try
        {
            while (!connection.IsStopped)
            {
                if (!connection.Control.Reader.TryRead(out var frame) && !connection.Audio.Reader.TryRead(out frame))
                { await connection.Ready.WaitAsync(connection.Stop.Token); continue; }
                // Stop invalidates queued PCM and late callbacks from the retired stream.
                // A complete frame already committed to the socket cannot be retracted.
                if (frame.Kind == 2 && (frame.AudioEpoch != Volatile.Read(ref connection.AudioEpoch)
                    || Stopwatch.GetElapsedTime(frame.Created).TotalMilliseconds > 150)) continue;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(connection.Stop.Token); deadline.CancelAfter(TimeSpan.FromMilliseconds(500));
                await WriteFrame(connection.Stream, frame.Kind, frame.Data, deadline.Token);
            }
        }
        catch { connection.Dispose(); }
    }
    internal static async Task WriteFrame(Stream stream, byte kind, byte[] data, CancellationToken token)
    {
        if (kind is not (1 or 2) || data.Length == 0 || data.Length > (kind == 1 ? MaxControlBytes : AudioLimits.MaxPacketBytes)) throw new InvalidDataException("Audio frame size rejected.");
        var header = new byte[5]; header[0] = kind; BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), data.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(data, token);
    }
    internal static async Task<(byte Kind, byte[] Data)> ReadFrame(Stream stream, CancellationToken token)
    {
        var header = new byte[5];
        // An idle authenticated peer is allowed. Once it begins a frame, incomplete
        // headers or payloads cannot retain a connection indefinitely.
        await stream.ReadExactlyAsync(header.AsMemory(0, 1), token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        await stream.ReadExactlyAsync(header.AsMemory(1), deadline.Token);
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
        if (header[0] is not (1 or 2) || length <= 0 || length > (header[0] == 1 ? MaxControlBytes : AudioLimits.MaxPacketBytes)) throw new InvalidDataException("Audio frame size rejected.");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, deadline.Token); return (header[0], bytes);
    }
    public void Disconnect(string deviceId)
    {
        lock (_gate)
        {
            if (_disposed) return;
            // Pending authentication has not necessarily disclosed its peer ID yet.
            foreach (var pending in _pending.Values) pending.Cancel();
            if (_peers.Remove(deviceId, out var connection))
            { connection.Dispose(); NotifyDisconnected(deviceId); }
        }
    }
    private void NotifyDisconnected(string deviceId)
    { try { PeerDisconnected?.Invoke(deviceId); } catch { ReportStatus("An audio disconnect callback failed."); } }
    private void ReportStatus(string message)
    { lock (_gate) { if (_disposed) return; try { Status?.Invoke(message); } catch { } } }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _stop.Cancel(); _listener?.Stop();
            foreach (var pending in _pending.Values) pending.Cancel();
            var connections = _peers.Values.ToArray(); _peers.Clear();
            foreach (var connection in connections) { connection.Dispose(); NotifyDisconnected(connection.Peer.DeviceId); }
        }
    }
    // Tests and graceful shutdown may await all TLS/read/write workers. Dispose itself
    // interrupts them immediately and does not block a UI or native audio callback.
    internal async Task WhenStoppedAsync()
    {
        while (true)
        {
            Task[] workers; lock (_gate) workers = _workers.ToArray();
            if (workers.Length == 0) return;
            try { await Task.WhenAll(workers); } catch { }
        }
    }
}
