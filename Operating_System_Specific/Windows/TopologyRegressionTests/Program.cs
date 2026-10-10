using System.Text.Json.Nodes;
using BatCave.WindowsAgent;
using BatCave.Universal.Topology;

var directory = Path.Combine(Path.GetTempPath(), "BatCaveGeometry-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var path = Path.Combine(directory, "topology.json");
var assertions = 0;
try
{
    var monitor = new MonitorDescriptor("test-monitor", "Monitor 1", new(-1920, -200, 1920, 1080));
    foreach (var side in Enum.GetValues<PeerSide>())
    foreach (var alignment in new[] { -100, -55, 0, 55, 100 })
    foreach (var size in new[] { (1280, 720), (2240, 1260), (1080, 1920) })
    {
        var peer = Hello(size.Item1, size.Item2);
        var layout = new MonitorLayoutSettings(monitor.Id, side, alignment);
        var scene = MonitorLayout.Build(new[] { monitor }, peer.VirtualDesktopBounds, layout);
        SecureTopology.Write(path, "local-test", new[] { monitor }, peer, layout);
        Require(TopologyMap.TryLoad(path, "local-test", out var topology, out _), "generated topology loads");
        var seam = scene.Seams.Single(); var axis = (seam.Start + seam.End) / 2;
        var (from, to) = Crossing(monitor.Bounds, scene.LocalEdge, axis);
        Require(topology!.TryMapCrossing(from, to, "local-test", out var handoff) && handoff.TargetDeviceId == "peer-test", "outward handoff");
        Require(handoff.TargetPosition is >= 0 and <= 1, "normalized destination");
        var targetAxis = scene.VerticalEdge ? handoff.TargetY : handoff.TargetX;
        (from, to) = Crossing(scene.Remote.Bounds, scene.RemoteEdge, targetAxis);
        Require(topology.TryMapCrossing(from, to, "peer-test", out var returned) && returned.TargetDeviceId == "local-test", "reverse handoff");
        Require(Math.Abs((scene.VerticalEdge ? returned.TargetY : returned.TargetX) - axis) <= 1, "round-trip keeps crossing position");
        var inward = Crossing(monitor.Bounds, scene.LocalEdge, axis);
        Require(!topology.TryMapCrossing(inward.To, inward.From, "local-test", out _), "inward motion does not hand off");
    }
    var neighbor = new MonitorDescriptor("neighbor", "Monitor 2", new(0, -200, 1920, 1080));
    Reject(() => MonitorLayout.Build(new[] { monitor, neighbor }, Hello(1920, 1080).VirtualDesktopBounds, new(monitor.Id)), "inside Windows edge blocked");
    var partial = neighbor with { Bounds = new(0, 100, 1920, 300) };
    var split = MonitorLayout.Build(new[] { monitor, partial }, Hello(1920, 1080).VirtualDesktopBounds, new(monitor.Id));
    Require(split.Seams.Count == 2, "partial obstruction splits the seam");
    SecureTopology.Write(path, "local-test", new[] { monitor, partial }, Hello(1920, 1080), new(monitor.Id));
    Require(TopologyMap.TryLoad(path, "local-test", out var splitMap, out _), "split topology loads");
    Require(!splitMap!.TryMapCrossing(new(-2, 200), new(2, 200), "local-test", out _), "internal seam never transfers input");
    Require(splitMap.TryMapCrossing(new(-2, 0), new(2, 0), "local-test", out _), "exposed seam transfers input");
    var validBytes = File.ReadAllBytes(path);
    Reject(() => SecureTopology.Write(path, "local-test", new[] { monitor, neighbor }, Hello(1920, 1080), new(monitor.Id)), "invalid layout rejected");
    Require(File.ReadAllBytes(path).SequenceEqual(validBytes), "failed save preserves usable topology");
    Reject(() => MonitorLayout.Build(new[] { monitor }, Hello(1920, 1080).VirtualDesktopBounds, new("missing")), "removed monitor rejected");
    Reject(() => MonitorLayout.Build(new[] { monitor }, Hello(1920, 1080).VirtualDesktopBounds, new(monitor.Id, (PeerSide)9)), "invalid direction rejected");
    Reject(() => MonitorLayout.Build(new[] { monitor }, Hello(1920, 1080).VirtualDesktopBounds, new(monitor.Id, Alignment: 101)), "invalid alignment rejected");
    var legacy = Hello(1920, 1080).VirtualDesktopBounds with { Right = 1920, Bottom = 1080 };
    Require(MonitorLayout.Build(new[] { monitor }, legacy, new(monitor.Id)).Seams.Count == 1, "exclusive v0 Mac bounds accepted");
    Reject(() => MonitorLayout.Build(new[] { monitor }, legacy with { Right = int.MaxValue }, new(monitor.Id)), "inconsistent remote bounds rejected");
    var storePath = Path.Combine(directory, "layouts.json");
    var store = new MonitorLayoutStore(storePath);
    store.Save("peer-a", new(monitor.Id, PeerSide.Above, -32));
    store.Save("peer-b", new(monitor.Id, PeerSide.Left, 58));
    store = new(storePath);
    Require(store.Get("peer-a") == new MonitorLayoutSettings(monitor.Id, PeerSide.Above, -32) && store.Get("peer-b")!.Side == PeerSide.Left, "layout persistence is per peer");
    var saved = File.ReadAllText(storePath);
    Reject(() => store.Save("peer-a", new(monitor.Id, Alignment: -101)), "bad settings rejected before write");
    Require(File.ReadAllText(storePath) == saved, "bad settings preserve saved state");
    store.Remove("peer-a"); Require(new MonitorLayoutStore(storePath).Get("peer-a") is null && store.Get("peer-b") is not null, "unpair removes only that layout");
    // Mutate valid profiles to prove fail-closed parsing, rather than merely
    // testing the serializer's own happy path.
    foreach (var mutate in new Action<JsonNode>[]
    {
        n => n["schema"] = "unknown",
        n => n["displays"]![0]!["bounds"]!["width"] = 0,
        n => n["displays"]![0]!["bounds"]!["right"] = int.MaxValue,
        n => n["displays"]![1]!["id"] = n["displays"]![0]!["id"]!.GetValue<string>(),
        n => n["links"]![0]!["from"]!["start"]!["position"] = 2,
        n => n["links"]![0]!["from"]!["start"]!["edge"] = "invalid",
        n => n["links"]![0]!["from"]!["start"]!["displayId"] = "unknown",
        n => n["links"]![0]!["from"]!["start"]!.AsObject().Remove("position"),
        n => n["displays"]![0]!["bounds"]!.AsObject().Remove("top"),
        n => n["displays"]![0]!["deviceId"] = 3,
        n => n["links"]![0]!["from"]!["end"] = n["links"]![0]!["from"]!["start"]!.DeepClone()
    })
    {
        var node = JsonNode.Parse(validBytes)!; mutate(node); File.WriteAllText(path, node.ToJsonString());
        Require(!TopologyMap.TryLoad(path, "local-test", out var failed, out _) && failed is null, "malformed topology safely rejected");
    }
    Console.WriteLine($"PASS: {assertions} monitor routing checks, four directions, alignment extremes, return mapping, internal-edge exclusion, per-peer persistence and malformed-state rejection.");
}
finally
{
    foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
    Directory.Delete(directory);
}

DeviceHelloPayload Hello(int width, int height) => new("peer-test", "Other computer", "test", "test", ScreenBounds.From(new(0, 0, width, height)));
void Require(bool condition, string reason) { assertions++; if (!condition) throw new Exception(reason); }
void Reject(Action action, string reason)
{
    try { action(); } catch (InvalidDataException) { assertions++; return; }
    throw new Exception(reason);
}
static (Point2D From, Point2D To) Crossing(VirtualDesktopBounds b, string edge, double axis) => edge switch
{
    "right" => (new(b.Right - 2, axis), new(b.Right + 2, axis)),
    "left" => (new(b.Left + 2, axis), new(b.Left - 2, axis)),
    "top" => (new(axis, b.Top + 2), new(axis, b.Top - 2)),
    _ => (new(axis, b.Bottom - 2), new(axis, b.Bottom + 2))
};
