"""Compositor-authorized source capture; no raw device or privileged fallback."""

import os
import uuid

from gi.repository import Gio, GLib

from .portal import DESTINATION, PATH, SESSION, Portal
from .protocol import Bounds, integer, number

CAPTURE = "org.freedesktop.portal.InputCapture"


def zones_and_bounds(values):
    if not isinstance(values, (list, tuple)) or not 1 <= len(values) <= 128:
        raise ValueError("Invalid capture zones.")
    zones = []
    for zone in values:
        if not isinstance(zone, (list, tuple)) or len(zone) != 4:
            raise ValueError("Invalid capture zone.")
        width, height, x, y = zone
        zones.append(Bounds(integer(x, -100000, 100000), integer(y, -100000, 100000),
                            integer(width, 1, 100000), integer(height, 1, 100000)))
    left, top = min(z.left for z in zones), min(z.top for z in zones)
    right, bottom = max(z.right for z in zones), max(z.bottom for z in zones)
    return zones, Bounds(left, top, integer(right - left + 1, 1, 100000), integer(bottom - top + 1, 1, 100000))


def barriers(zones, bounds, edge):
    if edge not in ("left", "right"):
        raise ValueError("Choose a left or right shared edge.")
    lines = set()
    for zone in zones:
        if edge == "left" and zone.left == bounds.left:
            lines.add((zone.left, zone.top, zone.left, zone.bottom))
        elif edge == "right" and zone.right == bounds.right:
            lines.add((zone.right + 1, zone.top, zone.right + 1, zone.bottom))
    if not lines:
        raise ValueError("No outside shared edge is available.")
    return {index + 1: line for index, line in enumerate(sorted(lines))}


