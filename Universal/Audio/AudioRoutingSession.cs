namespace BatCave.Audio;

// Pure routing state: no device discovery, OS defaults, capture, microphone access,
// network listeners, or background playback. The caller supplies authenticated peers.
public sealed class AudioRoutingSession : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceState> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<AudioOutputKey, float> _outputGains = new();
    private readonly Dictionary<AudioRouteKey, AudioJitterBuffer> _buffers = new();
    private readonly Dictionary<AudioPeerSession, HashSet<Guid>> _usedStreams = new();
    private AudioRoute[] _routes = Array.Empty<AudioRoute>();
    private bool _consent;
    private bool _disposed;
    private CancellationTokenSource _sharingCancellation = new();

    public AudioRoutingSession() => _sharingCancellation.Cancel();
    public bool IsConsented { get { lock (_gate) return _consent && !_disposed; } }
    public CancellationToken SharingToken { get { lock (_gate) return _sharingCancellation.Token; } }
    public IReadOnlyList<AudioRoute> Routes { get { lock (_gate) return Array.AsReadOnly((AudioRoute[])_routes.Clone()); } }
    public int BufferedPacketCount { get { lock (_gate) return _buffers.Values.Sum(b => b.Count); } }

    public void SetAuthenticatedInventory(AudioPeerSession peer, AudioDeviceInventory inventory)
    {
        if (!AudioValidation.Identifier(peer.DeviceId) || peer.SessionId == Guid.Empty
            || !AudioValidation.Inventory(inventory) || inventory.DeviceId != peer.DeviceId)
            throw new ArgumentException("Inventory must match its authenticated peer session.", nameof(inventory));
        // Own the collection so caller-side DTO mutation cannot change routing approval.
        var snapshot = inventory with { Outputs = Array.AsReadOnly(inventory.Outputs.ToArray()) };
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_devices.TryGetValue(peer.DeviceId, out var previous))
            {
                if (previous.Peer == peer)
                {
                    if (snapshot.Revision < previous.Inventory.Revision)
                        throw new ArgumentException("Stale audio inventory revision.", nameof(inventory));
                    if (snapshot.Revision == previous.Inventory.Revision)
                    {
                        if (!SameInventory(snapshot, previous.Inventory))
                            throw new ArgumentException("Conflicting audio inventory revision.", nameof(inventory));
                        return;
                    }
                    _devices[peer.DeviceId] = previous with { Inventory = snapshot };
                    // An unplug or capability change pauses only affected routes.
                    ClearBuffers();
                    if (previous.Inventory.SupportsSystemCapture && !snapshot.SupportsSystemCapture
                        || previous.Inventory.ExcludesPlaybackFromCapture && !snapshot.ExcludesPlaybackFromCapture)
                        RevokeConsent();
                }
                else
                {
                    _usedStreams.Remove(previous.Peer);
                    _devices[peer.DeviceId] = new(peer, snapshot, Guid.Empty);
                    RevokeConsent(); // audio permission cannot survive reconnection
                }
            }
            else
            {
                if (_devices.Count >= AudioLimits.MaxDevices)
                    throw new InvalidOperationException("Too many audio devices.");
                _devices.Add(peer.DeviceId, new(peer, snapshot, Guid.Empty));
                if (_consent) RevokeConsent();
            }
        }
    }

    public void RemoveDevice(string deviceId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_devices.Remove(deviceId, out var previous))
            {
                _usedStreams.Remove(previous.Peer);
                RevokeConsent();
            }
        }
    }

    // Consent is deliberately independent of keyboard/mouse pairing approval.
    public void SetConsent(bool enabled)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!enabled) { RevokeConsent(); return; }
            if (_consent) return;
            _sharingCancellation.Dispose();
            _sharingCancellation = new();
            _consent = true;
            ClearBuffers();
            foreach (var id in _devices.Keys.ToArray())
                _devices[id] = _devices[id] with { StreamId = Guid.Empty };
        }
    }

    public void SetRoutes(IEnumerable<AudioRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var snapshot = routes.Take(AudioLimits.MaxRoutes + 1).ToArray();
        lock (_gate)
        {
            ThrowIfDisposed();
            if (snapshot.Length > AudioLimits.MaxRoutes) throw new ArgumentException("Too many audio routes.", nameof(routes));
            var unique = new HashSet<AudioRouteKey>();
            foreach (var route in snapshot)
            {
                if (route is null || !AudioValidation.Identifier(route.SourceDeviceId)
                    || !AudioValidation.Identifier(route.Destination.DeviceId) || !AudioValidation.Identifier(route.Destination.EndpointId)
                    || !AudioValidation.Gain(route.Gain) || !unique.Add(new(route.SourceDeviceId, route.Destination))
                    || !_devices.TryGetValue(route.SourceDeviceId, out var source)
                    || !_devices.TryGetValue(route.Destination.DeviceId, out var target))
                    throw new ArgumentException("Invalid or duplicate audio route.", nameof(routes));
                var endpoint = target.Inventory.Outputs.FirstOrDefault(o => o.EndpointId == route.Destination.EndpointId);
                if (endpoint is null || !endpoint.IsConnected || !endpoint.CanReceiveStream)
                    throw new ArgumentException("Selected audio output is unavailable.", nameof(routes));
                if (!IsKeepLocal(route, endpoint) && (!source.Inventory.SupportsSystemCapture || !source.Inventory.ExcludesPlaybackFromCapture))
                    throw new ArgumentException("Source must exclude BatCave playback from system capture.", nameof(routes));
            }
            // Reciprocal device routes are safe because frames retain their local origin,
            // relaying is disallowed, and captured audio excludes BatCave's playback.
            _routes = (AudioRoute[])snapshot.Clone();
            ClearBuffers();
            foreach (var key in _outputGains.Keys.Where(key => !_routes.Any(r => r.Destination == key)).ToArray())
                _outputGains.Remove(key);
        }
    }

    public void SetOutputGain(AudioOutputKey output, float gain)
    {
        if (!AudioValidation.Gain(gain)) throw new ArgumentOutOfRangeException(nameof(gain));
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_routes.Any(r => r.Destination == output)) throw new ArgumentException("Output is not selected.", nameof(output));
            _outputGains[output] = gain;
        }
    }

    public void BeginStream(AudioPeerSession peer, Guid streamId)
    {
        if (streamId == Guid.Empty) throw new ArgumentException("Stream ID is required.", nameof(streamId));
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_consent || !_devices.TryGetValue(peer.DeviceId, out var state) || state.Peer != peer)
                throw new InvalidOperationException("Current authenticated session and explicit audio consent are required.");
            if (!state.Inventory.SupportsSystemCapture || !state.Inventory.ExcludesPlaybackFromCapture
                || !_routes.Any(r => r.SourceDeviceId == peer.DeviceId && RouteIsAvailable(r, out _, out var keepLocal) && !keepLocal))
                throw new InvalidOperationException("Source has no enabled audio destination.");
            if (state.StreamId == streamId) return;
            if (!_usedStreams.TryGetValue(peer, out var used)) _usedStreams.Add(peer, used = new());
            if (used.Contains(streamId)) throw new InvalidOperationException("A retired audio stream cannot resume.");
            if (used.Count >= AudioLimits.MaxStreamsPerPeerSession)
                throw new InvalidOperationException("Reconnect before starting another audio stream.");
            used.Add(streamId);
            _devices[peer.DeviceId] = state with { StreamId = streamId };
            foreach (var key in _buffers.Keys.Where(k => k.SourceDeviceId == peer.DeviceId).ToArray())
                _buffers.Remove(key);
        }
    }

    public bool TryAcceptPacket(AudioPeerSession authenticatedSender, AudioPacket packet)
    {
        if (packet is null || packet.Samples is null || packet.Samples.Length != AudioLimits.SamplesPerPacket
            || packet.VisitedDeviceIds is null || packet.VisitedDeviceIds.Count is < 1 or > AudioLimits.MaxHops + 1) return false;
        // Capture caller-owned arrays before validating so a producer cannot mutate
        // validated PCM into NaN or change its provenance while it is queued.
        packet = packet with { Samples = (float[])packet.Samples.Clone(),
            VisitedDeviceIds = Array.AsReadOnly(packet.VisitedDeviceIds.ToArray()) };
        if (!AudioValidation.Packet(packet) || packet.OriginDeviceId != authenticatedSender.DeviceId
            || packet.DeviceSessionId != authenticatedSender.SessionId || packet.VisitedDeviceIds.Count != 1) return false;
        lock (_gate)
        {
            if (_disposed || !_consent || !_devices.TryGetValue(authenticatedSender.DeviceId, out var source)
                || source.Peer != authenticatedSender || source.StreamId != packet.StreamId
                || !source.Inventory.SupportsSystemCapture || !source.Inventory.ExcludesPlaybackFromCapture) return false;
            var accepted = false;
            foreach (var route in _routes.Where(r => r.SourceDeviceId == authenticatedSender.DeviceId))
            {
                if (!RouteIsAvailable(route, out _, out var keepLocal) || keepLocal) continue;
                var key = new AudioRouteKey(route.SourceDeviceId, route.Destination);
                if (!_buffers.TryGetValue(key, out var buffer)) _buffers.Add(key, buffer = new());
                accepted |= buffer.TryEnqueue(packet.Sequence, packet.Samples);
            }
            return accepted;
        }
    }

    public void EndStream(AudioPeerSession peer, Guid streamId)
    {
        lock (_gate)
        {
            if (_disposed || !_devices.TryGetValue(peer.DeviceId, out var state)
                || state.Peer != peer || state.StreamId != streamId) return;
            _devices[peer.DeviceId] = state with { StreamId = Guid.Empty };
            foreach (var key in _buffers.Keys.Where(k => k.SourceDeviceId == peer.DeviceId).ToArray())
                _buffers.Remove(key);
        }
    }

    // The host calls once per 10 ms for each selected output. Missing packets are
    // silence. Every output consumes its own queue, so multiple sinks remain independent.
    public int Render(AudioOutputKey output, Span<float> destination)
    {
        if (destination.Length != AudioLimits.SamplesPerPacket)
            throw new ArgumentException("Render requires exactly one 10 ms stereo frame.", nameof(destination));
        destination.Clear();
        lock (_gate)
        {
            if (_disposed || !_consent) return 0;
            var contributors = 0;
            var outputGain = _outputGains.GetValueOrDefault(output, 1f);
            foreach (var route in _routes.Where(r => r.Destination == output))
            {
                if (!RouteIsAvailable(route, out _, out var keepLocal) || keepLocal
                    || !_buffers.TryGetValue(new(route.SourceDeviceId, output), out var buffer)) continue;
                var samples = buffer.Dequeue();
                if (samples is null) continue;
                var gain = route.Gain * outputGain;
                if (gain == 0) continue;
                contributors++;
                for (var i = 0; i < destination.Length; i++) destination[i] += samples[i] * gain;
            }
            for (var i = 0; i < destination.Length; i++) destination[i] = Math.Clamp(destination[i], -1f, 1f);
            return contributors;
        }
    }

    public bool IsRouteAvailable(AudioRoute route)
    {
        lock (_gate) return !_disposed && RouteIsAvailable(route, out _, out _);
    }

    private bool RouteIsAvailable(AudioRoute route, out AudioOutputDescriptor? output, out bool keepLocal)
    {
        output = null;
        keepLocal = false;
        if (!_devices.TryGetValue(route.SourceDeviceId, out var source)
            || !_devices.TryGetValue(route.Destination.DeviceId, out var target)) return false;
        output = target.Inventory.Outputs.FirstOrDefault(o => o.EndpointId == route.Destination.EndpointId);
        if (output is null || !output.IsConnected || !output.CanReceiveStream) return false;
        keepLocal = IsKeepLocal(route, output);
        return keepLocal || source.Inventory.SupportsSystemCapture && source.Inventory.ExcludesPlaybackFromCapture;
    }

    private static bool IsKeepLocal(AudioRoute route, AudioOutputDescriptor endpoint)
        => route.SourceDeviceId == route.Destination.DeviceId && endpoint.IsDefault;

    private static bool SameInventory(AudioDeviceInventory left, AudioDeviceInventory right)
        => left.DeviceId == right.DeviceId && left.DisplayName == right.DisplayName
            && left.DeviceIsPrimary == right.DeviceIsPrimary && left.Revision == right.Revision
            && left.SupportsSystemCapture == right.SupportsSystemCapture
            && left.ExcludesPlaybackFromCapture == right.ExcludesPlaybackFromCapture
            && left.Outputs.SequenceEqual(right.Outputs);

    private void ClearBuffers() => _buffers.Clear();

    private void RevokeConsent()
    {
        _consent = false;
        // CancelAsync marks the token canceled immediately and invokes external stop
        // callbacks outside this lock. A callback may wait on a producer that is trying
        // to enter Render/TryAcceptPacket, so synchronous cancellation could deadlock.
        _ = ObserveCancellation(_sharingCancellation.CancelAsync());
        ClearBuffers();
        foreach (var id in _devices.Keys.ToArray()) _devices[id] = _devices[id] with { StreamId = Guid.Empty };
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static async Task ObserveCancellation(Task cancellation)
    {
        try { await cancellation.ConfigureAwait(false); }
        catch { /* token is canceled even if an external cancellation callback fails */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            RevokeConsent();
            _devices.Clear();
            _routes = Array.Empty<AudioRoute>();
            _outputGains.Clear();
            _usedStreams.Clear();
            _disposed = true;
            _sharingCancellation.Dispose();
        }
    }

    private sealed record DeviceState(AudioPeerSession Peer, AudioDeviceInventory Inventory, Guid StreamId);
    private readonly record struct AudioRouteKey(string SourceDeviceId, AudioOutputKey Destination);
}
