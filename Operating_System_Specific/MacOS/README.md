# BatCave on macOS

The updated Mac source opens to Nearby computers. Open BatCave on both devices,
choose the other computer, compare all eight digits and choose **Codes match ·
Link and share** on both screens. Windows, Mac and Linux use the same pairing
contract. Permissions and Arrange screens are available when needed.

This 0.3.0 implementation is a separate review candidate; published 0.2.0 Mac
installers still use manual pairing. Working installations are not replaced by
source development. Physical LAN discovery, permission and input checks remain
release gates.

Build from a clean checkout on macOS 13+ with Swift 6 and a macOS SDK:

```sh
bash Operating_System_Specific/MacOS/Scripts/build_setup_preview.sh
bash Operating_System_Specific/MacOS/Scripts/install_setup_preview.sh
```

The installer creates a versioned app in your user Applications folder and the
BatCave — Latest desktop shortcut. Open the app and choose a nearby computer.
Older apps and device identities remain available. Build script and profile
identifiers retain their existing names for compatibility.

See [setup](SETUP_PREVIEW.md) and [installation](Scripts/INSTALL_SETUP_PREVIEW.md)
for technical details. The DMG identifies its architecture and includes a checksum.
The package is ad-hoc signed, without Developer ID signing or notarization.
Actual secure Windows/Mac input, first-install permissions and recovery still
need live verification before a completed cross-platform release can be claimed.

Accessibility and Input Monitoring grants are required for input. Pairing exchanges
fresh nonce commitments and signed public identities; private keys remain in
Keychain. Both computers must locally approve the matching code. Approval grants
only the first authenticated input session, for at most 30 seconds, in memory.
Reconnection leaves input paused.
Clipboard sharing and opening at login are optional. The Files shelf keeps source
icons locally; network file transfer and audio are disabled in this build.

Run `swift test --package-path Operating_System_Specific/MacOS` for regression
checks and `python3 Operating_System_Specific/MacOS/Scripts/test_setup_preview_install.py`
after packaging for disposable installation-preservation checks.
