# Shared audio core

This .NET 10 library supplies platform-independent routing and bounded mixing. It starts no capture or playback. Microphone capture is outside this protocol. Hosts must provide mutually authenticated device inventories and separately request audio consent before `SetConsent(true)`.

`AudioRoutingPolicy.SuggestDefaults` suggests the primary computer's single available Bluetooth output. When several Bluetooth outputs are available, exactly one system-default Bluetooth output takes precedence. Without an unambiguous output, each computer keeps its local audio until the user chooses. Suggestions never change system defaults or enable sharing.

Select sources and outputs with `AudioRoute`. A source may mirror to several outputs and several sources may mix at one output. The source's native output stays unchanged. Selecting that same source's local default output is passthrough and produces no duplicate playback. Selecting another local endpoint mirrors to that endpoint. Unplugging a selected endpoint pauses its route; audio never falls back to an unintended speaker.

Hosts must exclude BatCave playback from system capture. Capture capabilities default to false and must be advertised explicitly by a supported backend. Only frames with an origin bound to their authenticated sender and current device/stream session are accepted. Received audio is final playback and cannot be relayed as local audio. These constraints allow reciprocal device routing without recapturing remote streams. Route selection requires source capture exclusion capability.

Every packet is 480 stereo frames at 48 kHz, using 960 finite interleaved float samples in `[-1,1]`. `AudioPacketCodec` uses the `BAA1` binary format described in its source. GUIDs use network byte order; samples and sequence numbers use little endian. A packet is bounded to 4,274 bytes. Transport must enforce this cap before allocating a frame and authenticate inventory, stream start, and PCM together.

Each route holds at most eight packets (80 ms), with a two-packet prefill. Missing packets become silence; overload drops the oldest audio. Mixing applies route and output gains before clipping to `[-1,1]`. Independent output queues let several outputs render the same source.

Use a new random stream GUID for every capture start. Revocation and authenticated session replacement discard queued audio and cancel `SharingToken`. Permission does not survive reconnection. Retired stream GUIDs cannot resume; the bounded stream-ID registry requires reconnecting after 1,024 capture starts in one authenticated session. Inventory revisions reject stale or conflicting data. Endpoint IDs must be opaque public IDs, never native OS endpoint paths.

Run regression checks with `dotnet run --project Operating_System_Specific/Windows/AudioCoreTests -c Release`. The tests run on any .NET 10 platform without accessing audio devices.

The separate [Windows audio preview](../../Operating_System_Specific/Windows/AudioPreview/README.md) supplies an explicit source/output matrix and temporary pinned public-card pairing on port 47873. Receiving hosts must enable audio, and a local source must separately consent to sending before capture starts. It uses an isolated AudioPreview profile and keeps original local playback. Main-app identity integration and native macOS/Linux audio adapters remain pending. Synthetic tests do not establish live hardware playback, capture, unplug recovery, or synchronized multi-speaker output.
