using BatCave.WindowsAgent;

internal static partial class ModifierRegression
{
    private static void CheckFreshSetup()
    {
        var root = Path.Combine(Path.GetTempPath(), "BatCaveFreshSetup-" + Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(root, "SecurePreview");
        Directory.CreateDirectory(root);
        try
        {
            Require(FreshSetupBackup.Create(profile) is null, "empty setup needs no backup");
            Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile, "identity.json"), "synthetic key reference");
            File.WriteAllText(Path.Combine(profile, "approved-peers.json"), "synthetic pins");
            using (var locked = new FileStream(Path.Combine(profile, "identity.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var failed = false;
                try { FreshSetupBackup.Create(profile); } catch (IOException) { failed = true; }
                Require(failed && Directory.Exists(profile) && Directory.GetDirectories(root).Length == 1,
                    "an in-use profile cannot be partially moved");
            }
            var backup = FreshSetupBackup.Create(profile)!;
            Require(!Directory.Exists(profile) && Path.GetDirectoryName(backup) == root, "backup is an atomic sibling rename");
            Require(File.ReadAllText(Path.Combine(backup, "identity.json")) == "synthetic key reference"
                && File.ReadAllText(Path.Combine(backup, "approved-peers.json")) == "synthetic pins", "backup preserves identity and pins exactly");
            Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile, "identity.json"), "second synthetic identity");
            var second = FreshSetupBackup.Create(profile)!;
            Require(backup != second && File.ReadAllText(Path.Combine(backup, "identity.json")) == "synthetic key reference",
                "starting fresh twice never overwrites the first backup");
            Directory.Move(backup, profile);
            Require(File.ReadAllText(Path.Combine(profile, "approved-peers.json")) == "synthetic pins", "original setup can be restored intact");
            var rejected = false;
            try { FreshSetupBackup.Create(root); } catch (IOException) { rejected = true; }
            Require(rejected && Directory.Exists(profile), "unexpected folder cannot be moved");
            File.Delete(Path.Combine(profile, "identity.json")); File.Delete(Path.Combine(profile, "approved-peers.json"));
            Directory.Delete(profile);
            File.WriteAllText(profile, "not a directory");
            rejected = false;
            try { FreshSetupBackup.Create(profile); } catch (IOException) { rejected = true; }
            Require(rejected && File.ReadAllText(profile) == "not a directory", "file in place of profile is preserved and rejected");
            File.Delete(profile);
            File.Delete(Path.Combine(second, "identity.json")); Directory.Delete(second);
        }
        finally { Directory.Delete(root); }
        WithPairingFixture((window, _, _, _) =>
        {
            var agent = (LocalBridgeAgent)Get(window, "_agent");
            Set(agent, "_bridgeActive", true); Set(window, "_sharing", true);
            Call(window, "RequestFreshStart");
            Require(window.FreshStartRequested && window.IsDisposed && agent.IsDisposed && !agent.IsBridgeActive,
                "fresh setup request closes input and transport before profile backup");
        });
        Console.WriteLine("PASS: recoverable fresh setup preserves identities and pins, handles rename failure, and stops input first.");
    }
}
