"""Render actual GTK widgets; no permission request, input emission or network."""
import os
import sys
import tempfile
from pathlib import Path

import gi
gi.require_version("Gtk", "4.0")
gi.require_version("Gsk", "4.0")
gi.require_version("Graphene", "1.0")
from gi.repository import Gio, GLib, Graphene, Gtk

from batcave_linux.desktop import Application
from batcave_linux.identity import Approvals, Identity
from batcave_linux.storage import StateDirectory
from batcave_linux.protocol import Bounds
from test_core import MemoryVault
from test_native import NativeTests
from test_capture import CaptureFixture
from batcave_linux.capture_portal import CapturePortal
from batcave_linux.source import SourceSession

root = Path(__file__).resolve().parents[1]
Gtk.init()
with tempfile.TemporaryDirectory() as directory:
    state = StateDirectory(directory)
    identity = Identity.open(state, MemoryVault(), "Linux test computer")
    application = Application(root / ".build/libbatcave-ei.so", (state, identity, Approvals(state)))
    application.register(None); application.activate()
    window = application.window
    if window is None:
        raise RuntimeError("GTK application did not create a window.")
    errors = []
    assert not window.peer_controls.get_visible() and not window.remove_peer.get_visible()
    assert not window.peer_fingerprint.get_visible() and window.pair_guide.get_visible()
    # A connected callback queued before manual stop cannot revive the UI/session.
    old_epoch = window.epoch
    window.inbox.put(old_epoch, "connected", Bounds(0, 0, 1920, 1080))
    window._stop(); window._tick()
    assert not window.connected and window.input.epoch is None
    # A FIFO must be refused without waiting for a writer or retaining an FD.
    fifo = Path(directory) / "not-a-regular-file"; os.mkfifo(fifo)
    try:
        window._stage_files([Gio.File.new_for_path(str(fifo))])
        raise AssertionError("A FIFO entered the shelf")
    except ValueError:
        assert not window.files
    view = sys.argv[2] if len(sys.argv) > 2 else "permissions"
    if view not in {"permissions", "pair", "pair-receive", "pair-approved", "layout", "link", "files", "details", "details-approved", "pair-mac", "pair-linux", "source", "wait"}: raise ValueError("Unknown view")
    if len(sys.argv) > 3 and sys.argv[3] == "small": window.set_default_size(880, 640)
    # Navigation cannot create approvals, grant desktop input or open a socket.
    for step in range(4):
        window._show_step(step)
        assert not window.approvals.cards and not window.connected and not window.input.armed
    window._show_step(0)
    assert not window.details.get_expanded()
    # Exercise the real GUI action against an isolated EIS server, never the OS.
    native = NativeTests(); original_backend = window.backend
    original_lifecycle = window.lifecycle_ready
    native.setUp()
    try:
        peer_state = StateDirectory(Path(directory) / "peer")
        try:
            peer = Identity.open(peer_state, MemoryVault(), "Windows test computer with a long name to check the approved list")
            # Import opens a real approval prompt; opening it or choosing No cannot pair.
            connection_file = Path(directory) / "Windows.batcave-peer.json"
            connection_file.write_bytes(peer.card.serialize())
            class ChosenFile:
                def __init__(self, path): self.path = path
                def get_file(self): return Gio.File.new_for_path(str(self.path))
            original_dialog = window._dialog
            try:
                window._show_step(1)
                # Cancelling Save cannot advance the instruction or create trust.
                window._dialog = lambda *args, **kwargs: None
                window._activate_primary()
                assert not window.pair_import_ready and not window.approvals.cards
                exported_file = Path(directory) / "Linux.batcave-peer.json"
                window._dialog = lambda title, action, callback, **kwargs: callback(ChosenFile(exported_file if action == Gtk.FileChooserAction.SAVE else connection_file))
                window._activate_primary()
                assert exported_file.read_bytes() == identity.card.serialize()
                assert window.pair_import_ready and not window.approvals.cards
                assert not window.input.armed and not window.connected
                assert not window.send_guide.get_visible() and window.receive_guide.get_visible()
                for response in (Gtk.ResponseType.NO, Gtk.ResponseType.YES):
                    window._import()
                    prompts = [item for item in Gtk.Window.list_toplevels() if isinstance(item, Gtk.MessageDialog)]
                    assert len(prompts) == 1 and not window.approvals.cards
                    prompt = prompts[0]
                    assert peer.card.fingerprint in prompt.get_property("secondary-text")
                    assert prompt.get_default_widget() == prompt.get_widget_for_response(Gtk.ResponseType.NO)
                    assert not window.peer_controls.get_visible() and not window.remove_peer.get_visible()
                    prompt.response(response)
                    assert bool(window.approvals.cards) == (response == Gtk.ResponseType.YES)
                # A second approval selects that computer; later refreshes retain it.
                second_state = StateDirectory(Path(directory) / "second-peer")
                try:
                    second = Identity.open(second_state, MemoryVault(), "Windows test laptop")
                    connection_file.write_bytes(second.card.serialize()); window._import()
                    prompt = next(item for item in Gtk.Window.list_toplevels() if isinstance(item, Gtk.MessageDialog))
                    assert len(window.approvals.cards) == 1 and window._peer().device_id == peer.card.device_id
                    prompt.response(Gtk.ResponseType.YES)
                    assert len(window.approvals.cards) == 2 and window._peer().device_id == second.card.device_id
                    window._refresh_peers()
                    assert window._peer().device_id == second.card.device_id
                    assert not window.connected and not window.input.armed
                    window._unpair()
                    assert len(window.approvals.cards) == 1 and window._peer().device_id == peer.card.device_id
                finally:
                    second_state.close()
            finally:
                window._dialog = original_dialog
            assert window.peer_controls.get_visible() and window.remove_peer.get_visible()
            assert window.peer_fingerprint.get_visible() and not window.pair_guide.get_visible()
            assert not window.connected and not window.input.armed
        finally:
            peer_state.close()
        window.backend = window.input.backend = native.backend
        window.lifecycle_ready = True
        window.address.set_text("203.0.113.10")  # Documentation-only address; no connection is opened.
        window._show_step(3)
        for _ in range(2):
            window.inbox.put(window.epoch, "connected", Bounds(0, 0, 1920, 1080)); window._tick()
            assert window.connected and not window.input.armed
            window._show_step(2); window._activate_primary()
            assert window.setup_step == 3 and not window.input.armed
            assert window.primary.get_label() == "Enable Linux input" and window.primary.get_sensitive()
            window._save_setup()
            assert not window.input.armed
            window._activate_primary()
            assert window.input.armed and not window.input.owns_cursor
            native.pump()
            assert native.server.fixture_keys(native.fixture) == native.server.fixture_buttons(native.fixture) == 0
            window._stop()
            assert not window.connected and not window.input.armed
        window.inbox.put(window.epoch, "connected", Bounds(0, 0, 1920, 1080)); window._tick()
        window.lifecycle_ready = False; window._update_setup()
        assert not window.primary.get_sensitive()
        window._allow()
        assert not window.input.armed and not window.input.owns_cursor
        window._unpair()
        assert not window.approvals.cards and not window.connected and not window.input.armed
        assert not window.peer_controls.get_visible() and not window.remove_peer.get_visible()
        assert not window.peer_fingerprint.get_visible() and window.pair_guide.get_visible()
        # OS labels and source/receiver selection cannot create trust or arm input.
        for index, operating_system in enumerate(("Windows", "Mac", "Linux")):
            window.peer_os.set_selected(index); window._prepare_import(); window._show_step(1)
            assert window.primary.get_label() == "Add " + operating_system + " computer"
            assert not window.input.armed and not window.connected and not window.approvals.cards
        window.peer_os.set_selected(0)
        window.role.set_selected(1); window.source_supported = False; window.lifecycle_ready = True
        window._show_step(0); window._update_setup()
        assert not window.primary.get_sensitive() and not window.input.armed
        capture_fixture = CaptureFixture()
        previous_capture = window.capture_backend
        try:
            window.capture_backend = capture_fixture.backend
            window.approvals.approve(peer.card); window._refresh_peers()
            portal = CapturePortal(window.capture_backend, window._portal_changed, lambda *_: None, lambda *_: None)
            portal.session = "/org/freedesktop/portal/desktop/session/fixture/gui"
            portal.bounds = Bounds(-1000, 0, 1000, 800)
            portal.enable = lambda edge, callback: callback()  # Explicit consent is synthetic in this GUI fixture.
            window.portal = portal; window.source_supported = True
            window.source = SourceSession(identity.card.device_id, window.connection, portal, window.status.set_text)
            window.inbox.put(window.epoch, "connected", Bounds(0, 0, 1920, 1080)); window._tick(); window._show_step(3)
            assert window.connected and not window.source.armed and not window.input.armed
            assert window.primary.get_label() == "Start sharing" and window.primary.get_sensitive()
            window._activate_primary(); assert window.source.armed and not window.source.owns_cursor
            window._save_setup(); assert window.source.armed
            window.role.set_selected(0)
            assert not window.connected and window.source is None and not window.input.armed
            assert not capture_fixture.backend.ready()
        finally:
            window._stop(); capture_fixture.close(); window.capture_backend = previous_capture
            window._unpair(); window.pair_import_ready = False; window.source_supported = False
    finally:
        window._stop(); native.tearDown()
        window.backend = window.input.backend = original_backend
        window.lifecycle_ready = original_lifecycle
        window.address.set_text(""); window._show_step(0)
        window.status.set_text("Setup is ready. Continue to link your computers.")
    def select_view():
        if view == "files":
            example = Path(directory) / "Example document.txt"; example.write_text("Disposable UI fixture.")
            window._stage_files([Gio.File.new_for_path(str(example))]); window.stack.set_visible_child_name("files")
        else:
            if view in ("pair-mac", "pair-linux"):
                window.peer_os.set_selected(1 if view == "pair-mac" else 2); window._prepare_import()
            if view == "source": window.role.set_selected(1)
            if view == "wait": window.network_mode.set_selected(1)
            if view == "pair-receive": window._prepare_import()
            if view.endswith("-approved"):
                window.approvals.approve(peer.card); window._refresh_peers()
                window.status.set_text("Computer approved. Continue when both computers have approved each other.")
            window._show_step({"permissions": 0, "pair": 1, "pair-receive": 1, "pair-approved": 1, "layout": 2, "link": 3, "details": 3, "details-approved": 1, "pair-mac": 1, "pair-linux": 1, "source": 0, "wait": 2}[view])
            if view.startswith("details"):
                window.details.set_expanded(True)
                def reveal_details():
                    adjustment = window.detail_scroll.get_vadjustment()
                    adjustment.set_value(adjustment.get_upper() - adjustment.get_page_size())
                    return False
                GLib.timeout_add(200, reveal_details)
        GLib.timeout_add(350, capture)
        return False
    def capture():
        try:
            assert not window.connected and not window.input.armed and not window.backend.ready()
            assert window.stack.get_visible_child_name() == ("files" if view == "files" else "setup")
            assert bool(window.approvals.cards) == view.endswith("-approved")
            assert window.peer_controls.get_visible() == bool(window.approvals.cards)
            assert window.remove_peer.get_visible() == bool(window.approvals.cards)
            assert window.permission_label.get_text() == "Desktop access: not granted"
            # The fixed navigation and primary action remain inside the window.
            if window.get_width() > (880 if len(sys.argv) > 3 else 1000):
                def measure(widget, path=""):
                    size = widget.measure(Gtk.Orientation.HORIZONTAL, -1)[:2]
                    if size[0] > 500: print(path, type(widget).__name__, widget.get_css_classes(), size)
                    child = widget.get_first_child(); index = 0
                    while child:
                        measure(child, path + "/" + str(index)); child = child.get_next_sibling(); index += 1
                measure(window)
            assert window.get_width() <= (880 if len(sys.argv) > 3 else 1000), f"Unexpected width: {window.get_width()}"
            assert window.get_height() <= (668 if len(sys.argv) > 3 else 788), f"Unexpected height: {window.get_height()}"
            paintable = Gtk.WidgetPaintable.new(window)
            snapshot = Gtk.Snapshot.new()
            paintable.snapshot(snapshot, window.get_width(), window.get_height())
            node = snapshot.to_node()
            bounds = Graphene.Rect(); bounds.init(0, 0, window.get_width(), window.get_height())
            texture = window.get_native().get_renderer().render_texture(node, bounds)
            assert texture.save_to_png(sys.argv[1])
            print("PASS: GTK approval, empty/approved states and input gates checked; synthetic setup rendered without network or desktop input permission.")
        except BaseException as error:
            errors.append(error)
        finally:
            window.close(); application.quit(); loop.quit()
            assert not window.files and state.fd == -1
        return False
    loop = GLib.MainLoop(); GLib.timeout_add(100, select_view); loop.run()
    if errors: raise errors[0]
