# Security policy

This policy describes BatCave 0.3.0 and its remaining physical validation.
Windows, Mac and Linux use approved identities and authenticated transport.
Live cross-platform and fresh-device checks remain pending.
Older plaintext branches and packages are unsuitable for deployment.

## Trust boundary

Windows requires TLS 1.3 with mutual authentication, an explicitly approved
SHA-256 leaf-certificate fingerprint, and a matching device ID in the first hello.
Self-signed certificates are pinned directly; public CA names, discovery results,
and network addresses do not grant trust. Certificate expiry is enforced.
Unpairing disconnects that identity. After reconnect, sharing requires a fresh
user action.

An approved peer can affect the user's input and optional text clipboard while
sharing is active. Treat pairing approval like granting remote-control access.
This design does not protect against malware already running as the same OS user.
Windows device keys use the current user's CNG provider with export disabled.
Mac keys remain in Keychain; Linux keys remain in the desktop Secret Service.
Uninstall preserves pairing state for reinstall; it does not revoke peers.
Start fresh requires explicit confirmation and backs up that state, preserving
its original protected key. Backups remain sensitive and must stay private.

Incoming JSON frames are limited to 256 KiB and decoded clipboard text to 64 KiB.
Outbound Windows queues have message and byte limits and disconnect on overflow.
Windows input dispatch also bounds queued UI work and invalidates it when input
is paused, so re-enabling cannot replay input received under an earlier activation.
Key codes and typed text are excluded from routine logs. Local diagnostic logs
can still identify devices and must not be uploaded without review.

Windows 0.2.3 removes older per-key debug logging. Logs made by earlier Windows
versions may contain typed text; updating does not remove those existing files.
Keep them private and review them before sharing diagnostics.

The Mac guided app connects through pinned mutual TLS. Its legacy command-line
network path is disabled by default; explicit development access permits loopback
only. Windows installers exclude the legacy plaintext network mode.

## Nearby pairing

Version 0.3.0 includes the [nearby pairing protocol](Universal/Protocol/Nearby_Pairing.md).
Physical-device validation remains pending; older 0.2.x installers use manual pairing.

Nearby discovery advertises public device identity information on the local IPv4
network. It grants no trust. A separate, time-limited TCP endpoint exchanges only
public identity material. Fresh nonce commitments bind the comparison code to
both identities, and separate signatures prove key possession and approval.
Both screens must explicitly approve all eight matching digits before a peer pin
is saved. This bootstrap carries no input and provides no confidentiality for its
public metadata. The input connection still requires pinned mutual TLS 1.3.

**Link and share** explicitly authorizes the first matching input session for
30 seconds. That consent exists only in memory and is consumed once. Cancellation,
Pause, navigation, role changes, lock, sleep, disconnect or session replacement
revoke it. Reconnect does not restore it. Existing pins cannot be silently replaced
by discovery or nearby pairing. This is not an independently audited protocol;
shared vectors, adversarial fixtures and native interoperability checks are
release requirements, not a substitute for external security review.

## Updates and release checks

Build with a supported, patched .NET 10 SDK/runtime. Self-contained installers
must be rebuilt and redistributed for runtime security updates. Before a public
release promoted as fully validated, complete physical cross-platform recovery
and clean-device checks and obtain platform signing. Version 0.3.0 packages are
available for those installation tests after automated checks and package privacy
inspection. Windows is unsigned; Mac is ad-hoc signed and not notarized. This is
not an independent security audit.

## Reporting

If GitHub's **Report a vulnerability** option is available, use it for private
disclosure. Otherwise contact the maintainer, Tyler Harris, on X at
[@tyler_x_harris](https://x.com/tyler_x_harris) or [@ridefair](https://x.com/ridefair)
to arrange a private disclosure channel. Keep exploit details and sensitive data
out of public posts and issues.
Do not attach pairing state, keys, typed input, or unredacted local logs.

## Source publication

Publishing a cleanup commit does not erase old commits, tags, or pull-request
references. Keep private development history private and publish a reviewed
snapshot with fresh history, or carry out a separately approved history cleanup.
