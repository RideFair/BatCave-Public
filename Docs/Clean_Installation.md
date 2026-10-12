# Test a clean installation

Download **0.3.0 on both computers** from the [front page](../README.md#download-batcave-030).
You can keep an older app for recovery, but quit every BatCave copy before installing
or launching the new one. Windows 0.2.0/0.2.1 may still be in the system tray after
closing their window; choose **Quit BatCave** there.

1. Install the new package and open BatCave. If this computer has used BatCave
   before, choose **Start fresh** and confirm the backup. On Windows and Linux this is under
   **Permissions**; Windows also has **BatCave - Start fresh** in its Start menu. Reopen BatCave
   if the action asks you to. A computer with no saved setup needs no reset.
2. Open BatCave on the other computer. Select it from nearby computers, compare
   the eight digits and choose **Codes match · Link and share** on both. Approve
   the system input permissions if asked. No file exchange or address entry is needed.
3. Move the mouse across the right screen edge and type in a disposable text
   window. Cross back, pause, then quit and reopen one app. It should reconnect
   with input paused; resume sharing explicitly on both computers.

If the permission prompt outlasts the initial approval, choose **Start sharing**
or **Enable input** on each computer. **Arrange screens** is optional. Test a held modifier and mouse
button in a disposable window before relying on the app for daily use: stopping
or disconnecting must release both.

## What Start fresh preserves

Start fresh saves the whole current app profile as a sibling backup and keeps its
original protected device key. It does not reset operating-system permissions.
The app shows where the backup was saved. Keep this folder private, just like the
original setup. Starting fresh twice must not overwrite the first backup.

A reinstall alone preserves pairing. For a fully new OS-account test, use a
separate local account and fully log out the working account first. Previously
granted system permissions may still apply; record what the OS actually asks.
Do not delete just a device key while retaining its profile.

To recover the old pairing, quit BatCave, move the new profile aside, then rename
the chosen backup to the original profile name. Do not overwrite either folder.
The original key must still be in the same OS account's protected key store.

| Platform | Original profile |
| --- | --- |
| Windows | `%LOCALAPPDATA%\BatCave\SecurePreview` |
| Mac | `~/Library/Application Support/BatCave/SecurePreview` |
| Linux | `$XDG_DATA_HOME/batcave/secure-preview`, normally `~/.local/share/batcave/secure-preview` |

Windows upgrades the installed package in place. The Mac app has a versioned
filename so older apps can remain available; copies still use the same profile
unless Start fresh is chosen. The Linux package retains its original package ID
for upgrades. None of these installers silently erase pairing state.

## Verification scope

Automated protocol and native regression checks are distinct from this physical
test. The release notes identify the exact source and package checks. Physical LAN
discovery, OS permission prompts and actual cross-platform input/recovery must be
verified on the devices being used. Linux requires a full supported desktop; WSL
is not a substitute for that test. Windows is unsigned and Mac is not notarized.
