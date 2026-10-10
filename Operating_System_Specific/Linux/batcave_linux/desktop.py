"""Native GTK setup. Permission, trust and active input are separate decisions."""

import json
import os
import stat
import threading
from pathlib import Path

import gi
gi.require_version("Gtk", "4.0")
gi.require_version("Gdk", "4.0")
from gi.repository import Gdk, Gio, GLib, Gtk

from .ei import EiBackend
from .identity import Approvals, Card, Identity, SecretVault, strict_json
from .input import InputSession
from .portal import Portal
from .protocol import Bounds
from .storage import StateDirectory, profile_path
from .transfers import MAX_BATCH, MAX_FILE, leaf_name
from .transport import Connection, Inbox


def text(value, style=None):
    widget = Gtk.Label(label=value, xalign=0, wrap=True)
    if style: widget.add_css_class(style)
    return widget


def button(value, callback, primary=False):
    widget = Gtk.Button(label=value)
    widget.connect("clicked", lambda *_: callback())
    if primary: widget.add_css_class("suggested-action")
    return widget


def column(spacing=12):
    return Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=spacing)


def card(title, explanation):
    box = column()
    box.add_css_class("card")
    box.append(text(title, "card-title")); box.append(text(explanation, "muted"))
    return box


class Window(Gtk.ApplicationWindow):
    def __init__(self, application, fixture=None):
        super().__init__(application=application, title="BatCave — Linux secure preview", default_width=1080, default_height=760)
        self.set_size_request(860, 680)
        self.state = self.identity = self.approvals = self.connection = self.input = self.portal = None
        self.inbox = Inbox()
        self.epoch = 0
        self.connected = False
        self.backend = EiBackend(application.library)
        self.files = []
        self.bus_subscriptions = []
        self.grant_started = False
        self.lifecycle_ready = False
        self.shutting_down = False
        self.peer_ids = []
        self._build()
        self.connect("close-request", self._close)
        self.tick = GLib.timeout_add(20, self._tick)
        if fixture is not None:
            self._ready(*fixture)
        else:
            threading.Thread(target=self._initialize, daemon=True).start()

    def _build(self):
        css = Gtk.CssProvider()
        css.load_from_data(b"""
            .content { background: #f4f7fb; color: #18283d; }
            .header { background: #ffffff; padding: 22px 28px; border-bottom: 1px solid #dce4ef; }
            .brand { font-size: 26px; font-weight: 700; }
            .subtitle, .muted { color: #56677f; }
            .card { background: #ffffff; border: 1px solid #dce4ef; border-radius: 12px; padding: 22px; }
            .card-title { font-size: 18px; font-weight: 700; }
            .badge { background: #e7eef9; color: #34547b; border-radius: 20px; padding: 7px 12px; }
            .status { background: #e7eef9; color: #28445f; border-radius: 8px; padding: 14px; }
            .drop-zone { border: 2px dashed #aac0dc; border-radius: 12px; padding: 30px; }
            .file-row { padding: 10px; border-bottom: 1px solid #e3e9f1; }
            button { padding: 9px 14px; }
            entry { padding: 7px; }
        """)
        Gtk.StyleContext.add_provider_for_display(self.get_display(), css, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION)
        root = column(0); root.add_css_class("content"); self.set_child(root)
        header = Gtk.Box(spacing=20); header.add_css_class("header")
        brand = column(4); brand.set_hexpand(True)
        brand.append(text("BatCave", "brand")); brand.append(text("Connect your Linux desktop securely", "subtitle"))
        header.append(brand); header.append(text("LINUX PREVIEW", "badge")); root.append(header)
        tabs = Gtk.StackSwitcher(halign=Gtk.Align.CENTER, margin_top=16, margin_bottom=16)
        self.stack = Gtk.Stack(hexpand=True, vexpand=True)
        tabs.set_stack(self.stack); root.append(tabs); root.append(self.stack)
        setup = Gtk.Box(spacing=20, margin_start=28, margin_end=28, margin_bottom=20)
        left, right = column(16), column(16); left.set_hexpand(True); right.set_size_request(300, -1)
        setup.append(left); setup.append(right)
        setup_scroll = Gtk.ScrolledWindow(hscrollbar_policy=Gtk.PolicyType.NEVER, vexpand=True)
        setup_scroll.set_child(setup); self.stack.add_titled(setup_scroll, "setup", "Connection")
        pair = card("1   Pair your computers", "Exchange public cards through a trusted channel. Approve the full fingerprint on both computers.")
        self.fingerprint = text("Opening your desktop keyring…", "muted"); self.fingerprint.set_selectable(True)
        pair.append(self.fingerprint)
        actions = Gtk.Box(spacing=10)
        self.export = button("Export my card", self._export)
        self.import_card = button("Import a card", self._import)
        self.export.set_sensitive(False); self.import_card.set_sensitive(False)
        actions.append(self.export); actions.append(self.import_card); pair.append(actions); left.append(pair)
        connect = card("2   Connect to Windows", "Choose an approved computer and enter the address shown in its secure preview.")
        self.peers = Gtk.DropDown.new_from_strings([]); self.peers.connect("notify::selected", lambda *_: self._peer_changed())
        connect.append(self.peers)
        row = Gtk.Box(spacing=10)
        self.address = Gtk.Entry(placeholder_text="Windows IP address", hexpand=True)
        row.append(self.address); row.append(button("Unpair", self._unpair)); connect.append(row)
        self.grant = button("Grant desktop input permission", self._grant)
        connect.append(self.grant)
        row = Gtk.Box(spacing=10)
        self.connect_button = button("Connect securely", self._connect, True); self.connect_button.set_sensitive(False)
        row.append(self.connect_button); row.append(button("Disconnect", self._stop)); connect.append(row); left.append(connect)
        control = card("3   Allow input when ready", "A secure connection starts idle. Enable input, then cross from Windows to this desktop.")
        self.allow = button("Allow input", self._allow, True); self.allow.set_sensitive(False)
        control.append(self.allow); control.append(button("Pause and restore local control", self._stop)); left.append(control)
        state = card("Connection status", "Permissions and pairing are checked separately.")
        self.permission_label = text("Input permission: not granted", "muted")
        self.connection_label = text("Secure connection: disconnected", "muted")
        self.sharing_label = text("Input sharing: paused", "muted")
        for widget in (self.permission_label, self.connection_label, self.sharing_label): state.append(widget)
        right.append(state)
        safety = card("Your desktop stays in control", "Pause closes the connection and releases held input. Permission revocation, screen lock and sleep also stop sharing.")
        safety.append(text("This preview receives input from Windows. Linux-controlled Windows and live Wayland verification are still pending.", "muted"))
        right.append(safety)
        monitors = card("Monitor layout", "Windows configures the shared boundary. Linux uses the coordinate regions granted by its desktop, including gaps between monitors.")
        self.geometry = text("Grant permission to detect input regions.", "muted"); monitors.append(self.geometry); right.append(monitors)
        files = column(16); files.set_margin_start(28); files.set_margin_end(28); files.set_margin_bottom(20)
        intro = card("File shelf", "Drop local files here to prepare them. Network transfer stays disabled until the shared transfer protocol is available.")
        drop = column(); drop.add_css_class("drop-zone"); drop.append(text("Drop files here", "card-title"))
        drop.append(text("Original file icons appear in this shelf and native drag previews.", "muted"))
        target = Gtk.DropTarget.new(Gdk.FileList, Gdk.DragAction.COPY); target.connect("drop", self._drop)
        drop.add_controller(target); intro.append(drop); files.append(intro)
        self.file_list = column(0)
        scroll = Gtk.ScrolledWindow(vexpand=True); scroll.set_child(self.file_list); files.append(scroll)
        row = Gtk.Box(spacing=10); row.append(button("Choose files", self._choose_files)); row.append(button("Clear shelf", self._clear_files))
        disabled = button("Send files — not yet available", lambda: None); disabled.set_sensitive(False); row.append(disabled)
        files.append(row); self.stack.add_titled(files, "files", "Files")
        self.status = text("Setting up the secure preview…", "status")
        self.status.set_margin_start(28); self.status.set_margin_end(28); self.status.set_margin_bottom(20); root.append(self.status)

    def _initialize(self):
        state = None
        try:
            state = StateDirectory(profile_path())
            identity = Identity.open(state, SecretVault())
            approvals = Approvals(state)
            GLib.idle_add(self._ready, state, identity, approvals)
        except Exception:
            if state: state.close()
            GLib.idle_add(self._error, "Could not unlock the preview identity. Check your desktop keyring and private state directory; no identity was replaced.")

    def _ready(self, state, identity, approvals):
        if self.shutting_down:
            state.close(); return False
        self.state, self.identity, self.approvals = state, identity, approvals
        self.connection = Connection(identity, self.inbox)
        self.input = InputSession(identity.card.device_id, self.backend)
        self.fingerprint.set_text("My SHA-256 fingerprint\n" + " ".join(identity.card.fingerprint[i:i + 8] for i in range(0, 64, 8)))
        self.export.set_sensitive(True); self.import_card.set_sensitive(True)
        self._refresh_peers(); self._watch_lifecycle()
        try:
            saved = state.read("connection-preferences.json", 4096)
            if saved:
                preferences = strict_json(saved)
                address = preferences.get("address", "")
                if isinstance(address, str) and len(address) <= 64: self.address.set_text(address)
                peer_id = preferences.get("peerId")
                if peer_id in self.peer_ids: self.peers.set_selected(self.peer_ids.index(peer_id))
        except (ValueError, OSError, AttributeError):
            self.status.set_text("Saved connection preferences could not be read. Enter the Windows address again.")
        if self.lifecycle_ready:
            self.status.set_text("Pair both computers, grant input permission, then connect. Sharing stays paused until you allow it.")
        return False

    def _refresh_peers(self):
        self.peer_ids = list(self.approvals.cards)
        self.peers.set_model(Gtk.StringList.new([self.approvals.cards[key].display_name for key in self.peer_ids]))

    def _peer(self):
        selected = self.peers.get_selected()
        return self.approvals.cards[self.peer_ids[selected]] if selected < len(self.peer_ids) else None

    def _peer_changed(self):
        if self.connection: self._stop()

    def _dialog(self, title, action, callback, multiple=False, name=None):
        dialog = Gtk.FileChooserNative(title=title, transient_for=self, action=action, accept_label="Choose", cancel_label="Cancel")
        if name: dialog.set_current_name(name)
        dialog.set_select_multiple(multiple)
        def response(widget, result):
            if result == Gtk.ResponseType.ACCEPT:
                self._guard(lambda: callback(widget))
            widget.destroy()
        dialog.connect("response", response); dialog.show()

    def _export(self):
        def save(dialog):
            file = dialog.get_file()
            file.replace_contents(self.identity.card.serialize(), None, False, Gio.FileCreateFlags.PRIVATE, None)
            self.status.set_text("Public pairing card exported. Its private key stays on this computer.")
        self._dialog("Export public pairing card", Gtk.FileChooserAction.SAVE, save, name="Linux.batcave-peer.json")

    def _import(self):
        def selected(dialog):
            path = dialog.get_file().get_path()
            if not path: raise ValueError("Choose a local pairing card.")
            fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK)
            with os.fdopen(fd, "rb") as stream:
                if not stat.S_ISREG(os.fstat(stream.fileno()).st_mode): raise ValueError("Choose a regular pairing card file.")
                data = stream.read(16385)
            card_value = Card.read(data)
            if card_value.device_id == self.identity.card.device_id: raise ValueError("This is your own card.")
            prompt = Gtk.MessageDialog(transient_for=self, modal=True, message_type=Gtk.MessageType.QUESTION,
                  buttons=Gtk.ButtonsType.YES_NO, text="Approve " + card_value.display_name + "?",
                  secondary_text="Confirm this full SHA-256 fingerprint on the other computer:\n\n" + card_value.fingerprint)
            prompt.set_default_response(Gtk.ResponseType.NO)
            def approved(widget, response):
                if response == Gtk.ResponseType.YES:
                    self._stop(); self._guard(lambda: self.approvals.approve(card_value)); self._refresh_peers()
                widget.destroy()
            prompt.connect("response", approved); prompt.show()
        self._dialog("Import pairing card", Gtk.FileChooserAction.OPEN, selected)

    def _unpair(self):
        peer = self._peer()
        if peer:
            self._stop(); self._guard(lambda: self.approvals.remove(peer.device_id)); self._refresh_peers()

    def _grant(self):
        self._stop()
        def action():
            if not self.lifecycle_ready:
                raise RuntimeError("A desktop session with sleep and lock notifications is required.")
            if self.portal is None: self.portal = Portal(self.backend, self._portal_changed)
            self.portal.grant(); self.grant_started = True
        self._guard(action)

    def _portal_changed(self, message, failed=False):
        self.status.set_text(message)
        if failed:
            self.grant_started = False; self._stop()
            self.permission_label.set_text("Input permission: unavailable")

    def _connect(self):
        def action():
            if not self._peer(): raise ValueError("Import and approve the Windows computer's card first.")
            self._stop()
            self.epoch = self.connection.connect(self.address.get_text().strip(), self._peer(), self.backend.bounds())
            self.state.write("connection-preferences.json", {"peerId": self._peer().device_id, "address": self.address.get_text().strip()})
            self.status.set_text("Connecting with mutual TLS 1.3…")
        self._guard(action)

    def _allow(self):
        self._guard(lambda: self.input.enable(self.backend.bounds()))
        if self.input.armed: self.status.set_text("Input allowed. Cross the configured boundary from Windows to begin.")

    def _stop(self):
        try:
            if self.input: self.input.disconnect()
        finally:
            if self.connection: self.connection.stop()
        self.connected = False
        self.allow.set_sensitive(False)
        self.connection_label.set_text("Secure connection: disconnected")
        self.sharing_label.set_text("Input sharing: paused")
        self.status.set_text("Sharing paused. Reconnect and allow input when ready.")

    def _tick(self):
        if self.shutting_down: return False
        try:
            self.backend.poll()
            for epoch, kind, value in self.inbox.drain():
                if epoch != self.epoch: continue
                if kind == "connected":
                    self.connected = True; self.input.connected(epoch)
                    self.connection_label.set_text("Secure connection: TLS 1.3 verified")
                    self.status.set_text("Connected securely. Input stays paused until you allow it.")
                elif kind == "disconnected": self._stop(); self.status.set_text(value)
                elif kind == "message": self.input.handle(epoch, value)
            ready = self.backend.ready()
            if ready: self.grant_started = False
            self.grant.set_sensitive(self.lifecycle_ready and not self.grant_started)
            self.connect_button.set_sensitive(ready and self.connection is not None and self._peer() is not None)
            self.allow.set_sensitive(ready and self.connected and not self.input.armed)
            if ready:
                self.permission_label.set_text("Input permission: keyboard and mouse ready")
                bounds = self.backend.bounds(); self.geometry.set_text(f"Authorized desktop: {bounds.width} × {bounds.height}")
            self.sharing_label.set_text("Input sharing: active" if self.input and self.input.owns_cursor else
                                        "Input sharing: awaiting a handoff" if self.input and self.input.armed else "Input sharing: paused")
        except Exception:
            self._stop()
            if self.portal: self.portal.close()
            self._error("Input permission or session validation failed. Sharing stopped; grant permission and reconnect.")
        return True

    def _watch_lifecycle(self):
        try:
            session_bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
            subscription = session_bus.signal_subscribe("org.gnome.ScreenSaver", "org.gnome.ScreenSaver", "ActiveChanged",
                         None, None, Gio.DBusSignalFlags.NONE, lambda *args: self._suspend() if args[5].unpack()[0] else None)
            self.bus_subscriptions.append((session_bus, subscription))
            subscription = session_bus.signal_subscribe("org.freedesktop.ScreenSaver", "org.freedesktop.ScreenSaver", "ActiveChanged",
                         None, None, Gio.DBusSignalFlags.NONE, lambda *args: self._suspend() if args[5].unpack()[0] else None)
            self.bus_subscriptions.append((session_bus, subscription))
            system = Gio.bus_get_sync(Gio.BusType.SYSTEM, None)
            subscription = system.signal_subscribe("org.freedesktop.login1", "org.freedesktop.login1.Manager", "PrepareForSleep",
                         "/org/freedesktop/login1", None, Gio.DBusSignalFlags.NONE, lambda *args: self._suspend() if args[5].unpack()[0] else None)
            self.bus_subscriptions.append((system, subscription))
            result = system.call_sync("org.freedesktop.login1", "/org/freedesktop/login1", "org.freedesktop.login1.Manager",
                       "GetSessionByPID", GLib.Variant("(u)", (os.getpid(),)), GLib.VariantType.new("(o)"),
                       Gio.DBusCallFlags.NONE, 2000, None)
            session_path = result.unpack()[0]
            subscription = system.signal_subscribe("org.freedesktop.login1", "org.freedesktop.login1.Session", "Lock",
                       session_path, None, Gio.DBusSignalFlags.NONE, lambda *_: self._suspend())
            self.bus_subscriptions.append((system, subscription))
            subscription = system.signal_subscribe("org.freedesktop.login1", "org.freedesktop.DBus.Properties", "PropertiesChanged",
                       session_path, None, Gio.DBusSignalFlags.NONE,
                       lambda *args: self._suspend() if args[5].unpack()[1].get("LockedHint", False) else None)
            self.bus_subscriptions.append((system, subscription))
            subscription = system.signal_subscribe("org.freedesktop.DBus", "org.freedesktop.DBus", "NameOwnerChanged",
                       "/org/freedesktop/DBus", "org.freedesktop.login1", Gio.DBusSignalFlags.NONE,
                       lambda *args: self._lifecycle_lost() if args[5].unpack()[1] else None)
            self.bus_subscriptions.append((system, subscription))
            self.lifecycle_ready = True
        except GLib.Error:
            # A real desktop must supply lifecycle signals; do not arm without them.
            self.grant.set_sensitive(False)
            self.status.set_text("Desktop session services are unavailable. Setup works, but input sharing is disabled.")

    def _suspend(self):
        self._stop()
        self.grant_started = False
        if self.portal: self.portal.close()
        self.permission_label.set_text("Input permission: grant again after unlocking")

    def _lifecycle_lost(self):
        self.lifecycle_ready = False
        self._suspend()
        self.status.set_text("Desktop session services restarted. Restart this preview before sharing input again.")

    def _drop(self, _target, files, _x, _y):
        self._guard(lambda: self._stage_files(files.get_files()))
        return True

    def _choose_files(self):
        self._dialog("Choose files for the shelf", Gtk.FileChooserAction.OPEN,
                     lambda dialog: self._stage_files([dialog.get_files().get_item(i) for i in range(dialog.get_files().get_n_items())]), multiple=True)

    def _stage_files(self, files):
        self._stop()  # File/icon I/O cannot delay input cleanup while sharing.
        if len(files) + len(self.files) > 32: raise ValueError("The preview shelf limit is 32 files.")
        total = sum(os.fstat(fd).st_size for _, fd in self.files)
        pending = []
        try:
            for file in files:
                path = file.get_path()
                if not path: raise ValueError("Only local regular files can enter the shelf.")
                leaf_name(Path(path).name)
                fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK)
                pending.append((file, fd))
                info = os.fstat(fd)
                if not stat.S_ISREG(info.st_mode) or not 0 <= info.st_size <= MAX_FILE:
                    raise ValueError("Choose a regular file no larger than 1 GiB.")
                total += info.st_size
                if total > MAX_BATCH: raise ValueError("The preview shelf limit is 4 GiB in total.")
        except BaseException:
            for _, fd in pending: os.close(fd)
            raise
        for file, fd in pending:
            path = file.get_path()
            info = os.fstat(fd)
            self.files.append((file, fd))
            row = Gtk.Box(spacing=12); row.add_css_class("file-row")
            try: icon = file.query_info("standard::icon", Gio.FileQueryInfoFlags.NOFOLLOW_SYMLINKS, None).get_icon()
            except GLib.Error: icon = Gio.ThemedIcon.new("text-x-generic")
            image = Gtk.Image.new_from_gicon(icon); image.set_pixel_size(40); row.append(image)
            labels = column(3); labels.set_hexpand(True); labels.append(text(Path(path).name)); labels.append(text(f"{info.st_size:,} bytes · staged locally", "muted")); row.append(labels)
            source = Gtk.DragSource(actions=Gdk.DragAction.COPY)
            source.connect("prepare", lambda _source, _x, _y, file=file: Gdk.ContentProvider.new_for_value(Gdk.FileList.new_from_list([file])))
            paintable = Gtk.IconTheme.get_for_display(self.get_display()).lookup_by_gicon(icon, 64, 1, Gtk.TextDirection.NONE, Gtk.IconLookupFlags.PRELOAD)
            source.set_icon(paintable, 0, 0); row.add_controller(source); self.file_list.append(row)
        self.status.set_text("Files staged locally with their source icons. Network file transfer is not enabled in this preview.")

    def _clear_files(self):
        for _, fd in self.files: os.close(fd)
        self.files.clear()
        while self.file_list.get_first_child(): self.file_list.remove(self.file_list.get_first_child())

    def _error(self, message):
        self.status.set_text(message); return False

    def _guard(self, action):
        try: action()
        except Exception as error: self._error(str(error))

    def _close(self, *_):
        self.shutting_down = True
        GLib.source_remove(self.tick)
        self._stop()
        if self.portal: self.portal.dispose()
        for bus, subscription in self.bus_subscriptions: bus.signal_unsubscribe(subscription)
        self.backend.close(); self._clear_files()
        if self.state: self.state.close()
        return False


class Application(Gtk.Application):
    def __init__(self, library=None, fixture=None):
        super().__init__(application_id="dev.batcave.Linux.SecurePreview", flags=Gio.ApplicationFlags.DEFAULT_FLAGS)
        self.library, self.fixture, self.window = library, fixture, None

    def do_activate(self):
        if self.window is None: self.window = Window(self, self.fixture)
        self.window.present()
