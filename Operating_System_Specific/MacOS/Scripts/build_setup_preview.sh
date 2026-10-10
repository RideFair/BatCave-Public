#!/bin/bash
set -euo pipefail
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PREVIEW_VERSION="${BATCAVE_PREVIEW_VERSION:-0.2.0}"
PREVIEW_ARCH="$(uname -m)"
APP_DIR="$ROOT_DIR/Build/SetupPreview/BatCave.app"
PACKAGE_DIR="${BATCAVE_PREVIEW_OUTPUT:-$ROOT_DIR/Build/SetupPreview/Packages}"
if [[ ! "$PREVIEW_VERSION" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]]; then
    echo 'Version must use letters, digits, dots, underscores or hyphens.' >&2
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
    echo 'Commit source changes before packaging so the manifest identifies exact source.' >&2
    exit 2
fi
# Reuse the public builder's physical-path and working-app preservation guards.
BATCAVE_APP_DIR="$APP_DIR" CONFIGURATION=release bash "$ROOT_DIR/Scripts/build_app_bundle.sh"
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
<key>CFBundleName</key><string>BatCave</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleIconFile</key><string>BatCave</string>
<key>CFBundleShortVersionString</key><string>0.2.0</string>
<key>CFBundleVersion</key><string>2006</string>
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
ditto "$APP_DIR" "$STAGING_DIR/BatCave.app"
ln -s /Applications "$STAGING_DIR/Applications"
cat > "$STAGING_DIR/Start here.txt" <<'TXT'
BatCave

Drag BatCave to Applications and open it on both computers. Follow the guided
Permissions, Pair computers, Arrange screens and Link devices screens.
Approve each other's public device cards, then enable Mac input and choose
Start sharing on Windows. Quit another BatCave app before enabling input here.

This build is ad-hoc signed and is not notarized. If macOS blocks it, use the
specific app's Open Anyway control in System Settings > Privacy & Security.
Keep Gatekeeper enabled. The DMG filename identifies the required architecture;
macOS 13 or newer is required.

Existing settings and other installed apps remain separate. Clipboard sharing
and Open at login are optional. The local Files shelf does not send files between
computers. Live secure Windows/Mac input, fresh-install permissions and recovery
checks remain pending; a completed cross-platform release is not yet verified.
TXT
DMG="$PACKAGE_DIR/BatCave-$PREVIEW_VERSION-$PREVIEW_ARCH.dmg"
hdiutil create -volname 'BatCave' -srcfolder "$STAGING_DIR" -ov -format UDZO "$DMG"
(cd "$PACKAGE_DIR" && shasum -a 256 "$(basename "$DMG")") > "$DMG.sha256"
DIRTY="$(git status --porcelain --untracked-files=normal | wc -l | tr -d ' ')"
cat > "$PACKAGE_DIR/BatCave-build.json" <<JSON
{"version":"$PREVIEW_VERSION","architecture":"$PREVIEW_ARCH","minimumMacOS":"13.0","sourceCommit":"$SOURCE_COMMIT","sourceTreeDirty":$DIRTY,"signing":"ad-hoc","notarized":false,"transport":"mutual TLS 1.3","port":47871,"fileTransfer":"local staging only; shared wire integration pending"}
JSON
echo "App: $APP_DIR"
echo "Installer: $DMG"
