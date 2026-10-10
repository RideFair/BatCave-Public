"""Passive libei transport; no raw device access or input interception fallback."""
import ctypes as C
import os
from pathlib import Path


class CaptureEvent(C.Structure):
    _fields_ = [('kind', C.c_uint32), ('sequence', C.c_uint32), ('code', C.c_uint32), ('down', C.c_uint32),
                ('x', C.c_double), ('y', C.c_double)]


class CaptureBackend:
    def __init__(self, library=None):
        path = Path(library) if library else Path(__file__).resolve().parents[1] / 'native/libbatcave-ei.so'
        self.api = C.CDLL(str(path)); self.context = None
        self.modifier_maps = {}
        signatures = {'bc_capture_open': (C.c_void_p, [C.c_int]), 'bc_capture_close': (None, [C.c_void_p]),
                      'bc_capture_poll': (C.c_int, [C.c_void_p]), 'bc_capture_ready': (C.c_int, [C.c_void_p]),
                      'bc_capture_next': (C.c_int, [C.c_void_p, C.POINTER(CaptureEvent)])}
        signatures['bc_capture_keymap'] = (C.c_int, [C.c_void_p, C.c_uint32, C.c_void_p, C.c_uint32])
        for name, (result, arguments) in signatures.items():
            function = getattr(self.api, name); function.restype, function.argtypes = result, arguments

    def attach(self, fd):
        self.close()
        try: self.context = self.api.bc_capture_open(fd)
        finally: os.close(fd)
        if not self.context: raise RuntimeError('The desktop did not provide an authorized capture connection.')

    def poll(self):
        if self.context and self.api.bc_capture_poll(self.context) != 0:
            self.close(); raise RuntimeError('Capture devices changed or the desktop ended input capture.')

    def ready(self):
        return bool(self.context and self.api.bc_capture_ready(self.context))

    def events(self):
        result = []
        if not self.context: return result
        for _ in range(512):
            event = CaptureEvent(); status = self.api.bc_capture_next(self.context, C.byref(event))
            if status < 0: raise RuntimeError('Capture overflow; sharing stopped.')
            if not status: break
            if event.kind == 9:
                masks = self._modifier_masks(event.code)
                depressed = int(event.x) | int(event.y)
                # Translate named XKB modifiers, not assumptions about bit order.
                event.x = sum(1 << bit for bit, mask in enumerate(masks) if depressed & mask)
                event.y = 0
            result.append(event)
        return result

    def _modifier_masks(self, slot):
        if slot in self.modifier_maps: return self.modifier_maps[slot]
        size = self.api.bc_capture_keymap(self.context, slot, None, 0)
        if size <= 0: raise RuntimeError('The desktop did not provide a bounded keyboard modifier map.')
        data = C.create_string_buffer(size + 1)
        if self.api.bc_capture_keymap(self.context, slot, data, size) != size:
            raise RuntimeError('The desktop keyboard modifier map changed.')
        xkb = C.CDLL('libxkbcommon.so.0')
        xkb.xkb_context_new.argtypes = [C.c_int]; xkb.xkb_context_new.restype = C.c_void_p
        xkb.xkb_keymap_new_from_string.argtypes = [C.c_void_p, C.c_char_p, C.c_int, C.c_int]
        xkb.xkb_keymap_new_from_string.restype = C.c_void_p
        xkb.xkb_keymap_mod_get_index.argtypes = [C.c_void_p, C.c_char_p]
        xkb.xkb_keymap_mod_get_index.restype = C.c_uint32
        xkb.xkb_context_unref.argtypes = [C.c_void_p]
        xkb.xkb_keymap_unref.argtypes = [C.c_void_p]
        # Suppress parser diagnostics: a keymap is private desktop input data.
        xkb.xkb_context_set_log_level.argtypes = [C.c_void_p, C.c_int]
        context = xkb.xkb_context_new(0); keymap = None
        if not context: raise RuntimeError('Keyboard modifier translation is unavailable.')
        try:
            xkb.xkb_context_set_log_level(context, 0)
            keymap = xkb.xkb_keymap_new_from_string(context, data, 1, 0)
            if not keymap: raise RuntimeError('The desktop keyboard modifier map is invalid.')
            masks = []
            for name in (b'Shift', b'Control', b'Mod1', b'Mod4', b'Mod5'):
                index = xkb.xkb_keymap_mod_get_index(keymap, name)
                masks.append(1 << index if index < 32 else 0)
            self.modifier_maps[slot] = masks
            return masks
        finally:
            if keymap: xkb.xkb_keymap_unref(keymap)
            xkb.xkb_context_unref(context)

    def close(self):
        if self.context:
            self.api.bc_capture_close(self.context); self.context = None
        self.modifier_maps.clear()
