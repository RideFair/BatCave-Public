# PipeWire audio adapter preparation

This separate branch depends on Linux input PR #2. It prepares a native PipeWire
CLI playback adapter for the audio owner's shared contract in PR #3. It does not
change the shared routing, mixer, wire format or Windows implementation.

The adapter exposes only audio sinks, hashes native names into opaque endpoint
IDs and classifies Bluetooth only from PipeWire's OS transport property. Inventory
currently reports playback and capture capabilities as false. Nothing in the
Linux desktop UI or Debian package enables audio; the package does not install
PipeWire tools. The adapter is accessible only through an explicit experimental
constructor after a caller's local consent decision.

Experimental playback uses `pw-play` with an exact object serial, a tagged BatCave
application identity and `node.dont-fallback`, `node.dont-reconnect` and
`node.dont-move` properties. A separate bounded graph monitor cancels playback
when that endpoint disappears or its identity changes. It never changes defaults
or Bluetooth pairing. Eight 10 ms stereo packets, a small kernel pipe and a
150 ms stall deadline bound buffering. Native PipeWire playback consumes the
pipe; actual output-clock pacing, underrun behavior and session-manager property
enforcement are outstanding hardware gates. Python does no realtime processing.

Capture is deliberately unavailable. A whole-sink monitor contains received
BatCave audio and cannot satisfy exclusion. The review-only capture planner finds
other application playback streams already linked to the selected sink and
excludes BatCave-tagged or owned client nodes. It refuses microphone/source nodes
and does not alter existing local playback links. This is a plan, not proof of
safe system capture. A native per-application tap must preserve local playback,
handle stream churn and demonstrate zero received-audio recapture before capture
capabilities can become true. A microphone disguised as ordinary application
playback cannot be identified from graph class alone; only source nodes are
eligible for categorical microphone exclusion at this stage.

Five tests pass on actual Ubuntu/Python with real bounded OS pipes and synthetic
consumers. They check inventory privacy, candidate exclusion, selected target
identity, queue limits and stop cleanup. They do not exercise a real PipeWire
daemon, Bluetooth device, speaker or capture backend. The existing input preview
and its live compositor gates remain unchanged.

Next integration gate: run against a private PipeWire test graph, verify the
selected-output properties under the target WirePlumber version, measure native
silence/pacing and device loss, then validate per-application capture exclusion.
The shared owner retains TLS, consent negotiation and mixing; this adapter must
receive only already authenticated and accepted mixed PCM, and its controller
must stop it on disconnect, lock, sleep and app exit. No wire capability is
enabled before those checks and real-device tests pass.

Primary references:

- [Shared audio contract](https://github.com/RideFair/BatCave-Public/blob/feature/shared-audio-preview/Universal/Protocol/Shared_Audio_Preview.md)
- [PipeWire pw-cat/pw-play](https://docs.pipewire.org/page_man_pw-cat_1.html)
- [PipeWire node properties](https://docs.pipewire.org/page_man_pipewire-props_7.html)
