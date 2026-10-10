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

## Separate bulk channel

Do not multiplex file data into the input FIFO. The control-session server also
listens on encrypted bulk port 47872; its client connects using the control
socket's remote address, never a peer-supplied hostname. Validate the same mutually
approved certificate pins and certificate lifetime as input transport.

After common capability negotiation, the client sends `file.channelRequest` on
the control session with `channelSchema: "batcave.file.channel.v1"`. The server
responds with `file.channelReady` containing that schema, `port: 47872`, random
128-bit `channelId`, and a cryptographically random 256-bit base64url `ticket`.
The ticket is single-use, expires after 60 seconds, and is bound to the approved
peer and exact active control-session generation. Do not log or persist it.
Allow one pending ticket and one bound bulk socket per control session, at most
four concurrent TLS handshakes per listener, and one pending request from a client.
Consume a ticket atomically only after the matching pinned TLS identity supplies
a valid first frame. Concurrent or later replay must fail. Close an old bound
socket and cancel its batches before allowing a replacement.

The first bulk message, within five seconds of TLS establishment, is
`file.channelOpen`: channel schema, channel ID, ticket and the sender's `deviceId`.
Validate all fields against the control-issued ticket and certificate identity.
Return `file.channelOpened` with channel ID after successful binding. Reject
missing, replayed or mismatched tickets. All remaining file messages use this
bulk socket, which carries both directions. Closing/replacing the control session
also closes bulk and cancels its transfers. Bound concurrent handshakes, frames
and per-peer queues just as for input transport.

This separate socket protects input scheduling from bulk FIFO head-of-line delay;
cap chunk throughput and test network saturation as well. Audio uses its own
future negotiated channel and cannot take ownership of this file socket.

## Wire flow

Keep the `batcave.input.v0` envelope and bounded newline-delimited JSON framing.
Every batch message contains a random session-scoped `transferId`; channel
handshake messages have no batch yet and do not contain one. Initially allow
one batch in each direction, at most 32 regular files, 1 GiB per file and 4 GiB per
batch. Directories, links and special files are not supported in this first version.
Pending offers count toward the one-batch-per-direction limit before consent;
reject additional offers as busy without creating more state or staging files.

| Message | Payload |
| --- | --- |
| `file.offer` | Transfer ID and entries with random `fileId`, base `name`, `sizeBytes`, `sha256`; optional `iconPreview` descriptor |
| `file.icon` | Transfer ID, file ID and `iconPreview` PNG data for a declared icon |
| `file.accept` | Transfer ID and accepted file IDs; no destination paths |
| `file.reject` | Transfer ID and a short reason code |
| `file.chunk` | Transfer ID, file ID, exact byte `offset`, base64 `data` of at most 32 KiB decoded |
| `file.chunkAck` | Transfer ID, file ID, next expected offset |
| `file.finish` | Transfer ID and file ID; sent only after all bytes are acknowledged |
| `file.complete` | Transfer ID and file ID; receiver has verified size/hash and committed the destination file |
| `file.cancel` | Transfer ID and a short reason code; valid in either direction |

One chunk may be outstanding per direction on bulk. Prioritize cancel over file
chunks; no unbounded file queue or buffering entire files in memory.
File data starts only for explicitly accepted IDs. IDs must be unique; an
acceptance is a nonempty subset of the offer. Complete one file before starting
the next in that batch. Zero-byte files go directly from acceptance to finish.
An ack's `nextOffset` must equal the exact end of the last outstanding chunk;
duplicates or mismatches are protocol failures. All size/offset sums use checked
64-bit integers. Hash before offering off the input/UI thread.
Cancel on session replacement, disconnect, unpair, lock, sleep or application exit.
Reconnect leaves transfers idle; resume is outside v1. Unexpected IDs, offsets,
oversized base64 or excess data reject the transfer. Do not expose local paths
in errors, acknowledgements or logs. Source changes during hashing/reading cancel
the transfer rather than deliver a file with an incorrect announced hash.

Timeouts: channel request 10 seconds; TLS/first channel frame 5 seconds; pending
offer acceptance 120 seconds; chunk acknowledgement 10 seconds; finish/completion
60 seconds; otherwise no-progress bulk idle 30 seconds. Acceptance/completion
use their own deadlines instead of the general idle deadline. Cancel expired
batch state and owned staging; idle channels close while input stays connected.
Initial throughput cap is 1 MiB/s per direction with a 64 KiB maximum token-bucket
burst, configurable downward. Never increase it automatically without input
latency testing. Separate sockets do not guarantee prompt delivery on a saturated
network; pause bulk when control delivery stalls and test emergency release under
load before enabling transfer. File hashing, decoding and disk I/O cannot run on
the native input-hook thread.

## Payload examples and encodings

These are payloads inside the normal envelope, keyed below by message type for
reference. UUIDs are canonical lowercase UUIDv4 strings. A `deviceId` remains the
exact approved card ID. SHA-256 is 64 lowercase hex characters. Chunk/PNG data uses
standard padded base64; a ticket is 32 random bytes encoded as unpadded base64url
(43 ASCII characters). The repetitive sample ticket/IDs below are examples only;
production values require a cryptographically secure generator.

