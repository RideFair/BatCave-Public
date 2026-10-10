# Windows agent

BatCave runs on Windows 11 x64. Build with
a patched .NET 10 SDK:

```powershell
dotnet run --project Operating_System_Specific/Windows/BatCaveWindowsAgent
```

## Link your computers

Open BatCave on both computers on the same local network. Choose the other
computer from Nearby computers, compare the eight digits on both screens, and
choose **Codes match · Link and share** on each. The computer that started linking
supplies the keyboard and mouse. BatCave connects automatically and places the
other screen on its right; **Arrange screens** changes that placement when needed.
Windows needs no extra input permission. Allow BatCave on your private network if
Windows requests network access.

Nearby linking requires matching updated builds on both devices. Published 0.2.x
installers predate this flow. This branch is being checked across Windows, Mac and
Linux before new installers are released. Existing identities and approved peers
are preserved. Older peers without discovery can still connect using their saved
approved connection settings.

The approval starts only the first verified connection, within 30 seconds. A
cancelled or interrupted request cannot start input later. If initial connection
fails or a linked computer reconnects, choose the explicit sharing action to
resume. There is no automatic input authorization from a saved profile.

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

These checks cover nearby pairing, pinned transport, held-input cleanup, explicit
sharing consent and monitor routing. They do not replace live cross-platform tests. See
[packaging](Packaging/README.md) for installer checks.

Shared pairing vectors live in [Universal/Protocol](../../Universal/Protocol).
The nearby service tests hold both approval prompts open, prove nothing is saved
after one approval, and authenticate the resulting identities on the existing
mutual TLS input channel. They also check tampered commitments, signatures,
discovery bounds and cancellation.

The Windows peer test creates two isolated protected identities and exchanges
input over real pinned TLS in both roles. It routes an actual saved monitor
crossing into a recording input sink. UI dispatch tests cover queued input across
pause/re-enable and receiver navigation. No regression fixture injects input into
the user's desktop or replaces an installed app.

The source-only `--insecure-loopback-development` profile is for local regression
work. It binds only loopback and refuses remote destinations. Installed secure
packages do not include this profile.
