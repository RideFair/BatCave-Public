using System.Collections.Concurrent;
using System.Text.Json;
using BatCave.Audio;
using BatCave.AudioPreview;

var tests = new (string Name, Action Test)[] {
    ("no automatic capture, default passthrough and explicit sender consent", TestConsent),
    ("three-device source/output matrix mixes and deduplicates sends", TestMatrix),
    ("unsolicited offers and unselected endpoints are rejected", TestUnsolicited),
    ("reciprocal local origins never echo received PCM", TestNoEcho),
    ("pause, reconnect with reused session GUID, and hot unplug clear audio", TestRecovery),
    ("malformed controls do not open capture or playback", TestMalformed),
    ("local mirror works and remote default passthrough needs no self connection", TestLocalMirror),
    ("pause cancels blocked native activation before it can resume", TestBlockedActivation),
    ("delayed acceptance cannot enable capture after pause or a newer Start", TestLateAccept),
    ("receiver restart rotates origin while preserving other sinks and reverse audio", TestReceiverRestart)
};
foreach (var test in tests) { test.Test(); Console.WriteLine("PASS: " + test.Name + "."); }
Console.WriteLine($"PASS: {tests.Length} synthetic audio coordinator regression groups.");

static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Until(Func<bool> condition, string message)
{
    var deadline = DateTime.UtcNow.AddSeconds(4);
    while (DateTime.UtcNow < deadline) { if (condition()) return; Thread.Sleep(5); }
    throw new Exception(message);
}
static AudioRoute Route(string source, string destination, string endpoint = "speakers", float gain = 1)
    => new(source, new(destination, endpoint), gain);

static void TestConsent()
{
    using var fixture = new Fixture("alpha", "beta");
    var a = fixture.Host("alpha"); var b = fixture.Host("beta");
    Require(a.Platform.Captures.Count == 0 && b.Platform.Outputs.Count == 0, "construction starts no audio");
    a.Coordinator.Start(new[] { Route("alpha", "alpha") }, allowSending: false);
    Until(() => a.Coordinator.State.IsEnabled, "default local route enabled");
    Require(a.Platform.Captures.Count == 0 && a.Platform.Outputs.Count == 0, "default local speaker is native passthrough");
    b.Coordinator.Start(new[] { Route("alpha", "beta") }, allowSending: false);
    Until(() => b.Coordinator.State.IsEnabled && b.Platform.Outputs.ContainsKey("speakers"), "receiver explicitly enabled");
    Thread.Sleep(40);
    Require(a.Platform.Captures.Count == 0 && a.Coordinator.State.ActiveSendTargets == 0, "sender checkbox is independently required");
    a.Coordinator.Start(new[] { Route("alpha", "beta") }, allowSending: true);
    Until(() => a.Coordinator.State.ActiveSendTargets == 1 && a.Platform.LiveCapture is not null, "accepted offer begins capture");
    a.Platform.Emit(.25f, 2);
    Until(() => b.Platform.Pull("speakers")[0] == .25f, "approved route carries PCM");
}

static void TestMatrix()
{
    using var fixture = new Fixture("alpha", "beta", "gamma");
    var a = fixture.Host("alpha"); var b = fixture.Host("beta"); var c = fixture.Host("gamma");
    var routes = new[] { Route("alpha", "beta"), Route("alpha", "beta", "headphones"), Route("gamma", "beta") };
    a.Coordinator.Start(Array.Empty<AudioRoute>(), true);
    c.Coordinator.Start(Array.Empty<AudioRoute>(), true);
    Until(() => a.Coordinator.State.IsEnabled && c.Coordinator.State.IsEnabled, "source consent enabled before receiver subscriptions");
    b.Coordinator.Start(routes, false);
    Until(() => a.Coordinator.State.ActiveSendTargets == 1 && c.Coordinator.State.ActiveSendTargets == 1, "two origins share one receiver");
    a.Platform.Emit(.8f, 2); c.Platform.Emit(.7f, 2);
    Thread.Sleep(40); // allow both origin queues to reach their two-packet prefill before consuming
    Until(() => b.Platform.Pull("speakers")[0] == 1, "two origins mix and clip");
    Until(() => b.Platform.Pull("headphones")[0] == .8f, "second sink has independent source queue");
    Require(a.Link.SentAudio == 2, "same source is sent once per device, not once per endpoint");
    // A primary controller can request a third-device direct route; only the origin
    // sends and only the destination with its own matching ACL accepts.
    a.Coordinator.Start(new[] { Route("gamma", "beta") }, true);
    Until(() => c.Coordinator.State.ActiveSendTargets == 1, "third-device routing stays direct");
}

