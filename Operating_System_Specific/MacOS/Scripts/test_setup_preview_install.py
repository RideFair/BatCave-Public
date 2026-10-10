#!/usr/bin/env python3
"""Exercise installation in disposable homes without opening any app."""

import argparse
import hashlib
import pathlib
import plistlib
import subprocess
import sys
import tempfile


SCRIPT_DIRECTORY = pathlib.Path(__file__).resolve().parent
ALIAS_NAME = "BatCave — Latest"
ALIAS_INSPECTOR = r"""
import Foundation
let alias = URL(fileURLWithPath: CommandLine.arguments[2])
if CommandLine.arguments[1] == "create" {
    let target = URL(fileURLWithPath: CommandLine.arguments[3])
    try URL.writeBookmarkData(target.bookmarkData(options: .suitableForBookmarkFile,
        includingResourceValuesForKeys: nil, relativeTo: nil), to: alias)
} else {
    let data = try URL.bookmarkData(withContentsOf: alias)
    var stale = false
    let target = try URL(resolvingBookmarkData: data, options: [.withoutUI, .withoutMounting],
        relativeTo: nil, bookmarkDataIsStale: &stale)
    print(try alias.resourceValues(forKeys: [.isAliasFileKey]).isAliasFile == true ? "native-alias" : "other")
    print(target.path)
}
"""


def require(condition, detail):
    if not condition:
        raise RuntimeError(detail)


def run(arguments, succeeds=True):
    result = subprocess.run([str(argument) for argument in arguments], text=True, capture_output=True)
    require((result.returncode == 0) == succeeds,
            f"Unexpected command result: {arguments!r}\nExit: {result.returncode}\n"
            f"{result.stdout}{result.stderr}")
    return result


