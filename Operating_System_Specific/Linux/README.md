# BatCave for Linux

Linux can select a Windows, Mac or Linux peer and serve as input source or
receiver. Both connection directions use pinned mutual TLS 1.3. Automated tests
route passive libei compositor events over real Linux/Linux sockets into isolated
native receiving devices. Physical desktop permissions and crossings still need testing.
The Windows CNG/SChannel transport and Linux/OpenSSL client passed a real mutual
TLS 1.3 handshake and identity-bound hello exchange through a local WSL interface.
This build remains under development. A verified daily-use Linux release still
requires the live desktop checks below.

## Desktop requirements

Initial target: Ubuntu 24.04 or a compatible distribution with Python 3.12+,
GTK 4.12+, libei 1.2+ and a desktop Secret Service keyring. Receiving requires
RemoteDesktop portal version 2 with authorized keyboard and pointer devices.
Using Linux's keyboard and mouse requires InputCapture portal version 1 or 2,
relative pointer capture and keyboard modifier maps through passive `ConnectToEIS`.
The source action remains unavailable when its prerequisites are absent.
GNOME and KDE session support must be checked on the actual target installation;
distribution name alone does not prove that its portal backend supports input.

The app runs as your normal desktop user. It does not read raw input devices,
use uinput, install a privileged daemon, change firewall rules or fall back to
plaintext transport. Missing permissions or session services leave sharing disabled.
WSL can run the tests and display GTK setup, but does not prove native Wayland
permission, focus or lifecycle behavior.

## Build and install

On Ubuntu, install build and runtime dependencies:

```sh
sudo apt install build-essential pkg-config libei-dev libeis-dev python3 python3-gi \
  python3-cryptography gir1.2-gtk-4.0 gir1.2-secret-1
cd Operating_System_Specific/Linux
sh scripts/test.sh
sh scripts/package.sh
sudo apt install ./.build/BatCave-Linux-0.2.2-amd64.deb
```

Packaging requires a clean committed source tree. It creates a fresh stage,
copies only tracked application source, and builds a Debian package for the
current architecture. It appears in the application launcher as **BatCave**.
This branch's app and Debian package version are **0.2.2**. Its build does not
replace the frozen 0.2.1 candidate. The existing `batcave-linux-preview`
package and command names remain for upgrade compatibility. Installing upgrades that package,
separate from other BatCave installations. Per-user state stays under
`$XDG_DATA_HOME/batcave/secure-preview`, defaulting to
`~/.local/share/batcave/secure-preview`. Removal leaves identity and approvals
available for a later reinstall; it does not reset trusted peers silently.

Use `batcave-linux-preview --diagnose` to check prerequisites without creating an
identity, connecting to a peer or requesting input permission. To run from source,
build first and use `python3 -m batcave_linux` in this directory.

CI installs and removes the Debian package on a disposable Ubuntu runner, checks
the installed version and confirms that removal preserves existing user state.
This checks packaging; a fresh physical desktop's first-run keyring and permission
dialogs still need testing. Only amd64 has been tested so far.

## Pair and share

The Devices screen follows the Mac app's four steps: Permissions, Pair
computers, Arrange screens and Link devices. Each step shows one primary action.
Connection details remain optional, and Pause sharing stays accessible.

In Permissions, choose **Receive input here** or **Use this keyboard and mouse**,
then grant the desktop access for that role. Neither role selection nor consent
starts sharing. In Pair computers, choose the other operating system and exchange
connection files. Compare the complete SHA-256 fingerprint on the other computer
before approving. The OS choice only changes instructions; it grants no trust.
Private keys stay on their originating computers. Both peers must approve each other.

On Arrange screens, enter the other computer's local network address and choose
**Connect securely**, or choose **Wait for the other computer**. The connector
also accepts the selected approved peer on the same local port. Connection details
lets you change the port; both computers must agree. The Linux GUI defaults to
47870, matching current Windows. Only the selected approved peer can connect.
When both computers initiate, the lower ordinal device ID's outgoing socket wins;
replacement closes old ownership and requires fresh local input approval.