static void TestUnsolicited()
{
    using var fixture = new Fixture("alpha", "beta");
    var a = fixture.Host("alpha"); var b = fixture.Host("beta");
    a.Coordinator.Start(new[] { Route("alpha", "beta") }, true);
    Until(() => a.Coordinator.State.IsEnabled, "sender enabled");
    Thread.Sleep(50);
    Require(a.Coordinator.State.ActiveSendTargets == 0 && a.Platform.Captures.Count == 0 && b.Platform.Outputs.Count == 0,
        "unselected receiver neither plays nor starts sender capture");
    b.Coordinator.Start(new[] { Route("alpha", "beta", "headphones") }, false);
    Until(() => b.Coordinator.State.IsEnabled, "different selected output enabled");
    a.Link.SendControl("beta", "offer", new { streamId = Guid.NewGuid(), endpointId = "speakers", gain = 1f });
    Thread.Sleep(40);
    Require(!b.Platform.Outputs.ContainsKey("speakers"), "unselected endpoint cannot play");
}

static void TestNoEcho()
{
    using var fixture = new Fixture("alpha", "beta");
    var a = fixture.Host("alpha"); var b = fixture.Host("beta");
    var routes = new[] { Route("alpha", "beta"), Route("beta", "alpha") };
    a.Coordinator.Start(routes, true); b.Coordinator.Start(routes, true);
    Until(() => a.Coordinator.State.ActiveSendTargets == 1 && b.Coordinator.State.ActiveSendTargets == 1, "reciprocal sharing enabled");
    a.Platform.Emit(.3f, 2); b.Platform.Emit(.4f, 2);
    Until(() => a.Platform.Pull("speakers")[0] == .4f, "alpha receives beta's local origin");
    Until(() => b.Platform.Pull("speakers")[0] == .3f, "beta receives alpha's local origin");
    Require(a.Link.SentAudio == 2 && b.Link.SentAudio == 2, "rendered incoming PCM is never retransmitted");
}

static void TestRecovery()
{
    using var fixture = new Fixture("alpha", "beta");
    var a = fixture.Host("alpha"); var b = fixture.Host("beta");
    a.Coordinator.Start(new[] { Route("alpha", "beta") }, true); b.Coordinator.Start(new[] { Route("alpha", "beta") }, false);
    Until(() => a.Coordinator.State.ActiveSendTargets == 1, "initial session connected");
    a.Platform.Emit(.4f, 2);
    b.Coordinator.Pause("Test pause");
    Require(!b.Coordinator.State.IsEnabled, "Pause takes effect before queued teardown");
    Until(() => b.Platform.LiveOutputs == 0 && a.Platform.LiveCapture is null, "paused receiver tears down and stops sender");
    var previous = a.Link.Session("beta")!.Value;
    fixture.Disconnect("alpha", "beta");
    Require(!a.Coordinator.State.IsEnabled, "disconnect revokes sender immediately");
    fixture.Connect("alpha", "beta", reuseSessions: true);
    Until(() => a.Coordinator.State.Devices.Length == 2 && b.Coordinator.State.Devices.Length == 2, "same-GUID reconnect inventory handled");
    Require(a.Link.Session("beta") == previous && !a.Coordinator.State.IsEnabled && !b.Coordinator.State.IsEnabled, "same hello GUID does not preserve sharing consent");
    a.Coordinator.Start(new[] { Route("alpha", "beta") }, true); b.Coordinator.Start(new[] { Route("alpha", "beta") }, false);
    Until(() => a.Coordinator.State.ActiveSendTargets == 1, "fresh explicit consent reconnects");
    b.Inventory = b.Inventory with { Revision = 2, Outputs = b.Inventory.Outputs.Select(o => o with { IsConnected = false }).ToArray() };
    b.Coordinator.SetLocalInventory(b.Inventory);
    Until(() => !b.Coordinator.State.IsEnabled && b.Platform.LiveOutputs == 0, "hot unplug pauses all audio without fallback");
}