def snapshot(directory):
    # Do not follow symlinks; preserve inodes as well as content in refusal checks.
    records = []
    for path in sorted(directory.rglob("*")):
        status = path.lstat()
        contents = None
        if path.is_symlink():
            contents = str(path.readlink())
        elif path.is_file():
            digest = hashlib.sha256()
            with path.open("rb") as stream:
                for chunk in iter(lambda: stream.read(65536), b""):
                    digest.update(chunk)
            contents = digest.hexdigest()
        records.append((str(path.relative_to(directory)), status.st_ino,
                        status.st_mode, status.st_size, contents))
    return records


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=pathlib.Path,
                        default=SCRIPT_DIRECTORY.parent / "Build/SetupPreview/BatCave Preview.app",
                        help="built secure preview app (default: Build/SetupPreview/BatCave Preview.app)")
    options = parser.parse_args()
    require(sys.platform == "darwin", "These installer checks require macOS.")
    source = options.source.absolute()
    source_binary = source / "Contents/MacOS/BatCaveMacOSAgent"
    original_executable = hashlib.sha256(source_binary.read_bytes()).hexdigest()
    info = plistlib.loads((source / "Contents/Info.plist").read_bytes())
    version = info["BatCavePreviewVersion"]
    prefix = info["CFBundleShortVersionString"]

    with tempfile.TemporaryDirectory(prefix="BatCave-installer-smoke-", dir="/private/tmp") as temporary:
        root = pathlib.Path(temporary)
        helper = root / "install-helper"
        inspector = root / "inspect-alias"
        inspector_source = root / "InspectAlias.swift"
        inspector_source.write_text(ALIAS_INSPECTOR)
        run(["/usr/bin/xcrun", "swiftc", SCRIPT_DIRECTORY / "InstallSetupPreview.swift", "-o", helper])
        run(["/usr/bin/xcrun", "swiftc", inspector_source, "-o", inspector])

        def install(app_source, directory, *, succeeds=True, validate=False):
            arguments = [helper, "--source", app_source, "--user-home", directory]
            if validate:
                arguments += ["--validate-only"]
            return run(arguments, succeeds)

        def variant(destination, preview_version, build="2999"):
            run(["/usr/bin/ditto", source, destination])
            plist = destination / "Contents/Info.plist"
            data = plistlib.loads(plist.read_bytes())
            data["BatCavePreviewVersion"] = preview_version
            data["CFBundleVersion"] = build
            plist.write_bytes(plistlib.dumps(data))
            run(["/usr/bin/codesign", "--force", "--sign", "-", destination])
            return destination

        def check_alias(path, target):
            output = run([inspector, "inspect", path]).stdout.splitlines()
            require(output == ["native-alias", str(target)], f"Alias did not resolve to its app: {output!r}")

        home = root / "test-home"
        home.mkdir(mode=0o700)
        before = snapshot(home)
        install(source, home, validate=True)
        require(snapshot(home) == before, "Validation changed installation paths.")
        print("PASS validate-only creates no installation paths", flush=True)
        run(["bash", SCRIPT_DIRECTORY / "install_setup_preview.sh", "--source", source,
             "--user-home", home, "--validate-only"])
        require(snapshot(home) == before, "Wrapper validation changed installation paths.")
        print("PASS shell wrapper source override and validation", flush=True)

        app = home / "Applications" / f"BatCave Preview-{version}.app"
        alias = home / "Desktop" / ALIAS_NAME
        install(source, home)
        check_alias(alias, app)
        original_inode = app.stat().st_ino
        original_alias_inode = alias.stat().st_ino
        install(source, home)
        require(app.stat().st_ino == original_inode and alias.stat().st_ino == original_alias_inode,
                "Repeated installation replaced the app or alias.")
        require(not list(home.rglob(".batcave-preview-install-*")), "Installation left staging directories.")
        print("PASS native alias creation, same-version idempotence, staging cleanup", flush=True)

        next_version = f"{prefix}-preview.999999"
        if next_version == version:
            next_version = f"{prefix}-preview.999998"
        newer = variant(root / "Newer Preview.app", next_version)
        install(newer, home)
        new_app = home / "Applications" / f"BatCave Preview-{next_version}.app"
        check_alias(alias, new_app)
        require(app.stat().st_ino == original_inode, "Updating the alias replaced the old app.")
        require(not list(home.rglob(".batcave-preview-install-*")), "Alias update left staging directories.")
        print("PASS atomic latest-alias update preserves previous app", flush=True)

        conflicting = variant(root / "Conflicting Preview.app", next_version, build="2998")
        before = snapshot(home)
        install(conflicting, home, succeeds=False)
        require(snapshot(home) == before, "Same-version conflict changed existing files.")
        print("PASS different same-version app refused without mutation", flush=True)

        for kind in ["file", "directory", "symlink", "unrelated-alias"]:
            isolated = root / f"home-{kind}"
            (isolated / "Desktop").mkdir(parents=True, mode=0o700)
            desktop_item = isolated / "Desktop" / ALIAS_NAME
            if kind == "file":
                desktop_item.write_text("preserve this desktop item")
            elif kind == "directory":
                desktop_item.mkdir()
                (desktop_item / "original.txt").write_text("preserve this folder")
            elif kind == "symlink":
                desktop_item.symlink_to(source)
            else:
                unrelated = isolated / "original.txt"
                unrelated.write_text("unrelated file")
                run([inspector, "create", desktop_item, unrelated])
            before = snapshot(isolated)
            install(source, isolated, succeeds=False)
            require(snapshot(isolated) == before, f"Refusal changed the unrelated {kind}.")
        print("PASS unrelated regular files, folders, symlinks and aliases preserved", flush=True)

        for output in ["Applications", "Desktop"]:
            isolated = root / f"home-linked-{output}"
            isolated.mkdir(mode=0o700)
            target = root / f"linked-target-{output}"
            target.mkdir(mode=0o700)
            (isolated / output).symlink_to(target)
            before = snapshot(isolated)
            install(source, isolated, succeeds=False)
            require(snapshot(isolated) == before and snapshot(target) == [],
                    f"Refusal changed the symlinked {output} folder.")
        print("PASS symlinked output folders refused", flush=True)

        link_source = root / "Linked Preview.app"
        link_source.symlink_to(source)
        before = snapshot(home)
        install(link_source, home, succeeds=False)
        corrupt = variant(root / "Tampered Preview.app", f"{prefix}-preview.999997")
        binary = corrupt / "Contents/MacOS/BatCaveMacOSAgent"
        with binary.open("r+b") as stream:
            stream.seek(1000)
            value = stream.read(1)
            require(len(value) == 1, "Fixture executable unexpectedly too small.")
            stream.seek(1000)
            stream.write(bytes([value[0] ^ 1]))
        install(corrupt, home, succeeds=False)
        unsafe = variant(root / "Unsafe Version.app", f"{prefix}-preview.999996/escape")
        install(unsafe, home, succeeds=False)
        require(snapshot(home) == before, "Invalid source refusal changed existing installation.")
        print("PASS symlink source, tampered signature and unsafe version rejected", flush=True)

    require(hashlib.sha256(source_binary.read_bytes()).hexdigest() == original_executable,
            "Checks changed the original preview executable.")
    print("PASS original source preview unchanged; no apps launched", flush=True)


if __name__ == "__main__":
    try:
        main()
    except (OSError, RuntimeError, ValueError, KeyError) as error:
        print(f"Installer smoke check failed: {error}", file=sys.stderr)
        sys.exit(1)
