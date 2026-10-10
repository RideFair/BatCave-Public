# Windows installer

The installer targets Windows 11 x64, includes .NET 10, and installs for the
current user. The installer is unsigned. Clean-device installation and live
cross-platform recovery checks remain required before release approval.

The app retains its existing installer identity and `BatCave/SecurePreview` state
for compatible upgrades. New installations use the separate BatCave Desktop
directory; upgrades reuse their existing directory. Existing development apps
remain separate. Quit any other Windows agent before starting sharing.

Use a patched .NET 10 SDK and an official Inno Setup 6 compiler:

```powershell
.\Operating_System_Specific\Windows\Packaging\Build-Installer.ps1 `
    -InnoCompiler 'C:\Tools\Inno Setup 6\ISCC.exe'
```

The script runs all four Windows regression suites and publishes into a fresh
directory before creating an installer, SHA-256 sidecar, and source manifest.
Release builds omit debug symbols and map compiler paths to a generic source root.
Packaging excludes local pairing state and keys. Never add private state to a
publish directory. Review the binary package separately from the source tree.

`BatCave.WindowsAgent.exe --verify-install` checks the bundled host/runtime without
creating an identity or intercepting input. Rebuild self-contained packages when
Microsoft ships relevant runtime fixes. Follow Inno Setup's licensing terms for
the intended distribution.

Allow only the installed BatCave executable on the appropriate firewall profile
when Windows prompts. The installer creates no firewall exception. Pairing is
explicit, and additional devices receive their own identities when installed.
State is stored under `%LOCALAPPDATA%\BatCave\SecurePreview`; uninstall preserves
that state for reinstall and removes the app's optional login entry.
