# Shared audio preview v1

This contract belongs to the isolated audio preview. Native macOS and Linux audio adapters and main-app integration are pending. Advertising input support does not imply audio support. This preview mirrors other applications' playback; it does not mute the source's existing output, capture microphones, change Bluetooth pairing, or change an OS default device.

## Trust and connections

Use a separate TCP connection on **47873** with pinned, mutually authenticated TLS 1.3. Port 47872 belongs to file transfer. Audio never occupies the input or file queues. Public pairing cards use the existing `batcave.pairing-card.v1` identity format; the temporary Windows audio preview has a separate identity and approval profile. Certificate approval permits connecting and exchanging inventories. It does not permit capture or playback.

Each source connects directly to every receiving computer. A controller can request a route between two other computers, but cannot relay audio or grant either endpoint's consent. Both computers must already approve each other and have a direct audio connection. At most eight peers and four pending handshakes are allowed per Windows preview. Admission remains reserved through authenticated hello registration. Removing an approval cancels pending handshakes and the relevant established connection.

Every transport replacement or disconnect clears audio consent, even if a peer reuses its hello session ID. The app requires a fresh Start action. Closing, locking or sleeping the Windows preview also clears consent. Selected-output failure pauses sharing without changing the destination.

## Framing and hello

Each outer frame is a one-byte kind followed by a four-byte unsigned big-endian payload length, then exactly that payload. Kind 1 is UTF-8 JSON (at most 16,384 bytes); kind 2 is a `BAA1` PCM packet (at most 4,274 bytes). Zero lengths, unknown kinds, malformed data and oversized lengths end the connection before payload allocation. Once the first byte arrives, the remainder must arrive within two seconds; an idle authenticated connection is allowed. The writer uses a 500 ms deadline and bounded independent control/audio queues.

Both endpoints send this kind-1 hello first, within five seconds of completing TLS:

```json
{
  "protocol": "batcave.audio.v1",
  "type": "hello",
  "deviceId": "demo:source",
  "sessionId": "10000000-0000-4000-8000-000000000001",
  "inventory": {
    "deviceId": "demo:source",
    "displayName": "Source computer",
    "deviceIsPrimary": false,
    "revision": 1,
    "outputs": [{
      "endpointId": "output-speakers",
      "displayName": "Built-in speakers",
      "kind": 2,
      "isConnected": true,
      "isActive": true,
      "isDefault": true,
      "canReceiveStream": true
    }],
    "supportsSystemCapture": true,
    "excludesPlaybackFromCapture": true
  }
}
```

The hello identity must equal the device approved for the presented certificate. Its inventory identity must equal the same authenticated device. Session IDs are nonempty GUIDs generated anew for each connection. Inventory revisions increase within that connection; stale or conflicting revisions are rejected by routing state. Public endpoint IDs are stable opaque identifiers, never native OS endpoint paths. Windows hashes its local endpoint IDs. Identifiers use ASCII letters, digits, `-`, `_`, `:`, or `.` and are limited to 128 characters. Display names are at most 160 characters with no control characters. An inventory has at most 32 outputs. `isActive` means available for routing, not that audio is currently playing.

Output kind values are `0` unknown, `1` Bluetooth, `2` speakers, `3` other. A backend without safe system capture must leave both capture capabilities false. A backend without playback must set `canReceiveStream` false. Bluetooth classification must come from the OS transport information.

Later controls use `{ "type": "...", "payload": { ... } }`. An `inventory` control's payload is the complete inventory. Other controls below are limited to 2,048 JSON characters, reject unknown or repeated fields, and use finite gains in `[0,2]`. These example device and endpoint IDs are synthetic.

## Route negotiation

The receiver selects an authenticated source and a local output, then presses Start. This creates a local source/output permission. It may request the source with:

```json
{"type":"subscribe","payload":{"destinationDeviceId":"demo:receiver","endpointId":"output-headset","gain":0.7}}
```

