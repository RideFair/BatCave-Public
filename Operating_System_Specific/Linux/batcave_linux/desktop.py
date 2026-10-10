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
from .capture import CaptureBackend
from .capture_portal import CapturePortal
from .identity import Approvals, Card, Identity, SecretVault, strict_json
from .input import InputSession
from .source import SourceSession
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
        super().__init__(application=application, title="BatCave", default_width=1000, default_height=760)
        self.set_size_request(880, 640)
        self.state = self.identity = self.approvals = self.connection = self.input = self.portal = None
        self.inbox = Inbox()
        self.epoch = 0
        self.connected = False
        self.connecting = False
        self.remote_bounds = None
        self.backend = EiBackend(application.library)
        self.capture_backend = CaptureBackend(application.library)
        self.source = None
        self.source_supported = False
        self.source_reason = "Checking whether this desktop supports keyboard and mouse capture…"
        self.files = []
        self.bus_subscriptions = []
        self.grant_started = False
        self.lifecycle_ready = False
        self.shutting_down = False
        self.peer_ids = []
        self.setup_step = 0
        self.pair_import_ready = False
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
            .content { background: #f2f4f8; color: #1f2639; font-size: 14px; }
            .header { padding: 24px 26px 16px; }
            .brand { color: #ffffff; font-size: 27px; font-weight: 700; }
            .subtitle, .muted { color: #646e83; }
            .page-title { font-size: 27px; font-weight: 700; }
            .kicker { color: #646e83; font-size: 12px; }
            .card { background: #ffffff; border: 1px solid #dee2eb; border-radius: 14px; padding: 24px; }
            .card-title { font-size: 22px; font-weight: 700; }
            .link-card { padding: 14px 18px; }
            .desktop-tile { background: #f2f4f8; border: 1px solid #dee2eb; border-radius: 10px; padding: 12px; }
            .sidebar { background: #181d2f; padding: 24px 12px 20px; }
            .sidebar .subtitle { color: #97a2bb; font-size: 12px; }
            .sidebar .brand { padding: 0 12px; }
            .sidebar .tagline { padding: 0 12px 20px; }
            .step { background: transparent; border: 0; box-shadow: none; color: #ced5e7; padding: 12px 10px; }
            .step:hover { background: #2a3047; }
            .step.current { background: #312e4f; color: #ffffff; border-radius: 10px; }
            .step-heading { font-size: 13px; font-weight: 700; }
            .step-description { color: #97a2bb; font-size: 11px; }
            .step-number { background: #2c3348; color: #ffffff; border-radius: 14px; font-size: 12px; }
            .step.current .step-number { background: #634fe1; }
            .sidebar-status { color: #ced5e7; padding: 0 10px; font-size: 12px; }
            .sidebar button.pause { background: #232a40; color: #ced5e7; border-color: #353d55; }
            .sidebar button.pause:disabled { color: #97a2bb; }
            .section-heading { font-weight: 700; }
            .section-number { color: #634fe1; font-weight: 700; }
            .pair-status { background: #f2f4f8; border-radius: 8px; padding: 12px; }
            .status { color: #646e83; padding: 4px 0; font-size: 12px; }
            .drop-zone { border: 2px dashed #aac0dc; border-radius: 12px; padding: 24px; }
            .file-row { padding: 10px; border-bottom: 1px solid #e3e9f1; }
            button { background: #ffffff; color: #1f2639; border: 1px solid #dee2eb; border-radius: 8px; padding: 10px 16px; box-shadow: none; }
            button:hover { background: #f0edfc; }
            button:disabled { background: #e8ebf2; color: #646e83; }
            button.suggested-action { background: #634fe1; color: #ffffff; border-color: #634fe1; }
            button.suggested-action:hover { background: #4f3cc8; }
            button.suggested-action:disabled { background: #e8ebf2; color: #646e83; border-color: #dee2eb; }
            .header button:checked { background: #e9e5fd; color: #4f3cc8; border-color: #cabffa; }
            entry { padding: 8px; }
        """)
        Gtk.StyleContext.add_provider_for_display(self.get_display(), css, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION)
        root = Gtk.Box(spacing=0); root.add_css_class("content"); self.set_child(root)
        sidebar = column(6); sidebar.add_css_class("sidebar"); sidebar.set_size_request(226, -1)
        sidebar.append(text("BatCave", "brand"))
        tagline = text("Your computers, together.", "subtitle"); tagline.add_css_class("tagline"); sidebar.append(tagline)
        self.step_buttons = []
        descriptions = ("Allow desktop access", "Approve a computer", "Choose the shared edge", "Start when you're ready")
        for index, title in enumerate(STEP_TITLES):
            widget = button(title, lambda index=index: self._show_devices_step(index))
            row = Gtk.Box(spacing=10)
            number = Gtk.Label(label=str(index + 1)); number.add_css_class("step-number")
            number.set_size_request(28, 28); number.set_valign(Gtk.Align.CENTER); row.append(number)
            labels = column(3); labels.append(text(title, "step-heading")); labels.append(text(descriptions[index], "step-description"))
            row.append(labels); widget.set_child(row)
            widget.add_css_class("step"); sidebar.append(widget); self.step_buttons.append(widget)
        sidebar.append(Gtk.Box(vexpand=True))
        self.sidebar_status = text("Sharing is paused", "sidebar-status"); sidebar.append(self.sidebar_status)
        self.pause_button = button("Pause sharing", self._stop); self.pause_button.add_css_class("pause")
        self.pause_button.set_sensitive(False); sidebar.append(self.pause_button)
        root.append(sidebar)
        content = column(0); content.set_hexpand(True); content.set_vexpand(True); root.append(content)
        header = Gtk.Box(spacing=20); header.add_css_class("header")
        heading = column(4); heading.set_hexpand(True)
        self.page_kicker = text("Set up your computers", "kicker")
        self.page_title = text(STEP_TITLES[0], "page-title")
        heading.append(self.page_kicker); heading.append(self.page_title); header.append(heading)
        self.stack = Gtk.Stack(hexpand=True, vexpand=True)
        self.stack.connect("notify::visible-child-name", lambda *_: self._update_title())
        tabs = Gtk.StackSwitcher(halign=Gtk.Align.CENTER)
        tabs.set_stack(self.stack); header.append(tabs)
        content.append(header); content.append(self.stack)
        setup = column(16); setup.set_hexpand(True); setup.set_vexpand(True)
        setup.set_margin_start(26); setup.set_margin_end(26); setup.set_margin_bottom(20)
        self.stack.add_titled(setup, "setup", "Devices")
        self.page_scroll = Gtk.ScrolledWindow(hexpand=True, vexpand=True)
        self.page_scroll.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        setup.append(self.page_scroll)
        self.pages = Gtk.Stack(hexpand=True, vexpand=True, transition_type=Gtk.StackTransitionType.CROSSFADE)
        self.pages.set_vhomogeneous(False); self.pages.set_hhomogeneous(False)
        self.page_scroll.set_child(self.pages)

        permissions = card("One keyboard. Both computers.", "Choose where the keyboard and mouse will come from.")
        self.role = Gtk.DropDown.new_from_strings(["Receive input here", "Use this keyboard and mouse"])
        self.role.connect("notify::selected", lambda *_: self._configuration_changed())
        permissions.append(self.role)
        self.permission_summary = text("Grant access when you're ready.")
        permissions.append(self.permission_summary)
        self.permission_explanation = text("Your desktop will ask before BatCave can receive keyboard and mouse input.", "muted")
        permissions.append(self.permission_explanation)
        permissions.append(text("Access alone doesn't start sharing. You'll choose a trusted computer next.", "muted"))
        self.pages.add_named(permissions, "0")

        pair = card("Add your first computer", "Keep BatCave open on both computers. Exchange a connection file once to approve each other.")
        self.pair_title = pair.get_first_child()
        self.pair_explanation = self.pair_title.get_next_sibling()
        self.peer_os = Gtk.DropDown.new_from_strings(["Windows", "Mac", "Linux"])
        self.peer_os.connect("notify::selected", lambda *_: self._configuration_changed())
        pair.append(text("Other computer", "muted")); pair.append(self.peer_os)
        self.pair_guide = column(10)
        self.send_guide = column(10)
        self.send_guide.append(self._section_heading("1", "Send this Linux computer's file"))
        self.send_instructions = text("", "muted"); self.send_guide.append(self.send_instructions)
        self.skip_import = skip = button("I already have the other file", self._prepare_import); skip.set_halign(Gtk.Align.START)
        self.send_guide.append(skip); self.pair_guide.append(self.send_guide)
        self.receive_guide = column(10)
        self.receive_guide.append(self._section_heading("2", "Bring the other computer's file here"))
        self.receive_instructions = text("", "muted"); self.receive_guide.append(self.receive_instructions)
        again = button("Save Linux file again…", self._export); again.set_halign(Gtk.Align.START)
        self.receive_guide.append(again); self.pair_guide.append(self.receive_guide)
        self.pair_guide.append(text("Connection files identify your computers. Your private key stays here; exchanging a file doesn't start input sharing.", "muted"))
        pair.append(self.pair_guide)
        self.pair_summary = text("No computer has been approved yet.", "pair-status"); pair.append(self.pair_summary)
        self.peer_controls = column(8)
        self.peer_controls.append(text("Approved computer", "muted"))
        self.peers = Gtk.DropDown.new_from_strings([]); self.peers.set_hexpand(True)
        factory = Gtk.SignalListItemFactory()
        def setup_name(_factory, item):
            label = Gtk.Label(xalign=0, ellipsize=Pango.EllipsizeMode.END, max_width_chars=28)
            label.set_hexpand(True); item.set_child(label)
        def bind_name(_factory, item):
            name = item.get_item().get_string()
            item.get_child().set_text(name); item.get_child().set_tooltip_text(name)
        factory.connect("setup", setup_name); factory.connect("bind", bind_name)
        self.peers.set_factory(factory)
        self.peers.connect("notify::selected", lambda *_: self._peer_changed())
        self.peer_controls.append(self.peers)
        self.peer_controls.append(text("Both computers must approve each other's connection file before linking.", "muted"))
        approved_actions = Gtk.Box(spacing=10)
        self.export = button("Save Linux file…", self._export); self.export.set_sensitive(False)
        approved_actions.append(self.export)
        self.import_another = button("Add another computer…", self._import)
        approved_actions.append(self.import_another); self.peer_controls.append(approved_actions)
        self.peer_controls.set_visible(False); pair.append(self.peer_controls)
        older_windows = Gtk.Expander(label="Pairing with Windows 0.2.0?")
        older_help = text("On Windows 0.2.0, Save Windows card saves its connection file, and Import device card adds Linux. Connection details shows the full approval code and Windows address.", "muted")
        older_help.set_margin_top(8); older_windows.set_child(older_help); pair.append(older_windows)
        self.pages.add_named(pair, "1")

        layout = card("Arrange screens", "Put the other computer on the same side as its physical screen.")
        layout.add_css_class("link-card"); layout.set_spacing(8)
        self.network_mode = Gtk.DropDown.new_from_strings(["Connect to the other computer", "Wait for the other computer"])
        self.network_mode.connect("notify::selected", lambda *_: self._configuration_changed())
        layout.append(self.network_mode)
        self.address_box = column(6)
        self.address_box.append(text("Other computer's local network address", "muted"))
        self.address = Gtk.Entry(placeholder_text="Enter the local network address", hexpand=True)
        self.address.connect("changed", lambda *_: self._address_changed())
        self.address_box.append(self.address); layout.append(self.address_box)
        self.wait_guide = text("Choose Connect on the other computer and enter this Linux computer's network address. Use the same port on both computers; see Connection details.", "muted")
        layout.append(self.wait_guide)
        self.connection_summary = text("Choose your computer and enter its Windows address.")
        layout.append(self.connection_summary)
        self.side = Gtk.DropDown.new_from_strings(["Other computer is on the left", "Other computer is on the right"])
        self.side.connect("notify::selected", lambda *_: self._configuration_changed()); layout.append(self.side)
        self.desktop_tiles = Gtk.Box(spacing=12, homogeneous=True)
        self.windows_tile, self.windows_geometry = self._desktop_tile("Windows")
        self.linux_tile, self.linux_geometry = self._desktop_tile("This Linux computer")
        self._arrange_tiles(); layout.append(self.desktop_tiles)
        layout.append(text("Each computer is shown as one desktop. Set the shared edge on the computer whose keyboard and mouse you use.", "muted"))
        self.pages.add_named(layout, "2")

        link = card("Link devices", "Choose Receive input on one computer and use the keyboard and mouse of the other.")
        self.link_summary = text("Choose your computer and enter its address.")
        link.append(self.link_summary)
        link.append(text("Enable input on the receiving computer, then choose Start sharing on the source. Pause sharing restores local control.", "muted"))
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
        detail_box.append(text("Connection port", "muted"))
        self.port = Gtk.SpinButton.new_with_range(1, 65535, 1); self.port.set_value(47870)
        self.port.connect("value-changed", lambda *_: self._address_changed()); detail_box.append(self.port)
        detail_box.append(text("When waiting, BatCave accepts only the selected approved computer on this port. It never enables input automatically.", "muted"))
        self.remove_peer = button("Remove computer", self._unpair); self.remove_peer.set_halign(Gtk.Align.START)
        self.remove_peer.set_visible(False)
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
        self.back.set_visible(step > 0)
        self.save_setup.set_visible(step == 3)
        self._update_title()
        self._update_setup()

    def _show_devices_step(self, step):
        self.stack.set_visible_child_name("setup"); self._show_step(step)

    def _update_title(self):
        files = self.stack.get_visible_child_name() == "files"
        self.page_kicker.set_text("Your file shelf" if files else f"Step {self.setup_step + 1} of {len(STEP_TITLES)}")
        self.page_title.set_text("Files" if files else STEP_TITLES[self.setup_step])
        for index, widget in enumerate(self.step_buttons):
            if not files and index == self.setup_step: widget.add_css_class("current")
            else: widget.remove_css_class("current")

    def _section_heading(self, number, title):
        row = Gtk.Box(spacing=10); row.set_margin_top(6)
        row.append(text(number, "section-number")); row.append(text(title, "section-heading"))
        return row

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

    def _is_source(self):
        return self.role.get_selected() == 1

    def _is_listener(self):
        return self.network_mode.get_selected() == 1

    def _os_name(self):
        return ("Windows", "Mac", "Linux")[min(2, self.peer_os.get_selected())]

    def _address_changed(self):
        if self.connected or self.connecting: self._stop()
        self._update_setup()

    def _configuration_changed(self):
        if not hasattr(self, "primary"): return
        self._stop()
        if self.portal: self.portal.dispose(); self.portal = None
        self.source = None; self.capture_backend.close(); self.backend.close()
        self.grant_started = False
        self._arrange_tiles(); self._update_setup()

    def _permission_ready(self):
        return bool(self.lifecycle_ready and (self.portal and isinstance(self.portal, CapturePortal) and self.portal.ready()
                                             if self._is_source() else self.backend.ready()))

    def _bounds(self):
        return self.portal.bounds if self._is_source() else self.backend.bounds()

    def _save_setup(self):
        peer = self._peer()
        if not peer or not (self._is_listener() or self._setup_state().address_valid): raise ValueError("Pair a computer and enter its address, or choose Wait for computer.")
        self.state.write("connection-preferences.json", {"peerId": peer.device_id, "address": self.address.get_text().strip(),
                         "linuxOnRight": self.side.get_selected() == 0, "source": self._is_source(),
                         "listen": self._is_listener(), "peerOS": self._os_name(), "port": self.port.get_value_as_int()})
        self.status.set_text("Setup saved. Input still needs your approval each time you reconnect.")

    def _setup_state(self):
        try:
            address = ipaddress.ip_address(self.address.get_text().strip())
            address_valid = not (address.is_unspecified or address.is_multicast)
        except ValueError:
            address_valid = False
        return SetupState(self.identity is not None, self.lifecycle_ready, self.grant_started,
                          self._permission_ready(), self._peer() is not None if self.approvals else False,
                          address_valid, self.connecting, self.connected,
                          bool(self.source and self.source.armed if self._is_source() else self.input and self.input.armed),
                          self.pair_import_ready, source=self._is_source(), listen=self._is_listener(),
                          permission_supported=not self._is_source() or self.source_supported,
                          enable_pending=bool(self.source and self.source.pending_enable), peer_os=self._os_name())

    def _update_setup(self):
        if not hasattr(self, "primary"): return
        state = self._setup_state()
        self.permission_explanation.set_text("Your desktop will ask before BatCave can capture this keyboard and mouse. Capture begins only when you cross the chosen edge." if state.source else "Your desktop will ask before BatCave can receive keyboard and mouse input.")
        action = primary_action(self.setup_step, state)
        if self.primary.get_label() != action.label: self.primary.set_label(action.label)
        self.primary.set_sensitive(action.enabled)
        self.pause_button.set_sensitive(state.connected or state.connecting or state.armed)
        self.sidebar_status.set_text("Sharing is ready" if state.armed else "Linked · input is paused" if state.connected else "Sharing is paused")
        self.save_setup.set_sensitive(state.peer_selected and (state.listen or state.address_valid))
        self.address_box.set_visible(not state.listen); self.wait_guide.set_visible(state.listen)
        if state.permission_ready:
            self.permission_summary.set_text("Ready to share this keyboard and mouse." if state.source else "Ready to receive your keyboard and mouse.")
        elif state.permission_pending:
            self.permission_summary.set_text("Choose Allow in your desktop's permission window.")
        elif state.identity_ready and not state.desktop_ready:
            self.permission_summary.set_text("Desktop session services aren't available. Input sharing is paused.")
        elif state.source and not state.permission_supported:
            self.permission_summary.set_text(self.source_reason)
        else:
            self.permission_summary.set_text("Grant access when you're ready.")
        peer = self._peer() if self.approvals else None
        self.peer_controls.set_visible(bool(self.peer_ids))
        self.pair_guide.set_visible(not state.peer_selected)
        self.send_guide.set_visible(not self.pair_import_ready)
        self.receive_guide.set_visible(self.pair_import_ready)
        self.remove_peer.set_visible(state.peer_selected)
        self.remove_peer.set_sensitive(state.peer_selected)
        self.pair_title.set_text("Your other computer" if peer else "Add your first computer")
        self.pair_explanation.set_text("The other computer must also approve your Linux connection file before the computers can connect." if peer else "Keep BatCave open on both computers. Exchange a connection file once to approve each other.")
        self.send_instructions.set_text(f"Save this Linux file and send it to {state.peer_os} by email or USB. In BatCave there, open Pair computers and add this Linux computer.")
        self.receive_instructions.set_text(f"On {state.peer_os}, save its connection file in Pair computers. Bring that file here and choose Add {state.peer_os} computer. Compare the entire approval code on both computers.")
        self.skip_import.set_label("I already have the " + state.peer_os + " file")
        self.windows_tile.get_first_child().get_last_child().set_text(state.peer_os)
        self.pair_summary.set_text("Approved: " + peer.display_name + "." if peer else "No computer has been approved yet. Exchange the files above to get started.")
        self.peer_fingerprint.set_visible(state.peer_selected)
        self.peer_fingerprint.set_text("Approved computer SHA-256\n" + peer.fingerprint if peer else "")
        if state.permission_ready:
            bounds = self._bounds(); self.linux_geometry.set_text(f"{bounds.width} × {bounds.height}")
        else: self.linux_geometry.set_text("After desktop access")
        self.windows_geometry.set_text(f"{self.remote_bounds.width} × {self.remote_bounds.height}" if state.connected and self.remote_bounds else "After linking")
        if state.armed:
            self.link_summary.set_text("Enable input on the other computer, then cross the shared edge. F12 pauses sharing." if state.source else "Ready for input. Choose Start sharing on the other computer and move through the shared edge.")
        elif state.connected:
            self.link_summary.set_text("Devices are securely linked. Choose Enable Linux input when you're ready.")
        elif state.connecting:
            self.link_summary.set_text("Waiting for the selected approved computer…" if state.listen else "Linking your devices securely…")
        elif not state.permission_ready:
            self.link_summary.set_text("Grant desktop access before linking your devices.")
        else:
            self.link_summary.set_text("Choose your computer, then connect to it or wait for it to connect here.")
        self.connection_summary.set_text("Encrypted connection verified." if state.connected else self.link_summary.get_text())

    def _activate_primary(self):
        action = primary_action(self.setup_step, self._setup_state())
        if not action.enabled: return
        operations = {"next": lambda: self._show_step(min(3, self.setup_step + 1)), "grant": self._grant,
                      "export": self._export, "import": self._import, "permission": lambda: self._show_step(0),
                      "pair": lambda: self._show_step(1), "connect": self._connect,
                      "start": self._allow, "pause": self._stop}
        self._guard(operations.get(action.operation, lambda: None))

    def _prepare_import(self):
        # Choosing the next instruction grants neither trust nor input access.
        self.pair_import_ready = True; self._update_setup()

    def _initialize(self):
        state = None
        try:
            state = StateDirectory(profile_path())
            identity = Identity.open(state, SecretVault())
            approvals = Approvals(state)
            GLib.idle_add(self._ready, state, identity, approvals)
        except Exception:
            if state: state.close()
            GLib.idle_add(self._error, "Could not unlock your BatCave identity. Check your desktop keyring and private state directory; no identity was replaced.")

    def _ready(self, state, identity, approvals):
        if self.shutting_down:
            state.close(); return False
        self.state, self.identity, self.approvals = state, identity, approvals
        self.input = InputSession(identity.card.device_id, self.backend)
        self.connection = Connection(identity, self.inbox, lambda: self.input.generation,
                                      lambda peer: self.approvals.cards.get(peer.device_id) == peer)
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
                for key, widget in (("source", self.role), ("listen", self.network_mode)):
                    value = preferences.get(key, False)
                    if type(value) is bool: widget.set_selected(1 if value else 0)
                other_os = preferences.get("peerOS", "Windows")
                if other_os in ("Windows", "Mac", "Linux"): self.peer_os.set_selected(("Windows", "Mac", "Linux").index(other_os))
                port = preferences.get("port", 47870)
                if type(port) is int and 1 <= port <= 65535: self.port.set_value(port)
        except (ValueError, OSError, AttributeError):
            self.status.set_text("Saved connection preferences could not be read. Enter the other computer's address again.")
        probe = None
        try:
            probe = CapturePortal(self.capture_backend, lambda *_args, **_kwargs: None, lambda *_: None, lambda *_: None)
            probe.capability(); self.source_supported = True
        except Exception:
            self.source_reason = "This desktop does not provide keyboard and mouse capture permission. Choose Receive input here."
        finally:
            if probe: probe.dispose()
        if self.lifecycle_ready:
            self.status.set_text("Your device is ready. Continue to link your computers.")
        self._update_setup()
        return False

    def _refresh_peers(self, selected_id=None):
        selected = self.peers.get_selected()
        if selected_id is None and selected < len(self.peer_ids): selected_id = self.peer_ids[selected]
        self.peer_ids = list(self.approvals.cards)
        self.peers.set_model(Gtk.StringList.new([self.approvals.cards[key].display_name for key in self.peer_ids]))
        if selected_id in self.peer_ids: self.peers.set_selected(self.peer_ids.index(selected_id))
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
            self._prepare_import()
            self.status.set_text("Linux connection file saved. Send it to the other computer and add Linux there.")
        self._dialog("Save this Linux computer's connection file", Gtk.FileChooserAction.SAVE, save, name="Linux.batcave-peer.json")

    def _import(self):
        def selected(dialog):
            path = dialog.get_file().get_path()
            if not path: raise ValueError("Choose a connection file saved on this computer.")
            fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK)
            with os.fdopen(fd, "rb") as stream:
                if not stat.S_ISREG(os.fstat(stream.fileno()).st_mode): raise ValueError("Choose a regular connection file.")
                data = stream.read(16385)
            card_value = Card.read(data)
            if card_value.device_id == self.identity.card.device_id: raise ValueError("This is this Linux computer's file. Choose the other computer's connection file instead.")
            prompt = Gtk.MessageDialog(transient_for=self, modal=True, message_type=Gtk.MessageType.QUESTION,
                  buttons=Gtk.ButtonsType.YES_NO, text="Approve " + card_value.display_name + "?",
                  secondary_text="On the other computer, open its connection details and compare this entire approval code (SHA-256). Choose Yes only if it matches. Input sharing still needs your approval later.\n\n" + card_value.fingerprint)
            prompt.set_default_response(Gtk.ResponseType.NO)
            def approved(widget, response):
                if response == Gtk.ResponseType.YES:
                    self._stop(); self._guard(lambda: self.approvals.approve(card_value)); self._refresh_peers(card_value.device_id)
                    if card_value.device_id in self.approvals.cards:
                        self.status.set_text("Computer approved. Continue when both computers have approved each other.")
                widget.destroy()
            prompt.connect("response", approved); prompt.show()
        self._dialog("Choose the other computer's connection file", Gtk.FileChooserAction.OPEN, selected)

    def _unpair(self):
        peer = self._peer()
        if peer:
            self._stop(); self._guard(lambda: self.approvals.remove(peer.device_id)); self._refresh_peers()
            if peer.device_id not in self.approvals.cards:
                self.pair_import_ready = False; self._update_setup()
                self.status.set_text("Computer removed. Exchange connection files to approve it again.")

    def _grant(self):
        self._stop()
        def action():
            if not self.lifecycle_ready:
                raise RuntimeError("A desktop session with sleep and lock notifications is required.")
            if self.portal is None:
                if self._is_source():
                    self.portal = CapturePortal(self.capture_backend, self._portal_changed,
                                  lambda *args: self.source.activate(*args) if self.source else None,
                                  lambda *args: self.source.deactivated(*args) if self.source else None)
                    self.source = SourceSession(self.identity.card.device_id, self.connection, self.portal, self.status.set_text)
                else: self.portal = Portal(self.backend, self._portal_changed)
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
            if not self._permission_ready(): raise RuntimeError("Grant desktop access before linking devices.")
            if not self._peer(): raise ValueError("Add and approve the other computer's connection file first.")
            self._stop(preserve_permission=True)
            self._save_setup()
            if self._is_listener():
                self.epoch = self.connection.listen(self._peer(), self._bounds(), port=self.port.get_value_as_int())
            else:
                self.epoch = self.connection.connect(self.address.get_text().strip(), self._peer(), self._bounds(),
                                                     self.port.get_value_as_int(), listen_port=self.port.get_value_as_int())
            self.connecting = True
            self.status.set_text("Linking your devices securely…")
            self._update_setup()
        self._guard(action)

    def _allow(self):
        def action():
            if not self.lifecycle_ready or not self.connected: raise RuntimeError("Link devices before starting sharing.")
            if self._is_source(): self.source.start("left" if self.side.get_selected() == 0 else "right")
            else: self.input.enable(self.backend.bounds())
        self._guard(action)
        if self.input.armed: self.status.set_text("Ready for input. Choose Start sharing on the other computer.")
        self._update_setup()

    def _stop(self, preserve_permission=False):
        try:
            if self.input: self.input.disconnect()
            if self.source: self.source.disconnect(close_permission=not preserve_permission)
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
        self.status.set_text("Sharing paused. Connect again and approve input when ready.")

    def _tick(self):
        if self.shutting_down: return False
        try:
            self.backend.poll()
            self.capture_backend.poll()
            for epoch, kind, value in self.inbox.drain():
                if epoch != self.epoch: continue
                if kind == "connected":
                    if getattr(value, "connection_generation", self.connection.session_token) != self.connection.session_token: continue
                    if self.connected and self.source: self.source.stop(notify=False)
                    self.connected = True; self.connecting = False; self.input.connected(epoch)
                    self.remote_bounds = value
                    if self._is_source() and self.source: self.source.connected(epoch, self._peer(), value)
                    self.connection_label.set_text("Secure connection: TLS 1.3 verified")
                    self.status.set_text("Devices linked securely. Choose Enable Linux input when ready.")
                elif kind == "listening":
                    self.status.set_text(f"Waiting for the selected approved computer on port {value[-1]}…")
                elif kind == "disconnected": self._stop(); self.status.set_text(value)
                elif kind == "interrupted":
                    self.input.disconnect()
                    if self.source: self.source.disconnect()
                    self.connected = False; self.connecting = True; self.remote_bounds = None
                    self.status.set_text("Input paused. Waiting for the approved connection to settle…")
                elif kind == "message":
                    if getattr(value, "connection_generation", -1) != self.connection.session_token: continue
                    if self._is_source() and self.source: self.source.handle(epoch, value)
                    else: self.input.handle(epoch, value, getattr(value, "input_generation", -1))
            events = self.capture_backend.events()
            if self._is_source() and self.source: self.source.events(events)
            ready = self._permission_ready()
            if ready: self.grant_started = False
            self._update_setup()

            if ready:
                self.permission_label.set_text("Desktop access: keyboard and mouse ready")
                bounds = self._bounds(); self.geometry.set_text(f"Authorized desktop: {bounds.width} × {bounds.height}")
            else:
                self.permission_label.set_text("Desktop access: waiting for approval" if self.grant_started else "Desktop access: not granted")
                self.geometry.set_text("Display regions appear after granting access.")
            current = self.source if self._is_source() else self.input
            self.sharing_label.set_text("Input sharing: active" if current and current.owns_cursor else
                                        "Input sharing: awaiting a handoff" if current and current.armed else "Input sharing: paused")
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
        self.status.set_text("Desktop session services restarted. Restart BatCave before sharing input again.")

    def _drop(self, _target, files, _x, _y):
        self._guard(lambda: self._stage_files(files.get_files()))
        return True

    def _choose_files(self):
        self._dialog("Choose files for the shelf", Gtk.FileChooserAction.OPEN,
                     lambda dialog: self._stage_files([dialog.get_files().get_item(i) for i in range(dialog.get_files().get_n_items())]), multiple=True)

    def _stage_files(self, files):
        self._stop()  # File/icon I/O cannot delay input cleanup while sharing.
        if len(files) + len(self.files) > 32: raise ValueError("The shelf limit is 32 files.")
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
                if total > MAX_BATCH: raise ValueError("The shelf limit is 4 GiB in total.")
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
        self.status.set_text("Files staged locally with their source icons. Sending between devices is coming next.")
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
        self.backend.close(); self.capture_backend.close(); self._clear_files()
        if self.state: self.state.close()
        return False


class Application(Gtk.Application):
    def __init__(self, library=None, fixture=None):
        GLib.set_application_name("BatCave")
        super().__init__(application_id="dev.batcave.Linux.SecurePreview", flags=Gio.ApplicationFlags.DEFAULT_FLAGS)
        self.library, self.fixture, self.window = library, fixture, None

    def do_activate(self):
        if self.window is None: self.window = Window(self, self.fixture)
        self.window.present()
