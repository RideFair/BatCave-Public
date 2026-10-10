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
local address in Pair computers. Enter that address in Arrange screens on the
other computer and choose Connect securely. Both must use the same local network.
Then arrange your screens, enable input on the receiver, and choose Start sharing
on Windows. The matching Mac and Linux secure transports are included in this source.

Pause, Escape, F12, disconnect, sleep, and session lock restore local control.
Reconnect does not automatically resume sharing. Text clipboard sharing and
starting at login are opt-in. Closing the window leaves the app in the tray;
Quit exits it.

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

The source-only `--insecure-loopback-development` profile is for local regression
work. It binds only loopback and refuses remote destinations. Installed secure
packages do not include this profile.
