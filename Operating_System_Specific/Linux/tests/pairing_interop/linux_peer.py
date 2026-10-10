"""Disposable paired bootstrap then pinned TLS; never imports a native backend."""
import json
import socket
import sys
import tempfile
import threading
import time
from pathlib import Path

from batcave_linux.identity import Card, Identity, tls_context, verify_peer
from batcave_linux.pairing import Decision, Descriptor, run
from batcave_linux.storage import StateDirectory


class MemoryVault:
    def __init__(self): self.values = {}
    def get(self, name): return self.values.get(name)
    def put(self, name, value): self.values[name] = value
    def remove(self, name): self.values.pop(name, None)


exchange, address = Path(sys.argv[1]), sys.argv[2]
initiator = len(sys.argv) < 4 or sys.argv[3] != "listen"
deadline = time.monotonic() + 25


def wait_file(name):
    path = exchange / name
    while not path.exists():
        if time.monotonic() >= deadline: raise TimeoutError("Isolated fixture expired")
        time.sleep(0.02)
    return path.read_text()


def atomic(name, value):
    path = exchange / name
    pending = path.with_name(path.name + ".partial"); pending.write_text(value); pending.rename(path)


with tempfile.TemporaryDirectory() as directory, StateDirectory(directory) as state:
    identity = Identity.open(state, MemoryVault(), "Linux pairing fixture")
    expected = Card.read(wait_file("windows-card.json"))
    with socket.socket() as bootstrap, socket.socket() as input_listener:
        bootstrap.bind(("127.0.0.1" if initiator else address, 0)); bootstrap.listen(1); bootstrap.settimeout(15)
        input_listener.bind(("127.0.0.1" if initiator else address, 0)); input_listener.listen(1); input_listener.settimeout(15)
        local = Descriptor(identity.card, input_listener.getsockname()[1])
        atomic("linux-card.json", identity.card.serialize().decode())
        atomic("linux-ready.json", json.dumps(dict(pairingPort=bootstrap.getsockname()[1], inputPort=local.input_port)))
        ready = json.loads(wait_file("windows-ready.json"))
        stream = socket.create_connection((address, ready["pairingPort"]), timeout=10) if initiator else bootstrap.accept()[0]
        def confirm(value):
            atomic("linux-code.txt", value.code)
            decision = Decision(); decision.choose(wait_file("windows-code.txt") == value.code)
            return decision
        with stream:
            peer = run(stream, identity, local, initiator, confirm,
                       lambda value: value.card == expected, threading.Event(), expected=expected if initiator else None)
        raw = socket.create_connection((address, peer.input_port), timeout=10) if initiator else input_listener.accept()[0]
        with raw, tls_context(identity, peer.card, server=not initiator).wrap_socket(raw, server_side=not initiator,
                server_hostname="batcave-fixture" if initiator else None) as tls:
            verify_peer(tls, peer.card); tls.sendall(b"batcave-pairing-fixture\n")
            result = bytearray()
            while b"\n" not in result:
                part = tls.recv(64)
                if not part or len(result) > 64: raise ValueError("Missing isolated TLS response")
                result.extend(part)
            if result != b"batcave-pairing-fixture\n": raise ValueError("Wrong isolated TLS response")
        print("PASS: Linux " + ("initiates" if initiator else "responds") + " bootstrap and pinned TLS 1.3. No native input.")
