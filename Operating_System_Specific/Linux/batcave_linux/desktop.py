"""Native GTK setup. Permission, trust and active input are separate decisions."""

import ipaddress
import os
import stat
import threading
from pathlib import Path

import gi
gi.require_version("Gtk", "4.0")
gi.require_version("Gdk", "4.0")
from gi.repository import Gdk, Gio, GLib, Gtk, Pango

from .ei import EiBackend
from .identity import Approvals, Card, Identity, SecretVault, strict_json
from .input import InputSession
from .portal import Portal
from .protocol import Bounds
from .storage import StateDirectory, profile_path
from .transfers import MAX_BATCH, MAX_FILE, leaf_name
from .transport import Connection, Inbox
from .setup_flow import STEP_TITLES, SetupState, primary_action


def text(value, style=None):
    widget = Gtk.Label(label=value, xalign=0, wrap=True)
    widget.set_wrap_mode(Pango.WrapMode.WORD_CHAR)
    widget.set_max_width_chars(72)
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
        super().__init__(application=application, title="BatCave — Linux secure preview", default_width=1000, default_height=760)
        self.set_size_request(880, 640)
        self.state = self.identity = self.approvals = self.connection = self.input = self.portal = None
        self.inbox = Inbox()
        self.epoch = 0
        self.connected = False
        self.connecting = False
        self.remote_bounds = None
        self.backend = EiBackend(application.library)
        self.files = []
        self.bus_subscriptions = []
        self.grant_started = False
        self.lifecycle_ready = False
        self.shutting_down = False
        self.peer_ids = []
        self.setup_step = 0
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
            .content { background: #f5f7fb; color: #18283d; }
            .header { background: #ffffff; padding: 18px 24px; border-bottom: 1px solid #dce4ef; }
            .brand { font-size: 25px; font-weight: 700; }
            .subtitle, .muted { color: #5c6c83; }
            .card { background: #ffffff; border: 1px solid #dce4ef; border-radius: 14px; padding: 24px; }
            .card-title { font-size: 22px; font-weight: 700; }
            .link-card { padding: 14px 18px; }
            .desktop-tile { background: #edf2fa; border: 1px solid #dce4ef; border-radius: 10px; padding: 12px; }
            .hero { font-size: 30px; font-weight: 700; }
            .step { background: transparent; border: 0; box-shadow: none; color: #66778e; padding: 12px 8px; }
            .step.current { background: #e7eefb; color: #245496; font-weight: 700; border-radius: 10px; }
            .status { color: #425b79; padding: 4px 0; }
            .drop-zone { border: 2px dashed #aac0dc; border-radius: 12px; padding: 24px; }
            .file-row { padding: 10px; border-bottom: 1px solid #e3e9f1; }
            button { padding: 10px 16px; }
            entry { padding: 8px; }
        """)
        Gtk.StyleContext.add_provider_for_display(self.get_display(), css, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION)
        root = column(0); root.add_css_class("content"); self.set_child(root)
        header = Gtk.Box(spacing=20); header.add_css_class("header")
        brand = column(2); brand.set_hexpand(True)
        brand.append(text("BatCave", "brand")); brand.append(text("Your computers, together", "subtitle"))
        header.append(brand)
        self.stack = Gtk.Stack(hexpand=True, vexpand=True)
        tabs = Gtk.StackSwitcher(halign=Gtk.Align.CENTER)
        tabs.set_stack(self.stack); header.append(tabs)
        self.pause_button = button("Pause sharing", self._stop)
        self.pause_button.set_sensitive(False); header.append(self.pause_button); root.append(header)
        root.append(self.stack)
        devices = Gtk.Box(spacing=0)
        setup = column(16); setup.set_hexpand(True); setup.set_vexpand(True)
        setup.set_margin_start(24); setup.set_margin_end(24); setup.set_margin_top(16); setup.set_margin_bottom(20)
        self.stack.add_titled(devices, "setup", "Devices")
        steps = column(8); steps.set_size_request(215, -1)
        steps.set_margin_top(16); steps.set_margin_start(12)
        self.step_buttons = []
        for index, title in enumerate(STEP_TITLES):
            widget = button(f"{index + 1}  {title}", lambda index=index: self._show_step(index))
            widget.get_child().set_xalign(0)
            widget.add_css_class("step"); steps.append(widget); self.step_buttons.append(widget)
        devices.append(steps); devices.append(setup)
        self.page_scroll = Gtk.ScrolledWindow(hexpand=True, vexpand=True)
        self.page_scroll.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        setup.append(self.page_scroll)
        self.pages = Gtk.Stack(hexpand=True, vexpand=True, transition_type=Gtk.StackTransitionType.CROSSFADE)
        self.pages.set_vhomogeneous(False); self.pages.set_hhomogeneous(False)
        self.page_scroll.set_child(self.pages)

        permissions = card("One keyboard. Both computers.", "Use your Windows keyboard and mouse on this Linux desktop.")
        self.permission_summary = text("Grant access when you're ready.")
        permissions.append(self.permission_summary)
        permissions.append(text("Your desktop will ask before BatCave can receive keyboard and mouse input.", "muted"))
        permissions.append(text("Access alone doesn't start sharing. You'll choose a trusted computer next.", "muted"))
        self.pages.add_named(permissions, "0")

        pair = card("Pair with your Windows computer", "Exchange public device cards and confirm the fingerprint shown on the other computer.")
        self.pair_summary = text("Add the Windows computer you want to use.")
        pair.append(self.pair_summary)
        self.export = button("Export device card", self._export)
        self.export.set_sensitive(False)
        card_actions = Gtk.Box(spacing=10); card_actions.append(self.export)
        self.import_another = button("Import another card", self._import)
        self.import_another.set_visible(False); card_actions.append(self.import_another)
        pair.append(card_actions)
        pair.append(text("Paired computer", "muted"))
        self.peers = Gtk.DropDown.new_from_strings([]); self.peers.set_hexpand(True)
        self.peers.connect("notify::selected", lambda *_: self._peer_changed())
        pair.append(self.peers)
        pair.append(text("Cards contain public identity information. Your private key stays on this computer.", "muted"))
        self.pages.add_named(pair, "1")

        layout = card("Arrange screens", "Match this side in BatCave on Windows. Windows controls the shared screen boundary.")
        layout.add_css_class("link-card"); layout.set_spacing(8)
        layout.append(text("Windows address", "muted"))
        self.address = Gtk.Entry(placeholder_text="Address shown in BatCave on Windows", hexpand=True)
        self.address.connect("changed", lambda *_: self._update_setup())
        layout.append(self.address)
        self.connection_summary = text("Choose your computer and enter its Windows address.")
        layout.append(self.connection_summary)
        self.side = Gtk.DropDown.new_from_strings(["Linux is on the right", "Linux is on the left"])
        self.side.connect("notify::selected", lambda *_: self._arrange_tiles()); layout.append(self.side)
        self.desktop_tiles = Gtk.Box(spacing=12, homogeneous=True)
        self.windows_tile, self.windows_geometry = self._desktop_tile("Windows")
        self.linux_tile, self.linux_geometry = self._desktop_tile("Linux")
        self._arrange_tiles(); layout.append(self.desktop_tiles)
        layout.append(text("Each computer is shown as one desktop. This guide doesn't change the Windows layout.", "muted"))
        self.pages.add_named(layout, "2")

        link = card("Link devices", "Enable Linux input here, then choose Start sharing on Windows.")
        self.link_summary = text("Choose your computer and enter its address.")
        link.append(self.link_summary)
        link.append(text("Move through the shared edge on Windows to use Linux. Pause sharing restores local control.", "muted"))
        link.append(text("You can save this setup for next time. Reconnecting always asks you to enable input again.", "muted"))
        self.pages.add_named(link, "3")

        self.details = Gtk.Expander(label="Connection details")
        detail_box = column(6); detail_box.set_margin_top(10)
        self.fingerprint = text("Opening your device…", "muted"); self.fingerprint.set_selectable(True)
        self.peer_fingerprint = text("No device paired.", "muted"); self.peer_fingerprint.set_selectable(True)
        self.permission_label = text("Desktop access: not granted", "muted")
        self.connection_label = text("Connection: not linked", "muted")
        self.sharing_label = text("Sharing: paused", "muted")
        self.geometry = text("Display regions appear after granting access.", "muted")
        for widget in (self.fingerprint, self.peer_fingerprint, self.permission_label, self.connection_label, self.sharing_label, self.geometry):
            detail_box.append(widget)
        detail_box.append(text("Arrange the shared screen boundary in BatCave on Windows.", "muted"))
        self.remove_peer = button("Remove paired device", self._unpair)
        detail_box.append(self.remove_peer)
        self.detail_scroll = Gtk.ScrolledWindow(min_content_height=180)
        self.detail_scroll.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        self.detail_scroll.set_child(detail_box)
        self.details.set_child(self.detail_scroll); setup.append(self.details)
        self.status = text("Opening your device…", "status"); setup.append(self.status)
        actions = Gtk.Box(spacing=12)
        self.back = button("Back", lambda: self._show_step(max(0, self.setup_step - 1)))
        actions.append(self.back)
        spacer = Gtk.Box(hexpand=True); actions.append(spacer)
        self.save_setup = button("Save setup", lambda: self._guard(self._save_setup))
        actions.append(self.save_setup)
        self.primary = button("Continue", self._activate_primary, True)
        actions.append(self.primary); setup.append(actions)

        files = column(16); files.set_margin_start(24); files.set_margin_end(24); files.set_margin_top(16); files.set_margin_bottom(20)
        intro = card("Files", "Keep files here with their original icons. Sending between devices is coming next.")
        drop = column(); drop.add_css_class("drop-zone"); drop.append(text("Drop files here", "card-title"))
        drop.append(text("Or choose files below.", "muted"))
        target = Gtk.DropTarget.new(Gdk.FileList, Gdk.DragAction.COPY); target.connect("drop", self._drop)
        drop.add_controller(target); intro.append(drop); files.append(intro)
        self.file_list = column(0)
        scroll = Gtk.ScrolledWindow(vexpand=True); scroll.set_child(self.file_list); files.append(scroll)
        row = Gtk.Box(spacing=10); row.append(button("Choose files", self._choose_files)); row.append(button("Clear shelf", self._clear_files))
        disabled = button("Send files", lambda: None); disabled.set_sensitive(False); row.append(disabled)
        files.append(row)
        self.file_status = text("Files stay on this computer until you drag them out of the shelf.", "status")
        files.append(self.file_status); self.stack.add_titled(files, "files", "Files")
        self._show_step(0)

    def _show_step(self, step):
        if not 0 <= step < len(STEP_TITLES): return
        self.setup_step = step; self.pages.set_visible_child_name(str(step))
        for index, widget in enumerate(self.step_buttons):
            if index == step: widget.add_css_class("current")
            else: widget.remove_css_class("current")
        self.back.set_visible(step > 0)
        self.save_setup.set_visible(step == 3)
        self._update_setup()

    def _desktop_tile(self, title):
        tile = column(3); tile.add_css_class("desktop-tile")
        heading = Gtk.Box(spacing=8)
        heading.append(Gtk.Image.new_from_icon_name("computer-symbolic")); heading.append(text(title)); tile.append(heading)
        geometry = text("After linking", "muted"); tile.append(geometry)
        return tile, geometry

    def _arrange_tiles(self):
        if not hasattr(self, "windows_tile"): return
        while self.desktop_tiles.get_first_child(): self.desktop_tiles.remove(self.desktop_tiles.get_first_child())
        order = (self.windows_tile, self.linux_tile) if self.side.get_selected() == 0 else (self.linux_tile, self.windows_tile)
        for tile in order: self.desktop_tiles.append(tile)

    def _save_setup(self):
        peer = self._peer()
        if not peer or not self._setup_state().address_valid: raise ValueError("Pair a computer and enter its Windows address first.")
        self.state.write("connection-preferences.json", {"peerId": peer.device_id, "address": self.address.get_text().strip(),
                         "linuxOnRight": self.side.get_selected() == 0})
        self.status.set_text("Setup saved. Input still needs your approval each time you reconnect.")

    def _setup_state(self):
        try:
            address = ipaddress.ip_address(self.address.get_text().strip())
            address_valid = not (address.is_unspecified or address.is_multicast)
        except ValueError:
            address_valid = False
        return SetupState(self.identity is not None, self.lifecycle_ready, self.grant_started,
                          self.lifecycle_ready and self.backend.ready(), self._peer() is not None if self.approvals else False,
                          address_valid, self.connecting, self.connected, bool(self.input and self.input.armed))

    def _update_setup(self):
        if not hasattr(self, "primary"): return
        state = self._setup_state()
        action = primary_action(self.setup_step, state)
        if self.primary.get_label() != action.label: self.primary.set_label(action.label)
        self.primary.set_sensitive(action.enabled)
        self.pause_button.set_sensitive(state.connected or state.connecting or state.armed)
        self.save_setup.set_sensitive(state.peer_selected and state.address_valid)
        if state.permission_ready:
            self.permission_summary.set_text("Ready to receive your keyboard and mouse.")
        elif state.permission_pending:
            self.permission_summary.set_text("Choose Allow in your desktop's permission window.")
        elif state.identity_ready and not state.desktop_ready:
            self.permission_summary.set_text("Desktop session services aren't available. Input sharing is paused.")
        else:
            self.permission_summary.set_text("Grant access when you're ready.")
        peer = self._peer() if self.approvals else None
        self.import_another.set_visible(state.peer_selected)
        self.remove_peer.set_sensitive(state.peer_selected)
        self.pair_summary.set_text("Paired with " + peer.display_name + "." if peer else "Add the Windows computer you want to use.")
        self.peer_fingerprint.set_text("Paired device SHA-256\n" + peer.fingerprint if peer else "No device paired.")
        if state.permission_ready:
            bounds = self.backend.bounds(); self.linux_geometry.set_text(f"{bounds.width} × {bounds.height}")
        else: self.linux_geometry.set_text("After desktop access")
        self.windows_geometry.set_text(f"{self.remote_bounds.width} × {self.remote_bounds.height}" if state.connected and self.remote_bounds else "After linking")
        if state.armed:
            self.link_summary.set_text("Ready for Windows input. Choose Start sharing on Windows and move through the shared edge.")
        elif state.connected:
            self.link_summary.set_text("Devices are securely linked. Choose Enable Linux input when you're ready.")
        elif state.connecting:
            self.link_summary.set_text("Linking your devices securely…")
        elif not state.permission_ready:
            self.link_summary.set_text("Grant desktop access before linking your devices.")
        else:
            self.link_summary.set_text("Choose your computer and enter its Windows address.")
        self.connection_summary.set_text("Encrypted connection verified." if state.connected else self.link_summary.get_text())

    def _activate_primary(self):
        action = primary_action(self.setup_step, self._setup_state())
        if not action.enabled: return
        operations = {"next": lambda: self._show_step(min(3, self.setup_step + 1)), "grant": self._grant,
                      "import": self._import, "permission": lambda: self._show_step(0),
                      "pair": lambda: self._show_step(1), "connect": self._connect,
                      "start": self._allow, "pause": self._stop}
        self._guard(operations.get(action.operation, lambda: None))

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
        self.export.set_sensitive(True)
        self._refresh_peers(); self._watch_lifecycle()
        try:
            saved = state.read("connection-preferences.json", 4096)
            if saved:
                preferences = strict_json(saved)
                address = preferences.get("address", "")
                if isinstance(address, str) and len(address) <= 64: self.address.set_text(address)
                peer_id = preferences.get("peerId")
                if peer_id in self.peer_ids: self.peers.set_selected(self.peer_ids.index(peer_id))
                side = preferences.get("linuxOnRight", True)
                if type(side) is bool: self.side.set_selected(0 if side else 1)
        except (ValueError, OSError, AttributeError):
            self.status.set_text("Saved connection preferences could not be read. Enter the Windows address again.")
        if self.lifecycle_ready:
            self.status.set_text("Your device is ready. Continue to link your computers.")
        self._update_setup()
        return False

    def _refresh_peers(self):
        self.peer_ids = list(self.approvals.cards)
        self.peers.set_model(Gtk.StringList.new([self.approvals.cards[key].display_name for key in self.peer_ids]))
        self._update_setup()

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
            self._update_setup()
        self._guard(action)

    def _portal_changed(self, message, failed=False):
        self.status.set_text(message)
        if failed:
            self.grant_started = False; self._stop()
            self.permission_label.set_text("Input permission: unavailable")

    def _connect(self):
        def action():
            if not self.lifecycle_ready or not self.backend.ready(): raise RuntimeError("Grant desktop access before linking devices.")
            if not self._peer(): raise ValueError("Import and approve the Windows computer's card first.")
            self._stop()
            self._save_setup()
            self.epoch = self.connection.connect(self.address.get_text().strip(), self._peer(), self.backend.bounds())
            self.connecting = True
            self.status.set_text("Linking your devices securely…")
            self._update_setup()
        self._guard(action)

    def _allow(self):
        def action():
            if not self.lifecycle_ready or not self.connected: raise RuntimeError("Link devices before starting sharing.")
            self.input.enable(self.backend.bounds())
        self._guard(action)
        if self.input.armed: self.status.set_text("Ready for Windows input. Choose Start sharing on Windows.")
        self._update_setup()

    def _stop(self):
        try:
            if self.input: self.input.disconnect()
        finally:
            if self.connection:
                self.connection.stop()
                # Invalidate callbacks already queued before the socket closed.
                self.epoch = self.connection.epoch
            else:
                self.epoch += 1
        self.connected = False
        self.connecting = False
        self.remote_bounds = None
        self._update_setup()
        self.connection_label.set_text("Secure connection: disconnected")
        self.sharing_label.set_text("Input sharing: paused")
        self.status.set_text("Sharing paused. Reconnect and choose Enable Linux input when ready.")

    def _tick(self):
        if self.shutting_down: return False
        try:
            self.backend.poll()
            for epoch, kind, value in self.inbox.drain():
                if epoch != self.epoch: continue
                if kind == "connected":
                    self.connected = True; self.connecting = False; self.input.connected(epoch)
                    self.remote_bounds = value
                    self.connection_label.set_text("Secure connection: TLS 1.3 verified")
                    self.status.set_text("Devices linked securely. Choose Enable Linux input when ready.")
                elif kind == "disconnected": self._stop(); self.status.set_text(value)
                elif kind == "message": self.input.handle(epoch, value)
            ready = self.backend.ready()
            if ready: self.grant_started = False
            self._update_setup()

            if ready:
                self.permission_label.set_text("Desktop access: keyboard and mouse ready")
                bounds = self.backend.bounds(); self.geometry.set_text(f"Authorized desktop: {bounds.width} × {bounds.height}")
            else:
                self.permission_label.set_text("Desktop access: waiting for approval" if self.grant_started else "Desktop access: not granted")
                self.geometry.set_text("Display regions appear after granting access.")
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
            self._update_setup()
            self.status.set_text("Desktop session services are unavailable. Setup works, but input sharing is disabled.")

    def _suspend(self):
        self._stop()
        self.grant_started = False
        if self.portal: self.portal.close()
        self.permission_label.set_text("Input permission: grant again after unlocking")
        self._update_setup()

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
        self.file_status.set_text("Files staged locally with their source icons. Sending between devices is coming next.")

    def _clear_files(self):
        for _, fd in self.files: os.close(fd)
        self.files.clear()
        while self.file_list.get_first_child(): self.file_list.remove(self.file_list.get_first_child())
        self.file_status.set_text("Files stay on this computer until you drag them out of the shelf.")

    def _error(self, message):
        self.status.set_text(message)
        if self.stack.get_visible_child_name() == "files": self.file_status.set_text(message)
        return False

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
