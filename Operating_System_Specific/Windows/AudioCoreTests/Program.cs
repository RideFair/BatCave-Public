using System.Buffers.Binary;
using BatCave.Audio;

var passed = 0;
Run("default local playback and Bluetooth selection remain suggestions", TestDefaults);
Run("ambiguous primary/Bluetooth and disconnected outputs fail safely", TestAmbiguousDefaults);
Run("authentication, inventory revisions and hostile metadata are bounded", TestInventory);
Run("explicit consent, stale streams and revocation discard pending audio", TestConsent);
Run("multiple sources mix, clip and apply per-output gain", TestMixing);
Run("multiple sinks consume independently and local default is passthrough", TestMultipleSinks);
Run("reciprocal origins work but forwarded PCM and missing capture exclusion do not", TestProvenance);
Run("unplug pauses selected route without rerouting to speakers", TestUnplug);
Run("jitter reorders packets, rejects replay and bounds memory/latency", TestJitter);
Run("wire framing roundtrips and rejects malformed/nonfinite/oversize PCM", TestCodec);
Run("cancellation callbacks can stop producers without holding the mixer lock", TestCancellation);
Console.WriteLine($"PASS: {passed} shared audio core regression groups.");

void Run(string name, Action test)
{
    test();
    passed++;
    Console.WriteLine($"PASS: {name}.");
}

static AudioOutputDescriptor Output(string id = "speakers", AudioOutputKind kind = AudioOutputKind.Speakers,
    bool connected = true, bool active = true, bool isDefault = true)
    => new(id, id == "headphones" ? "Wireless headphones" : "Test output", kind, connected, active, isDefault);

static AudioDeviceInventory Device(string id, bool primary = false, AudioOutputDescriptor[]? outputs = null, long revision = 1)
    => new(id, "Test computer", primary, revision, outputs ?? new[] { Output() }, SupportsSystemCapture: true, ExcludesPlaybackFromCapture: true);

static AudioPeerSession Peer(string id) => new(id, Guid.NewGuid());
static AudioPacket Packet(AudioPeerSession peer, Guid stream, ulong sequence, float value = .25f)
    => new(peer.DeviceId, peer.SessionId, stream, sequence, new[] { peer.DeviceId }, Enumerable.Repeat(value, AudioLimits.SamplesPerPacket).ToArray());
static void Require(bool result, string reason)
{
    if (!result) throw new Exception(reason);
}
static void Throws<T>(Action action, string reason) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception(reason);
}

static void TestDefaults()
{
    var local = Device("alpha", primary: true);
    var source = Device("beta");
    var localOnly = AudioRoutingPolicy.SuggestDefaults(new[] { local, source });
    Require(localOnly.Reason == AudioDefaultReason.NoPrimaryBluetooth && localOnly.SuggestedRoutes.Count == 0
        && localOnly.RetainLocalDeviceIds.Count == 2, "without Bluetooth all devices retain their local output");
    var bluetooth = local with { Outputs = new[] { Output("headphones", AudioOutputKind.Bluetooth) } };
    var suggestion = AudioRoutingPolicy.SuggestDefaults(new[] { bluetooth, source });
    Require(suggestion.Reason == AudioDefaultReason.PrimaryBluetooth && suggestion.SuggestedRoutes.Count == 1
        && suggestion.SuggestedRoutes[0].SourceDeviceId == "beta"
        && suggestion.SuggestedRoutes[0].Destination == new AudioOutputKey("alpha", "headphones"), "active primary Bluetooth suggestion");
    var differentLocalDefault = local with { Outputs = new[] { Output(), Output("headphones", AudioOutputKind.Bluetooth, isDefault: false) } };
    suggestion = AudioRoutingPolicy.SuggestDefaults(new[] { differentLocalDefault, source });
    Require(suggestion.SuggestedRoutes.Count == 2, "primary audio can mirror to non-default Bluetooth without changing default");
    using var session = new AudioRoutingSession();
    session.SetAuthenticatedInventory(Peer("alpha"), bluetooth);
    session.SetAuthenticatedInventory(Peer("beta"), source);
    session.SetRoutes(suggestion.SuggestedRoutes.Where(r => r.SourceDeviceId == "beta"));
    Require(!session.IsConsented && session.SharingToken.IsCancellationRequested, "choosing defaults does not grant audio consent");
}

