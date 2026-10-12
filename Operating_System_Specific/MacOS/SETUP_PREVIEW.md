# Mac first-time device setup

BatCave runs on macOS 13+ on Apple Silicon. It retains the protected SecurePreview
profile and Keychain identity for upgrades. Source builds create a separate
versioned app and preserve installed working copies.

Build `Scripts/build_setup_preview.sh`. The resulting DMG contains a versioned
**BatCave-0.3.0.app**,
an Applications shortcut, and first-run instructions. Drag the
app into Applications and open it. The build script also produces a checksum
and source/version manifest. The 0.3.0 download is prepared for clean-install
testing, ad-hoc signed and unnotarized. Supported daily-use distribution still
requires signing, notarization and the pending physical-device checks.
`BATCAVE_PREVIEW_OUTPUT` may select a canonical directory inside this checkout's
`Build` directory; paths outside it and symbolic-link destinations are refused.

The app opens to **Nearby computers**, without a required permission wizard,
platform picker, connection file or address field. Open BatCave on both computers,
choose the other computer, compare all eight digits and choose **Codes match ·
Link and share** on both. The initiating computer supplies its keyboard and mouse;
the receiving computer starts on its right. Windows, Mac and Linux use the exact
same bootstrap contract and shared cryptographic vectors.

**Arrange screens** is optional. Its diagram represents each computer's aggregate
desktop; individual-monitor arrangement is a future refinement. **Permissions**
explains Accessibility and Input Monitoring when input access is needed. A linked
computer can use either input role. Changing roles/arrangement pauses sharing;
Start sharing explicitly resumes a connected, approved session.

Initial Link and share consent is bound to the approved identity, pin and role,
held only in memory for 30 seconds and consumed before the first matching fresh
input session activates. Missing permissions consume it as well: grant permission,
then use Start sharing. Cancel, navigation, role change, Pause, lock, sleep,
disconnect, replacement and quitting revoke pending consent. Reconnect always
stays paused. Existing Keychain keys and the SecurePreview profile are preserved.

Nearby uses IPv4 multicast, with a clear same-network/retry state when discovery
is blocked. The bootstrap listener is available only while Nearby computers is
foreground, for 180 seconds per local opening or Search again. Closing or leaving
that screen actually unbinds it. Normal return handoffs stay quiet. Closing the
window leaves a menu-bar app; Quit releases input and closes transports. Clipboard
sharing and Open at login remain opt-in. No installed app is changed during review.

## Start with a fresh setup

Uninstalling an app does not remove its saved setup, Keychain identity or OS
permissions. For a fresh app-state test, pause sharing, quit other BatCave copies
and turn off Open BatCave at login. Choose **BatCave > Start fresh…** in the app
menu or menu-bar icon. The confirmation shows the exact backup destination and
defaults to Cancel. Confirming stops network/input sources, atomically renames
the complete SecurePreview directory to a timestamp/UUID sibling backup and quits.
Reopen the installed app to use Nearby computers with a new identity.

The backup retains the original directory, files and modes. Existing Keychain
keys/certificates, installed apps and Desktop alias are never removed by this
action. Symlinked paths, changed directory identities, unsafe permissions and
backup collisions are refused; no existing backup is overwritten. A failure
after transport shutdown leaves setup in place and instructs you to quit/reopen.
Keep backups private and out of GitHub, including diagnostic logs made by older
versions. Restoring the original profile while the app is quit reuses its retained
Keychain identity; the other computer must also use its matching original profile.

