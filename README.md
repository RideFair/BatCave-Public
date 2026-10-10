# BatCave

One keyboard. Both computers. BatCave lets you move your mouse across a shared
screen edge and keep typing on another computer. Devices connect using mutual
TLS 1.3 and must approve each other before sharing input.

## Download BatCave 0.2.0

| Platform | Installer | Installation |
| --- | --- | --- |
| Windows 11 x64 | **[Download Windows installer (.exe)](https://github.com/RideFair/BatCave-Public/releases/download/v0.2.0/BatCave-0.2.0-Windows-x64-Setup.exe)** | Run the installer and open BatCave. .NET is included. |
| Apple Silicon Mac, macOS 13+ | **[Download Mac installer (.dmg)](https://github.com/RideFair/BatCave-Public/releases/download/v0.2.0/BatCave-0.2.0-arm64.dmg)** | Drag the versioned app to Applications and open it. An existing BatCave app stays separate. |
| Ubuntu 24.04 amd64 desktop | **[Download Linux installer (.deb)](https://github.com/RideFair/BatCave-Public/releases/download/v0.2.0/BatCave-Linux-0.2.0-amd64.deb)** | Open the package with your software installer, then launch BatCave. |

Downloads are public and do not require a GitHub login.
[Release details](https://github.com/RideFair/BatCave-Public/releases/tag/v0.2.0)
include checksums and verification status. Windows is unsigned; Mac is ad-hoc
signed and not notarized. Signing and physical-device checks remain pending.

## Link your computers

Open BatCave on both computers and follow **Permissions → Pair computers →
Arrange screens → Link devices**. Approve the device fingerprints, position your
screens, enable input on the receiving computer, and choose **Start sharing** on
Windows. **Pause sharing**, Escape, or F12 returns control to Windows.

This source includes the Windows controller and the matching secure Mac and Linux
apps. Live secure Mac/Linux input and fresh-device setup still need verification
before a verified daily-use release. Linux currently receives Windows input;
it cannot control Windows. WSL verifies builds and packages, but does not provide
the desktop permission and input checks required for Linux.

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