static void TestAmbiguousDefaults()
{
    var alpha = Device("alpha", true, new[] { Output("headphones", AudioOutputKind.Bluetooth, isDefault: false),
        Output("second", AudioOutputKind.Bluetooth, isDefault: false) });
    Require(AudioRoutingPolicy.SuggestDefaults(new[] { alpha, Device("beta") }).Reason == AudioDefaultReason.AmbiguousBluetooth,
        "do not arbitrarily pick from multiple active Bluetooth devices");
    var preferred = alpha with { Outputs = new[] { Output("headphones", AudioOutputKind.Bluetooth),
        Output("second", AudioOutputKind.Bluetooth, isDefault: false) } };
    var preferredSuggestion = AudioRoutingPolicy.SuggestDefaults(new[] { preferred, Device("beta") });
    Require(preferredSuggestion.Reason == AudioDefaultReason.PrimaryBluetooth
        && preferredSuggestion.SuggestedRoutes.Single().Destination.EndpointId == "headphones", "one system-default Bluetooth output wins among connected devices");
    Require(AudioRoutingPolicy.SuggestDefaults(new[] { preferred with { Outputs = new[] { Output("headphones", AudioOutputKind.Bluetooth),
        Output("second", AudioOutputKind.Bluetooth) } }, Device("beta") }).Reason == AudioDefaultReason.AmbiguousBluetooth,
        "conflicting Bluetooth default flags require an explicit choice");
    Require(AudioRoutingPolicy.SuggestDefaults(new[] { alpha, Device("beta", true) }).Reason == AudioDefaultReason.AmbiguousPrimary,
        "conflicting primary roles retain local audio");
    foreach (var output in new[] { Output("headphones", AudioOutputKind.Bluetooth, connected: false),
        Output("headphones", AudioOutputKind.Bluetooth, active: false), Output("headphones", AudioOutputKind.Bluetooth) with { CanReceiveStream = false } })
        Require(AudioRoutingPolicy.SuggestDefaults(new[] { alpha with { Outputs = new[] { output } }, Device("beta") }).SuggestedRoutes.Count == 0,
            "disconnected/inactive/unplayable Bluetooth cannot become the default suggestion");
    Throws<ArgumentException>(() => AudioRoutingPolicy.SuggestDefaults(new[] { alpha, alpha }), "duplicate device inventory rejected");
    Throws<ArgumentException>(() => AudioRoutingPolicy.SuggestDefaults(Enumerable.Range(0, AudioLimits.MaxDevices + 1).Select(i => Device("device-" + i))), "device enumeration capped");
}

