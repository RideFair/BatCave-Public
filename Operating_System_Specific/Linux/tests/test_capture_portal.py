"""InputCapture v1/v2 request ordering, explicit activation and revocation."""
import os
import time
import threading
import unittest

from gi.repository import Gio, GLib

from batcave_linux.capture_portal import CAPTURE, CapturePortal, barriers, zones_and_bounds
from batcave_linux.portal import DESTINATION, PATH, REQUEST
from test_capture import CaptureFixture

XML = """<node><interface name="org.freedesktop.portal.InputCapture">
<method name="CreateSession"><arg type="s" direction="in"/><arg type="a{sv}" direction="in"/><arg type="o" direction="out"/></method>
<method name="CreateSession2"><arg type="a{sv}" direction="in"/><arg type="a{sv}" direction="out"/></method>
<method name="Start"><arg type="o" direction="in"/><arg type="s" direction="in"/><arg type="a{sv}" direction="in"/><arg type="o" direction="out"/></method>
<method name="ConnectToEIS"><arg type="o" direction="in"/><arg type="a{sv}" direction="in"/><arg type="h" direction="out"/></method>
<method name="GetZones"><arg type="o" direction="in"/><arg type="a{sv}" direction="in"/><arg type="o" direction="out"/></method>
<method name="SetPointerBarriers"><arg type="o" direction="in"/><arg type="a{sv}" direction="in"/><arg type="aa{sv}" direction="in"/><arg type="u" direction="in"/><arg type="o" direction="out"/></method>
<method name="Enable"><arg type="o" direction="in"/><arg type="a{sv}" direction="in"/></method>
<method name="Release"><arg type="o" direction="in"/><arg type="a{sv}" direction="in"/></method>
<property name="version" type="u" access="read"/><property name="SupportedCapabilities" type="u" access="read"/>
</interface></node>"""
SESSION_PATH = "/org/freedesktop/portal/desktop/session/fixture/capture"


class ZoneTests(unittest.TestCase):
    def test_barriers_only_cover_the_outside_union_and_bound_negative_zones(self):
        zones, bounds = zones_and_bounds([(1000, 800, -1000, 0), (1000, 600, 0, 0)])
        self.assertEqual(barriers(zones, bounds, "left"), {1: (-1000, 0, -1000, 799)})
        self.assertEqual(barriers(zones, bounds, "right"), {1: (1000, 0, 1000, 599)})
        portal = CapturePortal.__new__(CapturePortal); portal.zones, portal.bounds = zones, bounds
        self.assertEqual(portal.return_point("right", 1), (997, 599))
        with self.assertRaises(ValueError): zones_and_bounds([(0, 800, 0, 0)])
        with self.assertRaises(ValueError): zones_and_bounds([(100000, 800, -100000, 0), (100000, 800, 0, 0)])


class CapturePortalTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if os.environ.get("BATCAVE_TEST_PORTAL_BUS") != "1": raise unittest.SkipTest("Use the isolated test bus")
        cls.bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
        cls.node = Gio.DBusNodeInfo.new_for_xml(XML)
        cls.bus.call_sync("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "RequestName",
                         GLib.Variant("(su)", (DESTINATION, 0)), GLib.VariantType.new("(u)"), Gio.DBusCallFlags.NONE, 2000, None)

    def setUp(self):
        self.version = 2; self.denied = False; self.immediate = True
        self.methods = []; self.responses = []; self.changed = []; self.activated = []; self.deactivated = []
        self.capture = CaptureFixture(attach=False)
        self.service_context = GLib.MainContext.new()
        self.service_loop = GLib.MainLoop.new(self.service_context, False)
        registered = threading.Event()
        def serve():
            self.service_context.push_thread_default()
            registration = self.bus.register_object(PATH, self.node.interfaces[0], self.method, self.property, None)
            registered.set(); self.service_loop.run()
            self.bus.unregister_object(registration); self.service_context.pop_thread_default()
        self.service = threading.Thread(target=serve, daemon=True); self.service.start()
        self.assertTrue(registered.wait(2))
        self.portal = CapturePortal(self.capture.backend, lambda message, failed=False: self.changed.append((message, failed)),
                                    lambda *args: self.activated.append(args), lambda *args: self.deactivated.append(args))

    def tearDown(self):
        self.portal.dispose(); self.service_loop.quit(); self.service.join(2); self.capture.close()

    def property(self, _bus, _sender, _path, _interface, name):
        return GLib.Variant("u", self.version if name == "version" else 3)

    def method(self, _bus, sender, _path, _interface, name, parameters, invocation):
        values = parameters.unpack(); self.methods.append((name, values))
        if name == "CreateSession2":
            invocation.return_value(GLib.Variant("(a{sv})", ({"session_handle": GLib.Variant("o", SESSION_PATH)},))); return
        if name == "ConnectToEIS":
            descriptors = Gio.UnixFDList.new(); index = descriptors.append(self.capture.fd)
            invocation.return_value_with_unix_fd_list(GLib.Variant("(h)", (index,)), descriptors); return
        if name in ("Enable", "Release"):
            invocation.return_value(None); return
        options = values[1] if name == "SetPointerBarriers" else values[-1]
        handle = "/org/freedesktop/portal/desktop/request/" + sender[1:].replace(".", "_") + "/" + options["handle_token"]
        results = {}
        if name == "CreateSession": results = {"session_handle": GLib.Variant("o", SESSION_PATH), "capabilities": GLib.Variant("u", 3)}
        elif name == "Start": results = {"capabilities": GLib.Variant("u", 3)}
        elif name == "GetZones": results = {"zones": GLib.Variant("a(uuii)", [(1000, 800, -1000, 0)]), "zone_set": GLib.Variant("u", 42)}
        elif name == "SetPointerBarriers": results = {"failed_barriers": GLib.Variant("au", [])}
        response = GLib.Variant("(ua{sv})", (1 if self.denied else 0, results))
        self.responses.append((handle, response))
        if self.immediate: self.bus.emit_signal(sender, handle, REQUEST, "Response", response)
        invocation.return_value(GLib.Variant("(o)", (handle,)))

    def pump(self, condition):
        deadline = time.monotonic() + 2
        while not condition() and time.monotonic() < deadline:
            GLib.MainContext.default().iteration(False); self.capture.pump()
        self.assertTrue(condition(), self.changed)

    def signal(self, name, options):
        self.bus.emit_signal(None, PATH, CAPTURE, name, GLib.Variant("(oa{sv})", (SESSION_PATH, options)))

    def test_v2_consent_does_not_enable_or_capture_and_requires_explicit_barriers(self):
        self.portal.grant(); self.pump(self.portal.ready)
        self.assertEqual([name for name, _ in self.methods][:2], ["CreateSession2", "Start"])
        self.assertFalse(self.portal.enabled or self.activated)
        self.assertFalse(any(name in ("Enable", "SetPointerBarriers") for name, _ in self.methods))
        start = next(values[-1] for name, values in self.methods if name == "Start")
        self.assertEqual(start["persist_mode"], 0); self.assertNotIn("restore_token", start)
        enabled = []; self.portal.enable("right", lambda: enabled.append(True)); self.pump(lambda: bool(enabled))
        values = next(values for name, values in self.methods if name == "SetPointerBarriers")
        self.assertEqual(values[2][0]["position"], (0, 0, 0, 799)); self.assertEqual(values[3], 42)
        self.signal("Activated", {"activation_id": GLib.Variant("u", 7), "cursor_position": GLib.Variant("(dd)", (5, 400)), "barrier_id": GLib.Variant("u", 1)})
        self.pump(lambda: bool(self.activated)); self.assertEqual(self.activated, [(7, (5.0, 400.0))])
        self.portal.release(7, (-2, 400)); self.pump(lambda: any(name == "Release" for name, _ in self.methods))
        self.assertIsNone(self.portal.active_id); self.assertTrue(self.portal.enabled)
        self.signal("ZonesChanged", {"zone_set": GLib.Variant("u", 42)})
        self.pump(lambda: any(failed for _, failed in self.changed)); self.assertFalse(self.portal.ready() or self.portal.enabled)

    def test_v1_consent_uses_deprecated_request_without_calling_v2_start(self):
        self.version = 1; self.portal.grant(); self.pump(self.portal.ready)
        self.assertEqual(self.methods[0][0], "CreateSession")
        self.assertEqual(self.methods[0][1][1]["capabilities"], 3)
        self.assertFalse(any(name in ("Start", "Enable") for name, _ in self.methods))

    def test_denial_and_late_response_cannot_restore_capture(self):
        self.denied = True; self.portal.grant(); self.pump(lambda: any(failed for _, failed in self.changed))
        self.assertFalse(self.portal.ready() or self.portal.enabled)
        self.assertFalse(any(name == "ConnectToEIS" for name, _ in self.methods))
        self.changed.clear(); self.denied = False; self.immediate = False
        self.portal.grant(); self.pump(lambda: len(self.responses) >= 2)
        self.portal.close(); path, response = self.responses[-1]
        self.bus.emit_signal(None, path, REQUEST, "Response", response)
        for _ in range(50): GLib.MainContext.default().iteration(False)
        self.assertFalse(self.portal.ready() or self.portal.enabled or self.activated)