static void TestMalformed()
{
    using var fixture = new Fixture("alpha", "beta");
    var a = fixture.Host("alpha"); var b = fixture.Host("beta");
    a.Coordinator.Start(Array.Empty<AudioRoute>(), true);
    Until(() => a.Coordinator.State.IsEnabled, "sender enabled without a destination");
    foreach (var value in new[] {
        "{\"destinationDeviceId\":\"beta\",\"endpointId\":\"speakers\",\"gain\":\"bad\"}",
        "{\"destinationDeviceId\":\"beta\",\"endpointId\":\"speakers\",\"gain\":1,\"other\":true}",
        "{\"destinationDeviceId\":\"beta\",\"endpointId\":\"speakers\",\"gain\":1,\"gain\":1}",
        "{\"destinationDeviceId\":\"beta\",\"endpointId\":\"bad/path\",\"gain\":1}" })
    {
        using var document = JsonDocument.Parse(value); b.Link.InjectControl("alpha", "subscribe", document.RootElement);
    }
    Thread.Sleep(60);
    Require(a.Platform.LiveCapture is null && b.Platform.LiveOutputs == 0 && a.Coordinator.State.IsEnabled,
        "malformed controls rejected without native audio or losing valid state");
}

static void TestLocalMirror()
{
    using var fixture = new Fixture("alpha", "beta");
    var a = fixture.Host("alpha");
    a.Coordinator.Start(new[] { Route("beta", "beta") }, false);
    Until(() => a.Coordinator.State.IsEnabled, "remote native default selection is passthrough");
    Require(a.Link.SentControls == 0 && a.Platform.Captures.Count == 0, "remote self default never requests a self connection");
    a.Coordinator.Start(new[] { Route("alpha", "alpha", "headphones") }, false);
    Until(() => a.Platform.LiveCapture is not null, "explicit local nondefault mirror starts capture");
    a.Platform.Emit(.6f, 2);
    Until(() => a.Platform.Pull("headphones")[0] == .6f, "local selected endpoint receives mirror");
    Require(a.Link.SentAudio == 0, "local mirror requires no network sending permission");
}

static void TestBlockedActivation()
{
    foreach (var blockCapture in new[] { true, false })
    {
        using var fixture = new Fixture("alpha", "beta");
        var a = fixture.Host("alpha");
        a.Platform.BlockCaptureStart = blockCapture; a.Platform.BlockOutputStart = !blockCapture;
        a.Coordinator.Start(new[] { Route("alpha", "alpha", "headphones") }, false);
        if (blockCapture)
        {
            Until(() => a.Platform.Captures.Any(c => c.StartEntered.IsSet), "capture Start is blocked");
            var capture = a.Platform.Captures.Single();
            a.Coordinator.Pause("Paused during capture activation");
            Require(!a.Coordinator.State.IsEnabled, "Pause immediately deactivates sharing");
            Until(() => capture.IsDisposed, "cancellation requests native capture stop before Start returns");
            capture.ReleaseStart.Set();
            Until(() => a.Platform.LiveOutputs == 0, "output also stopped");
            Require(!capture.Started, "canceled capture activation cannot resume audio");
        }
        else
        {
            Until(() => a.Platform.Outputs.TryGetValue("headphones", out var o) && o.StartEntered.IsSet, "output Start is blocked");
            var output = a.Platform.Outputs["headphones"];
            a.Coordinator.Pause("Paused during output activation");
            Until(() => output.IsDisposed, "cancellation requests native output stop before Start returns");
            output.ReleaseStart.Set();
            Until(() => a.Coordinator.State.Status.Contains("Paused during output"), "queued teardown completes");
            Require(!output.Started && a.Platform.Captures.Count == 0, "canceled output cannot resume or start capture");
        }
    }
}

