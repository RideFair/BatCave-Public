"""Actual GTK rendering and approval gates, with synthetic hints and no network."""
import os
import sys
import tempfile
import time
from pathlib import Path

import gi
gi.require_version("Gtk", "4.0")
gi.require_version("Gsk", "4.0")
gi.require_version("Graphene", "1.0")
from gi.repository import Gio, GLib, Graphene, Gtk

from batcave_linux.desktop import Application
from batcave_linux.discovery import Cache, Computer
from batcave_linux.identity import Approvals, Identity
from batcave_linux.initial_consent import InitialConsent
from batcave_linux.nearby_service import Result
from batcave_linux.pairing import Confirmation, Decision, Descriptor
from batcave_linux.protocol import Bounds
from batcave_linux.storage import StateDirectory
from batcave_linux.capture_portal import CapturePortal
from batcave_linux.source import SourceSession
from test_core import MemoryVault
from test_native import NativeTests


class FixtureDiscovery:
    def __init__(self, own_id):
        self.cache = Cache(own_id); self.network_generation = 0; self.error = None
    def on_lan(self, _address, _index=0): return True
    def close(self): self.cache.clear()


class FixturePairing:
    def __init__(self): self.generation = 1; self.is_open = True; self.busy = True; self.events = []
    def drain(self): result = self.events; self.events = []; return result
    def cancel(self): self.generation += 1; self.events = []; self.busy = False
    def close_window(self): self.cancel(); self.is_open = False
    def close(self): self.close_window()
    def acknowledge(self, generation): assert generation == self.generation; self.busy = False


class FixtureSourcePermission(CapturePortal):
    def __init__(self): self.bounds = Bounds(-1000, 0, 1000, 800); self.callback = None; self.closed = False
    def ready(self): return not self.closed
    def enable(self, _edge, callback): self.callback = callback
    def close(self): self.closed = True
    def dispose(self): self.close()


