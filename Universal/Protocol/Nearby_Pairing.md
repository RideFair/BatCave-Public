# Nearby pairing v1 (implementation review)

Open BatCave on both computers, choose a nearby computer, and confirm the same
eight digits on both screens. The user never moves an identity file or types a
network address. Windows, Mac and Linux use this same flow. Arrangement is
optional; the initiating computer supplies input with the other screen on its
right by default. Existing approved identities survive upgrades.

This contract is under implementation review, not a claim about current packages.
Native implementations must pass the shared vectors and interoperability checks
before this feature is released. Input still uses the existing pinned mutual TLS
1.3 channel. Discovery and pairing cannot carry input or clipboard messages.

## Discovery

Send UTF-8 JSON UDP announcements to IPv4 multicast `239.255.47.87:47872`, TTL 1,
every 3 seconds on active LAN interfaces. Join on each interface; refresh interface
membership after network changes. There is no request/response amplification.
Bind with address reuse so isolated installations can coexist. Announcements are
at most 1024 bytes, contain no secrets, and have exactly these fields:

```json
{"type":"batcave.nearby","version":1,"deviceId":"windows:example","displayName":"Office computer","platform":"windows","fingerprint":"64 uppercase hex digits","inputPort":47871,"pairingPort":47873,"pairing":true}
```

`platform` is `windows`, `macos` or `linux`. Ports are integers 1–65535 and distinct.
Use the datagram source address, never an address supplied in JSON. Ignore own
identity, invalid/unicast-ineligible source addresses, malformed/duplicate fields,
and unrecognized versions. Keep at most 64 entries, expire after 12 seconds, and
bound receive processing. Device names are untrusted labels, not authority.
Do not replace a saved certificate pin based on an announcement. An approved
peer's announced address/port may initiate only a pinned TLS connection.

The pairing listener is available only while the user has the foreground Nearby
computers screen open, for up to 180 seconds per local opening/refresh. Closing
that screen, locking, sleeping, pausing or quitting cancels pending pairing.
Known peers may advertise `pairing:false` for rediscovery. Actual input and
pairing ports are advertised; no platform-specific default may be assumed.

## Pairing transport and order

A separate TCP listener defaults to 47873. Bind failure is visible; do not silently
fall back to the input listener. Bootstrap sends public information only and is
not TLS: possession and user approval are established below. No private key,
password or input data may enter this endpoint. One session at a time, a maximum
of six incoming attempts per minute, at least five seconds between attempts.
Handshake deadline is 10 seconds; approval deadline is 120 seconds total. No
automatic retry of rejected/failed pairing. The user must initiate another attempt.

Frames are newline-terminated strict UTF-8 JSON objects, at most 16384 bytes before
the newline, maximum nesting depth 8. Duplicate or unknown properties, CR, invalid
types, and messages out of order terminate the session. Every frame has `type`,
`version:1` and `session` (32 lowercase hex digits, fresh random 16 bytes chosen by
the initiator). The responder echoes that session. `i` and `r` below are roles in
this exchange, not OS names. The initiator supplies input in the default layout.

1. i sends `pair.commit` with `commitment` (64 uppercase hex digits).
2. r sends its `pair.commit` with its own commitment.
3. i sends `pair.reveal` with `nonce` (64 uppercase hex), `card` (existing
   `batcave.pairing-card.v1` object) and `inputPort`.
4. r validates the commitment before sending its own `pair.reveal`.
5. Each side sends `pair.proof` with `signature` (strict base64), then reads and
   verifies the other's proof before presenting the comparison code.
6. After explicit local confirmation of **all eight matching digits**, send
   `pair.approve` with its separate `signature`. Each side must both locally
   approve and verify the remote approval before saving the peer's identity.
   A declined request closes the socket. No implicit approval on timeout.

The initiator checks the revealed responder ID and fingerprint against the
selected discovery entry. Both reject their own ID/fingerprint, expired or weak
keys, and conflicting existing pins. Never overwrite an existing different pin
through nearby pairing. Require explicit removal separately.

The UI remains inline, cancellable, and specific to this session. An unsolicited
request cannot steal focus or open repeated dialogs. Cancellation or connection
loss invalidates the pending approval immediately. A signature proves possession;
it does not replace local confirmation. Trust saving can be asymmetric if the
socket fails after one side receives approval; show an actionable retry state,
never silently approve the other side later.

## Exact cryptographic encoding

`pack(parts...)` concatenates each part as a 4-byte unsigned big-endian byte length
followed by its bytes. Strings use strict UTF-8 with no normalization. Numeric
ports are **decimal ASCII strings**. Session and nonce hex are decoded to raw
bytes. DER is decoded from the card. The card schema is fixed and its SHA-256
fingerprint must match DER. IDs and names contain 1–128 Unicode scalar values,
no control, format, surrogate, line/paragraph separator characters; at most 512
UTF-8 bytes. No trimming or case folding of names/IDs.

```
D(role) = pack("batcave.pair.v1.identity", sessionBytes, role,
               deviceId, displayName, certificateDER, decimalInputPort, nonceBytes)
commitment(role) = SHA256(D(role))
T = SHA256(pack("batcave.pair.v1.transcript", D("i"), D("r")))
S = SHA256(pack("batcave.pair.v1.code", T))
code = unsigned-big-endian-first-4-bytes(S) modulo 100000000
```

Display exactly eight zero-padded decimal digits, grouped `1234 5678`. Both
commitments precede either nonce reveal. Each nonce is an independent CSPRNG
32-byte value, generated once per session and never reused, including after
failure. Binding identity and nonce in commitments prevents adapting a public
key or nonce after learning both honest contributions to grind a matching code.
Rate limits and a new user action per attempt limit repeated online guesses.

The signed **message bytes** for each stage are:

```
pack("batcave.pair.v1." + stage, T, role)
```

`stage` is `proof` or `approve`. Sign with SHA-256, RSA PKCS#1 v1.5 for RSA device
keys (3072–8192 bits), or ECDSA using ASN.1 DER `(r,s)` for P-256/P-384 keys.
The algorithm follows the certificate key type, not a network-supplied selector.
Signature verification uses that role's revealed certificate. Approval is bound
to both identities, roles, session and fresh nonces; replay in another exchange
or substitution of proof for approval fails.

The commitment approach follows the short-authentication-string rationale in
[RFC 6189 section 4.4.1.1](https://www.rfc-editor.org/rfc/rfc6189#section-4.4.1.1),
but this is a distinct public-identity pairing protocol, not ZRTP implementation
or an independently audited cryptographic protocol.

## Connection and user consent

After mutual approval, automatically connect to the authenticated card's input
port at the observed socket address using unchanged pinned mutual TLS. Remember
the role and screen arrangement. Discover address changes without changing pins.
Navigation and reconnection never activate input. If an implementation combines
pairing approval with starting the initial session, it must explicitly say so,
bind that single-use consent to this pairing and the first matching authenticated
input session, expire it after 30 seconds, and invalidate it on cancellation,
Pause, lock, sleep or disconnect. No consent may be persisted for automatic replay.

When discovery is blocked, explain same-network requirements and expose a retry
action. Do not turn an empty device list into a blank dropdown or a mandatory file
transfer procedure. System permission requests must explain their purpose.
