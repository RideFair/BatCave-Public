"""Temporary raw-TCP pairing service with session-scoped main-thread events."""
import collections
import socket
import threading
import time
from dataclasses import dataclass

from .discovery import EXPIRY, PAIRING_PORT
from .pairing import Decision, run


class Attempts:
    def __init__(self): self.times = collections.deque()

    def admit(self, now):
        while self.times and now - self.times[0] >= 60: self.times.popleft()
        if len(self.times) >= 6 or self.times and now - self.times[-1] < 5: return False
        self.times.append(now); return True


@dataclass
class _Session:
    generation: int
    cancel: threading.Event
    stream: object = None

    def close(self):
        self.cancel.set()
        if self.stream:
            try: self.stream.shutdown(socket.SHUT_RDWR)
            except OSError: pass
            self.stream.close()


@dataclass(frozen=True)
class Result:
    peer: object
    address: str
    initiator: bool
    session: str


class PairingService:
    def __init__(self, identity, local, accept_peer, on_lan, pairing_port=PAIRING_PORT, host="0.0.0.0"):
        self.identity = identity; self.local = local; self.accept_peer = accept_peer; self.on_lan = on_lan
        self.port = pairing_port; self.host = host
        self.lock = threading.RLock(); self.generation = 0; self.events = collections.deque()
        self.active = None; self.listener = None; self.window = None; self.expires = 0
        self.attempts = Attempts(); self.closed = False

    @property
    def is_open(self):
        with self.lock: return self.window is not None and not self.window.is_set() and time.monotonic() < self.expires

    @property
    def busy(self):
        with self.lock: return self.active is not None

    def _event(self, generation, kind, value):
        with self.lock:
            if generation != self.generation: return
            # Only one session exists; overflow cancels instead of retaining UI work.
            if len(self.events) >= 16:
                self.close_window(); return
            self.events.append((generation, kind, value))

    def drain(self):
        with self.lock:
            result = list(self.events); self.events.clear(); return result

    def open_window(self, seconds=180):
        with self.lock:
            if self.closed: raise RuntimeError("Nearby pairing is closed.")
            self.close_window()
            listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            try:
                listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
                listener.bind((self.host, self.port)); listener.listen(4); listener.settimeout(0.2)
            except BaseException: listener.close(); raise
            self.listener = listener; self.port = listener.getsockname()[1]
            self.window = threading.Event(); self.expires = time.monotonic() + seconds
            threading.Thread(target=self._accept, args=(listener, self.window), daemon=True).start()

    def close_window(self):
        with self.lock:
            self.generation += 1
            if self.window: self.window.set()
            if self.active: self.active.close()
            if self.listener: self.listener.close()
            self.active = self.listener = self.window = None
            self.events.clear()

    def cancel(self):
        with self.lock:
            self.generation += 1
            if self.active: self.active.close()
            self.active = None; self.events.clear()

    def _accept(self, listener, window):
        try:
            while not window.is_set():
                with self.lock:
                    if self.window is not window: return
                    if time.monotonic() >= self.expires:
                        self.close_window(); self._event(self.generation, "expired", None); return
                try: stream, address = listener.accept()
                except socket.timeout: continue
                with self.lock:
                    if (not self.is_open or self.active or not self.on_lan(address[0])
                            or not self.attempts.admit(time.monotonic())):
                        stream.close(); rejected = True
                    else:
                        self.generation += 1
                        session = self.active = _Session(self.generation, threading.Event(), stream)
                        rejected = False
                if rejected: window.wait(0.02); continue
                threading.Thread(target=self._run, args=(session, address[0], False, None), daemon=True).start()
        except OSError:
            if not window.is_set():
                with self.lock:
                    if self.window is window:
                        self.close_window(); self._event(self.generation, "failed", "Nearby pairing stopped. Search again to retry.")
        finally: listener.close()

    def link(self, computer):
        with self.lock:
            if (not self.is_open or self.active or not computer.pairing or not self.on_lan(computer.address)
                    or not 0 <= time.monotonic() - computer.seen <= EXPIRY):
                raise ValueError("Open BatCave on both computers and search again.")
            self.generation += 1
            session = self.active = _Session(self.generation, threading.Event())
            threading.Thread(target=self._run, args=(session, computer.address, True, computer), daemon=True).start()

    def _run(self, session, address, initiator, expected):
        started = time.monotonic()
        try:
            if initiator:
                stream = socket.socket(socket.AF_INET, socket.SOCK_STREAM); stream.settimeout(5)
                with self.lock:
                    if session is not self.active or session.cancel.is_set(): stream.close(); return
                    session.stream = stream
                stream.connect((address, expected.pairing_port))

            confirmation = None
            def confirm(value):
                nonlocal confirmation
                confirmation = value
                decision = Decision()
                self._event(session.generation, "confirm", (value, decision))
                return decision

            peer = run(session.stream, self.identity, self.local, initiator, confirm, self.accept_peer,
                       session.cancel, expected=expected, handshake_seconds=max(0, 10 - (time.monotonic() - started)),
                       lifetime_seconds=max(0, 120 - (time.monotonic() - started)))
            with self.lock:
                if session is not self.active or session.cancel.is_set() or not self.is_open: return
                if not self.accept_peer(peer): raise ValueError("Approval changed during pairing.")
                self._event(session.generation, "completed", Result(peer, address, initiator, confirmation.session))
                # Hold this generation until the UI saves or rejects the result.
        except Exception:
            with self.lock:
                if session is self.active and not session.cancel.is_set():
                    self.active = None; self.generation += 1; self.events.clear()
                    self._event(self.generation, "failed", "Computers were not linked. Compare the code on both screens and search again to retry.")
        finally:
            if session.stream: session.stream.close()

    def acknowledge(self, generation):
        with self.lock:
            if generation != self.generation: return False
            if self.active: self.active.close()
            self.active = None; return True

    def close(self):
        with self.lock: self.closed = True; self.close_window()
