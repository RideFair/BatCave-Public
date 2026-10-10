# BatCave

One keyboard. Both computers. BatCave lets you move your mouse across a shared
screen edge and keep typing on another computer. Devices connect using mutual
TLS 1.3 and must approve each other before sharing input.

## Download BatCave

| Platform | Installer | Installation |
| --- | --- | --- |
| Windows 11 x64 | **[Download Windows 0.2.3 installer (.exe)](https://github.com/RideFair/BatCave-Public/releases/download/v0.2.3/BatCave-0.2.3-Windows-x64-Setup.exe)** | Run the installer and open BatCave. .NET is included. |
| Apple Silicon Mac, macOS 13+ | **[Download Mac 0.2.0 installer (.dmg)](https://github.com/RideFair/BatCave-Public/releases/download/v0.2.0/BatCave-0.2.0-arm64.dmg)** | Drag the versioned app to Applications and open it. An existing BatCave app stays separate. |
| Ubuntu 24.04 amd64 desktop | **[Download Linux 0.2.0 installer (.deb)](https://github.com/RideFair/BatCave-Public/releases/download/v0.2.0/BatCave-Linux-0.2.0-amd64.deb)** | Open the package with your software installer, then launch BatCave. |

Downloads are public; no GitHub account is required. Release notes and checksums:
[Windows 0.2.3](https://github.com/RideFair/BatCave-Public/releases/tag/v0.2.3),
[Mac/Linux 0.2.0](https://github.com/RideFair/BatCave-Public/releases/tag/v0.2.0).
Windows is unsigned; Mac is ad-hoc signed and not notarized. Physical fresh-device
setup and secure input/recovery checks remain incomplete; see the release notes.

## Link your computers

The updated source opens directly to nearby computers. Open BatCave on both
computers, choose the one you want to link, compare the eight digits, and choose
**Codes match · Link and share** on both. BatCave handles the connection and a
default screen arrangement. **Arrange screens** is available if you want to move
the other screen. Required system permission prompts explain what to allow.

**The 0.2.x downloads above still use the earlier manual pairing flow.** Nearby
setup is under cross-platform verification; matching new installers have not
been released. Existing paired identities are preserved by the updated source.
A new approval can start only the first verified connection, within 30 seconds.
Reconnecting later stays paused until you explicitly resume sharing.

Windows 0.2.3 can send or receive input; install it on both PCs for Windows-to-Windows.
The published Mac/Linux 0.2.0 apps receive Windows input. Their source roles need
updated native packages. Physical secure input and fresh-device setup still need
verification. WSL verifies builds and packages, but does not provide the desktop
permission and input checks required for Linux.

For source installation, see [Contributing](CONTRIBUTING.md#build-from-source).
The [monitor guide](Docs/Monitor_Layout.md) explains placement. Network file
transfer is not enabled; its [shared contract](Universal/Protocol/File_Transfer_Proposal.md)
is being developed.

## Help improve BatCave

Contributions are welcome. Open an [issue](https://github.com/RideFair/BatCave-Public/issues)
with a suggestion, or fork the repository and submit a pull request with your
improvement. [CONTRIBUTING.md](CONTRIBUTING.md) explains the workflow and checks.
Coding agents should also read [AGENTS.md](AGENTS.md).

## Security and privacy

See [SECURITY.md](SECURITY.md) for private vulnerability reporting and the trust
boundary. Private keys stay on their originating device. Clipboard sharing is
optional, and reconnecting never starts input by itself.

Local device state is excluded from source packages. Run
`python Scripts/check_public_source.py` before publishing; scan Git history for
secrets separately. Neither check guarantees the absence of every vulnerability.

## Maintainer and contact

BatCave is maintained by Tyler Harris. You can reach Tyler on X at
[@tyler_x_harris](https://x.com/tyler_x_harris) or [@ridefair](https://x.com/ridefair).
For security concerns, follow the private disclosure guidance in
[SECURITY.md](SECURITY.md).
