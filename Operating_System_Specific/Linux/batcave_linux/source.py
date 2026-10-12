"""Locally armed source ownership synchronized with the portal and passive EIS."""

import math
import time
import uuid

from .input import BUTTONS, EXTENDED_SCAN, VK_KEYS
from .protocol import integer, number

KEYS = {code: vk for vk, code in VK_KEYS.items()}
KEYS.update({58: 0x14, 69: 0x90, 70: 0x91, 96: 0x0D, 98: 0x6F, 127: 0x5D})
KEYS.update({55: 0x6A, 71: 0x67, 72: 0x68, 73: 0x69, 74: 0x6D, 75: 0x64,
             76: 0x65, 77: 0x66, 78: 0x6B, 79: 0x61, 80: 0x62, 81: 0x63, 82: 0x60, 83: 0x6E})
EXTENDED = {code: scan for scan, code in EXTENDED_SCAN.items()}
NAMES = {code: name for name, code in BUTTONS.items()}


def key_payload(code, down):
    integer(code, 1, 255)
    extended = code in EXTENDED
    if extended: scan = EXTENDED[code]
    elif 1 <= code <= 83: scan = code
    elif code in (87, 88): scan = code
    else: raise ValueError("This physical key cannot be shared safely with this protocol.")
    return dict(virtualKey=KEYS.get(code, 0), scanCode=scan, isExtended=extended, isKeyUp=not down, text=None)


