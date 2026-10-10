"""Passive compositor events routed over real TLS into isolated EIS devices."""
import ctypes as C
import tempfile
import time
import unittest
from pathlib import Path

from batcave_linux.capture import CaptureBackend, CaptureEvent
from batcave_linux.identity import Identity
from batcave_linux.input import InputSession
from batcave_linux.protocol import Bounds
from batcave_linux.source import SourceSession, key_payload
from batcave_linux.storage import StateDirectory
from batcave_linux.transport import Connection, Inbox
from test_core import MemoryVault
import test_native

ROOT = Path(__file__).resolve().parents[1]


class CaptureFixture:
    def __init__(self, attach=True):
        xkb = C.CDLL("libxkbcommon.so.0")
        xkb.xkb_context_new.restype = C.c_void_p; xkb.xkb_context_new.argtypes = [C.c_int]
        xkb.xkb_keymap_new_from_names.restype = C.c_void_p
        xkb.xkb_keymap_new_from_names.argtypes = [C.c_void_p, C.c_void_p, C.c_int]
        xkb.xkb_keymap_get_as_string.restype = C.c_void_p
        xkb.xkb_keymap_get_as_string.argtypes = [C.c_void_p, C.c_int]
        xkb.xkb_keymap_mod_get_index.restype = C.c_uint32
        xkb.xkb_keymap_mod_get_index.argtypes = [C.c_void_p, C.c_char_p]
        xkb.xkb_context_unref.argtypes = [C.c_void_p]; xkb.xkb_keymap_unref.argtypes = [C.c_void_p]
        context = xkb.xkb_context_new(0); keymap = xkb.xkb_keymap_new_from_names(context, None, 0)
        if not keymap: raise RuntimeError("Install distro xkb-data for the isolated keyboard fixture.")
        data = xkb.xkb_keymap_get_as_string(keymap, 1)
        self.shift_mask = 1 << xkb.xkb_keymap_mod_get_index(keymap, b"Shift")
        self.keymap = tempfile.TemporaryFile()
        self.keymap.write(C.string_at(data) + b"\0"); self.keymap.flush()
        size = self.keymap.tell()
        libc = C.CDLL(None); libc.free.argtypes = [C.c_void_p]; libc.free(data)
        xkb.xkb_keymap_unref(keymap); xkb.xkb_context_unref(context)
        self.server = C.CDLL(str(ROOT / ".build/libeis-capture-fixture.so"))
        signatures = {"open": (C.c_void_p, [C.POINTER(C.c_int), C.c_int, C.c_uint32]),
                      "motion": (None, [C.c_void_p, C.c_double, C.c_double]),
                      "key": (None, [C.c_void_p, C.c_uint32, C.c_int]),
                      "button": (None, [C.c_void_p, C.c_uint32, C.c_int]),
                      "start": (None, [C.c_void_p, C.c_uint32]), "modifiers": (None, [C.c_void_p, C.c_uint32])}
        for name in ("poll", "close", "scroll", "frame", "stop"):
            signatures[name] = (None, [C.c_void_p])
        for name, (result, arguments) in signatures.items():
            method = getattr(self.server, "capture_fixture_" + name)
            method.restype, method.argtypes = result, arguments
        fd = C.c_int()
        self.fixture = self.server.capture_fixture_open(C.byref(fd), self.keymap.fileno(), size)
        if not self.fixture: raise RuntimeError("Could not create isolated EIS capture fixture.")
        self.backend = CaptureBackend(ROOT / ".build/libbatcave-ei.so")
        self.fd = None if attach else fd.value
        if not attach: return
        self.backend.attach(fd.value)
        deadline = time.monotonic() + 2
        while not self.backend.ready() and time.monotonic() < deadline: self.pump()
        if not self.backend.ready(): raise RuntimeError("Passive EIS device negotiation failed.")
        for _ in range(3): self.pump()

    def emit(self, name, *values):
        getattr(self.server, "capture_fixture_" + name)(self.fixture, *values)

    def pump(self):
        self.server.capture_fixture_poll(self.fixture); self.backend.poll(); time.sleep(0.002)

    def collect(self):
        events = []
        for _ in range(8): self.pump(); events += self.backend.events()
        return events

    def close(self):
        if self.fd is not None:
            import os
            os.close(self.fd); self.fd = None
        self.backend.close(); self.server.capture_fixture_close(self.fixture); self.keymap.close()


class Permission:
    def __init__(self):
        self.bounds = Bounds(-1000, 0, 1000, 800); self.closed = 0; self.releases = []; self.enables = []
    def ready(self): return self.closed == 0
    def enable(self, edge, callback): self.enables.append(edge); callback()
    def release(self, activation, position): self.releases.append((activation, position))
    def return_point(self, edge, position): return self.bounds.point(edge, position)
    def close(self): self.closed += 1


