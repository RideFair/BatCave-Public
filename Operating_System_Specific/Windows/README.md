# Windows agent

The default app is the secure desktop preview for Windows 11 x64. Build with
a patched .NET 10 SDK:

```powershell
dotnet run --project Operating_System_Specific/Windows/BatCaveWindowsAgent
```

Export this computer's public pairing card and import the other device's card
through a trusted channel. Confirm the complete certificate fingerprint before
approval. A fresh profile trusts no peer. The selected approved peer must connect
using mutual TLS 1.3 on port 47871, then the user must choose Start sharing.
The matching secure Mac transport has not been released in this snapshot.

Pause, Escape, F12, disconnect, sleep, and session lock restore local control.
Reconnect does not automatically resume sharing. Text clipboard sharing and
starting at login are opt-in. Closing the window leaves the app in the tray;
Quit exits it.

## Checks

```powershell
dotnet run --project Operating_System_Specific/Windows/BridgeRegressionTests -c Release
dotnet run --project Operating_System_Specific/Windows/SecureTransportTests -c Release
dotnet run --project Operating_System_Specific/Windows/ModifierRegressionTests -c Release
```

These checks cover transport authentication, framing, reconnect/session cleanup,
and production modifier forwarding through loopback sockets. They do not replace
live Windows/macOS tests. See [packaging](Packaging/README.md) for installer checks.

The source-only `--insecure-loopback-development` profile is for local regression
work. It binds only loopback and refuses remote destinations. Installed secure
packages do not include this profile.
