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
    }
}
