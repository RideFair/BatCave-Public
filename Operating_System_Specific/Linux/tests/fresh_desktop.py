"""Run only under a disposable Xvfb display; no real profile, keyring or input."""
from pathlib import Path
import tempfile
import gi
gi.require_version("Gtk", "4.0")
from gi.repository import Gtk
from batcave_linux.desktop import Application
from batcave_linux.storage import StateDirectory
from batcave_linux.identity import Identity, Approvals
from test_core import MemoryVault


Gtk.init()
with tempfile.TemporaryDirectory() as directory:
    path = Path(directory) / "secure-preview"
    state = StateDirectory(path)
    state.lock_for_application()
    identity = Identity.open(state, MemoryVault(), "Synthetic desktop")
    original = state.read("identity.json")
    root = Path(__file__).resolve().parents[1]
    app = Application(root / ".build/libbatcave-ei.so", (state, identity, Approvals(state)))
    app.register(None); app.activate(); window = app.window
    window._request_fresh_start()
    dialog = next(item for item in Gtk.Window.list_toplevels() if isinstance(item, Gtk.MessageDialog))
    dialog.response(Gtk.ResponseType.CANCEL)
    assert state.read("identity.json") == original and not window.shutting_down
    window._request_fresh_start()
    dialog = next(item for item in Gtk.Window.list_toplevels() if isinstance(item, Gtk.MessageDialog))
    dialog.response(Gtk.ResponseType.ACCEPT)
    assert window.shutting_down and state.fd == -1 and not window.input.armed
    assert window.connection.socket is None and window.connection.listener is None
    assert not path.exists()
    backups = list(Path(directory).glob("secure-preview-backup-*"))
    assert len(backups) == 1 and (backups[0] / "identity.json").read_bytes() == original
    dialog = next(item for item in Gtk.Window.list_toplevels() if isinstance(item, Gtk.MessageDialog))
    dialog.response(Gtk.ResponseType.CLOSE)
    window.close(); app.quit()
print("PASS: native Start fresh cancellation preserves setup; confirmation stops input and transport before backup.")