root = Path(__file__).resolve().parents[1]
Gtk.init()
with tempfile.TemporaryDirectory() as directory:
    state = StateDirectory(directory)
    identity = Identity.open(state, MemoryVault(), "Linux test computer")
    application = Application(root / ".build/libbatcave-ei.so", (state, identity, Approvals(state)))
    application.register(None); application.activate(); window = application.window
    window.pages.set_transition_duration(0)  # Stationary layout QA, not a mid-animation blend.
    assert window.setup_step == 1 and window.page_title.get_text() == "Nearby computers"
    assert not window.peer_controls.get_visible() and not window.remove_peer.get_visible()
    assert not hasattr(window, "peer_os") and not hasattr(window, "address")
    for step in range(4):
        window._show_step(step)
        assert not window.approvals.cards and not window.connected and not window.input.armed
    window._show_step(1)
    old_epoch = window.epoch
    window.inbox.put(old_epoch, "connected", Bounds(0, 0, 1920, 1080))
    window._stop(); window._tick()
    assert not window.connected and window.input.epoch is None
    fifo = Path(directory) / "not-a-file"; os.mkfifo(fifo)
    try:
        window._stage_files([Gio.File.new_for_path(str(fifo))]); raise AssertionError("FIFO entered shelf")
    except ValueError: assert not window.files

    peer_state = StateDirectory(Path(directory) / "peer")
    peer = Identity.open(peer_state, MemoryVault(), "Office test computer")
    descriptor = Descriptor(peer.card, 47871)
    window.discovery = FixtureDiscovery(identity.card.device_id)
    service = FixturePairing(); window.pairing = service
    window._cancel_linking()
    assert not service.is_open and not window.nearby_allowed
    original_active = window.is_active; window.is_active = lambda: True; window.fixture = False
    window._nearby_visibility()
    assert not service.is_open  # Returning focus cannot undo an explicit cancellation.
    window.fixture = True; window.is_active = original_active
    service = FixturePairing(); window.pairing = service; window.nearby_allowed = True
    original_active = window.is_active; window.is_active = lambda: True
    original_grant = window._grant; grant_requests = []
    window._grant = lambda: grant_requests.append(True)
    decision = Decision(); confirmation = Confirmation(descriptor, "1234 5678", False, "00" * 16)
    service.events = [(service.generation, "confirm", (confirmation, decision))]
    window._nearby_tick()
    assert window.comparison.get_visible() and window.primary.get_label() == "Codes match · Link and share"
    assert "control this computer" in window.comparison_role.get_text()
    window._activate_primary(); assert decision.state()[0] is True
    assert not window.approvals.cards and not window.input.armed and not grant_requests
    old_generation = service.generation
    window._show_step(2); assert decision.state()[1]
    service.events = [(old_generation, "completed", Result(descriptor, "192.0.2.10", False, confirmation.session))]
    window._nearby_tick(); assert not window.approvals.cards
    window._show_step(1); service = FixturePairing(); window.pairing = service
    decision = Decision(); service.events = [(service.generation, "confirm", (confirmation, decision))]
    window._nearby_tick(); window._activate_primary()
    window.lifecycle_ready = True
    service.events = [(service.generation, "completed", Result(descriptor, "192.0.2.10", False, confirmation.session))]
    window._nearby_tick()
    assert window._peer() == peer.card and window.initial_consent is not None
    assert not window._is_source() and window.side.get_selected() == 0
    assert grant_requests and not window.input.armed and not window.connected
    preferences = window.state.read("connection-preferences.json")
    assert preferences and b"session" not in preferences and b"consent" not in preferences
    window.auto_link = False; window._clear_initial_consent()
    window._grant = original_grant; window.is_active = original_active

    native = NativeTests(); native.setUp()
    original_backend = window.backend
    try:
        window.backend = native.backend; window.input.backend = native.backend
        window.lifecycle_ready = True; window.connected = True; window.input.connected(window.epoch)
        window._allow(); assert window.input.armed
        window.pairing = FixturePairing(); original_active = window.is_active; window.is_active = lambda: True
        decision = Decision(); window.pairing.events = [(window.pairing.generation, "confirm", (confirmation, decision))]
        window._nearby_tick(); assert window.input.armed  # Unsolicited proof cannot pause existing sharing.
        window._approve_nearby(); assert not window.input.armed
        window._cancel_pairing(close_window=True); window.is_active = original_active
        window.connected = True; window.input.connected(window.epoch)
        window.fixture = False; window._allow(); window.fixture = True
        assert not window.input.armed  # A stale "connected" UI without a live socket cannot enable.
        consent = InitialConsent(peer.card, False, "00" * 16)
        assert consent.bind(peer.card, False, window.connection.session_token)
        window.initial_consent = consent; window._initial_share_tick()
        assert window.input.armed and window.initial_consent is None
        window._stop(); assert not window.input.armed
        window.connected = True; window.input.connected(window.epoch); window._initial_share_tick()
        assert not window.input.armed
        consent = InitialConsent(peer.card, False, "00" * 16); window.initial_consent = consent
        window._show_step(0); assert consent.closed and window.initial_consent is None
        window._stop()
    finally:
        window.backend = original_backend; window.input.backend = original_backend; native.tearDown()
    # Consumed initial consent cannot survive an asynchronous capture Enable.
    window._show_step(1); window.restoring = True; window.role.set_selected(1); window.restoring = False
    window.portal = FixtureSourcePermission(); window.connected = True; window.source_supported = True
    window.source = SourceSession(identity.card.device_id, window.connection, window.portal, window.status.set_text)
    window.source.connected(window.epoch, peer.card, Bounds(0, 0, 800, 600))
    consent = InitialConsent(peer.card, True, "00" * 16); consent.bind(peer.card, True, window.connection.session_token)
    window.initial_consent = consent; window._initial_share_tick()
    assert window.source.pending_enable and not window.source.armed and window.initial_activation is not None
    late = window.portal.callback; window._show_step(2); late()
    assert not window.source.armed and window.initial_activation is None
    window.portal = FixtureSourcePermission(); window.source.portal = window.portal
    window.source.connected(window.epoch, peer.card, Bounds(0, 0, 800, 600)); window._show_step(1)
    consent = InitialConsent(peer.card, True, "00" * 16, now=time.monotonic() - 29.98)
    consent.bind(peer.card, True, window.connection.session_token); window.initial_consent = consent
    window._initial_share_tick(); late = window.portal.callback; time.sleep(0.03); late()
    assert not window.source.armed and not window.source.pending_enable
    window._stop(); window.portal = None; window.source = None
    window.restoring = True; window.role.set_selected(0); window.restoring = False
    window.lifecycle_ready = False; window._stop()
    window.approvals.remove(peer.card.device_id); window._refresh_peers(); window.pairing = None
    window.discovery.cache.clear(); window.nearby_signature = None

    view = sys.argv[2] if len(sys.argv) > 2 else "nearby"
    allowed = {"nearby", "nearby-selected", "nearby-approved", "nearby-blocked", "checking", "comparison", "approval-wait", "permissions", "layout", "link", "files", "details"}
    if view not in allowed: raise ValueError("Unknown view")
    small = len(sys.argv) > 3 and sys.argv[3] == "small"
    if small: window.set_default_size(880, 640)
    if view in {"nearby-selected", "nearby-approved"}:
        now = time.monotonic()
        for index, platform in enumerate(("windows", "macos", "linux")):
            computer = Computer(peer.card.device_id if index == 0 else platform + ":synthetic-" + str(index),
                ("Office test computer", "Laptop test computer", "Studio test computer")[index], platform,
                peer.card.fingerprint if index == 0 else str(index) * 64, "192.0.2." + str(10 + index), 47871, 47873, True, now)
            window.discovery.cache.remember(computer, now)
        if view.endswith("approved"): window.approvals.approve(peer.card); window._refresh_peers(peer.card.device_id)
    if view == "nearby-blocked": window.discovery.error = "Nearby discovery is blocked. Allow BatCave on your private network, then search again."

    errors = []
    def select_view():
        try:
            window._show_step({"permissions": 0, "layout": 2, "link": 3}.get(view, 1))
            window._nearby_tick()
            if view in {"comparison", "approval-wait"}:
                service = FixturePairing(); window.pairing = service
                decision = Decision(); window.pending_confirmation = (service.generation, confirmation, decision)
                window.comparison_name.set_text("Compare with Office test computer")
                window.comparison_code.set_text("1234 5678")
                window.comparison_role.set_text("Link and share allows the other computer's keyboard and mouse to control this computer. Your desktop will ask for permission.")
                window.comparison.set_visible(True); window.nearby_empty.set_visible(False)
                window.pair_summary.set_text("Approve only after comparing all eight digits on both screens.")
                if view == "approval-wait": decision.choose(True)
            if view == "nearby-selected":
                service = FixturePairing(); service.busy = False; window.pairing = service
                window.nearby_list.select_row(window.nearby_list.get_first_child())
            if view == "checking": window.pairing = FixturePairing()
            if view == "files":
                example = Path(directory) / "Example.txt"; example.write_text("Synthetic fixture")
                window._stage_files([Gio.File.new_for_path(str(example))]); window.stack.set_visible_child_name("files")
            if view == "details": window.details.set_expanded(True)
            window._update_setup(); window.auto_link = False
            GLib.timeout_add(200, capture)
        except BaseException as error: errors.append(error); window.close(); loop.quit()
        return False

    def capture():
        try:
            assert not window.connected and not window.input.armed and not window.backend.ready()
            assert window.connection.socket is None and window.connection.listener is None
            assert not window.discovery.cache.snapshot() or view in {"nearby-selected", "nearby-approved"}
            assert window.get_width() <= (880 if small else 1000), f"Unexpected width: {window.get_width()}"
            assert window.get_height() <= (668 if small else 788), f"Unexpected height: {window.get_height()}"
            paintable = Gtk.WidgetPaintable.new(window); snapshot = Gtk.Snapshot.new()
            paintable.snapshot(snapshot, window.get_width(), window.get_height()); node = snapshot.to_node()
            bounds = Graphene.Rect(); bounds.init(0, 0, window.get_width(), window.get_height())
            texture = window.get_native().get_renderer().render_texture(node, bounds)
            assert texture.save_to_png(sys.argv[1])
            print("PASS: actual GTK nearby/approval and one-use EIS activation gates; synthetic render without network or native desktop input.")
        except BaseException as error: errors.append(error)
        finally:
            window.close(); application.quit(); loop.quit(); peer_state.close()
            assert not window.files and state.fd == -1
        return False
    loop = GLib.MainLoop(); GLib.timeout_add(100, select_view); loop.run()
    if errors: raise errors[0]
