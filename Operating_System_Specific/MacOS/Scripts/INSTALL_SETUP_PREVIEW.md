# Install a locally built device setup

Run these commands from `Operating_System_Specific/MacOS` on macOS 13 or newer.
The source installer requires the Xcode command-line tools and Swift. Build the
guided app from a clean commit first:

```bash
bash Scripts/build_setup_preview.sh
bash Scripts/install_setup_preview.sh --validate-only
bash Scripts/install_setup_preview.sh
```

The installer copies the built secure app into
`~/Applications/BatCave-<version>.app` and creates
`~/Desktop/BatCave — Latest` as a native Finder alias. Open that alias manually
to link your devices. Installation does not launch or stop an app, change input
permissions, register login items, modify settings, or connect to another device.
The existing working app and older app versions remain available.

`--validate-only` checks the source, destinations and any existing alias without
creating installation folders, copying the app or updating the alias. The Swift
source wrapper may use the toolchain's normal compiler caches. `--help` describes
all installer options. To select another trusted build, pass its absolute path:

```bash
bash Scripts/install_setup_preview.sh --source '/absolute/path/BatCave.app' --validate-only
```

The source must have the secure-preview bundle and signing identity, a true guided
preview flag, an executable backend, and a version such as `0.2.0`.
Its signature and bounded bundle contents are checked before copying and checked
again in private staging. A new version is published with an exclusive atomic
rename. Repeating installation of exactly the same version is harmless; different
contents with the same app version are refused. Build a new app version
instead of replacing an app that might be running.

The desktop alias is resolved without displaying UI or mounting another volume.
An existing alias is replaced atomically only when it resolves to a valid guided
BatCave secure app. An unrelated file, folder, symlink, broken alias or alias
to another app at `BatCave — Latest` causes a refusal and is preserved. Installation
also refuses symlinked source/output paths. If alias creation is refused after a
new app was copied, that verified app remains in the versioned Applications path.

Ad-hoc signature verification checks the integrity and declared identity of this
local build; it does not authenticate a public publisher. Use only a build from
source you trust. Developer ID signing, notarization and public release validation
remain required for a supported downloadable installer.

For a disposable installer check, supply a separate home-like directory. This
does not replace the shell's `HOME` or touch the real Applications/Desktop:

```bash
installer_test_root="$(mktemp -d /private/tmp/batcave-installer-check.XXXXXX)"
bash Scripts/install_setup_preview.sh --user-home "$installer_test_root" --validate-only
bash Scripts/install_setup_preview.sh --user-home "$installer_test_root"
```

The resulting app and native alias remain inside that temporary directory for
inspection. No app is opened. Remove the temporary directory when finished.

Run the automated preservation checks against the built app with:

```bash
python3 Scripts/test_setup_preview_install.py
```

These checks compile the helper once and use disposable folders to verify native
aliases, repeat installs, version updates, unrelated desktop items, symlink refusal
and signature rejection. They remove their own fixtures and leave the real home
directory and original source app unchanged. GitHub CI runs them after packaging.

For the reproducible smoke suite after packaging, run:

```bash
python3 Scripts/test_setup_preview_install.py
```

The suite compiles the helper once, uses disposable homes under `/private/tmp`,
and cleans them afterward. It checks validation without installation writes,
native alias resolution, repeated installation, version updates, collision and
unrelated-item preservation, symlink refusal, signature tampering, and unsafe
versions. `--source '/absolute/path/BatCave.app'` selects another built
preview. The suite does not launch any app or touch the real Desktop/Applications.