A controller may name a different approved destination. The authenticated source only offers its own application audio, and only while its own Start permission and Allow sending option are enabled. It must have a direct authenticated connection to that destination. It sends:

```json
{"type":"offer","payload":{"streamId":"20000000-0000-4000-8000-000000000001","endpointId":"output-headset","gain":0.7}}
```

The destination is always the receiving computer; the endpoint is local to it. The receiver accepts only if it has already selected this authenticated source and this exact local output and pressed Start. The receiver's chosen route gain takes precedence over the offered gain. An unsolicited offer cannot start playback.

```json
{"type":"accept","payload":{"streamId":"20000000-0000-4000-8000-000000000001","endpointId":"output-headset"}}
{"type":"reject","payload":{"streamId":"20000000-0000-4000-8000-000000000001","endpointId":"output-headset","reason":"Select this source/output and press Start on the receiver."}}
```

Only an acceptance matching a pending offer, current direct peer session, local stream ID and endpoint enables sending. A source sends one PCM frame per destination computer, even when that computer selects several outputs. Receivers independently mix accepted sources into each selected endpoint. A source's own default endpoint remains its existing local playback without a duplicate render. A different local endpoint can receive a mirror. Reciprocal computer routes are allowed only because capture excludes this app's playback and received frames cannot be forwarded.

`reject` may use the all-zero GUID when refusing a subscribe that has no stream ID yet. Reasons are short display text, not commands. A pause sends:

```json
{"type":"stop","payload":{"streamId":"00000000-0000-0000-0000-000000000000"}}
```

The zero GUID stops all incoming and outgoing streams with that authenticated peer. A nonzero GUID stops only the matching stream. Stop flushes queued PCM and invalidates pending transport frames from retired streams. A complete frame already committed to the socket cannot be retracted. Each capture restart creates a fresh random stream GUID; retired stream IDs cannot resume. If a receiver pauses and starts again while the source still permits sending, the source rotates its own stream and reoffers it to previously accepted sinks. Those receivers retain their own source/output permission, and reverse-origin audio remains independent. Inventory changes in the Windows preview pause sharing and require Start again.

## PCM and timing

The `BAA1` payload has these fields in order:

| Field | Encoding |
| --- | --- |
| Magic | Four ASCII bytes `BAA1` |
| Origin connection session | 16-byte GUID, network byte order |
| Stream ID | 16-byte GUID, network byte order |
| Sequence | Unsigned 64-bit little endian, monotonically increasing from zero, below `Int64.MaxValue` |
| Origin identifier length | One byte |
| Visited identifier count | One byte, exactly one on the wire |
| Sample count | Unsigned 16-bit little endian, exactly 960 |
| Origin identifier | ASCII bytes |
| Visited identifiers | One-byte length followed by ASCII bytes for each |
| Samples | 960 interleaved float32 little-endian values: stereo, 480 frames at 48 kHz |

Origin and its sole visited identifier must equal the authenticated sender. The session must match its hello and the stream must match an accepted offer. Samples must be finite and in `[-1,1]`. Playback is a terminal destination: received PCM never becomes a new local source. Maximum packet size is 4,274 bytes. Mixed output is clipped to `[-1,1]`.

Each uncompressed source uses about 3.1 Mbit/s before transport overhead. Queues drop old frames rather than growing: eight transport packets, eight per route and a two-packet render prefill. Native rendering pulls from the mixer on the output device clock. Missing audio becomes silence. Sender and controller queues discard frames older than 150 ms, but this does not guarantee end-to-end latency over TCP. Preview playback is best effort; synchronized speakers, clock drift correction and compressed transport require separate validation and work.

The default policy is a suggestion only. With exactly one primary computer, prefer its connected Bluetooth output; if there are several, prefer exactly one marked system default. Without an unambiguous Bluetooth output, suggest keeping every computer's existing local playback. Selecting a suggestion does not start capture or playback.
