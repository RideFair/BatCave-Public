# Windows preview installer

The installer targets Windows 11 x64, includes .NET 10, and installs for the
current user. It is an unsigned development preview. The Mac secure transport
and live cross-platform recovery tests remain release gates.

The preview has its own installer identity, installation directory, and
`BatCave/SecurePreview` state. It does not replace the existing development app or
load its topology. Keep the working app installed; quit its Windows agent for a
live sharing test, then reopen it afterward. Pairing/setup can be inspected while
the working app remains running.

Use a patched .NET 10 SDK and an official Inno Setup 6 compiler:

```powershell
.\Operating_System_Specific\Windows\Packaging\Build-SecurePreview.ps1 `
    -InnoCompiler 'C:\Tools\Inno Setup 6\ISCC.exe'
```

The script runs the three Windows regression suites and publishes into a fresh
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
