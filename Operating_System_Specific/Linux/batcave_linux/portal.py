"""Asynchronous portal consent, fd ownership and revocation handling."""

import uuid

from gi.repository import Gio, GLib

DESTINATION = "org.freedesktop.portal.Desktop"
PATH = "/org/freedesktop/portal/desktop"
REMOTE = "org.freedesktop.portal.RemoteDesktop"
REQUEST = "org.freedesktop.portal.Request"
SESSION = "org.freedesktop.portal.Session"


class Portal:
    def __init__(self, backend, changed):
        self.backend, self.changed = backend, changed
        self.bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
        self.session = None
        self.epoch = 0
        self.pending = {}
        self.closed_subscription = None
        self.cancel = Gio.Cancellable()
        self.request_subscription = self.bus.signal_subscribe(DESTINATION, REQUEST, "Response", None, None,
                                                              Gio.DBusSignalFlags.NONE, self._response)
        self.owner_subscription = self.bus.signal_subscribe("org.freedesktop.DBus", "org.freedesktop.DBus",
                       "NameOwnerChanged", "/org/freedesktop/DBus", DESTINATION, Gio.DBusSignalFlags.NONE, self._owner_changed)

    def capability(self):
        result = self.bus.call_sync(DESTINATION, PATH, "org.freedesktop.DBus.Properties", "GetAll",
                        GLib.Variant("(s)", (REMOTE,)), GLib.VariantType.new("(a{sv})"), Gio.DBusCallFlags.NONE, 2000, None)
        properties = result.unpack()[0]
        if properties.get("version", 0) < 2 or properties.get("AvailableDeviceTypes", 0) & 3 != 3:
            raise RuntimeError("This desktop needs RemoteDesktop portal version 2 with keyboard and pointer support.")

    def _request(self, method, signature, parameters, callback):
        token = "batcave_" + uuid.uuid4().hex
        options = {"handle_token": GLib.Variant("s", token)}
        arguments = parameters(options)
        sender = self.bus.get_unique_name()[1:].replace(".", "_")
        predicted = "/org/freedesktop/portal/desktop/request/" + sender + "/" + token
        epoch = self.epoch
        timeout = GLib.timeout_add_seconds(120, self._timeout, predicted, epoch)
        self.pending[predicted] = (callback, timeout, epoch)

        def returned(bus, result, _):
            try:
                actual = bus.call_finish(result).unpack()[0]
                if epoch != self.epoch:
                    self._close_object(actual, REQUEST)
                elif actual != predicted:
                    # A portal must honor handle_token. Refuse a race-prone fallback.
                    self._close_object(actual, REQUEST)
                    self.fail("The portal returned an unexpected permission request handle.")
            except GLib.Error:
                if epoch == self.epoch:
                    self.fail("The desktop input permission request failed.")

        self.bus.call(DESTINATION, PATH, REMOTE, method, GLib.Variant(signature, arguments),
                      GLib.VariantType.new("(o)"), Gio.DBusCallFlags.NONE, 5000, self.cancel, returned, None)

    def _response(self, _bus, _sender, path, _interface, _signal, parameters, *_):
        pending = self.pending.pop(path, None)
        if pending is None:
            return
        callback, timeout, epoch = pending
        GLib.source_remove(timeout)
        response, results = parameters.unpack()
        if epoch != self.epoch:
            return
        if response != 0:
            self.fail("Desktop input permission was cancelled or denied.")
            return
        try:
            callback(results)
        except Exception:
            self.fail("The desktop returned incomplete input permission data.")

    def _timeout(self, path, epoch):
        self.pending.pop(path, None)
        self._close_object(path, REQUEST)
        if epoch == self.epoch:
            self.fail("The desktop input permission request timed out.")
        return False

    def grant(self):
        self.close()
        self.capability()
        self.changed("Requesting keyboard and mouse permission…")

        def created(results):
            self.session = results["session_handle"]
            if not isinstance(self.session, str) or not self.session.startswith("/org/freedesktop/portal/desktop/session/"):
                raise ValueError("Invalid portal session path.")
            self.closed_subscription = self.bus.signal_subscribe(DESTINATION, SESSION, "Closed", self.session,
                                        None, Gio.DBusSignalFlags.NONE, lambda *_: self.fail("Desktop input permission was revoked."))
            self._request("SelectDevices", "(oa{sv})", lambda options: (self.session, dict(options,
                         types=GLib.Variant("u", 3), persist_mode=GLib.Variant("u", 0))), selected)

        def selected(_):
            self._request("Start", "(osa{sv})", lambda options: (self.session, "", options), started)

        def started(results):
            if results.get("devices", 0) & 3 != 3:
                raise ValueError("Keyboard and mouse permission are both required.")
            epoch = self.epoch

            def connected(bus, result, _):
                fd = None
                try:
                    value, descriptors = bus.call_with_unix_fd_list_finish(result)
                    fd = descriptors.get(value.unpack()[0])
                    if epoch != self.epoch:
                        import os
                        os.close(fd)
                        return
                    self.backend.attach(fd)
                    fd = None
                    self.changed("Permission granted. Waiting for authorized input devices…")
                except Exception:
                    if epoch == self.epoch:
                        self.fail("The desktop could not provide its authorized input connection.")

            self.bus.call_with_unix_fd_list(DESTINATION, PATH, REMOTE, "ConnectToEIS",
                       GLib.Variant("(oa{sv})", (self.session, {})), GLib.VariantType.new("(h)"),
                       Gio.DBusCallFlags.NONE, 5000, None, self.cancel, connected, None)

        self._request("CreateSession", "(a{sv})", lambda options: (dict(options,
                       session_handle_token=GLib.Variant("s", "batcave_" + uuid.uuid4().hex)),), created)

    def _owner_changed(self, _bus, _sender, _path, _interface, _signal, parameters, *_):
        name, old, new = parameters.unpack()
        if name == DESTINATION and old and old != new:
            self.fail("The desktop permission service restarted. Grant permission again.")

    def _close_object(self, path, interface):
        self.bus.call(DESTINATION, path, interface, "Close", None, None, Gio.DBusCallFlags.NONE, 2000, None, None, None)

    def fail(self, reason):
        self.close()
        self.changed(reason, failed=True)

    def close(self):
        self.epoch += 1
        self.cancel.cancel()
        self.cancel = Gio.Cancellable()
        for path, (_, timeout, _) in self.pending.items():
            GLib.source_remove(timeout)
            self._close_object(path, REQUEST)
        self.pending.clear()
        if self.closed_subscription is not None:
            self.bus.signal_unsubscribe(self.closed_subscription)
            self.closed_subscription = None
        if self.session is not None:
            self._close_object(self.session, SESSION)
            self.session = None
        self.backend.close()

    def dispose(self):
        self.close()
        self.bus.signal_unsubscribe(self.request_subscription)
        self.bus.signal_unsubscribe(self.owner_subscription)