static void TestLateAccept()
{
    using var fixture = new Fixture("alpha", "beta");
    var a = fixture.Host("alpha"); var b = fixture.Host("beta");
    b.Link.HoldAccepts = true;
    a.Coordinator.Start(new[] { Route("alpha", "beta") }, true);
    Until(() => a.Coordinator.State.IsEnabled, "source consent ready");
    b.Coordinator.Start(new[] { Route("alpha", "beta") }, false);
    Until(() => b.Link.HeldAccepts.Count > 0, "acceptance is delayed in transport");
    a.Coordinator.Pause("Paused with acceptance pending");
    Require(!a.Coordinator.State.IsEnabled, "source paused before delayed acceptance");
    a.Coordinator.Start(Array.Empty<AudioRoute>(), true);
    Until(() => a.Coordinator.State.IsEnabled && a.Coordinator.State.Routes.Length == 0, "new consent has a different generation and no offered route");
    b.Link.DeliverHeldAccepts();
    Thread.Sleep(40);
    Require(a.Platform.Captures.Count == 0 && a.Coordinator.State.ActiveSendTargets == 0,
        "old acceptance cannot reactivate capture in a newer consent generation");
}

static void TestReceiverRestart()
{
    using var fixture = new Fixture("alpha", "beta", "gamma");
    var a = fixture.Host("alpha"); var b = fixture.Host("beta"); var c = fixture.Host("gamma");
    a.Coordinator.Start(new[] { Route("alpha", "beta"), Route("alpha", "gamma"), Route("gamma", "alpha") }, true);
    b.Coordinator.Start(new[] { Route("alpha", "beta") }, false);
    c.Coordinator.Start(new[] { Route("alpha", "gamma"), Route("gamma", "alpha") }, true);
    Until(() => a.Coordinator.State.ActiveSendTargets == 2 && c.Coordinator.State.ActiveSendTargets == 1, "two sinks and reverse origin connected");
    var retiredCapture = a.Platform.LiveCapture!;
    var previous = a.Link.Offers.Last(o => o.Destination == "beta").Stream;
    a.Platform.Emit(.4f, 2); c.Platform.Emit(.2f, 2);
    Until(() => b.Platform.Pull("speakers")[0] == .4f, "first sink receives initial origin");
    Until(() => c.Platform.Pull("speakers")[0] == .4f, "second sink receives initial origin");
    Until(() => a.Platform.Pull("speakers")[0] == .2f, "reverse audio initially arrives");
    b.Coordinator.Pause("Receiver pause");
    Until(() => !b.Coordinator.State.IsEnabled && a.Coordinator.State.ActiveSendTargets == 1, "paused sink is removed while second sink remains");
    a.Platform.Emit(.5f, 2);
    Until(() => c.Platform.Pull("speakers")[0] == .5f, "other sink continues before resubscription");
    Require(b.Platform.Pull("speakers").All(v => v == 0), "paused sink receives no PCM");
    b.Coordinator.Start(new[] { Route("alpha", "beta") }, false);
    Until(() => a.Coordinator.State.ActiveSendTargets == 2 && a.Platform.Captures.Count == 2, "receiver restart renegotiates a fresh origin without restarting sender consent");
    var fresh = a.Link.Offers.Last(o => o.Destination == "beta").Stream;
    Require(fresh != previous && a.Coordinator.State.IsEnabled && c.Coordinator.State.IsEnabled,
        "origin GUID rotates while existing source and sink consent stay enabled");
    Require(c.Platform.Captures.Count == 1 && c.Coordinator.State.ActiveSendTargets == 1,
        "origin-specific stop does not stop the reverse source stream");
    var beforeStale = a.Link.SentAudio;
    retiredCapture.Emit(.9f); retiredCapture.Fail();
    Thread.Sleep(30);
    Require(a.Coordinator.State.IsEnabled && a.Link.SentAudio == beforeStale,
        "late packets and faults from the retired native capture cannot affect the new origin");
    a.Platform.Emit(.6f, 2); c.Platform.Emit(.3f, 2);
    Until(() => b.Platform.Pull("speakers")[0] == .6f, "restarted receiver gets fresh origin");
    Until(() => c.Platform.Pull("speakers")[0] == .6f, "previous sink accepts fresh origin using its existing exact ACL");
    Until(() => a.Platform.Pull("speakers")[0] == .3f, "reverse audio survives origin rotation");
}

