# BatCave

One keyboard. Both computers. Move your mouse across a shared screen edge and
keep typing on another computer. Windows, Mac and Linux can each supply the
keyboard and mouse or receive input from a linked computer.

## Download BatCave 0.3.0

| Your computer | Download | Install |
| --- | --- | --- |
| Windows 11 x64 | **[Windows installer (.exe)](https://github.com/RideFair/BatCave-Public/releases/download/v0.3.0/BatCave-0.3.0-Windows-x64-Setup.exe)** | Run the installer, then open BatCave. .NET is included. |
| Apple Silicon Mac, macOS 13+ | **[Mac installer (.dmg)](https://github.com/RideFair/BatCave-Public/releases/download/v0.3.0/BatCave-0.3.0-arm64.dmg)** | Open the disk image, drag BatCave-0.3.0 to Applications, then open it. |
| Ubuntu 24.04 amd64 desktop | **[Linux installer (.deb)](https://github.com/RideFair/BatCave-Public/releases/download/v0.3.0/BatCave-Linux-0.3.0-amd64.deb)** | Open with your software installer, or run `sudo apt install ./BatCave-Linux-0.3.0-amd64.deb` in its download folder. |

No GitHub account is needed. [Release notes and checksums](https://github.com/RideFair/BatCave-Public/releases/tag/v0.3.0)
include the verification record. Windows is unsigned; Mac is ad-hoc signed and
not notarized. Physical fresh-device input and recovery testing remains pending.
On Mac, use **Open Anyway** for this app in System Settings → Privacy & Security
if macOS blocks it; keep Gatekeeper enabled.

## Link your computers

Open BatCave on both computers on the same local network. Select the nearby
computer, compare the eight digits, then choose **Codes match · Link and share**
on both screens. No connection files or network addresses are needed.

The computer that starts pairing supplies the keyboard and mouse. BatCave places
the other screen on its right; **Arrange screens** lets you match your desk.
Approve the system permissions when asked. You can change input roles later.
If permission setup takes longer than the initial approval window, choose
**Start sharing** (or **Enable input** on the receiver). Reconnect stays paused
until you resume sharing on both computers.

Use 0.3.0 on both computers. Existing pairings survive upgrades. For a first-time
setup test on a computer that already used BatCave, see the short
[clean installation guide](Docs/Clean_Installation.md). Quit older copies before
opening this one; uninstalling an old package is optional and keeps its saved setup.

Linux needs a full desktop with the appropriate input portal and an unlocked
keyring. The app explains missing access. WSL can check builds but cannot verify
desktop input; see the [Linux requirements](Operating_System_Specific/Linux/README.md#desktop-requirements).
Network file transfer and shared audio are not included in this release.

For source builds, see [Contributing](CONTRIBUTING.md#build-from-source).
The [monitor guide](Docs/Monitor_Layout.md) explains screen placement.

## Help improve BatCave

Contributions are welcome. Open an [issue](https://github.com/RideFair/BatCave-Public/issues)
with a suggestion, or fork the repository and submit a pull request with your
improvement. [CONTRIBUTING.md](CONTRIBUTING.md) explains the workflow and checks.
Coding agents should also read [AGENTS.md](AGENTS.md).

## Security and privacy

Devices approve each other before sharing input over pinned mutual TLS 1.3.
Private keys stay on their originating device. See [SECURITY.md](SECURITY.md)
for the trust boundary and private vulnerability reporting. Clipboard sharing
is optional where supported; reconnecting never starts input by itself.

Local device state is excluded from source packages. Run
`python Scripts/check_public_source.py` before publishing; scan Git history for
secrets separately. These checks are not a guarantee that every vulnerability
has been found.

## Maintainer and contact

BatCave is maintained by Tyler Harris. You can reach Tyler on X at
[@tyler_x_harris](https://x.com/tyler_x_harris) or [@ridefair](https://x.com/ridefair).
For security concerns, follow the private disclosure guidance in
[SECURITY.md](SECURITY.md).