```json
{
  "file.channelRequest": { "channelSchema": "batcave.file.channel.v1" },
  "file.channelReady": { "channelSchema": "batcave.file.channel.v1", "channelId": "11111111-1111-4111-8111-111111111111", "port": 47872, "ticket": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" },
  "file.channelOpen": { "channelSchema": "batcave.file.channel.v1", "channelId": "11111111-1111-4111-8111-111111111111", "ticket": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "deviceId": "approved-device-id" },
  "file.channelOpened": { "channelId": "11111111-1111-4111-8111-111111111111" },
  "file.offer": { "transferId": "22222222-2222-4222-8222-222222222222", "entries": [ { "fileId": "33333333-3333-4333-8333-333333333333", "name": "example.txt", "sizeBytes": 0, "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" } ] },
  "file.accept": { "transferId": "22222222-2222-4222-8222-222222222222", "acceptedFileIds": [ "33333333-3333-4333-8333-333333333333" ] },
  "file.reject": { "transferId": "22222222-2222-4222-8222-222222222222", "reason": "user_declined" },
  "file.chunk": { "transferId": "22222222-2222-4222-8222-222222222222", "fileId": "33333333-3333-4333-8333-333333333333", "offset": 0, "data": "aGVsbG8=" },
  "file.chunkAck": { "transferId": "22222222-2222-4222-8222-222222222222", "fileId": "33333333-3333-4333-8333-333333333333", "nextOffset": 5 },
  "file.finish": { "transferId": "22222222-2222-4222-8222-222222222222", "fileId": "33333333-3333-4333-8333-333333333333" },
  "file.complete": { "transferId": "22222222-2222-4222-8222-222222222222", "fileId": "33333333-3333-4333-8333-333333333333" },
  "file.cancel": { "transferId": "22222222-2222-4222-8222-222222222222", "reason": "cancelled" },
  "file.icon": { "transferId": "22222222-2222-4222-8222-222222222222", "fileId": "33333333-3333-4333-8333-333333333333", "iconPreview": { "format": "image/png", "data": "<base64 PNG>" } }
}
```

The chunk/ack shapes above illustrate a nonempty file; the empty-file offer skips
them. Icon data is illustrative, not a valid PNG. The outer `sourceDeviceId`
always matches the pinned peer; channel open's `deviceId` must match it as well.
The server enforces ticket expiry with its own monotonic deadline.

Reason codes are `user_declined`, `unsupported`, `invalid_offer`, `invalid_name`,
`too_large`, `busy`, `timeout`, `cancelled`, `source_changed`,
`destination_unavailable`, `name_collision`, `integrity_failure`, `protocol_error`,
`session_closed`, and `io_error`. No free-form remote error text or local paths.
Reject malformed channel opens by closing bulk without affecting authenticated
input. Protocol failures on bulk cancel its batches and close that socket.

## Receiving safely

Treat the offered name as untrusted. Accept a plain basename, at most 240 UTF-8
bytes; refuse traversal, separators, drive/stream syntax, control characters,
reserved device names and platform-invalid endings. The receiver may propose a
safe local name. Never accept an absolute path or hidden destination from a peer.

Create an owned temporary regular file in the chosen directory with exclusive
creation. Use directory-relative operations and link/reparse-point defenses for
the platform; do not follow a substituted destination. Write sequential chunks
with a bounded buffer and running SHA-256. Verify final length and hash, then
commit on the same filesystem without overwriting an existing destination.
Before committing, re-read and hash the stored bytes from the same owned staging
descriptor and revalidate its identity. The incoming-stream hash alone is not
proof of the bytes on disk. A name
collision requires a user-approved new name. Cleanup removes only this transfer's
owned staging files. A received file is never opened or executed automatically.
Show the chosen receiver name before acceptance, including any explicitly approved
suffix. A collision discovered later requires another user decision or cancellation.

## File icons during drag and transfer

The optional offer's `iconPreview` descriptor is
`{ "format": "image/png", "width": 128, "height": 128, "sizeBytes": 0, "sha256": "<hash>" }`,
with actual dimensions and compressed byte size. A separate `file.icon` carries
`iconPreview: { "format": "image/png", "data": "<base64>" }` matching that hash.
Send at most one icon per offered file. A receiver may show native icons until
the preview arrives and retain only a bounded 128 KiB preview cache. This counts
encoded PNG plus decoded RGBA (`width × height × 4`); evict before decoding and
bound visible shelf entries separately. Do not put
all raster data in the offer: a batch could exceed the frame limit.
Capture the source file's visible icon locally and convert it to a PNG of at most
128 × 128 pixels and 32 KiB encoded bytes. Strip metadata before sending; no source
paths, icon-library references, resource forks or executable icon content cross
the network. The original file contents remain unchanged.

Validate the decoded size and PNG dimensions before allocating an image. Enforce
a decoded pixel budget, normalize through the platform image decoder, and reject
malformed previews. Descriptor dimensions, encoded size and hash must match exactly.
Missing or malformed optional icons fall back without cancelling valid file
content. Frame-limit/unknown-transfer protocol violations still close bulk.
Use the preview only as passive artwork in the drag overlay
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
