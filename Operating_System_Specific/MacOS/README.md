# BatCave on macOS

The updated Mac source opens to Nearby computers. Open BatCave on both devices,
choose the other computer, compare all eight digits and choose **Codes match ·
Link and share** on both screens. Windows, Mac and Linux use the same pairing
contract. Permissions and Arrange screens are available when needed.

The matching 0.3.0 packages are prepared for clean-install testing from
[BatCave-Public](https://github.com/RideFair/BatCave-Public/releases). They use the
nearby setup above; older 0.2.x packages use manual pairing. Physical LAN discovery,
fresh permissions, real input and recovery still require verification before a
completed daily-use release can be claimed.

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
The download is a clean-install test build. Actual secure cross-platform input,
first-install permissions and recovery still need live verification.

Accessibility and Input Monitoring grants are required for input. Pairing exchanges
fresh nonce commitments and signed public identities; private keys remain in
Keychain. Both computers must locally approve the matching code. Approval grants
only the first authenticated input session, for at most 30 seconds, in memory.
Reconnection leaves input paused.
Clipboard sharing and opening at login are optional. The Files shelf keeps source
icons locally; network file transfer and audio are disabled in this build.

To test first-time pairing again, pause sharing, quit other BatCave copies and
turn off Open BatCave at login. Choose **BatCave > Start fresh…**, review the
backup location and confirm. BatCave moves the complete saved setup into a unique
sibling backup and quits. Reopening creates a new setup and device identity.
Existing Keychain items, installed apps and the Desktop shortcut are retained.
Keep the backup private. Mac permissions are retained too; this tests fresh app
state, while permission onboarding still needs a deliberate permission reset or
a clean test environment. Removing and reinstalling the app alone retains setup.

Run `swift test --package-path Operating_System_Specific/MacOS` for regression
checks and `python3 Operating_System_Specific/MacOS/Scripts/test_setup_preview_install.py`
after packaging for disposable installation-preservation checks.
