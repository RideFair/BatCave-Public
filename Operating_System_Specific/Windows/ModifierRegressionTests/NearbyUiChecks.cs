using System.Net;
using BatCave.WindowsAgent;

internal static partial class ModifierRegression
{
    private static void CheckNearbyCancellation()
    {
        WithPairingFixture((window, approvals, first, _) =>
        {
            var identity = (DeviceIdentityStore)Get(window, "_identity");
            using var service = new NearbyPairingService(identity, new(identity.ExportPublicCard("Synthetic local computer"), 47871),
                _ => true, (_, _) => Task.FromResult(false), (_, _) => Task.CompletedTask, IPAddress.IsLoopback);
            service.Start(IPAddress.Loopback, 0); service.OpenWindow(); Set(window, "_pairing", service);
            window.ShowStep(1);
            using var request = new CancellationTokenSource();
            var pending = (Task<bool>)Call(window, "ConfirmNearbyAsync", new NearbyPairingProtocol.Confirmation(new(first, 47871), "1234 5678", true), request.Token)!;
            Application.DoEvents();
            Require(Get(window, "_comparison") is not null && !pending.IsCompleted, "approval waits for the current local comparison");
            window.ShowStep(2); Application.DoEvents();
            Require(pending.Wait(TimeSpan.FromSeconds(1)) && !pending.Result, "navigation declines a pending comparison");
            Require(!service.IsOpen && Get(window, "_comparison") is null && approvals.Peers.Length == 0, "navigation closes pairing without persisting a pin");

            using var stale = new CancellationTokenSource(); stale.Cancel();
            var save = (Task)Call(window, "CompleteNearbyAsync", new NearbyPairingService.Result(new(first, 47871), IPAddress.Loopback, true), stale.Token)!;
            Application.DoEvents();
            Require(save.IsCanceled && approvals.Peers.Length == 0, "a cancelled queued completion cannot save trust or create consent");
            Require(Get(window, "_initialSharingConsent") is null, "stale completion grants no sharing consent");

            Call(window, "ApplyApproval", first); window.ShowStep(1);
            Require(((Button)Get(window, "_retryLink")).Visible, "an incomplete link offers an explicit fresh-code retry");
            ((Button)Get(window, "_retryLink")).PerformClick(); Application.DoEvents();
            Require((bool)Get(window, "_addingComputer") && ((Button)Get(window, "_back")).Visible
                && ((Button)Get(window, "_back")).Text == "Cancel" && service.IsOpen, "retry returns to nearby selection with visible cancellation");
            ((Button)Get(window, "_back")).PerformClick(); Application.DoEvents();
            Require(!(bool)Get(window, "_addingComputer") && !service.IsOpen && approvals.Peers.Length == 1,
                "cancelling before the code appears closes pairing and preserves the existing approval");
            var grant = new InitialSharingConsent(first.DeviceId, first.CertificateSha256, false, 0, DateTimeOffset.UtcNow.AddSeconds(30));
            Set(window, "_initialSharingConsent", grant); Call(window, "UpdateSetup");
            Require(((Button)Get(window, "_pause")).Visible && ((Button)Get(window, "_pause")).Enabled, "pending initial sharing can be paused before connecting");
            ((Button)Get(window, "_pause")).PerformClick(); Application.DoEvents();
            Require(Get(window, "_initialSharingConsent") is null, "Pause revokes initial consent");
            Set(window, "_initialSharingConsent", grant); ((ComboBox)Get(window, "_role")).SelectedIndex = 1;
            Require(Get(window, "_initialSharingConsent") is null, "role change revokes initial consent");
            Set(window, "_initialSharingConsent", grant); window.ShowStep(2);
            Require(Get(window, "_initialSharingConsent") is null, "navigation revokes initial consent");
            Set(window, "_initialSharingConsent", grant); Call(window, "ReleaseAndDisconnect", "sleep fixture");
            Require(Get(window, "_initialSharingConsent") is null, "sleep revokes initial consent");
            Require(!((LocalBridgeAgent)Get(window, "_agent")).IsBridgeActive && !((LocalBridgeAgent)Get(window, "_agent")).IsReceivingEnabled,
                "cancelled requests and saved approval never arm input");
        });
        Console.WriteLine("PASS: nearby UI cancellation and stale callbacks cannot save trust; initial sharing consent is revoked by Pause, navigation, role change and sleep.");
        CheckReplacedHello();
        CheckDiscoveryPersistence();
    }

