#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP_NAME="BatCave Preview"
PRODUCT_NAME="BatCaveMacOSAgent"
BUNDLE_IDENTIFIER="dev.batcave.macos-agent.secure-preview"
CONFIGURATION="${CONFIGURATION:-release}"
DEFAULT_WINDOWS_HOST="${BATCAVE_DEFAULT_WINDOWS_HOST:-127.0.0.1}"
DEFAULT_PORT="${BATCAVE_DEFAULT_PORT:-47870}"
ICON_SOURCE="${BATCAVE_ICON_PATH:-}"
BUILD_DIR="$ROOT_DIR/Build"
STABLE_APP_DIR="$BUILD_DIR/BatCave.app"
DEFAULT_APP_DIR="$BUILD_DIR/Preview/$APP_NAME.app"
APP_DIR="${BATCAVE_APP_DIR:-$DEFAULT_APP_DIR}"

if [[ "$APP_DIR" != /* ]]; then
    APP_DIR="$ROOT_DIR/$APP_DIR"
fi

# Validate the physical destination before any recursive removal. A supplied
# path must name an app inside this checkout's real Build directory.
if [[ -L "$BUILD_DIR" ]]; then
    echo "Refusing a symlinked Build directory" >&2
    exit 2
fi
mkdir -p "$BUILD_DIR"
BUILD_DIR="$(cd "$BUILD_DIR" && pwd -P)"
APP_DIR="${APP_DIR%/}"
case "$APP_DIR" in
    "$BUILD_DIR/"*.app) ;;
    *) echo "App output must be an .app inside Build" >&2; exit 2 ;;
esac
case "$APP_DIR/" in
    */../*|*/./*|*//*) echo "App output must use a canonical path" >&2; exit 2 ;;
esac
APP_PARENT="$(dirname "$APP_DIR")"
mkdir -p "$APP_PARENT"
APP_PARENT="$(cd "$APP_PARENT" && pwd -P)"
case "$APP_PARENT/" in
    "$BUILD_DIR/"*) ;;
    *) echo "App output resolves outside Build" >&2; exit 2 ;;
esac
APP_DIR="$APP_PARENT/$(basename "$APP_DIR")"
if [[ -L "$APP_DIR" ]]; then
    echo "Refusing a symlinked app output" >&2
    exit 2
fi
STABLE_APP_DIR="$BUILD_DIR/BatCave.app"

if [[ "$APP_DIR" == "$STABLE_APP_DIR" || "$APP_DIR" == "$BUILD_DIR/Development/BatCave.app" \
    || "$APP_DIR" -ef "$STABLE_APP_DIR" || "$APP_DIR" -ef "$BUILD_DIR/Development/BatCave.app" ]]; then
    echo "Refusing to overwrite a preserved stable or development app" >&2
    exit 2
fi

CONTENTS_DIR="$APP_DIR/Contents"
MACOS_DIR="$CONTENTS_DIR/MacOS"
RESOURCES_DIR="$CONTENTS_DIR/Resources"

cd "$ROOT_DIR"
swift build -c "$CONFIGURATION" --product "$PRODUCT_NAME" \
    -Xswiftc -debug-prefix-map -Xswiftc "$ROOT_DIR=/_/BatCave/Operating_System_Specific/MacOS"

rm -rf "$APP_DIR"
mkdir -p "$MACOS_DIR" "$RESOURCES_DIR"

cp ".build/$CONFIGURATION/$PRODUCT_NAME" "$MACOS_DIR/$PRODUCT_NAME"
chmod +x "$MACOS_DIR/$PRODUCT_NAME"

if [[ -z "$ICON_SOURCE" && -f "$ROOT_DIR/Resources/BatCave.icns" ]]; then
    ICON_SOURCE="$ROOT_DIR/Resources/BatCave.icns"
elif [[ -z "$ICON_SOURCE" ]]; then
    ICON_SOURCE="$(find "$ROOT_DIR" -maxdepth 1 -type f -name "*.icns" -print -quit)"
elif [[ "$ICON_SOURCE" != /* ]]; then
    ICON_SOURCE="$ROOT_DIR/$ICON_SOURCE"
fi

ICON_PLIST_ENTRY=""
if [[ -n "$ICON_SOURCE" && -f "$ICON_SOURCE" ]]; then
    cp "$ICON_SOURCE" "$RESOURCES_DIR/BatCave.icns"
    ICON_PLIST_ENTRY='    <key>CFBundleIconFile</key>
    <string>BatCave</string>'
fi

cat > "$CONTENTS_DIR/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key>
    <string>en</string>
    <key>CFBundleExecutable</key>
    <string>$PRODUCT_NAME</string>
    <key>CFBundleIdentifier</key>
    <string>$BUNDLE_IDENTIFIER</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>$APP_NAME</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
$ICON_PLIST_ENTRY
    <key>CFBundleShortVersionString</key>
    <string>0.1.0</string>
    <key>CFBundleVersion</key>
    <string>1</string>
    <key>LSMinimumSystemVersion</key>
    <string>13.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>BatCaveDefaultWindowsHost</key>
    <string>$DEFAULT_WINDOWS_HOST</string>
    <key>BatCaveDefaultPort</key>
    <integer>$DEFAULT_PORT</integer>
</dict>
</plist>
PLIST

if command -v codesign >/dev/null 2>&1; then
    codesign \
        --force \
        --deep \
        --sign - \
        --requirements "=designated => identifier \"$BUNDLE_IDENTIFIER\"" \
        "$APP_DIR" >/dev/null
fi

echo "Built $APP_DIR"
if [[ -d "$STABLE_APP_DIR" && "$APP_DIR" != "$STABLE_APP_DIR" ]]; then
    echo "Stable app unchanged: $STABLE_APP_DIR"
fi
echo "Default Windows host: $DEFAULT_WINDOWS_HOST"
if [[ -n "$ICON_PLIST_ENTRY" ]]; then
    echo "App icon: $ICON_SOURCE"
fi
echo "Runtime config: ~/Library/Application Support/BatCave/SecurePreview/MacOSAgent.conf"
