using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using BatCave.WindowsAgent;
using BatCave.Universal.Topology;

internal static class WindowsPeerRoleChecks
{
    private sealed class Sink : IWindowsInputSink
    {
        public readonly List<string> Events = [];
        public void Move(int x, int y) => Events.Add($"move:{x},{y}");
        public void Key(HeldKey key, bool up) => Events.Add($"key:{key.VirtualKey}:{up}");
        public void Text(string text) => Events.Add("text:" + text);
        public void Button(string button, bool down) => Events.Add($"button:{button}:{down}");
        public void Wheel(int vertical, int horizontal) => Events.Add($"wheel:{vertical},{horizontal}");
    }

    internal static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "BatCave-Windows-Peers-" + Guid.NewGuid().ToString("N"));
        var aPath = Path.Combine(root, "a"); var bPath = Path.Combine(root, "b");
        using var aIdentity = DeviceIdentityStore.OpenOrCreate(aPath);
        using var bIdentity = DeviceIdentityStore.OpenOrCreate(bPath);
        try
        {
            var aPin = new ApprovedPeer(aIdentity.DeviceId, aIdentity.Fingerprint);
            var bPin = new ApprovedPeer(bIdentity.DeviceId, bIdentity.Fingerprint);
            var approvedA = new[] { bPin }; var approvedB = new[] { aPin };
            using var a = new LanBridgePeer(aIdentity, () => approvedA);
            using var b = new LanBridgePeer(bIdentity, () => approvedB);
            a.UpdateLocalBounds(new(0, 0, 1920, 1080)); b.UpdateLocalBounds(new(0, 0, 1920, 1080));
            var sinkA = new Sink(); var sinkB = new Sink(); var gate = new object();
            var receiveA = new WindowsInputReceiver(a.LocalDeviceId, sinkA);
            var receiveB = new WindowsInputReceiver(b.LocalDeviceId, sinkB);
            var messagesA = 0; var messagesB = 0;
            a.InputMessageReceived += (source, session, type, payload) =>
            { lock (gate) { receiveA.Handle(source, session, type, payload); messagesA++; } };
            b.InputMessageReceived += (source, session, type, payload) =>
            { lock (gate) { receiveB.Handle(source, session, type, payload); messagesB++; } };
            a.PeerDisconnected += _ => { lock (gate) receiveA.Pause(); };
            b.PeerDisconnected += _ => { lock (gate) receiveB.Pause(); };
            await a.StartListeningAsync(0);
            var listener = (TcpListener)typeof(LanBridgePeer).GetField("_listener", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(a)!;
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            await b.ConnectSecureAsync("127.0.0.1", aPin, port);
            await Until(() => a.HasPeer && b.HasPeer, "two Windows identities authenticate using the real secure connector/listener");
            Require(a.CurrentPeerDeviceId == bIdentity.DeviceId && b.CurrentPeerDeviceId == aIdentity.DeviceId, "hello IDs are certificate-bound in both directions");
            Require(b.VerifiedReachability is { } destination && destination.Address.Equals(IPAddress.Loopback) && destination.InputPort == port,
                "outgoing pinned hello proves the connected address and listening port");
            Require(a.VerifiedReachability is { } source && source.Address.Equals(IPAddress.Loopback) && source.InputPort is null,
                "incoming pinned hello never mistakes an ephemeral source port for a listening port");

            await Handoff(a, b.LocalDeviceId); a.PublishKeyInput(65, 30, false, false, "a"); await a.FlushAsync();
            await Until(() => { lock (gate) return messagesB >= 2; }, "pre-enable packets arrived");
            lock (gate) { Require(sinkB.Events.Count == 0, "authenticated network traffic cannot enable input"); receiveB.Enable(a.LocalDeviceId, b.SessionGeneration, new(0, 0, 1920, 1080)); }
            // Use the actual monitor arrangement: its target X is 1920, outside
            // the receiver's native desktop, and must be projected locally.
            var topologyPath = Path.Combine(root, "arranged-topology.json");
            try
            {
                SecureTopology.Write(topologyPath, a.LocalDeviceId, new(0, 0, 1920, 1080),
                    new DeviceHelloPayload(b.LocalDeviceId, "Receiving PC", "Windows", "test", new(0, 0, 1920, 1080, 1919, 1079)), true);
                Require(TopologyMap.TryLoad(topologyPath, a.LocalDeviceId, out var map, out _), "real saved arrangement loads");
                Require(map!.TryMapCrossing(new(1918, 540), new(1922, 540), a.LocalDeviceId, out var crossing), "real arrangement crosses to the receiver");
                Require(crossing.TargetX > 1919, "fixture uses source topology coordinates outside the receiver");
                a.PublishCursorHandoff(crossing);
            }
            finally { File.Delete(topologyPath); }
            a.PublishKeyInput(0xA0, 42, false, false, null); a.PublishMouseButton("left", true);
            a.PublishMouseDelta(20, 15); a.PublishMouseWheel(120, 0); await a.FlushAsync();
            await Until(() => { lock (gate) return sinkB.Events.Contains("wheel:120,0"); }, "mouse and held input cross the encrypted Windows-to-Windows link");
            lock (gate) Require(sinkB.Events.Contains("move:0,540"), "arranged handoff reaches native receiver coordinates");
            await Handoff(a, a.LocalDeviceId);
            await Until(() => { lock (gate) return sinkB.Events.Contains("button:left:False") && sinkB.Events.Contains("key:160:True"); }, "return crossing releases receiver-held state");

            // Swap the physical keyboard/mouse source on this same approved link.
            lock (gate) { receiveB.Pause(); receiveA.Enable(b.LocalDeviceId, a.SessionGeneration, new(0, 0, 1920, 1080)); }
            await Handoff(b, a.LocalDeviceId); b.PublishKeyInput(0xA2, 29, false, false, null); b.PublishMouseButton("right", true); await b.FlushAsync();
            await Until(() => { lock (gate) return sinkA.Events.Contains("button:right:True"); }, "either Windows peer can receive");
            approvedB = [];
            b.DisconnectUnapprovedPeer();
            await Until(() => !a.HasPeer && !b.HasPeer, "revoking approval disconnects both Windows peers");
            Require(a.VerifiedReachability is null && b.VerifiedReachability is null, "disconnected reachability cannot be saved as current");
            // HasPeer becomes false before the disconnect callback runs.
            await Until(() => { lock (gate) return !receiveA.Enabled && !receiveB.Enabled
                && sinkA.Events.Contains("key:162:True") && sinkA.Events.Contains("button:right:False"); },
                "disconnect cleans the receiving role");

            approvedB = [aPin];
            await b.ConnectSecureAsync("127.0.0.1", aPin, port);
            await Until(() => a.HasPeer && b.HasPeer, "Windows connector reconnects");
            int before, targetCount;
            lock (gate) { before = sinkA.Events.Count; targetCount = messagesA + 2; }
            await Handoff(b, a.LocalDeviceId); b.PublishKeyInput(65, 30, false, false, "a"); await b.FlushAsync();
            await Until(() => { lock (gate) return messagesA >= targetCount; }, "post-reconnect packets arrive");
            lock (gate) Require(!receiveA.Enabled && sinkA.Events.Count == before, "reconnect does not rearm receiver input");

            // A second hello cannot silently mutate the established session.
            await a.SendDeviceHelloAsync();
            await Until(() => !a.HasPeer && !b.HasPeer, "duplicate authenticated hello ends the session");
            approvedB = [aPin with { CertificateSha256 = bPin.CertificateSha256 }];
            try { await b.ConnectSecureAsync("127.0.0.1", approvedB[0], port); throw new Exception("wrong server pin accepted"); }
            catch (Exception ex) when (ex is AuthenticationException or IOException) { }
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); approvedB = [aPin];
            try { await b.ConnectSecureAsync("127.0.0.1", aPin, port, cancelled.Token); throw new Exception("cancelled connection accepted"); }
            catch (OperationCanceledException) { }
            Require(!b.HasPeer, "wrong pin and cancelled connects never install a peer");
            a.Disconnect(); b.Disconnect();
            await b.StartListeningAsync(0);
            var bListener = (TcpListener)typeof(LanBridgePeer).GetField("_listener", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(b)!;
            var bPort = ((IPEndPoint)bListener.LocalEndpoint).Port;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                a.Disconnect(); b.Disconnect();
                await Task.WhenAll(ConnectOrClose(a, bPin, bPort), ConnectOrClose(b, aPin, port));
                await Until(() => a.HasPeer && b.HasPeer, "simultaneous outgoing connects converge on one live connection");
                await Task.Delay(100);
                Require(a.HasPeer && b.HasPeer, "opposite connection directions do not tear each other down");
            }
            Console.WriteLine("PASS: two protected Windows identities connect over pinned mutual TLS, exchange input in both roles, release on return/unpair, and remain paused after reconnect; wrong pins, duplicate hello and cancelled connects fail closed.");
        }
        finally
        {
            aIdentity.DeleteIdentity(aPath); bIdentity.DeleteIdentity(bPath);
            Directory.Delete(aPath); Directory.Delete(bPath); Directory.Delete(root);
        }
    }

    private static async Task Handoff(LanBridgePeer sender, string target)
    {
        var method = typeof(LanBridgePeer).GetMethod("SendMessageSafelyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var sent = (Task<bool>)method.Invoke(sender, ["cursor.handoff", new
        {
            linkId = "fixture", segmentPosition = 0.5, sourceDisplayId = sender.LocalDeviceId + ":desktop",
            sourceEdge = "right", sourcePosition = 0.5, sourceX = 1919, sourceY = 100,
            targetDeviceId = target, targetDisplayId = target + ":desktop", targetEdge = "left",
            targetPosition = 0.5, targetX = 0, targetY = 100
        }])!;
        Require(await sent, "handoff frame was sent");
    }
    private static async Task ConnectOrClose(LanBridgePeer peer, ApprovedPeer approved, int port)
    {
        try { await peer.ConnectSecureAsync("127.0.0.1", approved, port); }
        catch (IOException) { /* The non-preferred duplicate can close during TLS. */ }
    }
    private static async Task Until(Func<bool> test, string reason)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!test()) { if (DateTime.UtcNow > deadline) throw new Exception(reason); await Task.Delay(10); }
    }
    private static void Require(bool ok, string reason) { if (!ok) throw new Exception(reason); }
}
