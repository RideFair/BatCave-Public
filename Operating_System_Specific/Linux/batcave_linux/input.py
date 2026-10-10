"""Session-scoped input ownership with balanced key/button cleanup."""

from .protocol import Bounds, boolean, integer, number

BUTTONS = {"left": 272, "right": 273, "middle": 274, "x1": 275, "x2": 276}
VK_KEYS = {0x08: 14, 0x09: 15, 0x0D: 28, 0x10: 42, 0x11: 29, 0x12: 56, 0x1B: 1, 0x20: 57,
           0x21: 104, 0x22: 109, 0x23: 107, 0x24: 102, 0x25: 105, 0x26: 103, 0x27: 106, 0x28: 108,
           0x2D: 110, 0x2E: 111, 0x5B: 125, 0x5C: 126, 0xA0: 42, 0xA1: 54, 0xA2: 29, 0xA3: 97,
           0xA4: 56, 0xA5: 100, 0xBA: 39, 0xBB: 13, 0xBC: 51, 0xBD: 12, 0xBE: 52, 0xBF: 53,
           0xC0: 41, 0xDB: 26, 0xDC: 43, 0xDD: 27, 0xDE: 40}
VK_KEYS.update(zip(map(ord, "ABCDEFGHIJKLMNOPQRSTUVWXYZ"), (30, 48, 46, 32, 18, 33, 34, 35, 23, 36, 37, 38, 50, 49, 24, 25, 16, 19, 31, 20, 22, 47, 17, 45, 21, 44)))
VK_KEYS.update(zip(map(ord, "1234567890"), range(2, 12)))
VK_KEYS.update({0x70 + i: 59 + i for i in range(10)})
VK_KEYS.update({0x7A: 87, 0x7B: 88})
EXTENDED_SCAN = {0x1C: 96, 0x1D: 97, 0x35: 98, 0x38: 100, 0x47: 102, 0x48: 103, 0x49: 104,
                 0x4B: 105, 0x4D: 106, 0x4F: 107, 0x50: 108, 0x51: 109, 0x52: 110, 0x53: 111,
                 0x5B: 125, 0x5C: 126, 0x5D: 127}


def keycode(payload):
    vk = integer(payload.get("virtualKey"), 0, 65535)
    scan = integer(payload.get("scanCode"), 0, 65535)
    extended = boolean(payload.get("isExtended"))
    if extended and scan in EXTENDED_SCAN:
        return EXTENDED_SCAN[scan]
    if not extended and 1 <= scan <= 83:
        return scan
    if vk in VK_KEYS:
        return VK_KEYS[vk]
    raise ValueError("Unsupported physical keyboard key.")


class InputSession:
    def __init__(self, local_id, backend):
        self.local_id = local_id
        self.backend = backend
        self.epoch = None
        self.armed = False
        self.owns_cursor = False
        self.keys = set()
        self.buttons = set()
        self.bounds = None
        self.cursor = (0, 0)

    def connected(self, epoch):
        self.pause()
        self.epoch = epoch

    def enable(self, bounds):
        if self.epoch is None or not self.backend.ready():
            raise RuntimeError("Connect securely and grant desktop input permission first.")
        self.pause()
        self.bounds = bounds
        self.armed = True

    def pause(self):
        self.armed = False
        self.owns_cursor = False
        errors = []
        for key in tuple(self.keys):
            try:
                self.backend.key(key, False)
            except Exception as error:
                errors.append(error)
        for button in tuple(self.buttons):
            try:
                self.backend.button(button, False)
            except Exception as error:
                errors.append(error)
        self.keys.clear()
        self.buttons.clear()
        try:
            self.backend.stop()
        except Exception as error:
            errors.append(error)
        if errors:
            # Closing the virtual devices is the fail-safe if releases cannot be sent.
            self.backend.close()
        return not errors

    def disconnect(self):
        self.pause()
        self.epoch = None

    def handle(self, epoch, message):
        if epoch != self.epoch or not self.armed:
            return
        kind, payload = message["type"], message["payload"]
        if kind == "control.testStop":
            self.pause()
            return
        if kind == "cursor.handoff":
            # Every handoff ends previous ownership before it can grant a new one.
            enabled = self.armed
            cleaned = self.pause()
            self.armed = enabled and cleaned
            if not self.armed:
                return
            if payload.get("targetDeviceId") != self.local_id:
                return
            if payload.get("targetDisplayId") != self.local_id + ":desktop":
                raise ValueError("The Linux preview requires an aggregate desktop handoff.")
            position = number(payload.get("targetPosition"), 0, 1)
            self.cursor = self.bounds.point(payload.get("targetEdge"), position)
            self.backend.start()
            self.cursor = self.backend.move(*self.cursor)
            self.owns_cursor = True
            return
        if not self.owns_cursor:
            return
        if kind == "input.mouseDelta":
            dx = integer(payload.get("deltaX"), -100000, 100000)
            dy = integer(payload.get("deltaY"), -100000, 100000)
            x = min(self.bounds.right, max(self.bounds.left, self.cursor[0] + dx))
            y = min(self.bounds.bottom, max(self.bounds.top, self.cursor[1] + dy))
            self.cursor = self.backend.move(x, y)
        elif kind == "input.mouseButton":
            button = BUTTONS.get(payload.get("button"))
            if button is None:
                raise ValueError("Unknown mouse button.")
            self._transition(self.buttons, button, boolean(payload.get("isDown")), self.backend.button)
        elif kind == "input.mouseWheel":
            vertical = integer(payload.get("verticalDelta"), -120000, 120000)
            horizontal = integer(payload.get("horizontalDelta"), -120000, 120000)
            self.backend.scroll(horizontal, -vertical)  # Windows positive wheel means scroll up.
        elif kind == "input.key":
            code = keycode(payload)
            self._transition(self.keys, code, not boolean(payload.get("isKeyUp")), self.backend.key)
        elif kind == "input.shortcut":
            target = {"copy": 46, "paste": 47, "cut": 45, "selectAll": 30}.get(payload.get("action"))
            if target is None:
                raise ValueError("Unknown editing shortcut.")
            # Preserve modifiers already held by the remote physical keyboard.
            temporary = [code for code in (29, target) if code not in self.keys]
            try:
                for code in temporary:
                    self._transition(self.keys, code, True, self.backend.key)
            finally:
                for code in reversed(temporary):
                    self._transition(self.keys, code, False, self.backend.key)
        # Clipboard, file and future message types cannot grant input ownership.

    @staticmethod
    def _transition(held, code, down, inject):
        if (code in held) == down:
            return
        if down:
            # Record first: failed emission still gets cleanup on the error path.
            held.add(code)
            inject(code, True)
        else:
            inject(code, False)
            held.discard(code)
