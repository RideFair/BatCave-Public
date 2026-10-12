using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using BatCave.Audio;

namespace BatCave.AudioPreview;

// All negotiation runs on one bounded worker. Native callbacks use immutable target
// snapshots and the thread-safe mixer; native disposal never holds a registration lock.
internal sealed class AudioCoordinator : IDisposable
{
    private const int MaxPendingWork = 256;
    private const int MaxRetiredStreams = 4_096;
    private readonly IAudioPeerLink _network;
    private readonly IAudioPlatform _platform;
    private readonly object _consentGate = new();
    private readonly AudioRoutingSession _core = new();
    private readonly AudioPeerSession _localPeer;
    private readonly Dictionary<string, Device> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Source, string Endpoint), float> _receiveAcl = new();
    private readonly Dictionary<string, Incoming> _incoming = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Device, string Endpoint), Outgoing> _outgoing = new();
    private readonly HashSet<(string Device, Guid Stream)> _seenInbound = new();
    private readonly HashSet<string> _stoppedDestinations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NativeOutput> _outputs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _peerEpochs = new(StringComparer.Ordinal);
    private readonly Channel<Action> _work = Channel.CreateBounded<Action>(new BoundedChannelOptions(MaxPendingWork)
        { SingleReader = true, SingleWriter = false });
    private readonly Task _worker;
    private AudioDeviceInventory _local;
    private AudioRoute[] _desired = Array.Empty<AudioRoute>();
    private SendTarget[] _sendTargets = Array.Empty<SendTarget>();
    private AudioPreviewState _state;
    private IAudioCapture? _capture;
    private CancellationTokenRegistration _captureCancellation;
    private Guid _localStream;
    private int _enabled;
    private bool _allowSending;
    private bool _captureWasStopped;
    private long _operation;
    private long _consentOperation;
    private string? _emergencyPause;
    private int _disposed;

    public AudioCoordinator(AudioDeviceInventory local, IAudioPeerLink network, IAudioPlatform platform)
    {
        _network = network; _platform = platform;
        _localPeer = new(local.DeviceId, Guid.NewGuid());
        _core.SetAuthenticatedInventory(_localPeer, local);
        _local = Snapshot(local);
        _devices.Add(local.DeviceId, new(_localPeer, _local));
        _state = new(false, "Audio is paused. Local playback stays unchanged.", new[] { _local }, Array.Empty<AudioRoute>(), 0);
        _network.InventoryReceived += OnInventory;
        _network.ControlReceived += OnControl;
        _network.PacketReceived += OnPacket;
        _network.PeerDisconnected += OnDisconnected;
        _worker = Task.Run(WorkLoop);
    }

    public AudioPreviewState State
    {
        get
        {
            var state = Volatile.Read(ref _state);
            return state with { IsEnabled = Volatile.Read(ref _enabled) == 1,
                Devices = (AudioDeviceInventory[])state.Devices.Clone(), Routes = (AudioRoute[])state.Routes.Clone() };
        }
    }

    public void SetLocalInventory(AudioDeviceInventory inventory)
    {
        if (inventory.DeviceId != _localPeer.DeviceId) throw new ArgumentException("Local inventory identity cannot change.", nameof(inventory));
        var snapshot = Snapshot(inventory);
        Enqueue(() =>
        {
            if (snapshot.Revision > _local.Revision && Volatile.Read(ref _enabled) == 1)
                PauseInternal("Audio devices changed. Check your selected outputs and press Start again.");
            _core.SetAuthenticatedInventory(_localPeer, snapshot);
            _local = snapshot; _devices[_localPeer.DeviceId] = new(_localPeer, snapshot);
            Publish(State.Status);
        });
    }

    public void Start(IEnumerable<AudioRoute> desiredRoutes, bool allowSending)
    {
        ArgumentNullException.ThrowIfNull(desiredRoutes);
        var routes = desiredRoutes.Take(AudioLimits.MaxRoutes + 1).ToArray();
        long operation;
        lock (_consentGate) operation = ++_operation;
        Enqueue(() => { if (operation == Volatile.Read(ref _operation)) StartInternal(routes, allowSending, operation); });
    }

    public void Pause(string reason)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        InvalidateConsent(); // queued/native PCM becomes silence immediately
        Enqueue(() => PauseInternal(SafeReason(reason)));
    }

    private async Task WorkLoop()
    {
        await foreach (var action in _work.Reader.ReadAllAsync())
        {
            var emergency = Interlocked.Exchange(ref _emergencyPause, null);
            if (emergency is not null) PauseInternal(emergency);
            try { action(); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            { InvalidateConsent(); PauseInternal("Audio paused: " + SafeReason(ex.Message)); }
        }
        PauseInternal("Audio stopped.");
        _core.Dispose();
    }

    private void Enqueue(Action action)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (_work.Writer.TryWrite(action)) return;
        InvalidateConsent();
        Interlocked.Exchange(ref _emergencyPause, "Audio paused because its bounded work queue was full.");
    }

    private void OnInventory(AudioPeerSession peer, AudioDeviceInventory inventory)
    {
        if (peer.DeviceId == _localPeer.DeviceId) return;
        var epoch = _peerEpochs.GetOrAdd(peer.DeviceId, 0);
        var snapshot = Snapshot(inventory);
        Enqueue(() =>
        {
            if (!Current(peer, epoch)) return;
            var changed = !_devices.TryGetValue(peer.DeviceId, out var previous)
                || previous.Peer != peer || previous.Inventory.Revision != snapshot.Revision;
            if (changed && Volatile.Read(ref _enabled) == 1)
                PauseInternal("An audio connection or output changed. Press Start again to consent.");
            _core.SetAuthenticatedInventory(peer, snapshot);
            _devices[peer.DeviceId] = new(peer, snapshot);
            Publish(State.Status);
        });
    }

    private void OnControl(AudioPeerSession peer, string type, JsonElement payload)
    {
        var epoch = _peerEpochs.GetOrAdd(peer.DeviceId, 0);
        var copy = payload.Clone();
        Enqueue(() => { if (Current(peer, epoch)) ProcessControl(peer, type, copy); });
    }

    private void OnPacket(AudioPeerSession peer, AudioPacket packet)
    {
        if (Volatile.Read(ref _enabled) != 1) return;
        var epoch = _peerEpochs.GetOrAdd(peer.DeviceId, 0);
        var created = Stopwatch.GetTimestamp();
        Enqueue(() =>
        {
            if (Volatile.Read(ref _enabled) != 1 || !Current(peer, epoch)
                || Stopwatch.GetElapsedTime(created).TotalMilliseconds > 150
                || !_incoming.TryGetValue(peer.DeviceId, out var incoming)
                || incoming.Peer != peer || incoming.StreamId != packet.StreamId) return;
            _core.TryAcceptPacket(peer, packet);
        });
    }

    private void OnDisconnected(string id)
    {
        _peerEpochs.AddOrUpdate(id, 1, (_, previous) => previous + 1);
        InvalidateConsent();
        Enqueue(() =>
        {
            PauseInternal("An audio peer disconnected. Press Start after reconnecting.");
            _devices.Remove(id); _core.RemoveDevice(id); Publish(State.Status);
        });
    }

    private bool Current(AudioPeerSession peer, long epoch) => peer.DeviceId != _localPeer.DeviceId
        && _peerEpochs.GetOrAdd(peer.DeviceId, 0) == epoch && _network.Session(peer.DeviceId) == peer;

    private void StartInternal(AudioRoute[] routes, bool allowSending, long operation)
    {
        PauseInternal("Preparing selected audio routes.");
        if (routes.Length > AudioLimits.MaxRoutes) throw new ArgumentException("Too many audio routes.");
        var unique = new HashSet<(string, AudioOutputKey)>();
        foreach (var route in routes)
        {
            if (route is null || !ValidId(route.SourceDeviceId) || !ValidId(route.Destination.DeviceId)
                || !ValidId(route.Destination.EndpointId) || !ValidGain(route.Gain)
                || !unique.Add((route.SourceDeviceId, route.Destination))
                || !_devices.TryGetValue(route.SourceDeviceId, out var source)
                || !_devices.TryGetValue(route.Destination.DeviceId, out var destination))
                throw new ArgumentException("Select an authenticated source and a connected output.");
            var endpoint = destination.Inventory.Outputs.FirstOrDefault(o => o.EndpointId == route.Destination.EndpointId);
            if (endpoint is null || !endpoint.IsConnected || !endpoint.CanReceiveStream)
                throw new ArgumentException("A selected output is disconnected or unsupported.");
            if (!(route.SourceDeviceId == route.Destination.DeviceId && endpoint.IsDefault)
                && (!source.Inventory.SupportsSystemCapture || !source.Inventory.ExcludesPlaybackFromCapture))
                throw new ArgumentException("A source does not support capture with playback exclusion.");
        }
        _desired = (AudioRoute[])routes.Clone();
        _allowSending = allowSending; _localStream = Guid.NewGuid(); _captureWasStopped = false;
        _receiveAcl.Clear();
        foreach (var route in routes.Where(r => r.Destination.DeviceId == _localPeer.DeviceId))
            _receiveAcl.Add((route.SourceDeviceId, route.Destination.EndpointId), route.Gain);
        _core.SetRoutes(routes.Where(r => r.Destination.DeviceId == _localPeer.DeviceId));
        CancellationToken consent;
        lock (_consentGate)
        {
            if (operation != _operation || Volatile.Read(ref _disposed) != 0) return;
            _core.SetConsent(true);
            _consentOperation = operation;
            consent = _core.SharingToken;
            Volatile.Write(ref _enabled, 1);
        }
        foreach (var route in routes.Where(r => r.Destination.DeviceId == _localPeer.DeviceId && !KeepLocal(r)))
            if (!EnsureOutput(route.Destination.EndpointId, operation, consent)) return;
        if (!Authorized(operation, consent)) return;
        if (HasLocalMirror())
        {
            _core.BeginStream(_localPeer, _localStream);
            EnsureCapture(operation);
        }
        if (!Authorized(operation, consent)) return;
        var waiting = false;
        foreach (var route in routes.Where(r => r.Destination.DeviceId != _localPeer.DeviceId))
        {
            if (!Authorized(operation, consent)) return;
            if (IsNativeDefaultRoute(route)) continue;
            if (route.SourceDeviceId == _localPeer.DeviceId)
            {
                if (!allowSending) { waiting = true; continue; }
                Offer(route.Destination.DeviceId, route.Destination.EndpointId, route.Gain);
            }
            else
            {
                waiting |= !_network.SendControl(route.SourceDeviceId, "subscribe", new
                    { destinationDeviceId = route.Destination.DeviceId, endpointId = route.Destination.EndpointId, gain = route.Gain });
            }
        }
        foreach (var route in routes.Where(r => r.Destination.DeviceId == _localPeer.DeviceId && r.SourceDeviceId != _localPeer.DeviceId))
            if (Authorized(operation, consent)) waiting |= !_network.SendControl(route.SourceDeviceId, "subscribe", new
                    { destinationDeviceId = _localPeer.DeviceId, endpointId = route.Destination.EndpointId, gain = route.Gain });
        Publish(waiting ? "Audio enabled; some routes need a direct connection or sending permission. Local playback stays unchanged."
            : "Audio enabled. Receivers must select matching routes and press Start. Local playback stays unchanged.");
    }

    private void ProcessControl(AudioPeerSession peer, string type, JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || payload.GetRawText().Length > 2_048) return;
        if (type == "subscribe")
        {
            if (!Fields(payload, "destinationDeviceId", "endpointId", "gain")
                || !Id(payload, "destinationDeviceId", out var destination) || !Id(payload, "endpointId", out var endpoint)
                || !Gain(payload, out var gain)) return;
            if (!Authorized(_consentOperation, _core.SharingToken) || !_allowSending)
            { Reject(peer.DeviceId, Guid.Empty, endpoint, "Enable sending and press Start on the source computer."); return; }
            if (!_local.SupportsSystemCapture || !_local.ExcludesPlaybackFromCapture)
            { Reject(peer.DeviceId, Guid.Empty, endpoint, "Source capture with playback exclusion is unsupported."); return; }
            if (destination == _localPeer.DeviceId || _network.Session(destination) is null)
            { Reject(peer.DeviceId, Guid.Empty, endpoint, "Source needs a direct authenticated connection to the destination."); return; }
            Offer(destination, endpoint, gain);
            return;
        }
        if (type == "offer")
        {
            if (!Fields(payload, "streamId", "endpointId", "gain") || !Stream(payload, out var stream)
                || !Id(payload, "endpointId", out var endpoint) || !Gain(payload, out _)) return;
            if (!Authorized(_consentOperation, _core.SharingToken) || !_receiveAcl.ContainsKey((peer.DeviceId, endpoint)))
            { Reject(peer.DeviceId, stream, endpoint, "Select this source/output and press Start on the receiver."); return; }
            if (!_devices.TryGetValue(peer.DeviceId, out var source) || source.Peer != peer
                || !source.Inventory.SupportsSystemCapture || !source.Inventory.ExcludesPlaybackFromCapture)
            { Reject(peer.DeviceId, stream, endpoint, "Source playback exclusion is unavailable."); return; }
            var key = (peer.DeviceId, stream);
            if (!_incoming.TryGetValue(peer.DeviceId, out var existing) || existing.StreamId != stream || existing.Peer != peer)
            {
                if (_seenInbound.Count >= MaxRetiredStreams)
                { Reject(peer.DeviceId, stream, endpoint, "Audio stream limit reached. Restart this preview."); return; }
                if (_seenInbound.Contains(key))
                { Reject(peer.DeviceId, stream, endpoint, "This stream is stale. Restart sharing with a new stream ID."); return; }
                _core.BeginStream(peer, stream); _seenInbound.Add(key);
                _incoming[peer.DeviceId] = new(peer, stream);
            }
            if (!EnsureOutput(endpoint, _consentOperation, _core.SharingToken)) return;
            _network.SendControl(peer.DeviceId, "accept", new { streamId = stream, endpointId = endpoint });
            Publish("Receiving selected shared audio. Local playback stays unchanged.");
            return;
        }
        if (type is "accept" or "reject")
        {
            if (!Fields(payload, "streamId", "endpointId", "reason") || !Stream(payload, out var stream, allowEmpty: type == "reject")
                || !Id(payload, "endpointId", out var endpoint)) return;
            var key = (peer.DeviceId, endpoint);
            if (type == "reject")
            {
                if (_outgoing.TryGetValue(key, out var pending) && pending.Peer == peer && pending.StreamId == stream)
                {
                    if (pending.Accepted) _stoppedDestinations.Add(peer.DeviceId);
                    _outgoing.Remove(key); RebuildTargets(); StopCaptureIfUnused();
                }
                if (payload.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                    Publish("Route needs attention: " + SafeReason(reason.GetString()));
                return;
            }
            if (!Authorized(_consentOperation, _core.SharingToken) || !_allowSending || stream != _localStream
                || !_outgoing.TryGetValue(key, out var outgoing) || outgoing.Peer != peer || outgoing.StreamId != stream) return;
            _outgoing[key] = outgoing with { Accepted = true };
            RebuildTargets(); EnsureCapture(_consentOperation);
            Publish("Streaming to selected outputs. Local playback stays unchanged.");
            return;
        }
        if (type == "stop")
        {
            if (!Fields(payload, "streamId") || !Stream(payload, out var stream, allowEmpty: true)) return;
            if (_incoming.TryGetValue(peer.DeviceId, out var incoming) && incoming.Peer == peer
                && (stream == Guid.Empty || stream == incoming.StreamId))
            { _core.EndStream(peer, incoming.StreamId); _incoming.Remove(peer.DeviceId); }
            var stopped = _outgoing.Where(p => p.Key.Device == peer.DeviceId && p.Value.Peer == peer
                && (stream == Guid.Empty || stream == p.Value.StreamId)).Select(p => p.Key).ToArray();
            if (stopped.Length != 0) _stoppedDestinations.Add(peer.DeviceId);
            foreach (var key in stopped) _outgoing.Remove(key);
            RebuildTargets(); StopCaptureIfUnused(); Publish("An audio stream stopped. Local playback stays unchanged.");
        }
    }

    private void Offer(string destination, string endpointId, float gain, bool permitRotation = true)
    {
        var peer = _network.Session(destination);
        if (peer is null || !_devices.TryGetValue(destination, out var destinationState) || destinationState.Peer != peer) return;
        var output = destinationState.Inventory.Outputs.FirstOrDefault(o => o.EndpointId == endpointId && o.IsConnected && o.CanReceiveStream);
        if (output is null) return;
        if (permitRotation && (_captureWasStopped || _stoppedDestinations.Contains(destination)))
        { RotateOrigin(destination, endpointId, gain); return; }
        var key = (destination, endpointId);
        if (_outgoing.TryGetValue(key, out var existing) && existing.Accepted && existing.Peer == peer && existing.StreamId == _localStream) return;
        _outgoing[key] = new(peer.Value, _localStream, false, gain);
        _network.SendControl(destination, "offer", new { streamId = _localStream, endpointId, gain });
    }

    private void Reject(string destination, Guid stream, string endpoint, string reason)
        => _network.SendControl(destination, "reject", new { streamId = stream, endpointId = endpoint, reason });

    private void RotateOrigin(string destination, string endpointId, float gain)
    {
        var operation = _consentOperation;
        var consent = _core.SharingToken;
        if (!Authorized(operation, consent) || !_allowSending) return;
        var previous = _localStream;
        var accepted = _outgoing.Where(p => p.Value.Accepted).Select(p =>
            (p.Key.Device, p.Key.Endpoint, p.Value.Gain)).ToList();
        if (!accepted.Any(p => p.Device == destination && p.Endpoint == endpointId))
            accepted.Add((destination, endpointId, gain));
        var previousPeers = _outgoing.Keys.Select(p => p.Device).Distinct(StringComparer.Ordinal).ToArray();
        Volatile.Write(ref _sendTargets, Array.Empty<SendTarget>());
        _outgoing.Clear();
        _core.EndStream(_localPeer, previous);
        StopNativeCapture();
        foreach (var peer in previousPeers) _network.SendControl(peer, "stop", new { streamId = previous });
        if (!Authorized(operation, consent)) return;
        _localStream = Guid.NewGuid(); _captureWasStopped = false;
        _stoppedDestinations.Clear();
        if (HasLocalMirror()) { _core.BeginStream(_localPeer, _localStream); EnsureCapture(operation); }
        foreach (var output in accepted)
            if (Authorized(operation, consent)) Offer(output.Device, output.Endpoint, output.Gain, permitRotation: false);
        Publish("Restarting the requested origin with a fresh stream. Receivers retain their own consent.");
    }

    private bool KeepLocal(AudioRoute route) => route.SourceDeviceId == _localPeer.DeviceId
        && route.Destination.DeviceId == _localPeer.DeviceId
        && _local.Outputs.Any(o => o.EndpointId == route.Destination.EndpointId && o.IsDefault);
    private bool IsNativeDefaultRoute(AudioRoute route) => route.SourceDeviceId == route.Destination.DeviceId
        && _devices.TryGetValue(route.SourceDeviceId, out var device)
        && device.Inventory.Outputs.Any(o => o.EndpointId == route.Destination.EndpointId && o.IsDefault);
    private bool HasLocalMirror() => _desired.Any(r => r.SourceDeviceId == _localPeer.DeviceId
        && r.Destination.DeviceId == _localPeer.DeviceId && !KeepLocal(r));

    private void EnsureCapture(long operation)
    {
        if (_capture is not null || !Authorized(operation, _core.SharingToken)) return;
        if (!HasLocalMirror() && Volatile.Read(ref _sendTargets).Length == 0) return;
        var stream = _localStream;
        var capture = _platform.CreateCapture(); _capture = capture;
        var consent = _core.SharingToken;
        long sequence = -1;
        _captureCancellation = consent.Register(() => SafeDispose(capture));
        capture.BlockAvailable += samples =>
        {
            if (!ReferenceEquals(Volatile.Read(ref _capture), capture) || !Authorized(operation, consent)) return;
            var next = Interlocked.Increment(ref sequence);
            if (next < 0) { Pause("Audio sequence limit reached. Restart sharing."); return; }
            var packetSequence = (ulong)next;
            _core.TryAcceptPacket(_localPeer, new(_localPeer.DeviceId, _localPeer.SessionId, stream, packetSequence,
                new[] { _localPeer.DeviceId }, samples));
            foreach (var target in Volatile.Read(ref _sendTargets))
                if (ReferenceEquals(Volatile.Read(ref _capture), capture) && Authorized(operation, consent)
                    && _network.Session(target.Peer.DeviceId) == target.Peer)
                    _network.SendAudio(target.Peer.DeviceId, stream, packetSequence, samples);
        };
        capture.Faulted += _ =>
        {
            if (ReferenceEquals(Volatile.Read(ref _capture), capture) && Authorized(operation, consent))
                Pause("System audio capture failed. Check the audio device and press Start again.");
        };
        capture.Start();
        if (!Authorized(operation, consent)) SafeDispose(capture);
    }

    private bool EnsureOutput(string endpoint, long operation, CancellationToken consent)
    {
        if (!Authorized(operation, consent)) return false;
        if (_outputs.ContainsKey(endpoint)) return true;
        var output = _platform.CreateOutput(endpoint);
        var cancellation = consent.Register(() => SafeDispose(output));
        _outputs.Add(endpoint, new(output, cancellation));
        var key = new AudioOutputKey(_localPeer.DeviceId, endpoint);
        output.SetFrameProvider(() =>
        {
            var samples = new float[AudioLimits.SamplesPerPacket];
            _core.Render(key, samples); return samples;
        });
        output.Faulted += _ =>
        {
            if (Authorized(operation, consent)) Pause("A selected output failed or disconnected. No fallback output was used.");
        };
        output.Start();
        if (!Authorized(operation, consent)) { SafeDispose(output); return false; }
        return true;
    }

    private void RebuildTargets() => Volatile.Write(ref _sendTargets, _outgoing.Values.Where(o => o.Accepted)
        .Select(o => new SendTarget(o.Peer)).Distinct().ToArray());

    private void StopCaptureIfUnused()
    {
        if (HasLocalMirror() || Volatile.Read(ref _sendTargets).Length != 0) return;
        StopNativeCapture();
    }

    private void StopNativeCapture()
    {
        var capture = _capture; _capture = null;
        _captureCancellation.Dispose(); _captureCancellation = default;
        if (capture is not null) _captureWasStopped = true;
        SafeDispose(capture);
    }

    private void PauseInternal(string reason)
    {
        var hadStreams = _incoming.Count != 0 || _outgoing.Count != 0 || Volatile.Read(ref _enabled) == 1;
        lock (_consentGate) { Volatile.Write(ref _enabled, 0); _core.SetConsent(false); }
        Volatile.Write(ref _sendTargets, Array.Empty<SendTarget>());
        _receiveAcl.Clear(); _incoming.Clear(); _outgoing.Clear(); _desired = Array.Empty<AudioRoute>();
        _stoppedDestinations.Clear();
        _core.SetRoutes(Array.Empty<AudioRoute>());
        if (hadStreams) foreach (var peer in _network.ConnectedIds) _network.SendControl(peer, "stop", new { streamId = Guid.Empty });
        var capture = _capture; _capture = null;
        _captureCancellation.Dispose(); _captureCancellation = default; SafeDispose(capture);
        foreach (var output in _outputs.Values) { output.Cancellation.Dispose(); SafeDispose(output.Output); } _outputs.Clear();
        Publish(SafeReason(reason));
    }

    private void Publish(string status) => Volatile.Write(ref _state,
        new(Volatile.Read(ref _enabled) == 1, status, _devices.Values.Select(d => d.Inventory).ToArray(),
            (AudioRoute[])_desired.Clone(), Volatile.Read(ref _sendTargets).Length));

    private static AudioDeviceInventory Snapshot(AudioDeviceInventory inventory)
    {
        if (inventory.Outputs is null || inventory.Outputs.Count > AudioLimits.MaxOutputsPerDevice)
            throw new ArgumentException("Invalid audio output inventory.");
        return inventory with { Outputs = Array.AsReadOnly(inventory.Outputs.ToArray()) };
    }

    private static bool ValidId(string? value) => !string.IsNullOrEmpty(value) && value.Length <= AudioLimits.MaxIdentifierLength
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':' or '.');
    private static bool ValidGain(float gain) => float.IsFinite(gain) && gain >= 0 && gain <= AudioLimits.MaxGain;
    private static string SafeReason(string? reason) => string.IsNullOrWhiteSpace(reason) ? "Audio is paused."
        : new string(reason.Where(c => !char.IsControl(c)).Take(160).ToArray());
    private static void SafeDispose(IDisposable? disposable)
    { try { disposable?.Dispose(); } catch { /* other devices must still be stopped */ } }
    private bool Authorized(long operation, CancellationToken consent) => !consent.IsCancellationRequested
        && Volatile.Read(ref _enabled) == 1 && operation == Volatile.Read(ref _operation)
        && operation == Volatile.Read(ref _consentOperation) && Volatile.Read(ref _disposed) == 0;
    private void InvalidateConsent()
    {
        lock (_consentGate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            _operation++; Volatile.Write(ref _enabled, 0); _core.SetConsent(false);
        }
    }
    private static bool Fields(JsonElement payload, params string[] allowed)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in payload.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) return false;
        return true;
    }
    private static bool Id(JsonElement payload, string name, out string value)
    {
        value = string.Empty;
        if (!payload.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString() ?? string.Empty; return ValidId(value);
    }
    private static bool Gain(JsonElement payload, out float gain)
    { gain = 0; return payload.TryGetProperty("gain", out var element) && element.ValueKind == JsonValueKind.Number
        && element.TryGetSingle(out gain) && ValidGain(gain); }
    private static bool Stream(JsonElement payload, out Guid stream, bool allowEmpty = false)
    { stream = Guid.Empty; return payload.TryGetProperty("streamId", out var element) && element.ValueKind == JsonValueKind.String
        && element.TryGetGuid(out stream) && (allowEmpty || stream != Guid.Empty); }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_consentGate) { _operation++; Volatile.Write(ref _enabled, 0); _core.SetConsent(false); }
        _network.InventoryReceived -= OnInventory; _network.ControlReceived -= OnControl;
        _network.PacketReceived -= OnPacket; _network.PeerDisconnected -= OnDisconnected;
        _work.Writer.TryComplete();
        if (Task.CurrentId != _worker.Id) _worker.GetAwaiter().GetResult();
    }

    private sealed record Device(AudioPeerSession Peer, AudioDeviceInventory Inventory);
    private sealed record Incoming(AudioPeerSession Peer, Guid StreamId);
    private sealed record Outgoing(AudioPeerSession Peer, Guid StreamId, bool Accepted, float Gain);
    private sealed record SendTarget(AudioPeerSession Peer);
    private sealed record NativeOutput(IAudioOutput Output, CancellationTokenRegistration Cancellation);
}