static void TestInventory()
{
    using var session = new AudioRoutingSession();
    var peer = Peer("alpha");
    var outputs = new[] { Output() };
    session.SetAuthenticatedInventory(peer, Device("alpha", outputs: outputs));
    outputs[0] = Output() with { IsConnected = false };
    // The session snapshots its input list rather than retaining mutable caller state.
    session.SetRoutes(new[] { new AudioRoute("alpha", new("alpha", "speakers")) });
    Throws<ArgumentException>(() => session.SetAuthenticatedInventory(peer, Device("beta")), "spoofed inventory identity rejected");
    Throws<ArgumentException>(() => session.SetAuthenticatedInventory(peer, Device("alpha", revision: 0)), "stale revision rejected");
    Throws<ArgumentException>(() => session.SetAuthenticatedInventory(peer, Device("alpha") with { DisplayName = "Different" }), "conflicting revision rejected");
    foreach (var bad in new[] {
        Device("alpha") with { Outputs = Enumerable.Repeat(Output(), 33).ToArray() },
        Device("alpha") with { Outputs = new[] { Output(), Output() } },
        Device("alpha") with { DisplayName = "bad\nname" },
        Device("alpha") with { Outputs = new[] { Output("native\\endpoint\\path") } },
        Device("alpha") with { Outputs = new[] { Output() with { Kind = (AudioOutputKind)99 } } }
    }) Throws<ArgumentException>(() => session.SetAuthenticatedInventory(peer, bad), "malformed inventory rejected");
    var route = new AudioRoute("alpha", new("alpha", "speakers"));
    Require(!new AudioDeviceInventory("beta", "Test computer", false, 1, new[] { Output() }).SupportsSystemCapture
        && !new AudioDeviceInventory("beta", "Test computer", false, 1, new[] { Output() }).ExcludesPlaybackFromCapture,
        "omitted platform capture capabilities fail closed");
    Throws<ArgumentException>(() => session.SetRoutes(new[] { route, route }), "duplicate route rejected");
    Throws<ArgumentException>(() => session.SetRoutes(Enumerable.Repeat(route, AudioLimits.MaxRoutes + 1)), "route count capped");
    Throws<ArgumentException>(() => session.SetRoutes(new[] { route with { Gain = float.NaN } }), "nonfinite gain rejected");
}

static void TestConsent()
{
    using var session = MakeSession(out var alpha, out var beta);
    var stream = Guid.NewGuid();
    var packet = Packet(alpha, stream, 0);
    Require(!session.TryAcceptPacket(alpha, packet), "input pairing alone does not enable audio");
    Throws<InvalidOperationException>(() => session.BeginStream(alpha, stream), "capture stream denied before audio consent");
    session.SetConsent(true);
    var token = session.SharingToken;
    session.BeginStream(alpha, stream);
    Require(session.TryAcceptPacket(alpha, packet), "explicit consent accepts current source");
    Require(session.BufferedPacketCount == 1, "queued frame exists");
    session.SetConsent(false);
    Require(token.IsCancellationRequested && session.BufferedPacketCount == 0, "revocation cancels producers and drops queued PCM");
    var destination = new float[AudioLimits.SamplesPerPacket];
    Array.Fill(destination, .4f);
    Require(session.Render(new("beta", "speakers"), destination) == 0 && destination.All(v => v == 0), "revocation immediately renders silence");
    session.SetConsent(true);
    Require(!session.TryAcceptPacket(alpha, packet), "old source stream cannot resume after consent reset");
    Throws<InvalidOperationException>(() => session.BeginStream(alpha, stream), "retired stream ID cannot be reused");
    var newStream = Guid.NewGuid();
    session.BeginStream(alpha, newStream);
    Require(session.TryAcceptPacket(alpha, Packet(alpha, newStream, 0)), "fresh capture stream can restart");
    var activeToken = session.SharingToken;
    var replaced = new AudioPeerSession(alpha.DeviceId, Guid.NewGuid());
    session.SetAuthenticatedInventory(replaced, Device(alpha.DeviceId));
    Require(!session.IsConsented && activeToken.IsCancellationRequested && session.BufferedPacketCount == 0,
        "connection replacement revokes previous session audio consent");
    Require(!session.TryAcceptPacket(alpha, Packet(alpha, newStream, 1)), "stale authenticated session denied");
    session.SetConsent(true);
    session.BeginStream(replaced, Guid.NewGuid());
    var disconnectToken = session.SharingToken;
    session.RemoveDevice(beta.DeviceId);
    Require(!session.IsConsented && disconnectToken.IsCancellationRequested, "disconnect revokes audio and cancels producers");
}

static AudioRoutingSession MakeSession(out AudioPeerSession alpha, out AudioPeerSession beta)
{
    alpha = Peer("alpha"); beta = Peer("beta");
    var session = new AudioRoutingSession();
    session.SetAuthenticatedInventory(alpha, Device("alpha"));
    session.SetAuthenticatedInventory(beta, Device("beta"));
    session.SetRoutes(new[] { new AudioRoute("alpha", new("beta", "speakers")) });
    return session;
}