    private static void CheckReplacedHello()
    {
        WithPairingFixture((window, _, first, _) =>
        {
            Call(window, "ApplyApproval", first); Application.DoEvents();
            ((System.Windows.Forms.Timer)Get(window, "_timer")).Stop();
            var lan = (LanBridgePeer)Get(window, "_lan");
            using var writer = new StreamWriter(Stream.Null);
            Set(lan, "_writer", writer); Set(lan, "_peerDeviceId", first.DeviceId);
            Set(lan, "_sessionGeneration", 1L);
            var hello = new DeviceHelloPayload(first.DeviceId, first.DisplayName, "Windows", "Synthetic test", new(0, 0, 1920, 1080, 1919, 1079));
            ((Action<DeviceHelloPayload>)Get(lan, "PeerHello"))(hello);
            // Replace the connection before its queued UI notification runs.
            Set(lan, "_sessionGeneration", 3L);
            var grant = new InitialSharingConsent(first.DeviceId, first.CertificateSha256, false, 2, DateTimeOffset.UtcNow.AddSeconds(30));
            Set(window, "_initialSharingConsent", grant);
            Application.DoEvents();
            Require(Get(window, "_hello") is null, "a replaced connection's queued hello cannot supply current geometry");
            Require(ReferenceEquals(Get(window, "_initialSharingConsent"), grant), "a stale hello cannot consume a newer approval");
            ((Action<DeviceHelloPayload>)Get(lan, "PeerHello"))(hello); Application.DoEvents();
            Require(ReferenceEquals(Get(window, "_hello"), hello) && Get(window, "_initialSharingConsent") is null,
                "the current hello supplies geometry and consumes initial consent once");
            using var agent = new LocalBridgeAgent(lan, inspectionOnly: true, inputSink: new RecordingInput());
            _ = agent.Handle;
            agent.EnableReceiving(1);
            Require(!agent.IsReceivingEnabled, "approval for an old connection cannot enable the replacement receiver");
            agent.EnableReceiving(3);
            Require(agent.IsReceivingEnabled, "explicit approval enables its exact current connection");
            var generation = (long)Get(agent, "_inputGeneration");
            agent.EnableReceiving(1); agent.PostStartPersistentHandoff(1); Application.DoEvents();
            Require(agent.IsReceivingEnabled && !agent.IsBridgeActive && (long)Get(agent, "_inputGeneration") == generation,
                "stale activation cannot stop current receiving or queue source sharing on a replacement");
            agent.Close();
        });
        Console.WriteLine("PASS: delayed connection notifications and activation cannot change geometry, consume newer approval, or enable a replacement session.");
    }

    private static void CheckDiscoveryPersistence()
    {
        WithPairingFixture((window, _, first, _) =>
        {
            Call(window, "ApplyApproval", first);
            ((System.Windows.Forms.Timer)Get(window, "_timer")).Stop();
            ((TextBox)Get(window, "_host")).Text = "192.0.2.10";
            Call(window, "SaveSettings");
            using var discovery = new NearbyDiscovery("windows:local-fixture", () => []);
            Set(window, "_discovery", discovery);
            discovery.Remember(new(first.DeviceId, first.DisplayName, "windows", first.CertificateSha256,
                IPAddress.Parse("192.0.2.99"), 49999, 47873, false, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);
            // Exercise the timer's cache refresh only; it starts no socket or input hook.
            Set(window, "_inspectionOnly", false);
            try { Call(window, "RefreshNearby"); }
            finally { Set(window, "_inspectionOnly", true); }
            ((ComboBox)Get(window, "_role")).SelectedIndex = 1;
            var saved = File.ReadAllText((string)Get(window, "_settingsPath"));
            Require(saved.Contains("192.0.2.10") && !saved.Contains("192.0.2.99") && !saved.Contains("49999"),
                "changing a setting cannot persist an unverified discovery address or port");
            Require(((string, int))Call(window, "ConnectionTarget", first)! == ("192.0.2.99", 49999),
                "a fresh candidate is available for pinned verification without saving it");
            var candidate = (NearbyComputer)Get(window, "_connectionCandidate");
            Set(window, "_connectionCandidate", candidate with { Seen = DateTimeOffset.UtcNow.AddSeconds(-13) });
            Require(((string, int))Call(window, "ConnectionTarget", first)! == ("192.0.2.10", 47871),
                "expired discovery falls back to the verified endpoint");
        });
        Console.WriteLine("PASS: discovery candidates cannot overwrite a verified saved endpoint through unrelated settings changes.");
    }
}
