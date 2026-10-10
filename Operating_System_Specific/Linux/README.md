# Linux secure preview

This branch adds a Linux receiver for a Windows-controlled BatCave session.
The network and native input code run on Linux, and automated tests exercise
real mutual TLS and the real libei protocol against an isolated EIS service.
A supported live desktop and Windows/Linux input crossings still need testing.
The Windows CNG/SChannel transport and Linux/OpenSSL client passed a real mutual
TLS 1.3 handshake and identity-bound hello exchange through a local WSL interface.
This is a development preview, not a verified daily-use Linux release.

## Desktop requirements

Initial target: Ubuntu 24.04 or a compatible distribution with Python 3.12+,
GTK 4.12+, libei 1.2+, a desktop Secret Service keyring and a RemoteDesktop portal
version 2 that grants keyboard and pointer devices through `ConnectToEIS`.
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
sudo apt install ./.build/batcave-linux-preview_0.2.0~preview.2_amd64.deb
```

Packaging requires a clean committed source tree. It creates a fresh stage,
copies only tracked application source, and builds a Debian package for the
current architecture. It installs as **BatCave Secure Preview**, separately
from other BatCave versions. Per-user state stays under
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

The Devices screen follows the Mac preview's four steps: Permissions, Pair
computers, Arrange screens and Link devices. Each step shows one primary action.
Connection details remain optional, and Pause sharing stays accessible.

Grant Linux desktop access when prompted. Export the Linux public card and import
it into the Windows secure preview. Import Windows's card on Linux and explicitly
approve the full SHA-256 fingerprint shown by Windows. Enter the Windows preview's
local address on Arrange screens and choose **Connect securely**. Continue to
Link devices and choose **Enable Linux input**, then **Start sharing** on Windows.
Connecting and saving setup leave input paused until you enable it.
A fresh Windows handoff is required after enablement or reconnection.
The preview remembers the last chosen peer and address; it never saves permission
to enable input automatically.

Windows owns the shared monitor layout. Linux announces the coordinate regions
provided by its authorized input service, derives inclusive bounds from their
size and clamps movement to actual regions rather than empty monitor gaps.
The peer target remains `<approved-deviceId>:desktop`; no new shared layout or
hello extension is required. A return handoff releases held input immediately.
The Arrange screens diagram shows each computer as one desktop. Its left/right
choice is a saved guide to match on Windows; it does not modify the Windows
layout or imply control of individual monitors. Verified dimensions appear only
after desktop access and a secure hello. Save setup retains the chosen side,
approved peer and address without saving permission to activate input.

Pause disconnects and balances held keys/buttons. Desktop permission revocation,
input-device replacement, screen lock, sleep and application exit also stop
sharing. The preview does not automatically reconnect or re-enable input.
Physical keyboard scan codes use the Linux desktop's active keyboard layout;
arbitrary Unicode text injection and every international layout are unverified.
Clipboard sharing is disabled in this first Linux preview.

Linux-controlled Windows is a separate milestone: Windows currently drives the
input session. A Linux controller needs InputCapture portal support and matching
Windows receiver behavior before it can be claimed as supported.

## Files and source icons

The native Files shelf accepts local regular files and preserves their local
file icons in the shelf and native drag preview. It does not send files to a peer
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

Optional source previews accept only a static PNG at most 128 × 128 and 32 KiB.
Pixel size is checked before decoding, decompression is bounded and ancillary
metadata is stripped. Shared wire integration will match the agreed descriptor
and hash rules. Preview artwork belongs in BatCave's shelf/drag UI. A Linux file
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
disconnects and discards queued input. Session generations prevent old callbacks
from reviving input after disconnect. Keys, clipboard, certificates, tickets and
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