sealed class Fixture : IDisposable
{
    private readonly Dictionary<string, Host> _hosts;
    public Fixture(params string[] ids)
    {
        _hosts = ids.ToDictionary(id => id, id => new Host(id));
        foreach (var host in _hosts.Values) host.Link.All = _hosts;
        for (var i = 0; i < ids.Length; i++) for (var j = i + 1; j < ids.Length; j++) Connect(ids[i], ids[j], false);
        foreach (var host in _hosts.Values)
        {
            var count = ids.Length;
            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (host.Coordinator.State.Devices.Length != count && DateTime.UtcNow < deadline) Thread.Sleep(5);
            if (host.Coordinator.State.Devices.Length != count) throw new Exception("Inventories did not converge.");
        }
    }
    public Host Host(string id) => _hosts[id];
    public void Connect(string first, string second, bool reuseSessions)
    {
        var a = _hosts[first]; var b = _hosts[second];
        a.Link.Connect(b, reuseSessions); b.Link.Connect(a, reuseSessions);
        a.Link.RaiseInventory(b); b.Link.RaiseInventory(a);
    }
    public void Disconnect(string first, string second)
    { _hosts[first].Link.Disconnect(second); _hosts[second].Link.Disconnect(first); }
    public void Dispose() { foreach (var host in _hosts.Values) host.Coordinator.Dispose(); }
}

sealed class Host
{
    public AudioDeviceInventory Inventory;
    public readonly FakeLink Link;
    public readonly FakePlatform Platform = new();
    public readonly AudioCoordinator Coordinator;
    public Host(string id)
    {
        Inventory = new(id, "Test computer", false, 1,
            new[] { new AudioOutputDescriptor("speakers", "Test speakers", AudioOutputKind.Speakers, true, true, true),
                new AudioOutputDescriptor("headphones", "Test headphones", AudioOutputKind.Bluetooth, true, true, false) }, true, true);
        Link = new FakeLink(id); Coordinator = new(Inventory, Link, Platform);
    }
}

sealed class FakeLink : IAudioPeerLink
{
    private readonly string _id;
    private readonly ConcurrentDictionary<string, AudioPeerSession> _connected = new();
    private readonly Dictionary<string, AudioPeerSession> _saved = new();
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    public Dictionary<string, Host> All = new();
    public int SentAudio;
    public int SentControls;
    public bool HoldAccepts;
    public readonly ConcurrentQueue<(string Destination, JsonElement Payload)> HeldAccepts = new();
    public readonly ConcurrentQueue<(string Destination, Guid Stream)> Offers = new();
    public event Action<AudioPeerSession, AudioDeviceInventory>? InventoryReceived;
    public event Action<AudioPeerSession, string, JsonElement>? ControlReceived;
    public event Action<AudioPeerSession, AudioPacket>? PacketReceived;
    public event Action<string>? PeerDisconnected;
    public FakeLink(string id) => _id = id;
    public AudioPeerSession? Session(string id) => _connected.TryGetValue(id, out var peer) ? peer : null;
    public string[] ConnectedIds => _connected.Keys.ToArray();
    public void Connect(Host host, bool reuse)
    {
        var peer = reuse && _saved.TryGetValue(host.Inventory.DeviceId, out var previous) ? previous
            : new AudioPeerSession(host.Inventory.DeviceId, Guid.NewGuid());
        _saved[host.Inventory.DeviceId] = peer; _connected[host.Inventory.DeviceId] = peer;
    }
    public void RaiseInventory(Host host) => InventoryReceived?.Invoke(Session(host.Inventory.DeviceId)!.Value, host.Inventory);
    public void Disconnect(string id) { _connected.TryRemove(id, out _); PeerDisconnected?.Invoke(id); }
    public bool SendControl(string destination, string type, object payload)
    {
        if (Session(destination) is null || All[destination].Link.Session(_id) is not { } peer) return false;
        Interlocked.Increment(ref SentControls);
        if (type == "offer") Offers.Enqueue((destination, JsonSerializer.SerializeToElement(payload, _json).GetProperty("streamId").GetGuid()));
        if (type == "accept" && HoldAccepts)
        { HeldAccepts.Enqueue((destination, JsonSerializer.SerializeToElement(payload, _json))); return true; }
        All[destination].Link.ControlReceived?.Invoke(peer, type, JsonSerializer.SerializeToElement(payload, _json)); return true;
    }
    public void InjectControl(string destination, string type, JsonElement payload)
    {
        var peer = All[destination].Link.Session(_id)!.Value;
        All[destination].Link.ControlReceived?.Invoke(peer, type, payload);
    }
    public void DeliverHeldAccepts()
    { while (HeldAccepts.TryDequeue(out var held)) InjectControl(held.Destination, "accept", held.Payload); }
    public bool SendAudio(string destination, Guid streamId, ulong sequence, float[] samples)
    {
        if (Session(destination) is null || All[destination].Link.Session(_id) is not { } peer) return false;
        Interlocked.Increment(ref SentAudio);
        All[destination].Link.PacketReceived?.Invoke(peer, new(_id, peer.SessionId, streamId, sequence, new[] { _id }, (float[])samples.Clone())); return true;
    }
}

