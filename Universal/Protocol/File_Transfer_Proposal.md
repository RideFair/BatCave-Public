# File transfer proposal

Status: design for review; not implemented or advertised by the current agents.
Windows owns this shared contract and Windows integration. Platform agents own
their receiving filesystem and native drag/drop adapters. File transfer must not
be enabled until both peers implement and negotiate the same contract.

## Session and consent

Use the approved peer's TLS 1.3 session. A future `device.hello` may include
`capabilities: ["batcave.file.v1", "batcave.file-icon.png.v1"]`. Both peers must
advertise file transfer before sending file messages. Icon previews require their
own common capability. Older peers continue input sharing without file transfer.
Approval of input access does not imply permission to write files.

Dropping files in the sender's transfer area creates an offer to the selected
connected peer. The receiver shows the names and total size and chooses a
destination before accepting. A drop into another computer's desktop/application
is a later native adapter: it must resolve to this same consent and transfer flow,
never an alternate clipboard payload or unauthenticated file server.

## Wire flow

Keep the `batcave.input.v0` envelope and bounded newline-delimited JSON framing.
Every file message contains a random session-scoped `transferId`. Initially allow
one batch in each direction, at most 32 regular files, 1 GiB per file and 4 GiB per
batch. Directories, links and special files are not supported in this first version.

| Message | Payload |
| --- | --- |
| `file.offer` | Transfer ID and entries with random `fileId`, base `name`, `sizeBytes`, `sha256`; optional icon preview |
| `file.accept` | Transfer ID and accepted file IDs; no destination paths |
| `file.reject` | Transfer ID and a short reason code |
| `file.chunk` | Transfer ID, file ID, exact byte `offset`, base64 `data` of at most 32 KiB decoded |
| `file.chunkAck` | Transfer ID, file ID, next expected offset |
| `file.finish` | Transfer ID and file ID; sent only after all bytes are acknowledged |
| `file.complete` | Transfer ID and file ID; receiver has verified size/hash and committed the destination file |
| `file.cancel` | Transfer ID and a short reason code; valid in either direction |

One chunk may be outstanding per direction. Prioritize input messages and cancel
over file chunks; no unbounded file queue or buffering entire files in memory.
Cancel on session replacement, disconnect, unpair, lock, sleep or application exit.
Reconnect leaves transfers idle; resume is outside v1. Unexpected IDs, offsets,
oversized base64 or excess data reject the transfer. Do not expose local paths
in errors, acknowledgements or logs. Source changes during hashing/reading cancel
the transfer rather than deliver a file with an incorrect announced hash.

## Receiving safely

Treat the offered name as untrusted. Accept a plain basename, at most 255 UTF-8
bytes; refuse traversal, separators, drive/stream syntax, control characters,
reserved device names and platform-invalid endings. The receiver may propose a
safe local name. Never accept an absolute path or hidden destination from a peer.

Create an owned temporary regular file in the chosen directory with exclusive
creation. Use directory-relative operations and link/reparse-point defenses for
the platform; do not follow a substituted destination. Write sequential chunks
with a bounded buffer and running SHA-256. Verify final length and hash, then
commit on the same filesystem without overwriting an existing destination. A name
collision requires a user-approved new name. Cleanup removes only this transfer's
owned staging files. A received file is never opened or executed automatically.

## File icons during drag and transfer

The optional `iconPreview` is `{ "format": "image/png", "data": "<base64>" }`.
Capture the source file's visible icon locally and convert it to a PNG of at most
128 × 128 pixels and 32 KiB encoded bytes. Strip metadata before sending; no source
paths, icon-library references, resource forks or executable icon content cross
the network. The original file contents remain unchanged.

Validate the decoded size and PNG dimensions before allocating an image. Enforce
a decoded pixel budget, normalize through the platform image decoder, and reject
malformed previews. Use the preview only as passive artwork in the drag overlay
and transfer list; fall back to a native file-type icon if unsupported. The first
version does not alter the destination file's association or install custom icons.
Persistent custom per-file icons need a separately reviewed native metadata adapter.

## Release tests

Before enabling the capability, test real TLS transfer in both directions with
an independently computed hash, rejection before any staging write, collisions,
path/link attacks, duplicate/out-of-order chunks, size/hash failures, disconnect
cleanup and malformed icons. Test that a large transfer cannot delay emergency
input release. Native cross-computer drag/drop needs live Windows, Mac and Linux
desktop testing beyond core protocol or filesystem tests.