static void TestMixing()
{
    using var session = MakeSession(out var alpha, out _);
    var gamma = Peer("gamma");
    session.SetAuthenticatedInventory(gamma, Device("gamma"));
    var output = new AudioOutputKey("beta", "speakers");
    session.SetRoutes(new[] { new AudioRoute("alpha", output), new AudioRoute("gamma", output) });
    session.SetConsent(true);
    var first = Guid.NewGuid(); var second = Guid.NewGuid();
    session.BeginStream(alpha, first); session.BeginStream(gamma, second);
    for (ulong i = 0; i < 2; i++)
    {
        Require(session.TryAcceptPacket(alpha, Packet(alpha, first, i, .8f)), "first source accepted");
        Require(session.TryAcceptPacket(gamma, Packet(gamma, second, i, .7f)), "second source accepted");
    }
    var samples = new float[AudioLimits.SamplesPerPacket];
    Require(session.Render(output, samples) == 2 && samples.All(v => v == 1), "multiple sources saturate rather than overflowing");
    session.SetOutputGain(output, .25f);
    Require(session.Render(output, samples) == 2 && samples.All(v => Math.Abs(v - .375f) < .0001f), "output gain applies before clipping");
    Throws<ArgumentOutOfRangeException>(() => session.SetOutputGain(output, float.PositiveInfinity), "nonfinite output gain rejected");
    Throws<ArgumentOutOfRangeException>(() => session.SetOutputGain(output, 2.1f), "excessive output gain rejected");
    Require(session.Render(output, samples) == 0 && samples.All(v => v == 0), "underrun is silence");
}

static void TestMultipleSinks()
{
    using var session = MakeSession(out var alpha, out var beta);
    var secondOutput = Output("headphones", AudioOutputKind.Bluetooth, isDefault: false);
    session.SetAuthenticatedInventory(alpha, Device("alpha", outputs: new[] { Output(), secondOutput }, revision: 2));
    session.SetRoutes(new[] { new AudioRoute("alpha", new("alpha", "speakers")),
        new AudioRoute("alpha", new("alpha", "headphones")), new AudioRoute("alpha", new("beta", "speakers")) });
    session.SetConsent(true);
    var stream = Guid.NewGuid(); session.BeginStream(alpha, stream);
    for (ulong i = 0; i < 2; i++) session.TryAcceptPacket(alpha, Packet(alpha, stream, i));
    var samples = new float[AudioLimits.SamplesPerPacket];
    Require(session.Render(new("alpha", "speakers"), samples) == 0, "local default is native passthrough, never replayed");
    Require(session.Render(new("alpha", "headphones"), samples) == 1 && samples[0] == .25f, "another local endpoint receives a mirror");
    Require(session.Render(new("beta", "speakers"), samples) == 1 && samples[0] == .25f, "remote endpoint has independent packet consumption");
    Require(session.BufferedPacketCount == 2, "two independent render queues remain");
}

