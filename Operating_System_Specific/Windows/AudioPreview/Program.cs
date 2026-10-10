using System.Drawing.Imaging;
using BatCave.WindowsAgent;

namespace BatCave.AudioPreview;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if ((args.Length is 2 or 3) && args[0] == "--render-preview")
        {
            // Synthetic UI only: no identity, listener, device discovery, capture or playback.
            using var preview = new AudioPreviewForm();
            if (args.Length == 3 && args[2] == "trust") preview.ShowTrustPreview();
            preview.Show(); Application.DoEvents();
            using var bitmap = new Bitmap(preview.Width, preview.Height);
            preview.DrawToBitmap(bitmap, preview.ClientRectangle with { Width = preview.Width, Height = preview.Height });
            bitmap.Save(Path.GetFullPath(args[1]), ImageFormat.Png);
            return;
        }
        // Process exclusion cannot protect against another preview process playing
        // a received mix. Permit one audio preview per Windows session.
        using var instance = new Mutex(true, "Local\\BatCave.AudioPreview", out var firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("BatCave Audio Preview is already running in this Windows session.", "BatCave Audio Preview", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatCave", "AudioPreview");
            var settings = AudioPreviewSettings.Load(directory);
            using var identity = DeviceIdentityStore.OpenOrCreate(directory);
            var approvals = new ApprovedPeerStore(directory);
            var platform = new WindowsAudioPlatform();
            var inventory = platform.Enumerate(identity.DeviceId, settings.DisplayName, settings.IsPrimary, 1);
            using var network = new AudioPeerNetwork(identity.DeviceId, identity.Certificate, () => approvals.Peers, () => inventory);
            using var coordinator = new AudioCoordinator(inventory, network, platform);
            using var form = new AudioPreviewForm(identity, approvals, network, coordinator, settings, directory,
                (name, primary) =>
                {
                    var candidate = platform.Enumerate(identity.DeviceId, name, primary, inventory.Revision + 1);
                    if (candidate.DisplayName == inventory.DisplayName && candidate.DeviceIsPrimary == inventory.DeviceIsPrimary
                        && candidate.Outputs.SequenceEqual(inventory.Outputs)) return;
                    inventory = candidate; coordinator.SetLocalInventory(candidate); network.RefreshInventory();
                });
            Application.Run(form);
        }
        catch (Exception)
        {
            MessageBox.Show("The audio preview could not start. Check that Windows 11 is installed and that the preview profile is writable. If an existing identity is unavailable, restore it or explicitly re-pair; it has not been replaced.", "BatCave Audio Preview", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { instance.ReleaseMutex(); }
    }
}
