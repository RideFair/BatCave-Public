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
from test_core import MemoryVault

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
    # A FIFO must be refused without waiting for a writer or retaining an FD.
    fifo = Path(directory) / "not-a-regular-file"; os.mkfifo(fifo)
    try:
        window._stage_files([Gio.File.new_for_path(str(fifo))])
        raise AssertionError("A FIFO entered the shelf")
    except ValueError:
        assert not window.files
    if len(sys.argv) > 2 and sys.argv[2] == "files":
        example = Path(directory) / "Example document.txt"; example.write_text("Disposable UI fixture.")
        window._stage_files([Gio.File.new_for_path(str(example))]); window.stack.set_visible_child_name("files")
    def capture():
        try:
            assert not window.connected and not window.input.armed and not window.backend.ready()
            paintable = Gtk.WidgetPaintable.new(window)
            snapshot = Gtk.Snapshot.new()
            paintable.snapshot(snapshot, window.get_width(), window.get_height())
            node = snapshot.to_node()
            bounds = Graphene.Rect(); bounds.init(0, 0, window.get_width(), window.get_height())
            texture = window.get_native().get_renderer().render_texture(node, bounds)
            assert texture.save_to_png(sys.argv[1])
            print("PASS: actual GTK setup rendered without a peer, network connection or input permission.")
        except BaseException as error:
            errors.append(error)
        finally:
            window.close(); application.quit(); loop.quit()
            assert not window.files and state.fd == -1
        return False
    loop = GLib.MainLoop(); GLib.timeout_add(300, capture); loop.run()
    if errors: raise errors[0]
