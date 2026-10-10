import base64
import json
import os
import socket
import ssl
import tempfile
import threading
import time
import unittest
from pathlib import Path

from batcave_linux.identity import Approvals, Card, Identity, tls_context, verify_peer
from batcave_linux.input import InputSession, keycode
from batcave_linux.protocol import Bounds, FrameReader, MAX_FRAME, decode, encode, hello
from batcave_linux.storage import StateDirectory
from batcave_linux.transport import Connection, Inbox


class MemoryVault:
    def __init__(self): self.values = {}
    def get(self, key): return self.values.get(key)
    def put(self, key, value): self.values[key] = value
    def remove(self, key): self.values.pop(key, None)


class Backend:
    def __init__(self): self.events = []; self.active = False
    def ready(self): return True
    def start(self): self.active = True; self.events.append(("start",))
    def stop(self): self.active = False; self.events.append(("stop",))
    def close(self): self.active = False; self.events.append(("close",))
    def key(self, code, down): self.events.append(("key", code, down))
    def button(self, code, down): self.events.append(("button", code, down))
    def scroll(self, x, y): self.events.append(("scroll", x, y))
    def move(self, x, y): self.events.append(("move", x, y)); return x, y


class CoreTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.state = StateDirectory(self.temporary.name)
        self.vault = MemoryVault()
        self.identity = Identity.open(self.state, self.vault)

    def tearDown(self):
        self.state.close()
        self.temporary.cleanup()

    def test_identity_encryption_persistence_missing_key(self):
        data = json.loads(self.state.read("identity.json"))
        encrypted = base64.b64decode(data["encryptedKey"])
        self.assertIn(b"ENCRYPTED", encrypted)
        self.assertEqual(Identity.open(self.state, self.vault).card, self.identity.card)
        self.vault.values.clear()
        with self.assertRaisesRegex(RuntimeError, "not replaced"):
            Identity.open(self.state, self.vault)
        self.assertEqual(json.loads(self.state.read("identity.json")), data)

    def test_pairing_fingerprint_tamper_and_duplicates(self):
        card = self.identity.card.dictionary()
        self.assertEqual(Card.read(json.dumps(card).encode()), self.identity.card)
        card["certificateSha256"] = "0" * 64
        with self.assertRaises(ValueError): Card.read(json.dumps(card).encode())
        with self.assertRaises(ValueError): Card.read(b'{"schema":"a","schema":"b"}')
        with self.assertRaises(ValueError): Card.read(b"x" * 16385)

    def test_atomic_permissions_symlinks_and_no_clobber(self):
        self.state.write("settings.json", {"value": 1}, replace=False)
        with self.assertRaises(FileExistsError): self.state.write("settings.json", {}, replace=False)
        self.assertEqual(json.loads(self.state.read("settings.json")), {"value": 1})
        path = Path(self.temporary.name) / "link.json"
        path.symlink_to("settings.json")
        with self.assertRaises(OSError): self.state.read("link.json")
        os.chmod(Path(self.temporary.name) / "settings.json", 0o644)
        with self.assertRaises(PermissionError): self.state.read("settings.json")
        with self.assertRaises(ValueError): self.state.write("../outside.json", {})
        os.mkfifo(Path(self.temporary.name) / "pipe.json", 0o600)
        with self.assertRaises(PermissionError): self.state.read("pipe.json")

    def test_approval_is_explicit_and_persistent(self):
        approvals = Approvals(self.state)
        self.assertFalse(approvals.cards)
        approvals.approve(self.identity.card)
        self.assertEqual(Approvals(self.state).cards, approvals.cards)
        approvals.remove(self.identity.card.device_id)
        self.assertFalse(Approvals(self.state).cards)

    def test_bounds_and_all_handoff_directions(self):
        bounds = Bounds(-1920, -1080, 1920, 1080)
        self.assertEqual(Bounds.read(bounds.dictionary()), bounds)
        for edge in ("left", "right", "top", "bottom"):
            x, y = bounds.point(edge, 0.5)
            self.assertTrue(bounds.left <= x <= bounds.right and bounds.top <= y <= bounds.bottom)
        value = bounds.dictionary(); value["right"] += 3
        with self.assertRaises(ValueError): Bounds.read(value)
        exclusive = bounds.dictionary(); exclusive["right"] += 1; exclusive["bottom"] += 1
        self.assertEqual(Bounds.read(exclusive), bounds)
        with self.assertRaises(ValueError): bounds.point("right", float("nan"))

    def test_framing_identity_utf8_duplicates_and_limit(self):
        peer = self.identity.card.device_id
        data = encode(peer, "input.key", {"value": "text"})[:-1]
        self.assertEqual(decode(data, peer)["type"], "input.key")
        with self.assertRaises(ValueError): decode(data, "other")
        with self.assertRaises(ValueError): decode(b"\xff", peer)
        with self.assertRaises(ValueError): decode(b'{"payload":{},"payload":{}}', peer)
        a, b = socket.socketpair()
        try:
            b.sendall(b"{}\n{}\n")
            reader = FrameReader()
            self.assertEqual(reader.read(a), b"{}")
            self.assertEqual(reader.read(a), b"{}")
        finally: a.close(); b.close()
        class Large:
            def recv(self, size): return b"a" * size
        with self.assertRaises(ValueError): FrameReader().read(Large())

    def test_queue_bounded_and_disconnect_discards_input(self):
        inbox = Inbox()
        inbox.put(1, "message", {}, 1048576)
        with self.assertRaises(ValueError): inbox.put(1, "message", {}, 1)
        inbox.terminal(1, "closed")
        self.assertEqual(inbox.drain(), [(1, "disconnected", "closed")])
        inbox.put(2, "connected", {}, 32)
        inbox.terminal(1, "previous worker closed")
        self.assertEqual(inbox.bytes, 32)
        self.assertEqual(inbox.drain(), [(2, "connected", {})])

    def test_fresh_handoff_enablement_and_cleanup(self):
        backend = Backend(); session = InputSession("linux:test", backend)
        session.connected(1)
        handoff = dict(targetDeviceId="linux:test", targetDisplayId="linux:test:desktop", targetEdge="left", targetPosition=0.5)
        session.handle(1, {"type": "cursor.handoff", "payload": handoff})
        self.assertFalse(session.owns_cursor)
        session.enable(Bounds(0, 0, 1920, 1080))
        session.handle(0, {"type": "cursor.handoff", "payload": handoff})
        self.assertFalse(session.owns_cursor)
        session.handle(1, {"type": "cursor.handoff", "payload": handoff})
        self.assertTrue(session.owns_cursor)
        key = dict(virtualKey=0xA0, scanCode=42, isExtended=False, isKeyUp=False)
        session.handle(1, {"type": "input.key", "payload": key})
        session.handle(1, {"type": "input.mouseButton", "payload": {"button": "left", "isDown": True}})
        session.disconnect()
        self.assertIn(("key", 42, False), backend.events)
        self.assertIn(("button", 272, False), backend.events)
        session.connected(2)
        session.handle(2, {"type": "cursor.handoff", "payload": handoff})
        self.assertFalse(session.owns_cursor)

    def test_return_handoff_balances_holds_and_preserves_arming(self):
        backend = Backend(); session = InputSession("linux:test", backend)
        session.connected(1); session.enable(Bounds(0, 0, 1920, 1080))
        session.handle(1, {"type": "cursor.handoff", "payload": dict(targetDeviceId="linux:test",
                       targetDisplayId="linux:test:desktop", targetEdge="top", targetPosition=0.2)})
        session.handle(1, {"type": "input.mouseButton", "payload": {"button": "left", "isDown": True}})
        session.handle(1, {"type": "cursor.handoff", "payload": {"targetDeviceId": "windows:test"}})
        self.assertFalse(session.owns_cursor); self.assertTrue(session.armed)
        self.assertIn(("button", 272, False), backend.events)

    def test_failed_release_or_stop_closes_virtual_devices(self):
        class FailedBackend(Backend):
            def stop(self): raise RuntimeError("Emulation stop failed")
        backend = FailedBackend(); session = InputSession("linux:test", backend)
        session.armed = session.owns_cursor = True
        session.keys.add(42); session.buttons.add(272)
        session.disconnect()
        self.assertFalse(session.armed or session.owns_cursor or session.keys or session.buttons)
        self.assertIn(("key", 42, False), backend.events)
        self.assertIn(("button", 272, False), backend.events)
        self.assertIn(("close",), backend.events)
        session.epoch = 1; session.armed = True
        session.handle(1, {"type": "cursor.handoff", "payload": {"targetDeviceId": "windows:test"}})
        self.assertFalse(session.armed)

    def test_keyboard_extended_mapping_and_shortcut_hold_preservation(self):
        self.assertEqual(keycode(dict(virtualKey=0xA3, scanCode=29, isExtended=True)), 97)
        self.assertEqual(keycode(dict(virtualKey=65, scanCode=0, isExtended=False)), 30)
        with self.assertRaises(ValueError): keycode(dict(virtualKey=65535, scanCode=0, isExtended=False))

    def test_real_mutual_tls_and_hello_binding(self):
        with tempfile.TemporaryDirectory() as directory, StateDirectory(directory) as state:
            server = Identity.open(state, MemoryVault(), "Test host")
            context = tls_context(server, self.identity.card, server=True)
            listener = socket.socket(); listener.bind(("127.0.0.1", 0)); listener.listen()
            outcomes = []
            def serve():
                try:
                    raw, _ = listener.accept()
                    with context.wrap_socket(raw, server_side=True) as stream:
                        verify_peer(stream, self.identity.card)
                        self.assertEqual(decode(FrameReader().read(stream), self.identity.card.device_id)["type"], "device.hello")
                        stream.sendall(hello(server.card, Bounds(0, 0, 1920, 1080)))
                        time.sleep(0.1)
                    outcomes.append(True)
                except BaseException as error: outcomes.append(error)
                finally: listener.close()
            worker = threading.Thread(target=serve); worker.start()
            inbox = Inbox(); connection = Connection(self.identity, inbox)
            epoch = connection.connect("127.0.0.1", server.card, Bounds(0, 0, 1920, 1080), listener.getsockname()[1])
            events = []
            deadline = time.monotonic() + 5
            while time.monotonic() < deadline and not any(event[1] == "connected" for event in events):
                events += inbox.drain(); time.sleep(0.01)
            connection.stop(); worker.join(5)
            self.assertTrue(any(event[:2] == (epoch, "connected") for event in events), events)
            self.assertEqual(outcomes, [True])

    def test_wrong_pin_rejects_real_tls(self):
        with tempfile.TemporaryDirectory() as directory, StateDirectory(directory) as state:
            server = Identity.open(state, MemoryVault())
            context = tls_context(server, self.identity.card, server=True)
            listener = socket.socket(); listener.bind(("127.0.0.1", 0)); listener.listen()
            port = listener.getsockname()[1]
            def serve():
                raw, _ = listener.accept()
                try:
                    with context.wrap_socket(raw, server_side=True): pass
                except ssl.SSLError: raw.close()
                finally: listener.close()
            worker = threading.Thread(target=serve); worker.start()
            # Trust only a different, otherwise valid certificate.
            context = tls_context(self.identity, self.identity.card)
            with socket.create_connection(("127.0.0.1", port), timeout=2) as raw:
                # TLS alerts may surface as a reset when the peer closes promptly.
                with self.assertRaises((ssl.SSLError, ConnectionResetError)):
                    context.wrap_socket(raw, server_hostname="batcave.local")
            worker.join(5)

    def test_raw_socket_without_client_certificate_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory, StateDirectory(directory) as state:
            server = Identity.open(state, MemoryVault())
            context = tls_context(server, self.identity.card, server=True)
            listener = socket.socket(); listener.bind(("127.0.0.1", 0)); listener.listen()
            port = listener.getsockname()[1]; rejected = []
            def serve():
                raw, _ = listener.accept()
                try:
                    with context.wrap_socket(raw, server_side=True): rejected.append(False)
                except ssl.SSLError: rejected.append(True); raw.close()
                finally: listener.close()
            worker = threading.Thread(target=serve); worker.start()
            context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
            context.check_hostname = False; context.verify_mode = ssl.CERT_NONE
            context.minimum_version = context.maximum_version = ssl.TLSVersion.TLSv1_3
            try:
                with socket.create_connection(("127.0.0.1", port), timeout=2) as raw:
                    with context.wrap_socket(raw, server_hostname="batcave.local") as stream:
                        try: stream.recv(1)
                        except OSError: pass
            except OSError: pass
            worker.join(5); self.assertEqual(rejected, [True])


if __name__ == "__main__": unittest.main()
