using System.Text.Json;
using BatCave.Universal.Topology;

namespace BatCave.WindowsAgent;

internal static class SecureTopology
{
    // This profile is built only from local geometry and an authenticated peer's
    // bounds. IDs come from approved cards; hostnames never select a trusted device.
    public static void Write(string path, string localId, VirtualDesktopBounds local, DeviceHelloPayload peer, bool peerOnRight)
    {
        var remote = peer.VirtualDesktopBounds;
        if (remote is null || remote.Width <= 0 || remote.Height <= 0 || remote.Width > 100000 || remote.Height > 100000)
            throw new InvalidDataException("Invalid peer screen bounds.");
        var localEdge = peerOnRight ? "right" : "left";
        var remoteEdge = peerOnRight ? "left" : "right";
        var localDisplay = localId + ":desktop";
        var remoteDisplay = peer.DeviceId + ":desktop";
        // A separate virtual rectangle avoids conflating Windows and Mac native
        // coordinate origins. Mac handoff uses normalized targetPosition.
        var remoteLeft = peerOnRight ? local.Right + 1 : local.Left - remote.Width;
        var remoteBounds = new VirtualDesktopBounds(remoteLeft, local.Top, remote.Width, remote.Height);
        object Selection(string id, string edge, double position) => new { displayId = id, edge, position };
        var profile = new
        {
            schema = "batcave.topology.v1",
            displays = new[]
            {
                new { id = localDisplay, deviceId = localId, label = "This computer", kind = "desktop", bounds = ScreenBounds.From(local) },
                new { id = remoteDisplay, deviceId = peer.DeviceId, label = peer.DeviceName, kind = "desktop", bounds = ScreenBounds.From(remoteBounds) }
            },
            links = new[] { new { id = "secure-desktop-bridge",
                from = new { start = Selection(localDisplay, localEdge, 0), end = Selection(localDisplay, localEdge, 1) },
                to = new { start = Selection(remoteDisplay, remoteEdge, 0), end = Selection(remoteDisplay, remoteEdge, 1) } } }
        };
        File.WriteAllText(path + ".new", JsonSerializer.Serialize(profile, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        File.Move(path + ".new", path, overwrite: true);
        if (!TopologyMap.TryLoad(path, localId, out _, out _)) { throw new InvalidDataException("Generated topology is invalid."); }
    }
}
