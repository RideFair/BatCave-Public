#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
if [[ "${1:-}" == "--help" || "${1:-}" == "-h" ]]; then
    cat <<'HELP'
Install a built BatCave without opening it.

Usage: bash Scripts/install_setup_preview.sh [options]
  --source APP       Absolute path to the signed setup preview .app.
                     Default: Build/SetupPreview/BatCave.app.
  --user-home DIR    Absolute home directory; defaults to the current user's home.
                     Intended for isolated installer smoke checks.
  --validate-only    Check the bundle and installation plan without writing files.
  --help            Show this help.

The app is installed as ~/Applications/BatCave Preview-<version>.app.
Desktop/BatCave — Latest is a native Finder alias. Existing unrelated desktop
items and different same-version bundles are preserved and cause a refusal.
No app is launched and no permissions, settings, devices or login items change.
HELP
    exit 0
fi

if [[ "$(uname -s)" != "Darwin" ]]; then
    echo 'This installer requires macOS.' >&2
    exit 2
fi
if ! /usr/bin/xcrun --find swift >/dev/null 2>&1; then
    echo 'Install the Xcode command-line tools before using this source installer.' >&2
    exit 2
fi
exec /usr/bin/xcrun swift "$SCRIPT_DIR/InstallSetupPreview.swift" --default-source "$SCRIPT_DIR/../Build/SetupPreview/BatCave.app" "$@"
