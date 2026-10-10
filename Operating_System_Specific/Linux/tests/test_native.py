import ctypes as C
import os
import time
import unittest
from pathlib import Path

from batcave_linux.ei import EiBackend
from batcave_linux.input import InputSession

ROOT = Path(__file__).resolve().parents[1]


@unittest.skipUnless((ROOT / ".build/libeis-fixture.so").exists(), "Build the real libeis fixture first")
class NativeTests(unittest.TestCase):
    def setUp(self):
        self.server = C.CDLL(str(ROOT / ".build/libeis-fixture.so"))
        self.server.fixture_open.restype = C.c_void_p
        self.server.fixture_open.argtypes = [C.POINTER(C.c_int)]
        for name in ("fixture_poll", "fixture_pause", "fixture_close"):
            getattr(self.server, name).argtypes = [C.c_void_p]
            getattr(self.server, name).restype = None
        for name in ("fixture_keys", "fixture_buttons"):
            getattr(self.server, name).argtypes = [C.c_void_p]
            getattr(self.server, name).restype = C.c_int
        for name in ("fixture_x", "fixture_y"):
            getattr(self.server, name).argtypes = [C.c_void_p]
            getattr(self.server, name).restype = C.c_double
        fd = C.c_int()
        self.fixture = self.server.fixture_open(C.byref(fd))
        self.assertTrue(self.fixture)
        self.backend = EiBackend(ROOT / ".build/libbatcave-ei.so")
        self.backend.attach(fd.value)
        deadline = time.monotonic() + 2
        while not self.backend.ready() and time.monotonic() < deadline:
            self.pump()
        self.assertTrue(self.backend.ready(), "Real libei/libeis device negotiation")

    def tearDown(self):
        self.backend.close(); self.server.fixture_close(self.fixture)

    def pump(self):
        self.server.fixture_poll(self.fixture); self.backend.poll(); time.sleep(0.005)

    def test_real_events_gap_clamping_and_balanced_disconnect(self):
        session = InputSession("linux:test", self.backend)
        session.connected(1); bounds = self.backend.bounds()
        self.assertEqual((bounds.width, bounds.height), (2200, 800))
        session.enable(bounds)
        session.handle(1, {"type": "cursor.handoff", "payload": dict(targetDeviceId="linux:test", targetDisplayId="linux:test:desktop", targetEdge="left", targetPosition=0.5)})
        for _ in range(3): self.pump()
        self.assertAlmostEqual(self.server.fixture_x(self.fixture), 2)
        self.assertAlmostEqual(self.server.fixture_y(self.fixture), 399.5)
        self.assertEqual(self.backend.move(1100, 900), (1200, 799))
        session.handle(1, {"type": "input.key", "payload": dict(virtualKey=0xA0, scanCode=42, isExtended=False, isKeyUp=False)})
        session.handle(1, {"type": "input.mouseButton", "payload": dict(button="left", isDown=True)})
        for _ in range(3): self.pump()
        self.assertEqual(self.server.fixture_keys(self.fixture), 1)
        self.assertEqual(self.server.fixture_buttons(self.fixture), 1)
        session.disconnect()
        for _ in range(3): self.pump()
        self.assertEqual(self.server.fixture_keys(self.fixture), 0)
        self.assertEqual(self.server.fixture_buttons(self.fixture), 0)

    def test_compositor_pause_during_emulation_fails_closed(self):
        self.backend.start(); self.server.fixture_pause(self.fixture)
        with self.assertRaises(RuntimeError):
            for _ in range(10): self.pump()
        self.assertFalse(self.backend.ready())


if __name__ == "__main__": unittest.main()
