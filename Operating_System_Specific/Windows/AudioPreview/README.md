# BatCave Audio Preview for Windows

This separate Windows 11 preview mirrors application audio between approved computers. It preserves the existing BatCave input app and uses its own `AudioPreview` profile. It does not change your system's default output, pair a Bluetooth device, mute an application, or capture a microphone. Closing or disabling the preview stops its audio sessions.

Extract the complete Windows x64 ZIP into a fresh folder, then double-click `BatCave.AudioPreview.exe`. Keep the other files beside the executable; the .NET runtime is included. This is a portable preview, not an installer or a stable release. You can leave the working mouse and keyboard app running. The separate profile is `%LOCALAPPDATA%\BatCave\AudioPreview`.

Each preview has its own identity for now. Export its public pairing card, transfer that card to the other computer, and approve it there after comparing the full certificate fingerprint through a trusted channel. Repeat in the other direction. A pairing card contains a public certificate; never copy identity files or private keys. Connect to the other computer using its local network address. The separate audio connection uses TCP port **47873** with mutually authenticated TLS. If Windows asks for firewall access, enable access only on the network you intend to use. Existing input pairing does not automatically grant this temporary audio preview access.

Enable audio on every receiving computer. In the routing matrix, check the source and connected output you want. A source computer must also explicitly allow **sending its audio** before its application audio is transmitted to another computer. Pressing Start for a local mirror authorizes capture for that selected local output. Pairing, connection, a suggested route, and an output selection do not grant capture consent. Reconnecting requires enabling audio again. Check the sending and receiving computers when a selected route produces silence. The volume control sets this computer's streamed playback level; receiving computers choose their own levels.

Choose one computer as primary, normally the PC hosting your Bluetooth connection. If it has a clearly selected connected Bluetooth output, the preview suggests sending other computers' audio there. With no suitable Bluetooth output, each computer keeps its normal local playback. You can select any advertised connected output instead, combine several sources at one output, or mirror a source to several computers' speakers. Playback already occurring on a source's default endpoint is retained without replaying a duplicate copy. Selecting another local output mirrors there as well. Original local playback remains audible while mirrored audio plays remotely; this preview does not silently mute it.

Windows captures other applications' playback while excluding this preview and its child processes. That exclusion prevents audio received from another computer from being captured and sent back. Unknown output transport is displayed as unknown rather than guessed from a device name. If a selected output disconnects, its route stops instead of switching to an unintended speaker. Refresh the output list and select the reconnected device. Bluetooth hardware introduces its own delay, and multiple outputs are not guaranteed to be synchronized in this preview.

The shared routing core and Windows native backend have automated synthetic checks. Live Windows capture, playback, and unplug validation remain pending. Native macOS and Linux audio adapters are still pending, so those platforms cannot participate in this audio preview yet. Integration with the main app's identity and GUI is also pending. This preview must not be presented as completed cross-platform audio support. Only one audio preview may run in a Windows session, so its capture cannot pick up a second preview's playback.

From source, use .NET 10 to build the project or run the synthetic checks:

```powershell
dotnet build Operating_System_Specific/Windows/AudioPreview -c Release
dotnet run --project Operating_System_Specific/Windows/AudioCoreTests -c Release
dotnet run --project Operating_System_Specific/Windows/AudioNativeTests -c Release
```

For a portable self-contained package, pass a fresh output directory:

```powershell
./Operating_System_Specific/Windows/AudioPreview/build-preview.ps1 -OutputDirectory ./artifacts/audio-preview
```

The packaging script builds in a fresh ignored directory and includes no installed profile or pairing material. It writes a ZIP, SHA-256 checksum, and build manifest. It performs no installation and does not launch the application. A source ZIP without Git metadata reports no source commit in the manifest. See [the shared audio contract](../../../Universal/Protocol/Shared_Audio_Preview.md) and [Windows native backend notes](Native/README.md) for implementation details.
