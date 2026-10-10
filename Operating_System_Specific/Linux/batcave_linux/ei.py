"""Portal-authorized libei backend; all calls run on the GTK main thread."""

import ctypes as C
import os
from pathlib import Path

from .protocol import Bounds


class Region(C.Structure):
    _fields_ = [(key, C.c_double) for key in ("x", "y", "width", "height")]


class EiBackend:
    def __init__(self, library=None):
        root = Path(__file__).resolve().parents[1]
        library = library or root / "native" / "libbatcave-ei.so"
        if not Path(library).exists():
            library = root / ".build" / "libbatcave-ei.so"
        self.api = C.CDLL(str(library))
        signatures = {
            "bc_open": (C.c_void_p, [C.c_int]), "bc_close": (None, [C.c_void_p]),
            "bc_poll": (C.c_int, [C.c_void_p]), "bc_ready": (C.c_int, [C.c_void_p]),
            "bc_start": (C.c_int, [C.c_void_p]), "bc_stop": (None, [C.c_void_p]),
            "bc_regions": (C.c_int, [C.c_void_p, C.POINTER(Region), C.c_int]),
            "bc_move": (C.c_int, [C.c_void_p, C.POINTER(C.c_double), C.POINTER(C.c_double)]),
            "bc_key": (C.c_int, [C.c_void_p, C.c_uint32, C.c_int]),
            "bc_button": (C.c_int, [C.c_void_p, C.c_uint32, C.c_int]),
            "bc_scroll": (C.c_int, [C.c_void_p, C.c_int32, C.c_int32]),
        }
        for name, (result, arguments) in signatures.items():
            function = getattr(self.api, name)
            function.restype, function.argtypes = result, arguments
        self.context = None

    def attach(self, fd):
        self.close()
        try:
            # libei duplicates the descriptor; the portal's copy is always closed.
            self.context = self.api.bc_open(fd)
        finally:
            os.close(fd)
        if not self.context:
            raise RuntimeError("Could not connect to the desktop's authorized input service.")

    def poll(self):
        if self.context and self.api.bc_poll(self.context) != 0:
            self.close()
            raise RuntimeError("The desktop input service disconnected or its devices changed.")

    def ready(self):
        return bool(self.context and self.api.bc_ready(self.context))

    def bounds(self):
        regions = (Region * 128)()
        count = self.api.bc_regions(self.context, regions, 128) if self.context else 0
        if count <= 0:
            raise RuntimeError("No authorized desktop input regions are available.")
        values = regions[:count]
        for region in values:
            if not 1 <= region.width <= 100000 or not 1 <= region.height <= 100000 or not 0 <= region.x <= 100000 or not 0 <= region.y <= 100000:
                raise ValueError("Unsupported desktop input region geometry.")
        left, top = min(r.x for r in values), min(r.y for r in values)
        right, bottom = max(r.x + r.width for r in values), max(r.y + r.height for r in values)
        bounds = Bounds(int(left), int(top), int(right - left), int(bottom - top))
        return Bounds.read(bounds.dictionary())

    def _call(self, function, *args):
        if not self.context or getattr(self.api, function)(self.context, *args) != 0:
            raise RuntimeError("The desktop rejected an input operation. Sharing stopped.")

    def start(self):
        self._call("bc_start")

    def stop(self):
        if self.context:
            self.api.bc_stop(self.context)

    def move(self, x, y):
        x, y = C.c_double(x), C.c_double(y)
        self._call("bc_move", C.byref(x), C.byref(y))
        return x.value, y.value

    def key(self, code, down):
        self._call("bc_key", code, int(down))

    def button(self, code, down):
        self._call("bc_button", code, int(down))

    def scroll(self, x, y):
        self._call("bc_scroll", x, y)

    def close(self):
        if self.context:
            self.api.bc_close(self.context)
            self.context = None
