"""D-Bus consent request races and cancellation against an isolated fake portal.

This exercises the real Gio interface, not an actual desktop permission prompt.
"""
import time
import unittest
import ctypes as C
import os
import threading
from pathlib import Path

from gi.repository import Gio, GLib

from batcave_linux.portal import DESTINATION, PATH, REMOTE, REQUEST, Portal

XML = """<node>
<interface name="org.freedesktop.portal.RemoteDesktop">
  <method name="CreateSession"><arg type="a{sv}" direction="in"/><arg type="o" direction="out"/></method>
  <method name="SelectDevices"><arg type="o" direction="in"/><arg type="a{sv}" direction="in"/><arg type="o" direction="out"/></method>
  <method name="Start"><arg type="o" direction="in"/><arg type="s" direction="in"/><arg type="a{sv}" direction="in"/><arg type="o" direction="out"/></method>
  <method name="ConnectToEIS"><arg type="o" direction="in"/><arg type="a{sv}" direction="in"/><arg type="h" direction="out"/></method>
  <property name="version" type="u" access="read"/>
  <property name="AvailableDeviceTypes" type="u" access="read"/>
</interface>
</node>"""


class Backend:
    def __init__(self): self.closed = 0
    def close(self): self.closed += 1


class PortalTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        # Only run under the deliberately isolated test bus; never impersonate a
        # portal on a user's real desktop session bus.
        import os
        if os.environ.get("BATCAVE_TEST_PORTAL_BUS") != "1":
            raise unittest.SkipTest("Use the isolated dbus-run-session test command")
        cls.bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
        cls.node = Gio.DBusNodeInfo.new_for_xml(XML)
        cls.bus.call_sync("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "RequestName",
                          GLib.Variant("(su)", (DESTINATION, 0)), GLib.VariantType.new("(u)"), Gio.DBusCallFlags.NONE, 2000, None)

    def setUp(self):
        self.denied = False; self.immediate = True; self.version = 2
        self.responses = []; self.events = []
        self.registration = self.bus.register_object(PATH, self.node.interfaces[0], self.method, self.property, None)
        self.backend = Backend(); self.portal = Portal(self.backend, self.changed)

    def tearDown(self):
        self.portal.dispose(); self.bus.unregister_object(self.registration)

    def changed(self, message, failed=False): self.events.append((message, failed))

    def property(self, _bus, _sender, _path, _iface, property_name):
        return GLib.Variant("u", self.version if property_name == "version" else 3)

    def method(self, _bus, sender, _path, _iface, method, parameters, invocation):
        values = parameters.unpack(); options = values[-1]
        if method == "ConnectToEIS":
            descriptors = Gio.UnixFDList.new()
            index = descriptors.append(self.eis_fd)
            invocation.return_value_with_unix_fd_list(GLib.Variant("(h)", (index,)), descriptors)
            return
        token = options["handle_token"]
        handle = "/org/freedesktop/portal/desktop/request/" + sender[1:].replace(".", "_") + "/" + token
        results = {}
        if getattr(self, "full_flow", False):
            if method == "CreateSession": results = {"session_handle": GLib.Variant("o", "/org/freedesktop/portal/desktop/session/test/session")}
            if method == "Start": results = {"devices": GLib.Variant("u", 3)}
        response = GLib.Variant("(ua{sv})", (1 if self.denied else 0, results))
        self.responses.append((handle, response))
        if self.immediate:
            # The response arrives before the asynchronous method reply callback.
            self.bus.emit_signal(sender, handle, REQUEST, "Response", response)
        invocation.return_value(GLib.Variant("(o)", (handle,)))

    def pump(self, condition):
        deadline = time.monotonic() + 2
        while not condition() and time.monotonic() < deadline:
            GLib.MainContext.default().iteration(False); time.sleep(0.001)
        self.assertTrue(condition())

    def test_early_permission_response_is_not_lost(self):
        completed = []
        self.portal._request("CreateSession", "(a{sv})", lambda options: (options,), lambda results: completed.append(results))
        self.pump(lambda: len(completed) == 1)
        self.assertEqual(completed, [{}]); self.assertFalse(self.portal.pending)

    def test_denial_closes_backend_and_never_calls_success(self):
        self.denied = True; completed = []
        self.portal._request("CreateSession", "(a{sv})", lambda options: (options,), lambda results: completed.append(results))
        self.pump(lambda: any(failed for _, failed in self.events))
        self.assertFalse(completed); self.assertFalse(self.portal.pending); self.assertGreater(self.backend.closed, 0)

    def test_stale_response_after_close_cannot_restore_permission(self):
        self.immediate = False; completed = []
        self.portal._request("CreateSession", "(a{sv})", lambda options: (options,), lambda results: completed.append(results))
        self.pump(lambda: bool(self.responses))
        self.portal.close()
        handle, response = self.responses[0]
        self.bus.emit_signal(None, handle, REQUEST, "Response", response)
        for _ in range(30): GLib.MainContext.default().iteration(False)
        self.assertFalse(completed); self.assertFalse(self.portal.pending)

    def test_full_consent_flow_unix_fd_and_real_eis_negotiation(self):
        from batcave_linux.ei import EiBackend
        root = Path(__file__).resolve().parents[1]
        server = C.CDLL(str(root / ".build/libeis-fixture.so"))
        server.fixture_open.restype = C.c_void_p; server.fixture_open.argtypes = [C.POINTER(C.c_int)]
        for name in ("fixture_poll", "fixture_close"):
            getattr(server, name).argtypes = [C.c_void_p]; getattr(server, name).restype = None
        fd = C.c_int(); fixture = server.fixture_open(C.byref(fd)); self.eis_fd = fd.value
        self.full_flow = True
        self.portal.backend = EiBackend(root / ".build/libbatcave-ei.so")
        errors = []
        def grant():
            try: self.portal.grant()
            except BaseException as error: errors.append(error)
        worker = threading.Thread(target=grant); worker.start()
        try:
            deadline = time.monotonic() + 3
            while not self.portal.backend.ready() and time.monotonic() < deadline:
                GLib.MainContext.default().iteration(False)
                server.fixture_poll(fixture); self.portal.backend.poll(); time.sleep(0.001)
            self.assertFalse(errors)
            self.assertTrue(self.portal.backend.ready(), self.events)
            self.assertEqual(len(self.responses), 3)
            self.assertEqual(self.portal.backend.bounds().width, 2200)
            self.portal.fail("Simulated revocation")
            self.assertFalse(self.portal.backend.ready())
        finally:
            self.portal.close(); os.close(fd.value); server.fixture_close(fixture); worker.join(3)


if __name__ == "__main__": unittest.main()
