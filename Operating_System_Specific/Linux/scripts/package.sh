#!/bin/sh
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
if test -n "$(git -C "$root" status --porcelain)"; then
    printf 'Commit the reviewed source before packaging.\n' >&2
    exit 1
fi
sh "$root/scripts/build.sh"
version=0.2.0~preview.1
architecture=$(dpkg --print-architecture)
output="$root/.build"
stage=$(mktemp -d "$output/package.XXXXXXXX")
# Use a fresh stage; no wildcard deletion and no user profile is copied.
mkdir -p "$stage/DEBIAN" "$stage/usr/lib/batcave-linux-preview/native" "$stage/usr/bin" "$stage/usr/share/applications" "$stage/usr/share/doc/batcave-linux-preview"
cat > "$stage/DEBIAN/control" <<EOF
Package: batcave-linux-preview
Version: $version
Architecture: $architecture
Maintainer: BatCave Contributors <contributors@example.invalid>
Depends: python3 (>= 3.12), python3-gi, python3-cryptography (>= 41), gir1.2-gtk-4.0 (>= 4.12), gir1.2-secret-1, libei1 (>= 1.2)
Section: net
Priority: optional
Description: BatCave secure Linux input-sharing preview
 Receives input from explicitly approved Windows hosts over mutual TLS 1.3.
 Requires a desktop session with the RemoteDesktop input portal.
 Live Wayland interoperability is an outstanding release gate.
EOF
mkdir -p "$stage/usr/lib/batcave-linux-preview/batcave_linux"
# Only reviewed, tracked Python source enters the package.
git -C "$root" ls-files -- batcave_linux/'*.py' | while IFS= read -r source; do
    cp "$root/$source" "$stage/usr/lib/batcave-linux-preview/batcave_linux/"
done
cp "$root/.build/libbatcave-ei.so" "$stage/usr/lib/batcave-linux-preview/native/"
cp "$root/launcher.py" "$stage/usr/lib/batcave-linux-preview/"
printf '#!/bin/sh\nexec /usr/bin/python3 -I /usr/lib/batcave-linux-preview/launcher.py "$@"\n' > "$stage/usr/bin/batcave-linux-preview"
chmod 755 "$stage/usr/bin/batcave-linux-preview"
cp "$root/packaging/batcave-linux-preview.desktop" "$stage/usr/share/applications/"
cp "$root/README.md" "$stage/usr/share/doc/batcave-linux-preview/"
revision=$(git -C "$root" rev-parse HEAD)
printf 'Source: https://github.com/RideFair/BatCave-Public\nCommit: %s\nVersion: %s\nArchitecture: %s\nLive desktop validation: pending\n' "$revision" "$version" "$architecture" > "$stage/usr/share/doc/batcave-linux-preview/build-info.txt"
find "$stage" -type d -exec chmod 755 -- {} +
find "$stage/usr/lib" -type f -exec chmod 644 -- {} +
SOURCE_DATE_EPOCH=$(git -C "$root" log -1 --format=%ct)
export SOURCE_DATE_EPOCH
dpkg-deb --root-owner-group --build "$stage" "$output/batcave-linux-preview_${version}_${architecture}.deb"
(cd "$output" && sha256sum "batcave-linux-preview_${version}_${architecture}.deb" > SHA256SUMS)
printf 'Package built; it has not been installed or published as a release.\n'
