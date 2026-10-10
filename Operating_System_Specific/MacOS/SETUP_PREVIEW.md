# Mac first-time setup preview

This is a separate, locally signed test app for macOS 13+ on Apple Silicon.
It does not replace the stable or development app, read their connection
settings, or approve their hostname-based identities.

Build `Scripts/build_setup_preview.sh`. The resulting DMG contains **BatCave
Preview.app**, an Applications shortcut, and first-run instructions. Drag the
app into Applications and open it. The build script also produces a checksum
and source/version manifest. Developer ID signing and notarization are release
gates; do not describe the local preview as a public-ready release.
`BATCAVE_PREVIEW_OUTPUT` may select a canonical directory inside this checkout's
`Build` directory; paths outside it and symbolic-link destinations are refused.

The guided setup has four pages:

1. **Permissions:** open Accessibility and Input Monitoring settings, show
   their actual status, and continue only after both are granted.
2. **Pair computers:** export/copy this Mac's public JSON card, import/paste
   the Windows card, and explicitly approve its full fingerprint. Import the
   Mac card in the Windows preview as well.
3. **Arrange screens:** select whether the Mac sits left or right of Windows,
   match that setting on Windows, enter its local address, and connect securely.
   The diagram represents each computer's aggregate desktop. Individual
   monitor placement is a future refinement, not implemented by this picker.
4. **Link devices:** quit other BatCave controllers before enabling Mac input;
   choose Start sharing on Windows. Pause Mac input stops the connection and
   restores local control. Save setup keeps the paired-device preferences.
   Clipboard sharing and Open at login are optional on this page.

Normal return handoffs do not show a HUD. Text clipboard sharing and Open at
login are opt-in. Closing the setup window leaves a menu-bar app; Quit closes
the transport and releases input. Pause closes the connection so Windows can
restore local control. A lost connection releases input; reconnect remains
idle until Mac input is explicitly enabled and Windows starts a fresh handoff.

## Security and interoperability

- TLS 1.3 only, mutually authenticated with pinned certificates, on port 47871.
  The app's default launch has no plaintext fallback.
- Exact `batcave.pairing-card.v1` camelCase fields agreed with Windows. Cards
  are at most 16 KiB UTF-8 and validate their DER SHA-256 and validity dates.
- Random device identity persists in a separate public reference file; the
  non-extractable P-256 private signing key stays in the Mac Keychain. A missing
  key does not silently replace identity. The local unsigned preview uses
  the macOS login Keychain; revisit stable signing/access requirements in review.
- Hello must arrive within five seconds and match the approved card exactly.
  Input additionally requires a new handoff and explicit local enablement.
- 256 KiB frame, 64 KiB clipboard text, 4096 messages and 1 MiB outbound limits;
  invalid fields/frames close the connection. Queues belong to one socket.
- Clipboard callbacks recheck their connection session on the UI thread.
  Routine preview logs do not record input or clipboard contents.
- Preview bundle ID is `dev.batcave.macos-agent.secure-preview`, matching the
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
Actual Windows `SslStream` interoperability, first install on another Mac,
held-drag/modifier crossings, and sleep/wake still require live validation.
Bonjour discovery and QR pairing are not part of this preview.

For live validation, cross in both directions, type and try Shift+Arrow in a
regular document, hold a modifier/drag across the boundary, then repeat after
restarting Windows and after Mac sleep/wake. These checks remain release gates;
the setup interface does not present self-reported checkboxes as test results.

## Local files preview

Choose **Files preview** from the setup sidebar or menu bar. Drop regular files
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