class SourceSession:
    def __init__(self, local_id, transport, portal, changed):
        self.local_id, self.transport, self.portal, self.changed = local_id, transport, portal, changed
        self.epoch = None; self.peer = None; self.remote = None
        self.armed = self.pending_enable = self.owns_cursor = False
        self.activation = self.last_activation = None
        self.pending = []; self.pending_since = None
        self.local = None; self.cursor = (0, 0); self.return_position = (0, 0)
        self.keys = set(); self.buttons = set(); self.wheel = {}
        self.fraction = [0.0, 0.0]
        self.link = str(uuid.uuid4())
        self.enable_generation = 0

    def connected(self, epoch, peer, remote):
        self.stop(notify=False, close_permission=False)
        self.epoch, self.peer, self.remote = epoch, peer, remote
        self.connection_generation = getattr(remote, "connection_generation", None)
        self.last_activation = None

    def start(self, edge, authorize=None, activate=None):
        if self.epoch is None or self.peer is None or not self.portal.ready():
            raise RuntimeError("Link an approved computer and grant capture permission first.")
        if self.armed or self.pending_enable: return
        if edge not in ("left", "right"): raise ValueError("Choose a shared edge.")
        self.edge = edge; self.local = self.portal.bounds
        self.pending.clear(); self.pending_since = None
        self.pending_enable = True
        epoch = self.epoch
        self.enable_generation += 1; generation = self.enable_generation
        def finish():
            if self.epoch != epoch or self.enable_generation != generation or not self.pending_enable: return
            if authorize is not None and not authorize():
                self.stop(notify=False)
                self.changed("Sharing stays paused. Start again when ready.")
                return
            self.pending_enable = False; self.armed = True
            self.changed("Sharing is ready. Cross the shared edge to use the other computer. F12 pauses sharing.")
        def enabled():
            if activate is None: finish()
            elif not activate(finish) and self.enable_generation == generation:
                self.stop(notify=False)
        self.portal.enable(edge, enabled)

    def _send(self, kind, payload):
        if not self.transport.send(kind, payload, self.epoch, connection_generation=self.connection_generation):
            raise RuntimeError("The approved connection ended. Sharing stopped.")

    def activate(self, activation, position):
        if not self.armed:
            self.portal.release(activation, position); return
        integer(activation, 0, 0xffffffff)
        if self.last_activation is not None:
            distance = (activation - self.last_activation) & 0xffffffff
            if distance == 0 or distance >= 0x80000000: return
        if self.activation is not None: self._return()
        self.last_activation = self.activation = activation
        x, y = (number(value, -200000, 200000) for value in position)
        normalized = max(0, min(1, (y - self.local.top) / max(1, self.local.height - 1)))
        self.return_position = self.portal.return_point(self.edge, normalized)
        self.cursor = self.remote.point("left" if self.edge == "right" else "right", normalized)
        pending, self.pending = self.pending, []
        self.pending_since = None
        for event in pending: self._event(event)

    def events(self, events):
        for event in events:
            if not self.armed: continue
            if self.activation == event.sequence:
                self._event(event)
            elif self.last_activation is None or 0 < ((event.sequence - self.last_activation) & 0xffffffff) < 0x80000000:
                if len(self.pending) >= 512: raise RuntimeError("Capture synchronization overflow. Sharing stopped.")
                if self.pending_since is None: self.pending_since = time.monotonic()
                self.pending.append(event)
        if self.pending_since is not None and time.monotonic() - self.pending_since > 2:
            raise RuntimeError("Capture activation could not be verified. Sharing stopped.")

    def _event(self, event):
        if not self.armed or event.sequence != self.activation: return
        if event.kind == 1:
            if not self.owns_cursor:
                self._send("cursor.handoff", self._handoff(self.local_id, self.local, self.edge,
                             self.peer.device_id, self.remote, "left" if self.edge == "right" else "right", self.cursor[1]))
                self.owns_cursor = True; self.changed("Using the other computer. F12 pauses sharing.")
            return
        if event.kind == 2:
            self._return(); return
        if not self.owns_cursor: return
        if event.kind == 3:
            dx, dy = number(event.x, -100000, 100000), number(event.y, -100000, 100000)
            self.fraction[0] += dx; self.fraction[1] += dy
            dx, dy = math.trunc(self.fraction[0]), math.trunc(self.fraction[1])
            self.fraction[0] -= dx; self.fraction[1] -= dy
            x, y = self.cursor[0] + dx, self.cursor[1] + dy
            if (self.edge == "right" and x < self.remote.left) or (self.edge == "left" and x > self.remote.right):
                self.cursor = (max(self.remote.left, min(self.remote.right, x)), max(self.remote.top, min(self.remote.bottom, y)))
                self._return(); return
            x, y = max(self.remote.left, min(self.remote.right, x)), max(self.remote.top, min(self.remote.bottom, y))
            dx, dy = round(x - self.cursor[0]), round(y - self.cursor[1])
            if dx or dy: self._send("input.mouseDelta", {"deltaX": dx, "deltaY": dy})
            self.cursor = (x, y)
        elif event.kind == 4:
            if event.code not in NAMES: raise ValueError("Unsupported mouse button. Sharing stopped.")
            down = bool(event.down)
            if (event.code in self.buttons) != down:
                self._send("input.mouseButton", {"button": NAMES[event.code], "isDown": down})
                if down: self.buttons.add(event.code)
                else: self.buttons.discard(event.code)
        elif event.kind == 5:
            if event.code == 88 and event.down:
                self.stop(); self.changed("Sharing paused with F12. Grant access to start again."); return
            down = bool(event.down)
            if (event.code in self.keys) != down:
                self._send("input.key", key_payload(event.code, down))
                if down: self.keys.add(event.code)
                else: self.keys.discard(event.code)
        elif event.kind in (6, 7):
            state = self.wheel.setdefault(event.code, [0.0, 0.0, 0.0, 0.0, False])
            offset = 0 if event.kind == 6 else 2
            state[offset] += event.x; state[offset + 1] += event.y
            state[4] |= event.kind == 6
        elif event.kind == 8:
            state = self.wheel.pop(event.code, None)
            if state:
                x, y = state[:2] if state[4] else [value * 8 for value in state[2:4]]
                x, y = integer(round(x), -120000, 120000), integer(round(-y), -120000, 120000)
                if x or y: self._send("input.mouseWheel", {"horizontalDelta": x, "verticalDelta": y})
        elif event.kind == 9:
            for bit, equivalents in enumerate(((42, 54), (29, 97), (56,), (125, 126), (100,))):
                held = self.keys.intersection(equivalents)
                wanted = bool(int(event.x) & (1 << bit))
                if wanted and not held:
                    code = equivalents[0]; self._send("input.key", key_payload(code, True)); self.keys.add(code)
                elif not wanted:
                    for code in held:
                        self._send("input.key", key_payload(code, False)); self.keys.discard(code)

    def _handoff(self, source_id, source, source_edge, target_id, target, target_edge, y):
        position = max(0, min(1, (y - target.top) / max(1, target.height - 1)))
        target_x, target_y = target.point(target_edge, position)
        source_x, source_y = source.point(source_edge, position)
        return dict(linkId=self.link, segmentPosition=position, sourceDisplayId=source_id + ":desktop",
                    sourceEdge=source_edge, sourcePosition=position, sourceX=round(source_x), sourceY=round(source_y),
                    targetDeviceId=target_id, targetDisplayId=target_id + ":desktop", targetEdge=target_edge,
                    targetPosition=position, targetX=round(target_x), targetY=round(target_y))

    def _return(self):
        activation, self.activation = self.activation, None
        try:
            if self.owns_cursor:
                position = max(0, min(1, (self.cursor[1] - self.remote.top) / max(1, self.remote.height - 1)))
                self.return_position = self.portal.return_point(self.edge, position)
                self._send("cursor.handoff", self._handoff(self.peer.device_id, self.remote,
                     "left" if self.edge == "right" else "right", self.local_id, self.local, self.edge, self.return_position[1]))
        finally:
            self.owns_cursor = False; self.keys.clear(); self.buttons.clear(); self.wheel.clear()
            self.fraction = [0.0, 0.0]
            if activation is not None: self.portal.release(activation, self.return_position)
        self.changed("Local control restored. Cross the edge to use the other computer again.")

    def deactivated(self, activation):
        if activation == self.activation: self._return()

    def handle(self, epoch, message):
        if epoch == self.epoch and message["type"] == "control.testStop":
            self.stop(notify=False)
            self.changed("The other computer paused sharing. Grant access to start again.")

    def stop(self, notify=True, close_permission=True):
        self.enable_generation += 1
        try:
            if notify and self.epoch is not None and (self.owns_cursor or self.armed or self.pending_enable):
                try: self.transport.send("control.testStop", {"reason": "Sharing paused"}, self.epoch,
                                         connection_generation=self.connection_generation)
                except Exception: pass  # Socket/queue failure cannot prevent local cleanup.
        finally:
            self.armed = self.pending_enable = self.owns_cursor = False
            activation, self.activation = self.activation, None
            self.pending.clear(); self.pending_since = None
            self.keys.clear(); self.buttons.clear(); self.wheel.clear(); self.fraction = [0.0, 0.0]
            if close_permission: self.portal.close()
            elif activation is not None: self.portal.release(activation, self.return_position)

    def disconnect(self, close_permission=True):
        try: self.stop(close_permission=close_permission)
        finally: self.epoch = None; self.peer = self.remote = None
