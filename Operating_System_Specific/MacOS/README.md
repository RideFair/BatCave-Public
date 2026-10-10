# Mac secure setup preview

This source contains a separate macOS setup preview with permission checks,
public pairing-card exchange, mutually authenticated TLS 1.3, and a left/right
computer layout picker. See [the setup preview guide](SETUP_PREVIEW.md) for
features, limitations, and the live interoperability checks still required.

On macOS 13 or newer with Swift 6:

```bash
swift build --package-path Operating_System_Specific/MacOS -c release
```

`Scripts/build_setup_preview.sh` packages the guided app from a clean commit
into `Build/SetupPreview`. It records the source revision and checksum. The
underlying `Scripts/build_app_bundle.sh` also creates a command-line backend
bundle under `Build/Preview`. Both use a separate bundle identity and
configuration under `BatCave/SecurePreview`, and refuse to overwrite preserved
stable and development bundles. Ad-hoc signing is not Developer ID signing or
notarization and is unsuitable for a public installer release.

After building, `bash Scripts/install_setup_preview.sh --validate-only` checks
the installation plan. Run it without `--validate-only` to install a versioned
preview in your user Applications folder and create the native desktop alias
`BatCave — Latest`. It preserves older app bundles and refuses to replace
unrelated desktop items. See [the installation guide](Scripts/INSTALL_SETUP_PREVIEW.md)
for source selection and disposable installer checks. The installer does not
open the app or change permissions, devices, settings, or login items.

Input capture and injection require Input Monitoring and Accessibility permission
for the development app or terminal in System Settings. Grant these permissions
only to builds you trust.

The command-line backend refuses network connections by default and has no
listener. An explicit `--insecure-loopback-development` flag permits connections
to literal loopback destinations for local experiments. Clipboard sharing
additionally requires `--share-clipboard` and is disabled by default. Neither
flag enables plaintext LAN access. The guided preview connects only to an
explicitly approved TLS peer. Routine logs omit typed text and key codes.

The preview requires its own OS permission approval. Install it alongside the
working app; pause the working app before a live input-sharing test. The local
files shelf supports Finder file selection and verified local receipts;
cross-computer file transport remains disabled. Security review, first-install
validation and secure Windows/macOS interoperability must pass before producing
a supported Mac package.
