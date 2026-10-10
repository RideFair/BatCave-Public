using System.Text.Json;
using BatCave.Universal.Topology;

namespace BatCave.WindowsAgent;

internal static class SecureTopology
{
    public static void Write(string path, string localId, VirtualDesktopBounds local, DeviceHelloPayload peer, bool peerOnRight)
        => Write(path, localId, new[] { new MonitorDescriptor("desktop", "This computer", local) }, peer,
            new("desktop", peerOnRight ? PeerSide.Right : PeerSide.Left));

    public static void Write(string path, string localId, IReadOnlyList<MonitorDescriptor> monitors, DeviceHelloPayload peer, MonitorLayoutSettings settings)
    {
        if (string.IsNullOrWhiteSpace(localId) || string.IsNullOrWhiteSpace(peer.DeviceId) || localId == peer.DeviceId)
            throw new InvalidDataException("Topology requires two different device identities.");
        var scene = MonitorLayout.Build(monitors, peer.VirtualDesktopBounds, settings);
        var remote = scene.Remote.Bounds;
        var localDisplay = localId + ":" + scene.Local.Id;
        var remoteDisplay = peer.DeviceId + ":desktop";
        object Selection(string id, string edge, double position) => new { displayId = id, edge, position };
        object Display(string id, string device, string label, VirtualDesktopBounds bounds)
            => new { id, deviceId = device, label, kind = "desktop", bounds = ScreenBounds.From(bounds) };
        var displays = monitors.Select(m => Display(localId + ":" + m.Id, localId, m.Label, m.Bounds)).ToList();
        displays.Add(Display(remoteDisplay, peer.DeviceId, "Other computer", remote));
        var localAxis = scene.VerticalEdge ? scene.Local.Bounds.Top : scene.Local.Bounds.Left;
        var localLength = (scene.VerticalEdge ? scene.Local.Bounds.Height : scene.Local.Bounds.Width) - 1;
        var peerAxis = scene.VerticalEdge ? remote.Top : remote.Left;
        var peerLength = (scene.VerticalEdge ? remote.Height : remote.Width) - 1;
        var profile = new
        {
            schema = "batcave.topology.v1", displays,
            links = scene.Seams.Select((s, i) => new
            {
                id = "secure-monitor-bridge-" + i,
                from = new { start = Selection(localDisplay, scene.LocalEdge, (s.Start - localAxis) / localLength),
                    end = Selection(localDisplay, scene.LocalEdge, (s.End - localAxis) / localLength) },
                to = new { start = Selection(remoteDisplay, scene.RemoteEdge, (s.Start - peerAxis) / peerLength),
                    end = Selection(remoteDisplay, scene.RemoteEdge, (s.End - peerAxis) / peerLength) }
            })
        };
        var temporary = path + ".new";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(profile, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            if (!TopologyMap.TryLoad(temporary, localId, out var map, out _) || map is null || !map.HasLocalSourceLinks(localId))
                throw new InvalidDataException("Generated topology is invalid.");
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