static void TestProvenance()
{
    using var session = MakeSession(out var alpha, out var beta);
    session.SetRoutes(new[] { new AudioRoute("alpha", new("beta", "speakers")), new AudioRoute("beta", new("alpha", "speakers")) });
    session.SetConsent(true);
    var a = Guid.NewGuid(); var b = Guid.NewGuid();
    session.BeginStream(alpha, a); session.BeginStream(beta, b);
    Require(session.TryAcceptPacket(alpha, Packet(alpha, a, 0)) && session.TryAcceptPacket(beta, Packet(beta, b, 0)), "reciprocal local origins are allowed");
    Require(!session.TryAcceptPacket(beta, Packet(alpha, a, 1)), "source identity must equal authenticated sender");
    Require(AudioPacketCodec.TryMarkPlayback(Packet(alpha, a, 1), "beta", out var played), "playback hop is recorded");
    Require(!session.TryAcceptPacket(alpha, played!), "received audio cannot be sent again as a local origin");
    Require(!AudioPacketCodec.TryMarkPlayback(played!, "gamma", out _), "one hop maximum blocks relay loops");
    Require(AudioPacketCodec.TryMarkPlayback(Packet(alpha, a, 2), "alpha", out var localCopy)
        && !session.TryAcceptPacket(alpha, localCopy!), "same-device endpoint copy is also final playback");
    session.SetAuthenticatedInventory(alpha, Device("alpha", revision: 2) with { ExcludesPlaybackFromCapture = false });
    Require(!session.TryAcceptPacket(alpha, Packet(alpha, a, 3)), "source without playback exclusion cannot create feedback");
    Throws<ArgumentException>(() => session.SetRoutes(new[] { new AudioRoute("alpha", new("beta", "speakers")) }), "unsafe source capture cannot be selected");
}

static void TestUnplug()
{
    using var session = MakeSession(out var alpha, out var beta);
    session.SetAuthenticatedInventory(beta, Device("beta", outputs: new[] { Output(), Output("headphones", AudioOutputKind.Bluetooth, isDefault: false) }, revision: 2));
    var route = new AudioRoute("alpha", new("beta", "headphones"));
    session.SetRoutes(new[] { route }); session.SetConsent(true);
    var stream = Guid.NewGuid(); session.BeginStream(alpha, stream);
    session.TryAcceptPacket(alpha, Packet(alpha, stream, 0));
    session.TryAcceptPacket(alpha, Packet(alpha, stream, 1));
    session.SetAuthenticatedInventory(beta, Device("beta", outputs: new[] { Output(), Output("headphones", AudioOutputKind.Bluetooth, connected: false, isDefault: false) }, revision: 3));
    Require(session.Routes.Single() == route && !session.IsRouteAvailable(route) && session.BufferedPacketCount == 0, "unplug retains paused route, clears pending PCM");
    Require(!session.TryAcceptPacket(alpha, Packet(alpha, stream, 2)), "disconnected destination receives nothing");
    var samples = new float[AudioLimits.SamplesPerPacket];
    Require(session.Render(new("beta", "speakers"), samples) == 0, "no fallback to speakers reveals private headphone audio");
    session.SetAuthenticatedInventory(beta, Device("beta", outputs: new[] { Output(), Output("headphones", AudioOutputKind.Bluetooth, isDefault: false) }, revision: 4));
    Require(session.IsRouteAvailable(route) && session.TryAcceptPacket(alpha, Packet(alpha, stream, 3)), "same selected endpoint can reconnect without changing route");
}

static void TestJitter()
{
    using var session = MakeSession(out var alpha, out _);
    var output = new AudioOutputKey("beta", "speakers");
    session.SetConsent(true); var stream = Guid.NewGuid(); session.BeginStream(alpha, stream);
    var samples = new float[AudioLimits.SamplesPerPacket];
    session.TryAcceptPacket(alpha, Packet(alpha, stream, 1, .1f));
    Require(session.Render(output, samples) == 0, "jitter prefill is required");
    session.TryAcceptPacket(alpha, Packet(alpha, stream, 0, .2f));
    Require(session.Render(output, samples) == 1 && samples[0] == .2f, "out-of-order packets render by sequence");
    Require(!session.TryAcceptPacket(alpha, Packet(alpha, stream, 0)), "consumed packet replay rejected");
    Require(session.Render(output, samples) == 1 && samples[0] == .1f, "next sequence follows");
    Require(!session.TryAcceptPacket(alpha, Packet(alpha, stream, 2_000)), "hostile sequence gap rejected");
    for (ulong i = 2; i < 1_002; i++) session.TryAcceptPacket(alpha, Packet(alpha, stream, i, .3f));
    Require(session.BufferedPacketCount <= AudioLimits.MaxBufferedPackets, "queue memory and latency stay bounded under overload");
    Require(session.Render(output, samples) == 1 && samples[0] == .3f, "overload retains recent audio");
    session.SetRoutes(new[] { new AudioRoute("alpha", output, .5f) });
    Require(session.BufferedPacketCount == 0, "route changes discard old samples");
    session.TryAcceptPacket(alpha, Packet(alpha, stream, 1_010, .8f));
    session.TryAcceptPacket(alpha, Packet(alpha, stream, 1_011, .8f));
    Require(session.Render(output, samples) == 1 && samples[0] == .4f, "per-route gain applies after routing update");
}

