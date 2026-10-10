# Windows agent

BatCave runs on Windows 11 x64. Build with
a patched .NET 10 SDK:

```powershell
dotnet run --project Operating_System_Specific/Windows/BatCaveWindowsAgent
```

Export this computer's public pairing card and import the other device's card
through a trusted channel. Confirm the complete certificate fingerprint before
approval. A fresh profile trusts no peer. The selected approved peer must connect
using mutual TLS 1.3 on port 47871, then the user must choose Start sharing.
The same four setup screens are used on Windows, macOS, and Linux: Permissions,
Pair computers, Arrange screens, and Link devices. Windows needs no additional
input permission; use Continue, copy/paste or import the other device's card,
arrange the screen edge, and choose Start sharing. The Mac and Linux transport
changes are in their linked review branches until integrated.

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
