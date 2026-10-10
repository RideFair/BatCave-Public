# BatCave

BatCave is a desktop input-sharing project in development. The Windows 11 x64
preview uses explicitly approved device certificates and mutual TLS 1.3.
It is not yet a verified Windows/macOS release.

The Mac agent in this source snapshot has its network listener disabled.
Connections are refused by default while its authenticated transport is pending.
An explicit `--insecure-loopback-development` flag permits local experiments only.
Do not deploy older plaintext versions on a network.

## Build

Windows requires the .NET 10 SDK. Run the native app with:

```powershell
dotnet run --project Operating_System_Specific/Windows/BatCaveWindowsAgent
```

The [Windows guide](Operating_System_Specific/Windows/README.md) explains pairing
and verification. [Packaging instructions](Operating_System_Specific/Windows/Packaging/README.md)
describe the self-contained preview installer. The installer is unsigned and
secure interoperability with macOS remains a release gate.

The [Mac guide](Operating_System_Specific/MacOS/README.md) describes the development
build and its current limits. Shared routing code lives in `Universal/`.

The [monitor arrangement guide](Docs/Monitor_Layout.md) covers Windows screen
selection and saved layouts. File transfer with source-icon drag previews is
[proposed](Universal/Protocol/File_Transfer_Proposal.md); it is not enabled yet.

## Security and privacy

See [SECURITY.md](SECURITY.md) for the trust boundary and reporting guidance.
Private keys stay on their originating device; pairing cards contain public
identity data and should be exchanged through a trusted channel. Approving a
peer grants it input-sharing capabilities. Clipboard sharing is optional.

Local configuration and development records are excluded from source packages.
Run `python Scripts/check_public_source.py` before publishing a source snapshot.
That check complements a full-history secret scan; it does not certify the absence
of every vulnerability or remove data from old Git commits.

## Maintainer and contact

BatCave is maintained by Tyler Harris. You can reach Tyler on X at
[@tyler_x_harris](https://x.com/tyler_x_harris) or [@ridefair](https://x.com/ridefair).
For security concerns, follow the private disclosure guidance in
[SECURITY.md](SECURITY.md).