static void TestCodec()
{
    var peer = Peer("opaque-identity:alpha"); var stream = Guid.NewGuid();
    var packet = Packet(peer, stream, 7);
    packet.Samples[0] = -1; packet.Samples[1] = 1;
    var bytes = AudioPacketCodec.Encode(packet);
    Require(AudioPacketCodec.TryDecode(bytes, out var decoded) && decoded!.OriginDeviceId == packet.OriginDeviceId
        && decoded.DeviceSessionId == packet.DeviceSessionId && decoded.StreamId == stream
        && decoded.Sequence == 7 && decoded.Samples.SequenceEqual(packet.Samples), "wire packet roundtrip");
    for (var length = 0; length < bytes.Length; length++)
        Require(!AudioPacketCodec.TryDecode(bytes.AsSpan(0, length), out _), "truncated frame rejected");
    Require(!AudioPacketCodec.TryDecode(new byte[AudioLimits.MaxPacketBytes + 1], out _), "oversize frame rejected before PCM allocation");
    Require(!AudioPacketCodec.TryDecode(bytes.Concat(new byte[] { 0 }).ToArray(), out _), "trailing bytes rejected");
    var mutated = (byte[])bytes.Clone(); mutated[0] ^= 1;
    Require(!AudioPacketCodec.TryDecode(mutated, out _), "magic mismatch rejected");
    mutated = (byte[])bytes.Clone(); mutated[45] = 3;
    Require(!AudioPacketCodec.TryDecode(mutated, out _), "excessive hop count rejected");
    mutated = (byte[])bytes.Clone(); BinaryPrimitives.WriteUInt16LittleEndian(mutated.AsSpan(46, 2), ushort.MaxValue);
    Require(!AudioPacketCodec.TryDecode(mutated, out _), "hostile sample length rejected");
    foreach (var value in new[] { float.NaN, float.PositiveInfinity, 1.01f, -1.01f })
    {
        mutated = (byte[])bytes.Clone(); BinaryPrimitives.WriteSingleLittleEndian(mutated.AsSpan(mutated.Length - 4), value);
        Require(!AudioPacketCodec.TryDecode(mutated, out _), "nonfinite/out-of-range sample rejected");
    }
    var random = new Random(812);
    for (var i = 0; i < 1_000; i++)
    {
        var malformed = new byte[random.Next(AudioLimits.MaxPacketBytes + 64)]; random.NextBytes(malformed);
        Require(!AudioPacketCodec.TryDecode(malformed, out _), "malformed packet fuzz rejected without exception");
    }
}

static void TestCancellation()
{
    using var session = MakeSession(out var alpha, out _);
    session.SetConsent(true); session.BeginStream(alpha, Guid.NewGuid());
    using var completed = new ManualResetEventSlim();
    var acquired = false;
    using var registration = session.SharingToken.Register(() =>
    {
        var render = Task.Run(() => session.Render(new("beta", "speakers"), new float[AudioLimits.SamplesPerPacket]));
        acquired = render.Wait(TimeSpan.FromSeconds(2));
        completed.Set();
    });
    session.SetConsent(false);
    Require(!session.IsConsented && session.SharingToken.IsCancellationRequested, "cancellation state is immediate");
    Require(completed.Wait(TimeSpan.FromSeconds(3)) && acquired, "cancellation callback runs without blocking the core lock");
}
