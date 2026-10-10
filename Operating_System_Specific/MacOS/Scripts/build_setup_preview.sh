#!/bin/bash
set -euo pipefail
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PREVIEW_VERSION="${BATCAVE_PREVIEW_VERSION:-0.2.0-preview.3}"
PREVIEW_ARCH="$(uname -m)"
APP_DIR="$ROOT_DIR/Build/SetupPreview/BatCave Preview.app"
PACKAGE_DIR="${BATCAVE_PREVIEW_OUTPUT:-$ROOT_DIR/Build/SetupPreview/Packages}"
if [[ ! "$PREVIEW_VERSION" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]]; then
    echo 'Preview version must use letters, digits, dots, underscores or hyphens.' >&2
    exit 2
fi
if [[ "$PACKAGE_DIR" != /* ]]; then
    PACKAGE_DIR="$ROOT_DIR/$PACKAGE_DIR"
fi
PACKAGE_DIR="${PACKAGE_DIR%/}"
case "$PACKAGE_DIR" in
    "$ROOT_DIR/Build/"*) ;;
    *) echo 'Package output must stay inside this checkout Build directory.' >&2; exit 2 ;;
esac
case "$PACKAGE_DIR/" in
    */../*|*/./*|*//*) echo 'Package output must use a canonical path.' >&2; exit 2 ;;
esac
CHECK_PATH="$PACKAGE_DIR"
while [[ "$CHECK_PATH" != "$ROOT_DIR" ]]; do
    if [[ -L "$CHECK_PATH" ]]; then
        echo 'Refusing a symlinked package output path.' >&2
        exit 2
    fi
    CHECK_PATH="$(dirname "$CHECK_PATH")"
done
cd "$ROOT_DIR"
SOURCE_COMMIT="$(git rev-parse HEAD)"
if [[ -n "$(git status --porcelain --untracked-files=normal)" ]]; then
    echo 'Commit preview changes before packaging so the manifest identifies exact source.' >&2
    exit 2
fi
# Reuse the public builder's physical-path and working-app preservation guards.
BATCAVE_APP_DIR="$APP_DIR" CONFIGURATION=release "$ROOT_DIR/Scripts/build_app_bundle.sh"
if [[ "$(git rev-parse HEAD)" != "$SOURCE_COMMIT" || -n "$(git status --porcelain --untracked-files=normal)" ]]; then
    echo 'Source changed during compilation; rebuild from a clean, fixed commit.' >&2
    exit 2
fi
mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources" "$PACKAGE_DIR"
strip -S "$APP_DIR/Contents/MacOS/BatCaveMacOSAgent"
chmod +x "$APP_DIR/Contents/MacOS/BatCaveMacOSAgent"
cp Resources/BatCave.icns "$APP_DIR/Contents/Resources/BatCave.icns"
cat > "$APP_DIR/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>dev.batcave.macos-agent.secure-preview</string>
<key>CFBundleExecutable</key><string>BatCaveMacOSAgent</string>
<key>CFBundleName</key><string>BatCave Preview</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleIconFile</key><string>BatCave</string>
<key>CFBundleShortVersionString</key><string>0.2.0</string>
<key>CFBundleVersion</key><string>2003</string>
<key>BatCavePreviewVersion</key><string>$PREVIEW_VERSION</string>
<key>LSMinimumSystemVersion</key><string>13.0</string>
<key>NSHighResolutionCapable</key><true/>
<key>BatCaveSetupPreview</key><true/>
<key>NSLocalNetworkUsageDescription</key><string>Connect to the computer you approve to share its mouse and keyboard securely.</string>
</dict></plist>
PLIST
# Local preview only. Developer ID signing/notarization must precede distribution.
codesign --force --sign - "$APP_DIR"
codesign --verify --strict "$APP_DIR"
STAGING_DIR="$(mktemp -d "$ROOT_DIR/Build/SetupPreview/dmg.XXXXXX")"
trap 'rm -rf "$STAGING_DIR"' EXIT
ditto "$APP_DIR" "$STAGING_DIR/BatCave Preview.app"
ln -s /Applications "$STAGING_DIR/Applications"
cat > "$STAGING_DIR/Start here.txt" <<'TXT'
BatCave Preview — first-time setup test

1. Drag BatCave Preview to Applications, then open it.
2. Follow Permissions → Pair computers → Arrange screens → Try it out.
3. Export/import a public pairing card on BOTH computers and compare fingerprints.
4. Open the matching Windows secure preview and enter its local address on Mac.
5. Keep your working BatCave apps closed only while testing preview input.

This local test package is not Developer ID signed or notarized. If macOS blocks
it, allow this specific preview under System Settings → Privacy & Security.
Do not disable Gatekeeper. It requires macOS 13+ and Apple Silicon.

The preview uses separate settings and never replaces your working BatCave app.
Text clipboard sharing and Open at login are off until you choose to enable them.
Files preview accepts local Finder files and prepares source icons. Cross-computer
file transfer is not enabled in this build; Send files remains disabled.
Public-release signing, the security review, and Windows/Mac integration testing
are still required. No release has been published.
TXT
DMG="$PACKAGE_DIR/BatCavePreview-$PREVIEW_VERSION-$PREVIEW_ARCH.dmg"
hdiutil create -volname 'BatCave Preview' -srcfolder "$STAGING_DIR" -ov -format UDZO "$DMG"
(cd "$PACKAGE_DIR" && shasum -a 256 "$(basename "$DMG")") > "$DMG.sha256"
DIRTY="$(git status --porcelain --untracked-files=normal | wc -l | tr -d ' ')"
cat > "$PACKAGE_DIR/BatCavePreview-build.json" <<JSON
{"version":"$PREVIEW_VERSION","architecture":"$PREVIEW_ARCH","minimumMacOS":"13.0","sourceCommit":"$SOURCE_COMMIT","sourceTreeDirty":$DIRTY,"signing":"ad-hoc local preview","notarized":false,"transport":"mutual TLS 1.3","port":47871,"fileTransfer":"local staging only; shared wire integration pending"}
JSON
echo "Preview app: $APP_DIR"
echo "Installable preview: $DMG"