sealed class FakePlatform : IAudioPlatform
{
    public readonly ConcurrentBag<FakeCapture> Captures = new();
    public readonly ConcurrentDictionary<string, FakeOutput> Outputs = new();
    public bool BlockCaptureStart;
    public bool BlockOutputStart;
    public FakeCapture? LiveCapture => Captures.FirstOrDefault(c => !c.IsDisposed && c.Started);
    public int LiveOutputs => Outputs.Values.Count(o => !o.IsDisposed && o.Started);
    public IAudioCapture CreateCapture() { var capture = new FakeCapture(BlockCaptureStart); Captures.Add(capture); return capture; }
    public IAudioOutput CreateOutput(string endpoint) { var output = new FakeOutput(BlockOutputStart); Outputs[endpoint] = output; return output; }
    public void Emit(float value, int count) { for (var i = 0; i < count; i++) LiveCapture?.Emit(value); }
    public float[] Pull(string endpoint) => Outputs.TryGetValue(endpoint, out var output) && !output.IsDisposed
        ? output.Pull() : new float[AudioLimits.SamplesPerPacket];
}
sealed class FakeCapture : IAudioCapture
{
    public bool Started; public bool IsDisposed;
    public readonly ManualResetEventSlim StartEntered = new();
    public readonly ManualResetEventSlim ReleaseStart;
    public FakeCapture(bool blocked = false) => ReleaseStart = new(!blocked);
    public event Action<float[]>? BlockAvailable;
    public event Action<Exception>? Faulted;
    public void Start() { StartEntered.Set(); ReleaseStart.Wait(); Started = !IsDisposed; }
    public void Emit(float value) => BlockAvailable?.Invoke(Enumerable.Repeat(value, AudioLimits.SamplesPerPacket).ToArray());
    public void Fail() => Faulted?.Invoke(new InvalidOperationException("Synthetic retired capture failure"));
    public void Dispose() => IsDisposed = true;
}
sealed class FakeOutput : IAudioOutput
{
    public bool Started; public bool IsDisposed;
    public readonly ManualResetEventSlim StartEntered = new();
    public readonly ManualResetEventSlim ReleaseStart;
    public FakeOutput(bool blocked = false) => ReleaseStart = new(!blocked);
    private Func<float[]?>? _provider;
    public event Action<Exception>? Faulted { add { } remove { } }
    public void SetFrameProvider(Func<float[]?> provider) => _provider = provider;
    public void Start() { StartEntered.Set(); ReleaseStart.Wait(); Started = !IsDisposed; }
    public float[] Pull() => _provider?.Invoke() ?? new float[AudioLimits.SamplesPerPacket];
    public void Dispose() => IsDisposed = true;
}