Enable input on the receiver, then choose **Start sharing** on the source. Linux
source control begins only when its compositor activates the configured left/right
barrier and a matching EIS activation sequence arrives. F12 pauses source sharing.
The source computes return crossings; target-away handoffs release receiving input.
Linux uses the existing `<peerId>:desktop` target and existing v0 input messages.
It sends no persistent `control.testStart`. Receiver handoff coordinates are
materialized from the edge and normalized position in the current desktop.
Native receiving input clamps to real regions, and source return placement avoids gaps.

Pause, role changes and lifecycle interruptions release input and invalidate
queued actions. Source Pause closes its capture grant, so starting again needs
fresh desktop consent. Reconnect does not rearm either role. Saved setup retains
preferences only. Linux does not automatically retry a lost connection yet.

Physical keys and logical held modifiers use the existing Windows-format payloads.
Boundary dragging, every international layout and lock-key state synchronization
remain physical QA gates. Clipboard sharing is disabled. Linux source control
requires a peer version with receiving behavior; older Windows releases only source input.

## Files and source icons

The native Files shelf accepts local regular files and preserves their local
file icons in the shelf and native drag image. It does not send files to a peer
yet. Copy-only network transfer waits for the common capability-negotiated bulk
contract owned by Windows; Linux does not advertise file transfer prematurely.
An arbitrary file-manager drag across a computer boundary also needs a later
native drag-session adapter. A local shelf is not proof of that behavior.

The shelf is bounded to 32 files and 4 GiB in total. The receiving core already
enforces ordered 32 KiB chunks, a 1 GiB file limit, 240-byte portable names,
private destination-relative staging, running and stored-file SHA-256 checks,
descriptor identity validation, atomic non-overwrite completion and cancellation cleanup. The receiver chooses
the destination and must accept before staging starts. No file is opened or
executed automatically. A destination must be owned by the current user and
disallow group/public writes; symlink destinations are refused.

Optional source icon images accept only a static PNG at most 128 × 128 and 32 KiB.
Pixel size is checked before decoding, decompression is bounded and ancillary
metadata is stripped. Shared wire integration will match the agreed descriptor
and hash rules. Source icon artwork belongs in BatCave's shelf/drag UI. A Linux file
manager may still display its own file-type icon; universal persistent custom
file icons are not promised. Original file bytes stay unchanged.

## Identity protection and release gates

The Linux identity is a self-signed P-256 TLS certificate. Its PKCS8 key is
encrypted on disk; the unlock secret lives in the desktop keyring. OpenSSL loads
the decrypted key through an anonymous memfd rather than a plaintext key file.
This is a software key, not a non-extractable hardware-backed identity. Processes
that compromise the same desktop account are outside this trust boundary.
Missing keyring secrets or invalid stored identities fail closed without replacement.

TLS is restricted to version 1.3, requires client and server certificates and
trusts only the approved leaf. A separate constant-time SHA-256 check enforces
the pin; certificate validity and the five-second hello bind the device ID.
Frames are bounded to 256 KiB and the input queue to 512 frames/1 MiB; overflow
disconnects and discards queued input. Socket and local enable generations prevent old callbacks
from reviving input after disconnect or Pause. Keys, clipboard, certificates, tickets and
local addresses are not logged. Startup disables core dumps.

Before a release, test a real supported desktop's consent/revocation, keyboard
layout, multi-monitor scaling, physical interruption, lock/sleep, held-input
cleanup and Windows/Linux crossings. Test a clean physical desktop's setup and
the agreed file-transfer path.
Security/core tests and a GTK screenshot do not substitute for those checks.

## Optional Windows/Linux transport check

`tests/windows_interop` links the existing Windows identity and TLS transport
source without modifying it. Build its project with the Windows .NET 10 SDK.
Run the resulting DLL with a fresh empty exchange directory and the Windows
interface address reachable from the Linux test environment. Then run
`linux_peer.py <exchange-directory> <windows-address>` on Linux, with this Linux
source directory on `PYTHONPATH`. The exchange directory must be reachable by
both test processes; the supplied listener address binds only that interface and
the port is chosen by the OS. Do not expose the test listener to an untrusted network.

The test exchanges disposable public cards, uses a temporary encrypted Linux
identity and deletes its temporary Windows certificate and CNG key on exit.
It times out after 30 seconds, sends no input and never opens an installed app's
profile. Keep its exchange directory out of source archives. This optional test
is separate from automated EIS/portal fixtures and live compositor verification.
