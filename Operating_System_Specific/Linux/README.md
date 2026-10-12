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
use uinput, install a privileged daemon, change firewall rules or weaken the
authenticated input transport. Missing permissions leave sharing disabled.
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
sudo apt install ./.build/BatCave-Linux-0.3.0-amd64.deb
```

Packaging requires a clean committed source tree. It creates a fresh stage,
copies only tracked application source, and builds a Debian package for the
current architecture. It appears in the application launcher as **BatCave**.
This branch's app and Debian package version are **0.3.0**. Its build does not
replace frozen 0.2.x candidates. The existing `batcave-linux-preview`
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

The app opens to **Nearby computers**, just like Windows and Mac. Open BatCave
on both devices, select a computer, compare all eight digits, then choose
**Codes match · Link and share** on both screens. No connection file or network
address is required. Names found on the network are untrusted until comparison
and approval succeed. Changed saved pins require explicit removal first.

The initiating computer supplies input; the other screen starts on its right.
Permissions and screen arrangement are optional navigation. Linux requests the
appropriate desktop portal consent when needed. Source control is unavailable
on desktops without InputCapture support; the app explains how to choose receive
input instead. Setup never bypasses an OS permission request.

Link-and-share consent remains in memory for at most 30 seconds. It applies only
once, to the exact approved peer, pin and role on the first authenticated input
session. It expires on navigation, Pause, role change, disconnect/replacement or
lifecycle interruption. If permission takes longer, choose the explicit local
sharing action after allowing access. Reconnect stays paused.

Nearby discovery uses bounded IPv4 multicast, without an Avahi dependency. The
public-only TCP pairing listener actually closes on cancellation, loss of foreground
Nearby, expiry, lock/sleep or shutdown. Proof and approval have separate signatures
under the [shared contract](../../Universal/Protocol/Nearby_Pairing.md). Neither
bootstrap nor discovery can dispatch input. Actual ports are announced; changed
addresses are candidates for unchanged pinned mutual TLS, never replacements for
trust. Only the lower ordinal device ID automatically connects; the other listens.
Approved peers reconnect automatically when rediscovered, without rearming input.
Blocked discovery has an explanation and Search again action. IPv6-only networks
and isolated Wi-Fi cannot currently use this IPv4 discovery path.

After sharing is active, Linux source control begins only on compositor barrier
activation and the matching passive EIS sequence. F12 pauses capture. Return
crossings release held receiving input; actual receiving regions and gaps are
respected. The existing v0 input messages and `<peerId>:desktop` target remain.
No persistent `control.testStart` is sent. Screen geometry authorized by the
portal takes precedence; a changed connection cannot reuse initial consent.

Preferences and existing identities survive upgrades, but consent is never saved.
The local Files shelf remains available; network file transfer and shared audio
are separate work and are not advertised by this pairing implementation.

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

## Nearby pairing validation

`scripts/test.sh` currently runs 66 distinct Linux regressions, including both
isolated portal buses. Shared public vectors match the exact commitments,
transcript, eight-digit code and RSA/ECDSA message signatures. Tests reject
cross-stage replay and selected-identity substitution, and enforce cache/attempt
bounds, cancellation, listener unbinding and existing-pin preservation.
Connection adoption and input dispatch/activation share one lock. Capture Enable
callbacks retain their request generation and cannot arm a replacement session or
survive cancellation of initial sharing consent.

Actual GTK fixtures check the approval actions and one-use receiver activation
against an in-process EIS server. They also check delayed source enablement after
navigation/expiry and a connected UI with no live transport. Twelve synthetic
views are rendered at default and minimum sizes, without network traffic or an
OS input grant. These are layout and action checks, not live desktop approval.

`tests/pairing_interop` links the Windows reference protocol without editing it.
Build `PairingInterop.csproj` on Windows with .NET 10. Run its DLL with an empty
disposable exchange directory and an isolated interface address; run the adjacent
Linux `linux_peer.py` with the same directory and address on `PYTHONPATH` containing
this Linux source. The default has Linux initiate and Windows respond. For the
opposite direction, add `connect` to the Windows invocation and `listen` to Linux,
using the Linux interface address. Both directions independently passed matching
code and mutual RSA/ECDSA approvals followed by pinned TLS 1.3. The fixture
automatically simulates two local confirmation clicks after exact code comparison;
it does not test the human comparison process. It binds ephemeral TCP ports,
deletes its disposable Windows CNG identity and never touches an installed profile
or imports a native input backend. Keep the exchange directory out of source.

Multicast across physical computers, real foreground/lock/sleep behavior, fresh
keyring and portal dialogs, and cross-platform input still require physical QA.
IPv4 WSL socket fixtures do not establish those results. Version 0.3.0 is a review
candidate; do not present it as a verified daily-use release.
