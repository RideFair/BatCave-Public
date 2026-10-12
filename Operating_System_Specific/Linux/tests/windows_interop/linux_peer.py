"""Use a real Linux/OpenSSL client, with an ephemeral encrypted test identity."""
import json
import sys
import tempfile
import time
from pathlib import Path

from batcave_linux.identity import Card, Identity
from batcave_linux.protocol import Bounds
from batcave_linux.storage import StateDirectory
from batcave_linux.transport import Connection, Inbox

class MemoryVault:
    def __init__(self): self.values = {}
    def get(self, key): return self.values.get(key)
    def put(self, key, value): self.values[key] = value
    def remove(self, key): self.values.pop(key, None)

exchange, host = Path(sys.argv[1]), sys.argv[2]
listen = len(sys.argv) > 3 and sys.argv[3] == "listen"
deadline = time.monotonic() + 20
while not (exchange / ("windows-card.json" if listen else "ready.json")).exists():
    if time.monotonic() > deadline: raise TimeoutError("Windows test listener did not start")
    time.sleep(0.02)
port = None if listen else json.loads((exchange / "ready.json").read_text())["port"]
card = Card.read((exchange / "windows-card.json").read_bytes())
with tempfile.TemporaryDirectory() as directory, StateDirectory(directory) as state:
    identity = Identity.open(state, MemoryVault(), "Linux interoperability fixture")
    (exchange / "linux-card.json.partial").write_bytes(identity.card.serialize())
    (exchange / "linux-card.json.partial").replace(exchange / "linux-card.json")
    inbox = Inbox(); connection = Connection(identity, inbox)
    if listen:
        epoch = connection.listen(card, Bounds(0, 0, 2200, 800), host, 0)
        (exchange / "linux-ready.json.partial").write_text(json.dumps({"port": connection.bound_address[1]}))
        (exchange / "linux-ready.json.partial").replace(exchange / "linux-ready.json")
    else:
        epoch = connection.connect(host, card, Bounds(0, 0, 2200, 800), port)
    events = []
    try:
        while time.monotonic() < deadline and not any(event[1] in {"connected", "disconnected"} for event in events):
            events.extend(inbox.drain()); time.sleep(0.01)
        connected = [value for generation, kind, value in events if generation == epoch and kind == "connected"]
        if len(connected) != 1 or connected[0].dictionary() != Bounds(0, 0, 1920, 1080).dictionary():
            raise RuntimeError("Cross-platform TLS/hello validation failed")
        print("PASS: Linux/OpenSSL pinned the Windows RSA-3072 identity and verified its hello over mutual TLS 1.3.")
    finally: connection.stop()
