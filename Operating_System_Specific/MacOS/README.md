# Mac agent development

This source snapshot retains the macOS input backend for development. Its
network listener is disabled and outbound connections are refused by default
until the authenticated Mac transport is implemented and reviewed. It is not
a supported network-sharing app.

On macOS 13 or newer with Swift 6:

```bash
swift build --package-path Operating_System_Specific/MacOS -c release
```

`Scripts/build_app_bundle.sh` creates `Build/Preview/BatCave Preview.app` with
a separate bundle identity and configuration under `BatCave/SecurePreview`.
It refuses to overwrite the preserved stable and development bundles.
Ad-hoc signing is not Developer ID signing or
notarization and is unsuitable for a public installer release.

Input capture and injection require Input Monitoring and Accessibility permission
for the development app or terminal in System Settings. Grant these permissions
only to builds you trust.

An explicit `--insecure-loopback-development` flag permits connections to literal
loopback destinations for local experiments. Clipboard sharing additionally
requires `--share-clipboard` and is disabled by default. Neither flag enables
plaintext LAN access. Routine logs omit typed text and key codes.

The existing private development apps are separate from this source snapshot.
The preview requires its own OS permission approval. Install it alongside the
working app; pause the working app before a live input-sharing test.
macOS build verification and secure Windows/macOS interoperability must pass
before producing a supported Mac package.
