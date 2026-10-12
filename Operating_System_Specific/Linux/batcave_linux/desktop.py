"""Native GTK setup. Permission, trust and active input are separate decisions."""

import os
import stat
import threading
import time
from pathlib import Path

import gi
gi.require_version("Gtk", "4.0")
gi.require_version("Gdk", "4.0")
from gi.repository import Gdk, Gio, GLib, Gtk, Pango

from .ei import EiBackend
from .capture import CaptureBackend
from .capture_portal import CapturePortal
from .identity import Approvals, Identity, SecretVault, strict_json
from .input import InputSession
from .source import SourceSession
from .portal import Portal
from .protocol import Bounds
from .storage import StateDirectory, profile_path, backup_profile
from .transfers import MAX_BATCH, MAX_FILE, leaf_name
from .transport import Connection, Inbox
from .setup_flow import STEP_TITLES, SetupState, primary_action
from .discovery import Computer, Discovery, PAIRING_PORT, announcement
from .nearby_service import PairingService
from .pairing import Descriptor
from .initial_consent import InitialConsent


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
        self.setup_step = 1
        self.fixture = fixture is not None
        self.discovery = self.pairing = None
        self.nearby_allowed = True
        self.nearby_signature = None
        self.nearby_rows = {}
        self.selected_nearby = None
        self.pending_confirmation = None
        self.initial_consent = None
        self.initial_activation = None
        self.network_generation = None
        self.local_input_port = 47870
        self.auto_link = False
        self.next_link = 0
        self.connected_bounds = None
        self.connected_token = None
        self.authenticated_endpoint = None
        self.listen_mode = True
        self.restoring = False
        self._build()
        self.connect("close-request", self._close)
        self.connect("notify::is-active", lambda *_: self._nearby_visibility())
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
            .pair-code { font-size: 36px; font-weight: 700; color: #4f3cc8; padding: 12px 0; }
            .nearby-row { padding: 12px; border-radius: 8px; }
            .boxed-list > row:selected { background: #eee9fd; color: #30236f; }
            .boxed-list > row:selected .muted { color: #4f3cc8; }
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
        self.step_buttons = {}
        descriptions = ("Allow desktop access", "Approve a computer", "Choose the shared edge", "Start when you're ready")
        for index in (1, 0, 2, 3):
            title = STEP_TITLES[index]
            widget = button(title, lambda index=index: self._show_devices_step(index))
            row = Gtk.Box(spacing=10)
            icon = Gtk.Image.new_from_icon_name(("security-high-symbolic", "network-workgroup-symbolic", "video-display-symbolic", "input-keyboard-symbolic")[index]); row.append(icon)
            labels = column(3); labels.append(text(title, "step-heading")); labels.append(text(descriptions[index], "step-description"))
            row.append(labels); widget.set_child(row)
            widget.add_css_class("step"); sidebar.append(widget); self.step_buttons[index] = widget
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
        self.fresh_start = button("Start fresh…", self._request_fresh_start)
        self.fresh_start.set_sensitive(False)
        permissions.append(self.fresh_start)
        permissions.append(text("Save a backup of this setup and link your computers again.", "muted"))
        self.pages.add_named(permissions, "0")

        pair = card("Your computers, together.", "Open BatCave on both computers, choose one below, then compare the code.")
        self.pair_title = pair.get_first_child()
        self.pair_explanation = self.pair_title.get_next_sibling()
        self.nearby_empty = column(8)
        self.nearby_empty.append(Gtk.Image.new_from_icon_name("network-workgroup-symbolic"))
        self.nearby_empty.append(text("Looking for nearby computers…", "card-title"))
        self.nearby_empty.append(text("Keep both apps open on the same local network. If nothing appears, allow BatCave on your private network and search again.", "muted"))
        pair.append(self.nearby_empty)
        self.nearby_list = Gtk.ListBox(selection_mode=Gtk.SelectionMode.SINGLE)
        self.nearby_list.add_css_class("boxed-list")
        self.nearby_list.connect("row-selected", self._nearby_selected)
        self.nearby_list.set_visible(False); pair.append(self.nearby_list)
        self.comparison = column(6); self.comparison.set_visible(False)
        self.comparison_name = text("Compare on both computers", "card-title")
        self.comparison_code = text("", "pair-code"); self.comparison_code.set_selectable(True)
        self.comparison.append(self.comparison_name); self.comparison.append(self.comparison_code)
        self.comparison.append(text("Compare all eight digits on both screens. Approve only if every digit matches.", "muted"))
        self.comparison_role = text("", "muted"); self.comparison.append(self.comparison_role)
        self.comparison.append(button("Cancel linking", self._cancel_linking))
        pair.append(self.comparison)
        self.pair_summary = text("Nearby computers have not been approved yet.", "pair-status"); pair.append(self.pair_summary)
        self.peer_controls = column(6)
        self.peer_controls.append(text("Previously approved computer", "muted"))
        self.peers = Gtk.DropDown.new_from_strings([]); self.peers.set_hexpand(True)
        self.peers.connect("notify::selected", lambda *_: self._peer_changed())
        self.peer_controls.append(self.peers); self.peer_controls.set_visible(False); pair.append(self.peer_controls)
        self.search_again = button("Search again", self._retry_nearby)
        self.search_again.set_halign(Gtk.Align.START); pair.append(self.search_again)
        self.retry_pair = button("Compare code again", self._link_nearby)
        self.retry_pair.set_halign(Gtk.Align.START); self.retry_pair.set_visible(False); pair.append(self.retry_pair)
        self.cancel_linking = button("Cancel linking", self._cancel_linking)
        self.cancel_linking.set_halign(Gtk.Align.START); self.cancel_linking.set_visible(False); pair.append(self.cancel_linking)
        self.pages.add_named(pair, "1")

        layout = card("Arrange screens", "Put the other computer on the same side as its physical screen.")
        layout.add_css_class("link-card"); layout.set_spacing(8)
        self.connection_summary = text("Choose a nearby computer. BatCave fills in its connection automatically.")
        layout.append(self.connection_summary)
        self.side = Gtk.DropDown.new_from_strings(["Other computer is on the left", "Other computer is on the right"])
        self.side.connect("notify::selected", lambda *_: self._configuration_changed()); layout.append(self.side)
        self.desktop_tiles = Gtk.Box(spacing=12, homogeneous=True)
        self.windows_tile, self.windows_geometry = self._desktop_tile("Windows")
        self.linux_tile, self.linux_geometry = self._desktop_tile("This Linux computer")
        self._arrange_tiles(); layout.append(self.desktop_tiles)
        layout.append(text("Each computer is shown as one desktop. Set the shared edge on the computer whose keyboard and mouse you use.", "muted"))
        self.pages.add_named(layout, "2")

        link = card("Link devices", "Your keyboard and mouse are shared only with your approved computer.")
        self.link_summary = text("Choose a nearby computer to link securely.")
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
        detail_box.append(text("This computer's input port", "muted"))
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
        self._show_step(1)

    def _show_step(self, step):
        if not 0 <= step < len(STEP_TITLES): return
        if step != self.setup_step:
            self._clear_initial_consent()
            self._cancel_pairing(close_window=True)
            self.nearby_allowed = step == 1
        self.setup_step = step; self.pages.set_visible_child_name(str(step))
        self.back.set_visible(False); self.save_setup.set_visible(step in (2, 3))
        self._update_title(); self._update_setup(); self._nearby_visibility()

    def _show_devices_step(self, step):
        self.stack.set_visible_child_name("setup"); self._show_step(step)

    def _update_title(self):
        files = self.stack.get_visible_child_name() == "files"
        self.page_kicker.set_text("Your file shelf" if files else "One keyboard. Both computers.")
        self.page_title.set_text("Files" if files else STEP_TITLES[self.setup_step])
        for index, widget in self.step_buttons.items():
            if not files and index == self.setup_step: widget.add_css_class("current")
            else: widget.remove_css_class("current")
        if files:
            self._clear_initial_consent(); self._cancel_pairing(close_window=True)

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
        peer = self._peer() if self.approvals else None
        if peer and self.identity:
            return self.identity.card.device_id.encode("utf-16-be") > peer.device_id.encode("utf-16-be")
        return True

    def _os_name(self):
        peer = self._peer() if self.approvals else None
        return {"windows": "Windows", "macos": "Mac", "linux": "Linux"}.get(peer.device_id.split(":", 1)[0], "Computer") if peer else "Computer"

    def _address_changed(self):
        if not hasattr(self, "primary") or self.restoring: return
        value = self.port.get_value_as_int()
        if value == PAIRING_PORT:
            self.status.set_text("Choose an input port different from the nearby pairing port."); return
        self._stop(); self.local_input_port = value
        if self.pairing:
            self.pairing.close(); self.pairing = self._make_pairing()
        self._update_setup()

    def _configuration_changed(self):
        if not hasattr(self, "primary") or self.restoring: return
        self._stop()
        if self.portal: self.portal.dispose(); self.portal = None
        self.source = None; self.capture_backend.close(); self.backend.close()
        self.grant_started = False
        self._arrange_tiles(); self._update_setup()
        if self.state and self._peer(): self._guard(self._save_setup)

    def _permission_ready(self):
        return bool(self.lifecycle_ready and (self.portal and isinstance(self.portal, CapturePortal) and self.portal.ready()
                                             if self._is_source() else self.backend.ready()))

    def _bounds(self):
        if self._permission_ready(): return self.portal.bounds if self._is_source() else self.backend.bounds()
        monitors = self.get_display().get_monitors()
        regions = [monitors.get_item(i).get_geometry() for i in range(monitors.get_n_items())]
        if not regions: raise RuntimeError("No desktop display is available.")
        left = min(r.x for r in regions); top = min(r.y for r in regions)
        return Bounds(left, top, max(r.x + r.width for r in regions) - left, max(r.y + r.height for r in regions) - top)

    def _save_setup(self):
        peer = self._peer()
        if not peer: raise ValueError("Choose an approved computer first.")
        self.state.write("connection-preferences.json", {"peerId": peer.device_id,
                         "linuxOnRight": self.side.get_selected() == 0, "source": self._is_source(), "port": self.local_input_port})

    def _setup_state(self):
        peer = self._peer() if self.approvals else None
        endpoint = self._endpoint(peer)
        pending = self.pending_confirmation
        return SetupState(identity_ready=self.identity is not None, desktop_ready=self.lifecycle_ready,
                          permission_pending=self.grant_started, permission_ready=self._permission_ready(),
                          peer_selected=peer is not None, address_valid=endpoint is not None,
                          connecting=self.connecting, connected=self.connected,
                          armed=bool(self.source and self.source.armed if self._is_source() else self.input and self.input.armed),
                          source=self._is_source(), listen=self._is_listener(),
                          permission_supported=not self._is_source() or self.source_supported,
                          enable_pending=bool(self.source and self.source.pending_enable),
                          nearby_selected=bool(self.selected_nearby and self.selected_nearby.pairing and self.selected_nearby.device_id not in (self.approvals.cards if self.approvals else {})),
                          nearby_open=bool(self.pairing and self.pairing.is_open), pairing_busy=bool(self.pairing and self.pairing.busy),
                          confirmation=pending is not None, confirmation_sent=bool(pending and pending[2].state()[0] is True))

    def _update_setup(self):
        if not hasattr(self, "primary"): return
        state = self._setup_state()
        action = primary_action(self.setup_step, state)
        self.primary.set_label(action.label); self.primary.set_sensitive(action.enabled)
        self.pause_button.set_sensitive(state.connected or state.connecting or state.armed or state.pairing_busy or self.initial_consent is not None)
        self.sidebar_status.set_text("Sharing is ready" if state.armed else "Linked · input is paused" if state.connected else "Sharing is paused")
        self.save_setup.set_sensitive(state.peer_selected)
        self.cancel_linking.set_visible(state.pairing_busy and not state.confirmation)
        self.nearby_list.set_sensitive(not state.pairing_busy)
        selected = self.selected_nearby
        saved = self.approvals.cards.get(selected.device_id) if selected and self.approvals else None
        self.retry_pair.set_visible(bool(saved and selected.pairing and saved.fingerprint == selected.fingerprint
                                   and state.nearby_open and not state.connected and not state.pairing_busy))
        self.permission_explanation.set_text("Your desktop will ask before BatCave can capture this keyboard and mouse. Capture begins only when you cross the shared edge." if state.source else "Your desktop will ask before BatCave can receive keyboard and mouse input.")
        self.permission_summary.set_text("Desktop access is ready." if state.permission_ready else "Choose Allow in the desktop permission window." if state.permission_pending else self.source_reason if state.source and not state.permission_supported else "Grant access when you're ready.")
        peer = self._peer() if self.approvals else None
        self.peer_controls.set_visible(bool(self.peer_ids))
        self.remove_peer.set_visible(peer is not None); self.remove_peer.set_sensitive(peer is not None)
        self.peer_fingerprint.set_visible(peer is not None)
        self.peer_fingerprint.set_text("Approved computer SHA-256\n" + peer.fingerprint if peer else "")
        self.windows_tile.get_first_child().get_last_child().set_text(peer.display_name if peer else "Other computer")
        if state.permission_ready:
            bounds = self._bounds(); self.linux_geometry.set_text(f"{bounds.width} × {bounds.height}")
        else: self.linux_geometry.set_text("This desktop")
        self.windows_geometry.set_text(f"{self.remote_bounds.width} × {self.remote_bounds.height}" if state.connected and self.remote_bounds else "After linking")
        message = "Sharing is ready. Cross the shared edge; F12 pauses sharing." if state.armed and state.source else "Ready to receive input from your approved computer." if state.armed else "Devices are securely linked. Sharing is paused." if state.connected else "Linking your approved computer securely…" if state.connecting else "Choose a nearby computer. Connection details are filled in automatically."
        if state.connected and state.source and not state.permission_supported:
            message = self.source_reason + " Open Permissions to choose Receive input here."
        self.link_summary.set_text(message); self.connection_summary.set_text(message)
        if not self.pending_confirmation:
            self.pair_summary.set_text("Checking the computer before showing its comparison code…" if state.pairing_busy else "Linked securely: " + peer.display_name + "." if peer and state.connected else "Approved: " + peer.display_name + ". Waiting for its secure connection." if peer else "Choose a computer below. Nearby names alone do not grant access.")

    def _activate_primary(self):
        action = primary_action(self.setup_step, self._setup_state())
        if not action.enabled: return
        operations = {"grant": self._grant, "pair": lambda: self._show_devices_step(1),
                      "search": self._retry_nearby, "link": self._link_nearby, "approve": self._approve_nearby,
                      "connect": self._connect, "start": self._allow, "pause": self._stop}
        self._guard(operations.get(action.operation, lambda: None))



    def _initialize(self):
        state = None
        try:
            state = StateDirectory(profile_path())
            state.lock_for_application()
            identity = Identity.open(state, SecretVault())
            approvals = Approvals(state)
            GLib.idle_add(self._ready, state, identity, approvals)
        except Exception:
            if state: state.close()
            GLib.idle_add(self._error, "Could not open your BatCave setup. Quit any other BatCave session, then check that your desktop keyring is unlocked. No identity was replaced.")

    def _ready(self, state, identity, approvals):
        if self.shutting_down:
            state.close(); return False
        self.state, self.identity, self.approvals = state, identity, approvals
        self.fresh_start.set_sensitive(True)
        self.input = InputSession(identity.card.device_id, self.backend)
        self.connection = Connection(identity, self.inbox, lambda: self.input.generation,
                                      lambda peer: self.approvals.cards.get(peer.device_id) == peer)
        self.fingerprint.set_text("My SHA-256 fingerprint\n" + " ".join(identity.card.fingerprint[i:i + 8] for i in range(0, 64, 8)))
        self._refresh_peers(); self._watch_lifecycle()
        self.restoring = True
        try:
            saved = state.read("connection-preferences.json", 4096)
            if saved:
                preferences = strict_json(saved)
                peer_id = preferences.get("peerId")
                if peer_id in self.peer_ids: self.peers.set_selected(self.peer_ids.index(peer_id))
                side = preferences.get("linuxOnRight", True)
                if type(side) is bool: self.side.set_selected(0 if side else 1)
                source = preferences.get("source", False)
                if type(source) is bool: self.role.set_selected(1 if source else 0)
                value = preferences.get("port", 47870)
                if type(value) is int and 1 <= value <= 65535 and value != PAIRING_PORT:
                    self.local_input_port = value; self.port.set_value(value)
        except (ValueError, OSError, AttributeError):
            self.status.set_text("Saved preferences could not be read. Choose your nearby computer again.")
        finally: self.restoring = False
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
        if not self.fixture:
            self.discovery = Discovery(identity.card.device_id, lambda: announcement(identity.card, self.local_input_port,
                                       self.pairing.port if self.pairing else PAIRING_PORT, bool(self.pairing and self.pairing.is_open)))
            self.pairing = self._make_pairing()
            try: self.discovery.start()
            except OSError: self.status.set_text("Nearby discovery is blocked. Search again to retry.")
            self.auto_link = self._peer() is not None
            self._nearby_visibility()
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
        if not hasattr(self, "primary") or self.restoring: return
        if self.connection: self._stop()
        self.auto_link = self._peer() is not None
        self._update_setup()

    def _make_pairing(self):
        def accept_peer(descriptor):
            saved = self.approvals.cards.get(descriptor.card.device_id)
            return (saved is None and len(self.approvals.cards) < 64 or saved is not None and saved.fingerprint == descriptor.card.fingerprint)
        return PairingService(self.identity, Descriptor(self.identity.card, self.local_input_port), accept_peer,
                              lambda address: bool(self.discovery and self.discovery.on_lan(address)))

    def _clear_initial_consent(self):
        if self.initial_consent: self.initial_consent.close()
        self.initial_consent = None
        ticket = self.initial_activation; self.initial_activation = None
        if ticket and self.source is not None and self.source is ticket[0] and self.source.pending_enable:
            self.source.stop(notify=False)

    def _cancel_pairing(self, close_window=True):
        if self.pending_confirmation: self.pending_confirmation[2].close()
        self.pending_confirmation = None
        if hasattr(self, "comparison"): self.comparison.set_visible(False)
        if self.pairing:
            if close_window: self.pairing.close_window()
            else: self.pairing.cancel()
        if hasattr(self, "primary"): self._update_setup()

    def _cancel_linking(self):
        self.nearby_allowed = False
        self._clear_initial_consent(); self._cancel_pairing(close_window=True)
        self.status.set_text("Linking cancelled. Search again when both computers are ready.")

    def _nearby_visibility(self):
        if not self.pairing or self.fixture: return
        foreground = self.is_active() and self.stack.get_visible_child_name() == "setup" and self.setup_step == 1
        if not foreground:
            self._cancel_pairing(close_window=True)
        elif self.nearby_allowed and self.lifecycle_ready and not self.pairing.is_open:
            try: self.pairing.open_window()
            except OSError:
                self.nearby_allowed = False
                self.status.set_text("Nearby pairing cannot open. Another app may use its port; close it and search again.")
        self._update_setup()

    def _retry_nearby(self):
        self._clear_initial_consent(); self._cancel_pairing(close_window=True)
        self.nearby_allowed = True; self.selected_nearby = None
        if self.discovery:
            self.discovery.close()
            self.discovery = Discovery(self.identity.card.device_id, lambda: announcement(self.identity.card, self.local_input_port,
                                       self.pairing.port if self.pairing else PAIRING_PORT, bool(self.pairing and self.pairing.is_open)))
            self.network_generation = None
            try: self.discovery.start()
            except OSError: self.status.set_text("Nearby discovery is blocked. Allow BatCave on your private network, then search again.")
        self.auto_link = self._peer() is not None; self.next_link = 0
        self._nearby_visibility(); self._update_setup()

    def _endpoint(self, peer):
        if peer is None or self.discovery is None: return None
        value = self.authenticated_endpoint
        if (value and value.device_id == peer.device_id and value.fingerprint == peer.fingerprint
                and 0 <= time.monotonic() - value.seen <= 12 and self.discovery.on_lan(value.address)):
            return value
        return next((value for value in self.discovery.cache.snapshot() if value.device_id == peer.device_id
                     and value.fingerprint == peer.fingerprint and self.discovery.on_lan(value.address, value.interface)), None)

    def _nearby_selected(self, _list, row):
        self.selected_nearby = getattr(row, "computer", None)
        if self.selected_nearby and self.approvals:
            saved = self.approvals.cards.get(self.selected_nearby.device_id)
            if saved:
                if saved.fingerprint != self.selected_nearby.fingerprint:
                    self.status.set_text("This computer's identity changed. Remove its previous approval before linking again.")
                else:
                    self.peers.set_selected(self.peer_ids.index(saved.device_id))
                    self.auto_link = True; self.next_link = 0
        self._update_setup()

    def _link_nearby(self):
        if not self.pairing or not self.selected_nearby: return
        self._stop(close_pairing=False)
        self.pairing.link(self.selected_nearby)
        self.status.set_text("Checking the selected computer before showing its comparison code…")
        self._update_setup()

    def _approve_nearby(self):
        pending = self.pending_confirmation
        if not pending or not self.pairing or pending[0] != self.pairing.generation: return
        choice, closed = pending[2].state()
        if closed or choice is not None: return
        self._stop(close_pairing=False)
        if pending[2].choose(True):
            self.status.set_text("Waiting for approval on the other computer…")
        self._update_setup()

    def _nearby_tick(self):
        if self.discovery:
            generation = self.discovery.network_generation
            if self.network_generation is not None and generation != self.network_generation:
                self._stop(); self.discovery.cache.clear()
                self.status.set_text("The network changed. Search again to link your computers.")
            self.network_generation = generation
            computers = self.discovery.cache.snapshot()
            signature = tuple((v.device_id, v.fingerprint, v.address, v.input_port, v.pairing_port, v.pairing, v.display_name, v.platform) for v in computers)
            if signature != self.nearby_signature:
                chosen = self.selected_nearby
                self.restoring = True
                try:
                    while self.nearby_list.get_first_child(): self.nearby_list.remove(self.nearby_list.get_first_child())
                    self.nearby_rows = {}; self.selected_nearby = None
                    for computer in computers:
                        row = Gtk.ListBoxRow(); row.computer = computer
                        content = Gtk.Box(spacing=12); content.add_css_class("nearby-row")
                        content.append(Gtk.Image.new_from_icon_name("computer-symbolic"))
                        name = column(3); name.set_hexpand(True)
                        heading = Gtk.Label(label=computer.display_name, xalign=0, ellipsize=Pango.EllipsizeMode.END, max_width_chars=35)
                        heading.set_tooltip_text(computer.display_name); name.append(heading)
                        saved = self.approvals.cards.get(computer.device_id)
                        detail = "Identity changed · approval required" if saved and saved.fingerprint != computer.fingerprint else "Previously approved · checking secure connection" if saved else {"windows": "Windows", "macos": "Mac", "linux": "Linux"}[computer.platform]
                        name.append(text(detail, "muted")); content.append(name)
                        if not saved: content.append(text("Ready to link" if computer.pairing else "Open Nearby there", "muted"))
                        row.set_child(content); self.nearby_list.append(row); self.nearby_rows[computer.device_id] = row
                        if chosen and computer.device_id == chosen.device_id and computer.fingerprint == chosen.fingerprint:
                            self.nearby_list.select_row(row); self.selected_nearby = computer
                finally: self.restoring = False
                self.nearby_signature = signature
            elif self.selected_nearby:
                # Refresh time/address only for the identity actually selected.
                self.selected_nearby = next((v for v in computers if v.device_id == self.selected_nearby.device_id
                                            and v.fingerprint == self.selected_nearby.fingerprint), None)
            self.nearby_empty.set_visible(not computers and not self.pending_confirmation)
            self.nearby_list.set_visible(bool(computers) and not self.pending_confirmation)
            if self.discovery.error and not self.pending_confirmation: self.status.set_text(self.discovery.error)
        if self.pairing:
            if self.pending_confirmation and self.pending_confirmation[0] != self.pairing.generation:
                self.pending_confirmation[2].close(); self.pending_confirmation = None; self.comparison.set_visible(False)
            for generation, kind, value in self.pairing.drain():
                if generation != self.pairing.generation: continue
                if kind == "confirm":
                    confirmation, decision = value
                    if self.setup_step != 1 or not self.is_active(): decision.close(); continue
                    self.pending_confirmation = (generation, confirmation, decision)
                    self.comparison_name.set_text("Compare with " + confirmation.peer.card.display_name)
                    self.comparison_code.set_text(confirmation.code)
                    self.comparison_role.set_text("Link and share uses this computer's keyboard and mouse on the other computer. Your desktop may ask for permission." if confirmation.initiator else "Link and share allows the other computer's keyboard and mouse to control this computer. Your desktop will ask for permission.")
                    self.comparison.set_visible(True); self.nearby_empty.set_visible(False); self.nearby_list.set_visible(False)
                    self.pair_summary.set_text("Approve only after comparing all eight digits on both screens.")
                elif kind == "completed":
                    pending = self.pending_confirmation
                    if not pending or pending[0] != generation or pending[1].session != value.session or pending[2].state()[0] is not True:
                        self._cancel_pairing(); continue
                    try:
                        self.approvals.approve(value.peer.card)
                        self.pairing.acknowledge(generation); self._cancel_pairing(close_window=True); self.nearby_allowed = False
                        self._refresh_peers(value.peer.card.device_id)
                        self.role.set_selected(1 if value.initiator else 0)
                        self.side.set_selected(1 if value.initiator else 0)
                        self.initial_consent = InitialConsent(value.peer.card, value.initiator, value.session)
                        self.authenticated_endpoint = Computer(value.peer.card.device_id, value.peer.card.display_name,
                            "linux", value.peer.card.fingerprint, value.address, value.peer.input_port, PAIRING_PORT,
                            False, time.monotonic())
                        self.auto_link = True; self.next_link = 0; self._save_setup()
                        self.status.set_text("Computers approved. Allow desktop access to start this first sharing session.")
                        if not self._permission_ready() and self.lifecycle_ready and (not self._is_source() or self.source_supported): self._grant()
                        elif self._is_source() and not self.source_supported: self.status.set_text(self.source_reason + " Open Permissions to choose Receive input here.")
                    except Exception:
                        self._clear_initial_consent(); self._cancel_pairing(close_window=True)
                        self.status.set_text("Linking was incomplete. Search again and compare a fresh code on both screens.")
                elif kind in {"failed", "expired"}:
                    self._clear_initial_consent(); self._cancel_pairing(close_window=True); self.nearby_allowed = False
                    self.status.set_text(value if kind == "failed" else "Nearby linking timed out. Search again when both computers are ready.")
        self._update_setup()

    def _auto_link_tick(self):
        if self.fixture: return
        if not self.auto_link or self.connected or self.connecting or not self._peer() or time.monotonic() < self.next_link: return
        if self.initial_consent and self.grant_started: return  # First portal consent precedes our first connection.
        if self._is_listener() or self._endpoint(self._peer()): self._connect(automatic=True)

    def _initial_share_tick(self):
        consent = self.initial_consent
        if not consent: return
        if not consent.valid(self._peer(), self._is_source()): self._clear_initial_consent(); return
        if not self.connected or not self._permission_ready(): return
        if consent.take(self._peer(), self._is_source(), self.connection.session_token):
            self.initial_consent = None
            self._allow(initial_deadline=consent.expires)

    def _initial_activation_valid(self, ticket):
        peer = self._peer()
        return (self.initial_activation is ticket and self.source is ticket[0] and self.epoch == ticket[1]
                and self.connection.session_token == ticket[2] and time.monotonic() < ticket[3]
                and self.connected and self._is_source() and peer is not None
                and peer.device_id == ticket[4] and peer.fingerprint == ticket[5])

    def _in_session(self, action, epoch=None, generation=None):
        # The fixture has no sockets and is constructible only by local test code.
        if self.fixture: action(); return True
        return self.connection.current(self.epoch if epoch is None else epoch,
                                       self.connected_token if generation is None else generation, action)

    def _dialog(self, title, action, callback, multiple=False, name=None):
        dialog = Gtk.FileChooserNative(title=title, transient_for=self, action=action, accept_label="Choose", cancel_label="Cancel")
        if name: dialog.set_current_name(name)
        dialog.set_select_multiple(multiple)
        def response(widget, result):
            if result == Gtk.ResponseType.ACCEPT:
                self._guard(lambda: callback(widget))
            widget.destroy()
        dialog.connect("response", response); dialog.show()





    def _unpair(self):
        peer = self._peer()
        if peer:
            self._stop(); self._guard(lambda: self.approvals.remove(peer.device_id)); self._refresh_peers()
            if peer.device_id not in self.approvals.cards:
                self._update_setup()
                self.status.set_text("Computer removed. Search nearby and compare a fresh code to link it again.")

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
                    if self.connected and self.remote_bounds: self.source.connected(self.epoch, self._peer(), self.remote_bounds)
                else: self.portal = Portal(self.backend, self._portal_changed)
            self.portal.grant(); self.grant_started = True
            self._update_setup()
        self._guard(action)

    def _portal_changed(self, message, failed=False):
        self.status.set_text(message)
        if failed:
            self.grant_started = False; self._stop()
            self.permission_label.set_text("Input permission: unavailable")

    def _connect(self, automatic=False):
        def action():
            peer = self._peer()
            if not peer: raise ValueError("Choose an approved nearby computer first.")
            self.auto_link = True
            self._stop(preserve_permission=True, reconnect=True, close_pairing=False, preserve_consent=automatic)
            self._save_setup(); self.next_link = time.monotonic() + 3
            bounds = self._bounds(); self.connected_bounds = bounds
            if self._is_listener(): self.epoch = self.connection.listen(peer, bounds, port=self.local_input_port)
            else:
                endpoint = self._endpoint(peer)
                if not endpoint: raise ValueError("This computer is not nearby yet. Open BatCave there and search again.")
                self.epoch = self.connection.connect(endpoint.address, peer, bounds, endpoint.input_port, listen_port=self.local_input_port)
            self.connecting = True; self.status.set_text("Linking your approved computer securely…"); self._update_setup()
        self._guard(action)

    def _allow(self, initial_deadline=None):
        self._clear_initial_consent()
        self._cancel_pairing(close_window=True); self.nearby_allowed = False
        def action():
            if not self.lifecycle_ready or not self.connected: raise RuntimeError("Link devices before starting sharing.")
            if self._is_source():
                ticket = None
                if initial_deadline is not None:
                    peer = self._peer()
                    ticket = self.initial_activation = (self.source, self.epoch, self.connection.session_token,
                                                         initial_deadline, peer.device_id, peer.fingerprint)
                self.source.start("left" if self.side.get_selected() == 0 else "right",
                                  (lambda: self._initial_activation_valid(ticket)) if ticket else None,
                                  activate=lambda callback, epoch=self.epoch, token=self.connected_token:
                                      self._in_session(callback, epoch, token))
            else: self.input.enable(self.backend.bounds())
        def activate():
            if not self._in_session(action):
                self._clear_initial_consent()
                raise RuntimeError("The connection changed. Sharing stays paused; reconnect before starting again.")
        self._guard(activate)
        if self.input.armed: self.status.set_text("Ready for input. Choose Start sharing on the other computer.")
        self._update_setup()

    def _stop(self, preserve_permission=False, reconnect=False, close_pairing=True, preserve_consent=False):
        if not preserve_consent: self._clear_initial_consent()
        if close_pairing: self.nearby_allowed = False; self._cancel_pairing(close_window=True)
        self.auto_link = reconnect
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
        self.connected_token = None
        self.connecting = False
        self.remote_bounds = None
        self._update_setup()
        self.connection_label.set_text("Secure connection: disconnected")
        self.sharing_label.set_text("Input sharing: paused")
        self.status.set_text("Sharing paused. Connect again and approve input when ready.")

    def _tick(self):
        if self.shutting_down: return False
        try:
            self._nearby_tick()
            self.backend.poll()
            self.capture_backend.poll()
            for epoch, kind, value in self.inbox.drain():
                if epoch != self.epoch: continue
                if kind == "connected":
                    token = getattr(value, "connection_generation", None)
                    if token is None:
                        if not self.fixture: continue
                        token = self.connection.session_token
                    def adopted():
                        if self.connected:
                            self._clear_initial_consent()
                            if self.source: self.source.stop(notify=False)
                        self.connected = True; self.connected_token = token
                        self.connecting = False; self.input.connected(epoch); self.remote_bounds = value
                        if self.initial_consent: self.initial_consent.bind(self._peer(), self._is_source(), token)
                        if self._is_source() and self.source: self.source.connected(epoch, self._peer(), value)
                        self.connection_label.set_text("Secure connection: TLS 1.3 verified")
                        self.status.set_text("Devices linked securely. Sharing is paused.")
                    self._in_session(adopted, epoch, token)
                elif kind == "listening":
                    self.status.set_text(f"Waiting for the selected approved computer on port {value[-1]}…")
                elif kind == "disconnected": self._stop(preserve_permission=True, reconnect=True); self.status.set_text(value)
                elif kind == "interrupted":
                    self._clear_initial_consent()
                    self.input.disconnect()
                    if self.source: self.source.disconnect()
                    self.connected = False; self.connecting = True; self.remote_bounds = None
                    self.status.set_text("Input paused. Waiting for the approved connection to settle…")
                elif kind == "message":
                    if getattr(value, "connection_generation", -1) != self.connected_token: continue
                    def dispatch():
                        if self._is_source() and self.source: self.source.handle(epoch, value)
                        else: self.input.handle(epoch, value, getattr(value, "input_generation", -1))
                    self._in_session(dispatch)
            events = self.capture_backend.events()
            if self._is_source() and self.source:
                was_armed = self.source.armed or self.source.pending_enable
                if self.connected:
                    if not self._in_session(lambda: self.source.events(events)): self.source.disconnect()
                elif not was_armed: self.source.events(events)
                else: self.source.disconnect()
                if was_armed and not self.source.armed and not self.source.pending_enable:
                    self._clear_initial_consent(); self._cancel_pairing(close_window=True); self.nearby_allowed = False
            ready = self._permission_ready()
            if ready and self.connected and self.connected_bounds != self._bounds():
                # Portal geometry is authoritative. A replacement cannot reuse consent.
                self._clear_initial_consent(); self._connect(automatic=True); ready = self._permission_ready()
            self._initial_share_tick()
            if self.initial_activation and (time.monotonic() >= self.initial_activation[3] or not self.source or not self.source.pending_enable):
                self._clear_initial_consent()
            self._auto_link_tick()
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

    def _request_fresh_start(self):
        if not self.state or self.shutting_down: return
        dialog = Gtk.MessageDialog(transient_for=self, modal=True,
                                   message_type=Gtk.MessageType.WARNING, text="Start fresh?")
        dialog.set_property("secondary-text", "Stop sharing and save your current setup in a backup folder under:\n"
                                     + str(self.state.path.parent)
                                     + "\n\nYour original key stays in the keyring. System permissions stay as they are.")
        dialog.add_button("Cancel", Gtk.ResponseType.CANCEL)
        dialog.add_button("Back up and start fresh", Gtk.ResponseType.ACCEPT)
        dialog.set_default_response(Gtk.ResponseType.CANCEL)
        def response(widget, result):
            widget.destroy()
            if result == Gtk.ResponseType.ACCEPT: self._finish_fresh_start()
        dialog.connect("response", response); dialog.present()

    def _finish_fresh_start(self):
        if not self.state or self.shutting_down: return
        try:
            self._close(close_state=False)
            backup = backup_profile(self.state)
            message = "Your previous setup is saved here:\n" + str(backup) + "\n\nOpen BatCave again to link your computers from scratch."
            title = "Ready to start fresh"
        except Exception as error:
            message = "Your setup was kept.\n\n" + str(error) + "\n\nReopen BatCave after resolving this problem."
            title = "Setup was kept"
        finally:
            self.state.close()
        self.get_child().set_sensitive(False)
        dialog = Gtk.MessageDialog(transient_for=self, modal=True, text=title, buttons=Gtk.ButtonsType.CLOSE)
        dialog.set_property("secondary-text", message)
        def response(widget, _result):
            widget.destroy(); self.get_application().quit()
        dialog.connect("response", response); dialog.present()

    def _close(self, *_, close_state=True):
        if self.shutting_down: return False
        self.shutting_down = True
        GLib.source_remove(self.tick)
        self._stop()
        if self.portal: self.portal.dispose()
        if self.discovery: self.discovery.close()
        if self.pairing: self.pairing.close()
        for bus, subscription in self.bus_subscriptions: bus.signal_unsubscribe(subscription)
        self.backend.close(); self.capture_backend.close(); self._clear_files()
        if close_state and self.state: self.state.close()
        return False


class Application(Gtk.Application):
    def __init__(self, library=None, fixture=None):
        GLib.set_application_name("BatCave")
        super().__init__(application_id="dev.batcave.Linux.SecurePreview", flags=Gio.ApplicationFlags.DEFAULT_FLAGS)
        self.library, self.fixture, self.window = library, fixture, None

    def do_activate(self):
        if self.window is None: self.window = Window(self, self.fixture)
        self.window.present()