class CapturePortal(Portal):
    def __init__(self, backend, changed, activated, deactivated):
        super().__init__(backend, changed, CAPTURE)
        self.activated, self.deactivated = activated, deactivated
        self.capture_subscriptions = []
        self.zones = []
        self.bounds = None
        self.zone_set = None
        self.barriers = {}
        self.enabled = False
        self.active_id = None

    def capability(self):
        result = self.bus.call_sync(DESTINATION, PATH, "org.freedesktop.DBus.Properties", "GetAll",
                    GLib.Variant("(s)", (CAPTURE,)), GLib.VariantType.new("(a{sv})"),
                    Gio.DBusCallFlags.NONE, 2000, None).unpack()[0]
        self.version = integer(result.get("version", 0), 0, 100)
        if self.version < 1 or integer(result.get("SupportedCapabilities", 0), 0, 0xffffffff) & 3 != 3:
            raise RuntimeError("This desktop does not provide InputCapture keyboard and pointer permission. Choose Receive input here.")

    def _session(self, results):
        session = results.get("session_handle")
        if not isinstance(session, str) or not session.startswith("/org/freedesktop/portal/desktop/session/"):
            raise ValueError("Invalid capture session.")
        self.session = session
        epoch = self.epoch
        def closed(*_):
            if epoch == self.epoch: self.fail("Keyboard and mouse capture permission was revoked.")
        self.closed_subscription = self.bus.signal_subscribe(DESTINATION, SESSION, "Closed", session,
                         None, Gio.DBusSignalFlags.NONE, closed)
        for signal in ("Activated", "Deactivated", "Disabled", "ZonesChanged"):
            def received(_bus, _sender, _path, _interface, name, parameters, *_):
                if epoch == self.epoch: self._signal(name, parameters.unpack())
            self.capture_subscriptions.append(self.bus.signal_subscribe(DESTINATION, CAPTURE, signal, PATH,
                              None, Gio.DBusSignalFlags.NONE, received))

    def grant(self):
        self.close(); self.capability()
        self.changed("Requesting permission to share this keyboard and mouse…")
        def started(results):
            if integer(results.get("capabilities", 0), 0, 0xffffffff) & 3 != 3:
                raise ValueError("Keyboard and pointer capture are both required.")
            self._connect_eis()
            self._request("GetZones", "(oa{sv})", lambda options: (self.session, options), self._zones)
        if self.version == 1:
            def created(results):
                self._session(results); started(results)
            self._request("CreateSession", "(sa{sv})", lambda options: ("", dict(options,
                 session_handle_token=GLib.Variant("s", "batcave_" + uuid.uuid4().hex),
                 capabilities=GLib.Variant("u", 3))), created)
        else:
            epoch = self.epoch
            def created(bus, result, _):
                try:
                    results = bus.call_finish(result).unpack()[0]
                    if epoch != self.epoch:
                        session = results.get("session_handle")
                        if isinstance(session, str): self._close_object(session, SESSION)
                        return
                    self._session(results)
                    self._request("Start", "(osa{sv})", lambda options: (self.session, "", dict(options,
                         capabilities=GLib.Variant("u", 3), persist_mode=GLib.Variant("u", 0))), started)
                except Exception:
                    if epoch == self.epoch: self.fail("The desktop could not create keyboard and mouse capture permission.")
            self.bus.call(DESTINATION, PATH, CAPTURE, "CreateSession2", GLib.Variant("(a{sv})", ({
                   "session_handle_token": GLib.Variant("s", "batcave_" + uuid.uuid4().hex)},)),
                   GLib.VariantType.new("(a{sv})"), Gio.DBusCallFlags.NONE, 5000, self.cancel, created, None)

    def _connect_eis(self):
        epoch = self.epoch
        def connected(bus, result, _):
            fd = None
            try:
                value, descriptors = bus.call_with_unix_fd_list_finish(result)
                fd = descriptors.get(value.unpack()[0])
                if epoch != self.epoch: return
                owned, fd = fd, None
                self.backend.attach(owned)
                self.changed("Permission granted. Waiting for authorized capture devices…")
            except Exception:
                if epoch == self.epoch: self.fail("The desktop could not provide its authorized capture connection.")
            finally:
                if fd is not None: os.close(fd)
        self.bus.call_with_unix_fd_list(DESTINATION, PATH, CAPTURE, "ConnectToEIS",
                      GLib.Variant("(oa{sv})", (self.session, {})), GLib.VariantType.new("(h)"),
                      Gio.DBusCallFlags.NONE, 5000, None, self.cancel, connected, None)

    def _zones(self, results):
        self.zones, self.bounds = zones_and_bounds(results.get("zones"))
        self.zone_set = integer(results.get("zone_set"), 0, 0xffffffff)
        self.changed("Capture permission granted. Sharing remains paused.")

    def ready(self):
        return self.session is not None and self.bounds is not None and self.backend.ready()

    def enable(self, edge, enabled):
        if not self.ready(): raise RuntimeError("Grant keyboard and mouse capture permission first.")
        self.barriers = barriers(self.zones, self.bounds, edge)
        entries = [{"barrier_id": GLib.Variant("u", key), "position": GLib.Variant("(iiii)", line)}
                   for key, line in self.barriers.items()]
        def configured(results):
            if results.get("failed_barriers", []): raise ValueError("The desktop rejected the shared screen edge.")
            self._command("Enable", {}, lambda: self._enabled(enabled))
        self._request("SetPointerBarriers", "(oa{sv}aa{sv}u)",
                      lambda options: (self.session, options, entries, self.zone_set), configured)

    def _enabled(self, callback):
        self.enabled = True; callback()

    def _command(self, name, options, callback=None):
        epoch = self.epoch
        def returned(bus, result, _):
            try:
                bus.call_finish(result)
                if epoch == self.epoch and callback: callback()
            except Exception:
                if epoch == self.epoch: self.fail("The desktop could not update input capture. Sharing stopped.")
        self.bus.call(DESTINATION, PATH, CAPTURE, name, GLib.Variant("(oa{sv})", (self.session, options)),
                      None, Gio.DBusCallFlags.NONE, 2000, self.cancel, returned, None)

    def release(self, activation_id, position):
        if self.session is not None:
            self.active_id = None
            self._command("Release", {"activation_id": GLib.Variant("u", activation_id),
                                      "cursor_position": GLib.Variant("(dd)", position)})

    def return_point(self, edge, position):
        # A union's bounding rectangle may contain holes. Restore the pointer
        # inside a real zone on the configured outer edge, never inside a gap.
        y = self.bounds.top + number(position, 0, 1) * (self.bounds.height - 1)
        zones = [zone for zone in self.zones if zone.left == self.bounds.left] if edge == "left" else \
                [zone for zone in self.zones if zone.right == self.bounds.right]
        if not zones: raise ValueError("The authorized shared edge disappeared.")
        zone = min(zones, key=lambda zone: abs(y - max(zone.top, min(zone.bottom, y))))
        return zone.point(edge, max(0, min(1, (y - zone.top) / max(1, zone.height - 1))))

    def _signal(self, name, values):
        if len(values) != 2 or values[0] != self.session: return
        options = values[1]
        try:
            if name == "Activated":
                activation = integer(options.get("activation_id"), 0, 0xffffffff)
                position = options.get("cursor_position")
                if not isinstance(position, (list, tuple)) or len(position) != 2: raise ValueError("Invalid activation position")
                position = tuple(number(v, -200000, 200000) for v in position)
                barrier = integer(options.get("barrier_id", 0), 0, 0xffffffff)
                if barrier and barrier not in self.barriers: raise ValueError("Unknown activated barrier")
                self.active_id = activation
                if not self.enabled: self.release(activation, position)
                else: self.activated(activation, position)
            elif name == "Deactivated":
                activation = integer(options.get("activation_id"), 0, 0xffffffff)
                if activation == self.active_id:
                    self.active_id = None; self.deactivated(activation)
            else:
                self.fail("The shared edge or capture permission changed. Grant access and start again.")
        except Exception:
            self.fail("The desktop returned invalid capture activation data. Sharing stopped.")

    def close(self):
        for subscription in getattr(self, "capture_subscriptions", []): self.bus.signal_unsubscribe(subscription)
        self.capture_subscriptions = []
        self.zones = []; self.bounds = None; self.zone_set = None; self.barriers = {}
        self.enabled = False; self.active_id = None
        super().close()
