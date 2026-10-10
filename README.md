# BatCave

One keyboard. Both computers. BatCave lets you move your mouse across a shared
screen edge and keep typing on another computer. Devices connect using mutual
TLS 1.3 and must approve each other before sharing input.

## Install

Get packaged downloads from [Releases](https://github.com/RideFair/BatCave-Public/releases).
Each release lists its supported platforms and remaining limitations.
The combined 0.2.0 installers are not published yet; follow the release checks
below before distributing them.

| Platform | Installation |
| --- | --- |
| Windows 11 x64 | Download the Windows Setup executable, run it, and open BatCave. The installer includes .NET; no developer tools are needed. |
| macOS 13+ | Open the DMG, drag the versioned BatCave app to Applications, and open it. The versioned name keeps an existing BatCave app separate. |
| Ubuntu 24.04 amd64 desktop | Install the `.deb` with your software installer, then open BatCave in your application launcher. |

Open BatCave on both computers and follow **Permissions → Pair computers →
Arrange screens → Link devices**. Approve the device fingerprints, position your
screens, enable input on the receiving computer, and choose **Start sharing** on
Windows. **Pause sharing**, Escape, or F12 returns control to Windows.

This source includes the Windows controller and the matching secure Mac and Linux
apps. Live secure Mac/Linux input and fresh-device setup still need verification
before the combined binary release. Linux currently receives Windows input;
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