@unittest.skipUnless((ROOT / ".build/libeis-capture-fixture.so").exists(), "Build the isolated capture fixture first")
class CaptureTests(unittest.TestCase):
    def test_old_enable_callback_cannot_arm_new_start_on_same_session(self):
        permission = Permission(); callbacks = []
        permission.enable = lambda _edge, callback: callbacks.append(callback)
        source = SourceSession("linux:source-test", None, permission, lambda *_: None)
        source.connected(1, object(), Bounds(0, 0, 800, 600)); source.start("right")
        old = callbacks[-1]; source.stop(close_permission=False)
        source.start("left"); old()
        self.assertFalse(source.armed); self.assertTrue(source.pending_enable)
        callbacks[-1](); self.assertTrue(source.armed); source.stop()

    def test_delayed_enable_needs_live_initial_authorization(self):
        permission = Permission(); callbacks = []
        permission.enable = lambda _edge, callback: callbacks.append(callback)
        source = SourceSession("linux:source-test", None, permission, lambda *_: None)
        source.connected(1, object(), Bounds(0, 0, 800, 600))
        authorized = True
        source.start("right", authorize=lambda: authorized)
        self.assertTrue(source.pending_enable); self.assertFalse(source.armed)
        authorized = False; callbacks[0]()
        self.assertFalse(source.pending_enable or source.armed); self.assertGreater(permission.closed, 0)

    def test_old_enable_callback_cannot_arm_replacement_session(self):
        permission = Permission(); callbacks = []
        permission.enable = lambda _edge, callback: callbacks.append(callback)
        source = SourceSession("linux:source-test", None, permission, lambda *_: None)
        source.connected(1, object(), Bounds(0, 0, 800, 600)); source.start("right")
        old = callbacks[-1]; source.disconnect()
        permission = Permission(); permission.enable = lambda _edge, callback: callbacks.append(callback)
        source.portal = permission; source.connected(2, object(), Bounds(0, 0, 800, 600)); source.start("left")
        old(); self.assertFalse(source.armed); self.assertTrue(source.pending_enable)
        callbacks[-1](); self.assertTrue(source.armed); source.stop()

    def test_real_capture_matches_activation_and_returns_balanced_input_over_tls(self):
        capture = CaptureFixture(); output = test_native.NativeTests(); output.setUp()
        self.addCleanup(capture.close); self.addCleanup(output.tearDown)
        with tempfile.TemporaryDirectory() as directory, StateDirectory(Path(directory) / "a") as state_a, StateDirectory(Path(directory) / "b") as state_b:
            a = Identity.open(state_a, MemoryVault(), "Source fixture")
            b = Identity.open(state_b, MemoryVault(), "Receiver fixture")
            incoming_a, incoming_b = Inbox(), Inbox()
            receiver = InputSession(b.card.device_id, output.backend)
            connection_a = Connection(a, incoming_a)
            connection_b = Connection(b, incoming_b, lambda: receiver.generation)
            self.addCleanup(connection_a.stop); self.addCleanup(connection_b.stop)
            epoch_a = connection_a.listen(b.card, Bounds(-1000, 0, 1000, 800), host="127.0.0.1", port=0)
            port = connection_a.listener.getsockname()[1]
            epoch_b = connection_b.connect("127.0.0.1", a.card, output.backend.bounds(), port)
            connected = set()
            permission = Permission(); source = SourceSession(a.card.device_id, connection_a, permission, lambda *_: None)
            deadline = time.monotonic() + 3
            while len(connected) < 2 and time.monotonic() < deadline:
                for epoch, kind, value in incoming_a.drain():
                    if kind == "connected": source.connected(epoch, b.card, value); connected.add("a")
                for epoch, kind, value in incoming_b.drain():
                    if kind == "connected": receiver.connected(epoch); connected.add("b")
                time.sleep(0.002)
            self.assertEqual(connected, {"a", "b"})
            self.assertFalse(source.armed or receiver.armed)
            receiver.enable(output.backend.bounds()); source.start("right")
            capture.emit("start", 7); capture.emit("modifiers", capture.shift_mask)
            capture.emit("button", 272, 1); capture.emit("motion", 20, 5)
            capture.emit("scroll"); capture.emit("frame")
            source.events(capture.collect())  # EIS may arrive before the D-Bus activation signal.
            self.assertFalse(source.owns_cursor or receiver.owns_cursor)
            source.activate(7, (0, 400))
            kinds = []
            def receive_until(condition):
                deadline = time.monotonic() + 2
                while not condition() and time.monotonic() < deadline:
                    for epoch, kind, value in incoming_b.drain():
                        if kind == "message":
                            kinds.append(value["type"])
                            receiver.handle(epoch, value, value.input_generation)
                        elif kind == "disconnected": self.fail(value)
                    output.pump()
                self.assertTrue(condition(), kinds)
            receive_until(lambda: receiver.keys == {42} and receiver.buttons == {272} and "input.mouseWheel" in kinds)
            self.assertEqual(kinds.count("input.mouseWheel"), 1)  # Continuous + discrete describe one wheel event.
            self.assertAlmostEqual(receiver.cursor[0], 22)
            for _ in range(3): output.pump()
            self.assertEqual(output.server.fixture_keys(output.fixture), 1)
            self.assertEqual(output.server.fixture_buttons(output.fixture), 1)
            capture.emit("motion", -100, 0); capture.emit("frame"); source.events(capture.collect())
            receive_until(lambda: not receiver.owns_cursor)
            self.assertTrue(receiver.armed and source.armed)
            for _ in range(3): output.pump()
            self.assertEqual(output.server.fixture_keys(output.fixture), 0)
            self.assertEqual(output.server.fixture_buttons(output.fixture), 0)
            self.assertEqual(permission.releases[-1][0], 7)
            capture.emit("stop"); capture.emit("start", 8); capture.emit("key", 30, 1); capture.emit("frame")
            source.activate(8, (0, 400)); source.events(capture.collect())
            receive_until(lambda: receiver.keys == {30})
            receiver.pause()
            self.assertTrue(connection_b.send("control.testStop", {"reason": "Receiver paused"}, epoch_b))
            deadline = time.monotonic() + 2
            while source.armed and time.monotonic() < deadline:
                for epoch, kind, value in incoming_a.drain():
                    if kind == "message": source.handle(epoch, value)
                time.sleep(0.002)
            self.assertFalse(source.armed or source.owns_cursor); self.assertGreater(permission.closed, 0)
            source.handle(epoch_a, {"type": "control.testStop", "payload": {"reason": "Again"}})
            for _ in range(3): output.pump()
            self.assertEqual(output.server.fixture_keys(output.fixture), 0)

    def test_no_source_forwarding_before_fresh_local_start_and_matching_activation(self):
        class Transport:
            def __init__(self): self.sent = []
            def send(self, kind, payload, epoch, **_kwargs): self.sent.append((kind, payload)); return True
        transport = Transport(); permission = Permission()
        source = SourceSession("linux:source", transport, permission, lambda *_: None)
        source.connected(1, type("Peer", (), {"device_id": "linux:peer"})(), Bounds(0, 0, 1000, 800))
        events = [CaptureEvent(1, 7, 0, 0, 0, 0), CaptureEvent(5, 7, 30, 1, 0, 0)]
        source.events(events); source.activate(7, (0, 400))
        self.assertFalse(transport.sent)
        source.start("right"); source.activate(8, (0, 400)); source.events(events)
        self.assertFalse(transport.sent)
        source.events([CaptureEvent(1, 8, 0, 0, 0, 0)])
        self.assertTrue(source.owns_cursor)
        source.disconnect(); count = len(transport.sent)
        source.events(events); source.activate(9, (0, 400))
        self.assertEqual(len(transport.sent), count)

    def test_physical_keyboard_payload_roundtrips_existing_v0(self):
        from batcave_linux.input import keycode
        for code in (1, 14, 29, 30, 42, 54, 56, 58, 69, 87, 88, 96, 97, 100, 103, 105, 125, 126):
            self.assertEqual(keycode(key_payload(code, True)), code)
        with self.assertRaises(ValueError): key_payload(240, True)

    def test_both_source_edges_return_and_activation_serial_wrap_never_replays(self):
        class Transport:
            def __init__(self): self.sent = []
            def send(self, kind, payload, epoch, **_kwargs): self.sent.append((kind, payload)); return True
        for edge in ("left", "right"):
            transport = Transport(); permission = Permission()
            source = SourceSession("linux:source", transport, permission, lambda *_: None)
            source.connected(1, type("Peer", (), {"device_id": "linux:peer"})(), Bounds(0, 0, 1000, 800))
            source.start(edge); source.activate(0xfffffffe, (-2, 400))
            source.events([CaptureEvent(1, 0xfffffffe, 0, 0, 0, 0), CaptureEvent(5, 0xfffffffe, 42, 1, 0, 0)])
            source.events([CaptureEvent(3, 0xfffffffe, 0, 0, 100 if edge == "left" else -100, 0)])
            self.assertFalse(source.owns_cursor or source.keys); self.assertTrue(source.armed)
            self.assertEqual(transport.sent[-1][1]["targetDeviceId"], "linux:source")
            self.assertEqual(transport.sent[-1][1]["targetEdge"], edge)
            source.activate(1, (-2, 400)); source.events([CaptureEvent(1, 1, 0, 0, 0, 0)])
            self.assertTrue(source.owns_cursor)
            count = len(transport.sent)
            source.activate(0xfffffffe, (-2, 400)); source.events([CaptureEvent(5, 0xfffffffe, 30, 1, 0, 0)])
            self.assertEqual(len(transport.sent), count); self.assertEqual(source.activation, 1)
            source.stop(); self.assertFalse(source.armed or source.owns_cursor); self.assertGreater(permission.closed, 0)
