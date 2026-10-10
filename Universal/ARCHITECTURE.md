# Architecture

BatCave routes a virtual cursor across configured display boundaries. Shared
topology code maps normalized positions between display edges; native backends
capture raw relative movement and materialize the resulting cursor position.

The transport must authenticate a peer before its messages can affect the OS.
The Windows desktop profile owns device identity, explicit pairing approval,
and mutual TLS. A protocol hello identifies a device within that authenticated
session; the hello is not itself authentication.

Input state belongs to one session. Disconnect or pause releases held input,
and queued work from an obsolete session must not act after reconnect.
The current layout supports one selected approved peer at a time. Discovery and
more precise monitor placement remain future work.

The Mac authenticated transport is pending, so its network listener is disabled.
Message shapes are documented in [Protocol/README.md](Protocol/README.md).
