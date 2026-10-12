namespace BatCave.WindowsAgent;

internal static class FreshSetupBackup
{
    // Call only after the window, transport, identity and log have closed, while
    // holding the application's single-instance mutex. Keys remain in CNG.
    internal static string? Create(string directory)
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var parent = Path.GetDirectoryName(source);
        if (Path.GetFileName(source) != "SecurePreview" || string.IsNullOrEmpty(parent)
            || Path.GetPathRoot(parent) == parent)
            throw new IOException("The BatCave setup folder is not valid. Nothing was moved.");
        for (var path = source; path is not null; path = Path.GetDirectoryName(path))
        {
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                    throw new IOException("The BatCave setup folder uses a link or is not a folder. Nothing was moved.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        if (!Directory.Exists(source)) return null;
        var destination = Path.Combine(parent, "SecurePreview-backup-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")
            + "-" + Guid.NewGuid().ToString("N"));
        // One rename on the same volume: failure leaves the original in place.
        // No copying, key deletion or directory traversal is needed.
        Directory.Move(source, destination);
        return destination;
    }

    internal static void EnsureNoOtherAgent()
    {
        var agents = System.Diagnostics.Process.GetProcessesByName("BatCave.WindowsAgent");
        try
        {
            if (agents.Any(process => process.Id != Environment.ProcessId))
                throw new InvalidOperationException("Quit the other BatCave app before starting fresh. Your saved setup has not changed.");
        }
        finally { foreach (var process in agents) process.Dispose(); }
    }

    internal static bool Confirm(IWin32Window? owner, string directory) => MessageBox.Show(owner,
        "Start with no linked computers or saved screen arrangement?\n\n"
        + "BatCave will stop sharing and save your current setup in a backup folder under:\n"
        + Path.GetDirectoryName(directory)
        + "\n\nYour original device key stays on this PC, so the backup can be restored. System permissions will stay as they are.",
        "Start fresh", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.OK;
}
