# Native Windows backend

This .NET 10 WinForms backend captures raw input and routes it through shared
topology code. The desktop profile owns explicit pairing and TLS transport.
The [Windows guide](../README.md) is the entry point for building and testing.

Development commands require `--insecure-loopback-development`. Plaintext
connections cannot leave the local computer. Release installers compile with
`SECURE_DESKTOP_ONLY` and always open the secure desktop profile.