This action does not reset Accessibility, Input Monitoring or Local Network
permissions. Verify their initial status separately for a full permission test.
A separate local test account preserves the working account's profile/Keychain;
log out the working account to stop its listeners. Account separation alone is
not claimed as proof of fresh OS permissions. Apple's
[permission-reset guide](https://developer.apple.com/documentation/xcode/resetting-access-to-protected-resources-in-macos)
describes explicitly targeting one app; no automatic reset runs here.

## Security and interoperability

- TLS 1.3 only, mutually authenticated with pinned certificates, on port 47871.
  The app's default launch has no plaintext fallback.
- Exact `Nearby_Pairing.md` binary encodings and `batcave.pairing-card.v1`
  fields: strict bounded JSON, duplicate/unknown-property refusal, SHA-256 DER,
  validity dates and supported certificate key types. Bootstrap signatures use
  separate proof and approval domains; discovery grants no trust.
- UDP 239.255.47.87:47872, TTL 1, per-interface discovery; 64-entry cache with
  12-second expiry. Separate public-metadata TCP pairing on 47873, bounded
  handshake/approval deadlines and attempts. Input stays on pinned mutual TLS.
- Mac secure transport can connect or listen. Duplicate authenticated links
  prefer the lower ordinal ID's outgoing socket. Source event tap sends the
  existing Windows-format v0 input payloads; receiver requires handoff ownership.
  Activation epochs reject work queued before Pause or socket replacement.
- Random device identity persists in a separate public reference file; the
  non-extractable P-256 private signing key stays in the Mac Keychain. A missing
  key does not silently replace identity. The ad-hoc signed app uses
  the macOS login Keychain; revisit stable signing/access requirements in review.
- Hello must arrive within five seconds and match the approved card exactly.
  Input additionally requires a new handoff and explicit local enablement.
- 256 KiB frame, 64 KiB clipboard text, 4096 messages and 1 MiB outbound limits;
  invalid fields/frames close the connection. Queues belong to one socket.
- Clipboard callbacks recheck their connection session on the UI thread.
  Routine logs do not record input or clipboard contents.
- The retained bundle ID is `dev.batcave.macos-agent.secure-preview`, matching the
  public-candidate isolation plan. Peer cards/profile live in Application
  Support/BatCave/SecurePreview. No keys,
  pairing cards, runtime logs, or machine addresses belong in Git commits.
- The command-line prototype cannot connect by default. The explicit
  `--insecure-loopback-development` switch permits literal loopback only;
  listeners stay disabled and legacy clipboard still requires opt-in.
  Its configuration is isolated from the working development app too.

Automated checks cover native input cleanup, identity persistence and missing
keys, certificate signing/expiry/tampering, TLS pin rejection, and real local
mutual TLS with hello/ownership gating. They install no desktop event tap.
Native Mac/Mac socket and recording-sink fixtures establish local behavior only.
Actual Windows/Linux interoperability, multicast across physical computers,
first install on another Mac, held-drag/modifier crossings and sleep/wake still
require live validation. The source does not claim those checks have passed.

For live validation, cross in both directions, type and try Shift+Arrow in a
regular document, hold a modifier/drag across the boundary, then repeat after
restarting Windows and after Mac sleep/wake. These checks remain release gates;
the setup interface does not present self-reported checkboxes as test results.

## Local files

Choose **Files** from the setup sidebar or menu bar. Drop regular files
from Finder into the shelf. Selection is limited to 32 files, 1 GiB per file
and 4 GiB total. Folders, aliases and symbolic links are rejected. Original files
stay on disk; the shelf stores references and an optional icon snapshot.

Source file icons are rendered on a bounded background worker as static PNGs
up to 128 × 128 pixels and 32 KiB. Received icon metadata must pass SHA-256,
PNG structure/CRC, dimensions and single-image checks, then is re-rendered to
strip source metadata. Animations, SVG and executable icon formats are refused.
The 128 KiB cache counts both encoded and raster memory. Optional icon failures leave
a normal document icon and do not prevent file content transfer.

The local receiving store requires a caller-approved destination, stages files
privately, validates contiguous chunks, declared length and SHA-256, then saves
with an atomic collision-safe name. Cancel/disconnect removes unfinished data.
Only verified received files can be dragged from the shelf into Finder using
native copy operations. The source icon stays in the shelf and drag preview.
The native helper for applying a custom Finder icon has been tested separately;
it has no UI action and is not enabled in this build, pending native adapter review.

This build has **local staging only**. Send files stays disabled until the
Windows-owned transfer contract is agreed and all peers implement it. No file
messages, capabilities or bulk channel are silently enabled. Cross-computer
transfer and seamless Explorer-to-Finder boundary dragging still require
native integration and live Windows/Mac/Linux checks. Native per-file icon
persistence on Windows/Linux is platform-dependent; source appearance in the
BatCave shelf/drag preview is the shared target.
