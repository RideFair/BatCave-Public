# BatCave

One keyboard. Both computers. BatCave lets you move your mouse across a shared
screen edge and keep typing on another computer. Devices connect using mutual
TLS 1.3 and must approve each other before sharing input.

## Install

Get packaged downloads from [Releases](https://github.com/RideFair/BatCave-Public/releases).
Each release lists its supported platforms and remaining limitations.

| Platform | Installation |
| --- | --- |
| Windows 11 x64 | Download the Windows Setup executable, run it, and open BatCave. The installer includes .NET; no developer tools are needed. |
| macOS 13+ | Open the DMG and install the app as described in its release. A public Mac download is still pending; the [Mac source branch](https://github.com/RideFair/BatCave-Public/tree/codex/macos-secure-preview-20261010) provides the guided app and installer. |
| Linux | Install the `.deb` with your software installer, then open BatCave in your desktop session. A public Linux download is still pending; the [Linux source branch](https://github.com/RideFair/BatCave-Public/tree/codex/linux-secure-preview-20261010) targets Ubuntu 24.04 amd64. |

Open BatCave on both computers and follow **Permissions → Pair computers →
Arrange screens → Link devices**. Approve the device fingerprints, position your
screens, enable input on the receiving computer, and choose **Start sharing** on
Windows. **Pause sharing**, Escape, or F12 returns control to Windows.

The matching Mac and Linux changes are under review in
[Mac PR #5](https://github.com/RideFair/BatCave-Public/pull/5) and
[Linux PR #2](https://github.com/RideFair/BatCave-Public/pull/2).
Main currently contains the Windows controller and the older disabled Mac network
backend. Live secure Mac/Linux input and fresh-device setup still need verification
before a completed cross-platform release can be claimed. Linux currently
receives Windows input; it cannot control Windows.

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
