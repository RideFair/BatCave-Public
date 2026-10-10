# Contributing to BatCave

Help is welcome. For a larger change, open an issue first so we can agree on the
behavior and avoid duplicating work already underway. Report security problems
privately using [SECURITY.md](SECURITY.md).

## Send an improvement

Fork `RideFair/BatCave-Public` on GitHub and create a focused branch in your fork.
Make the change, run the relevant checks, then open a pull request back to this
repository. Describe the problem, the resulting behavior, and what you tested.
Include a screenshot for a visible GUI change, using synthetic device details.
The maintainer reviews and merges accepted changes; contributors do not need
write access to this repository. Suggestions without code can use GitHub issues.

For work still in a platform review branch, discuss the base with its owner before
starting. Keep native code in `Operating_System_Specific/` and common contracts in
`Universal/`. Preserve third-party notices when reusing code.

## Build from source

Clone your fork for contribution work, or the public repository for a local build:

```sh
git clone https://github.com/RideFair/BatCave-Public.git
cd BatCave-Public
```

### Windows

On Windows 11 x64 with the .NET 10 SDK, from the repository root:

```powershell
dotnet run --project Operating_System_Specific/Windows/BatCaveWindowsAgent
```

See the [Windows guide](Operating_System_Specific/Windows/README.md) for pairing
and [packaging](Operating_System_Specific/Windows/Packaging/README.md) for the
self-contained installer.

### macOS

On macOS 13+ with a Swift 6 toolchain and macOS SDK:

```sh
cd Operating_System_Specific/MacOS
bash Scripts/build_setup_preview.sh
bash Scripts/install_setup_preview.sh
```

The installer creates a versioned app in your user Applications folder and a
desktop shortcut. Open it and use the guided permission/linking screens.
The [Mac guide](Operating_System_Specific/MacOS/README.md)
records signing and release verification status. Existing script names remain
compatible with earlier build instructions.

### Linux

Use a full Ubuntu 24.04 amd64 desktop with an unlocked desktop keyring and a
RemoteDesktop v2 portal supporting `ConnectToEIS`:

```sh
sudo apt update
sudo apt install git build-essential pkg-config libei-dev libeis-dev python3 \
  python3-gi python3-cryptography gir1.2-gtk-4.0 gir1.2-secret-1 dbus-x11
cd Operating_System_Specific/Linux
sh scripts/test.sh
sh scripts/package.sh
sudo apt install ./.build/BatCave-Linux-0.2.0-amd64.deb
batcave-linux-preview --diagnose
batcave-linux-preview
```

Packaging requires a clean committed checkout. Run the app as your normal desktop
user. Diagnosis reports missing session support without granting input access.
The [Linux guide](Operating_System_Specific/Linux/README.md)
records live desktop checks. Package/launcher identifiers are retained for upgrades;
use the versioned package filename produced by your checkout if it differs.

## Check your change

From the Windows repository root:

```powershell
dotnet run --project Operating_System_Specific/Windows/BridgeRegressionTests -c Release
dotnet run --project Operating_System_Specific/Windows/SecureTransportTests -c Release
dotnet run --project Operating_System_Specific/Windows/ModifierRegressionTests -c Release
dotnet run --project Operating_System_Specific/Windows/TopologyRegressionTests -c Release
```

Mac changes use `swift test --package-path Operating_System_Specific/MacOS` and
the installation checks in its guide. Linux changes use `scripts/test.sh` and
package install/removal in a disposable environment. Automated tests do not
replace actual desktop permission, recovery, and screen-crossing checks.

Before submitting, run `python Scripts/check_public_source.py` and `git diff --check`.
Scan commits for secrets as well. Keep local profiles and unredacted logs out of
pull requests.
