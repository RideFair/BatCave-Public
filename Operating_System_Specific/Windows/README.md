# Windows agent

BatCave runs on Windows 11 x64. Build with
a patched .NET 10 SDK:

```powershell
dotnet run --project Operating_System_Specific/Windows/BatCaveWindowsAgent
```

The same four setup screens are used on Windows, macOS, and Linux: Permissions,
Pair computers, Arrange screens, and Link devices. Windows needs no additional
input permission. In Pair computers, choose your other computer's platform and
save the Windows connection file. Send it to that computer by email or USB and
import it in BatCave there. Export that computer's file, bring it back to Windows,
and choose Add computer. Existing files can skip the Windows export step;
copy/paste is available if you already have a way to share text.

Compare the full approval code (certificate fingerprint) with the originating
computer's Connection details before approving. Connection files contain public
identity information; private keys stay on their originating computers. A fresh
profile trusts no peer. Approval selects the new computer and reveals this PC's
local address in Pair computers. On another Windows PC, choose Connect from this
PC instead, enter that address, and choose Connect securely. Current Mac/Linux
packages connect from Arrange screens. Connect from only one computer, on the same
local network.

For two Windows PCs, install 0.2.3 on both. In Pair computers, set Keyboard and
mouse to This PC on the source and The other computer on the receiver. Arrange
screens on the source. In Link devices, choose Enable input on this PC on the
receiver, then Start sharing on the source. Windows remembers each approved
computer's role and outgoing address. Saving either never enables input.

The Mac/Linux 0.2.0 packages receive Windows input. Their native source roles need
updated packages; choosing a platform does not add capture support to an older app.

Pause, Escape, F12, disconnect, sleep, and session lock restore local control.
Reconnect does not automatically resume sharing. Text clipboard sharing and
starting at login are opt-in. Minimize the window to keep BatCave running.
Closing the window or choosing Quit stops sharing and exits. Windows can close
the app for an update; a cancelled update leaves sharing paused.

## Checks

```powershell
dotnet run --project Operating_System_Specific/Windows/BridgeRegressionTests -c Release
dotnet run --project Operating_System_Specific/Windows/SecureTransportTests -c Release
dotnet run --project Operating_System_Specific/Windows/ModifierRegressionTests -c Release
dotnet run --project Operating_System_Specific/Windows/TopologyRegressionTests -c Release
```

These checks cover transport, held-input cleanup, setup activation and monitor
routing. They do not replace live cross-platform tests. See
[packaging](Packaging/README.md) for installer checks.

The Windows peer test creates two isolated protected identities and exchanges
input over real pinned TLS in both roles. It routes an actual saved monitor
crossing into a recording input sink. UI dispatch tests cover queued input across
pause/re-enable and receiver navigation. No regression fixture injects input into
the user's desktop or replaces an installed app.

The source-only `--insecure-loopback-development` profile is for local regression
work. It binds only loopback and refuses remote destinations. Installed secure
packages do not include this profile.
