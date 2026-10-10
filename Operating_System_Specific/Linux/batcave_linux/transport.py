"""Selected-peer mutual TLS in either direction, with bounded single-worker I/O."""

import collections
import ipaddress
import select
import socket
import threading
import time

from .identity import tls_context, verify_peer
from .protocol import Bounds, FrameReader, decode, encode, hello, read_hello


class Inbox:
    def __init__(self):
        self.lock = threading.Lock()
        self.items = collections.deque()
        self.bytes = 0

    def put(self, epoch, kind, value, size=0):
        with self.lock:
            if len(self.items) >= 512 or self.bytes + size > 1048576:
                raise ValueError("Network input queue overflow; sharing stopped.")
            self.items.append((epoch, kind, value, size))
            self.bytes += size

    def terminal(self, epoch, reason, kind="disconnected"):
        with self.lock:
            newer = collections.deque(item for item in self.items if item[0] > epoch)
            if newer:
                # A previous worker must not erase a newer connection's events.
                self.items = newer
                self.bytes = sum(item[3] for item in newer)
                return
            # Disconnect wins over queued input; no replay after reconnection.
            self.items.clear()
            self.bytes = 0
            self.items.append((epoch, kind, reason, 0))

    def drain(self, count=64):
        with self.lock:
            result = []
            while self.items and len(result) < count:
                epoch, kind, value, size = self.items.popleft()
                self.bytes -= size
                result.append((epoch, kind, value))
            return result

    def connected(self, epoch, value):
        with self.lock:
            self.items = collections.deque(item for item in self.items if item[0] > epoch)
            self.bytes = sum(item[3] for item in self.items)
            self.items.append((epoch, "connected", value, 0))


class Connection:
    def __init__(self, identity, inbox, generation=lambda: 0, approved=lambda _peer: True):
        self.identity = identity
        self.inbox = inbox
        self.generation = generation
        self.approved = approved
        self.peer = None
        self.preferred = False
        self.session_token = 0
        self.outbound_pending = 0
        self.epoch = 0
        self.socket = None
        self.listener = None
        self.bound_address = None
        self.wake = None
        self.outgoing = collections.deque()
        self.outgoing_bytes = 0
        self.cancel = threading.Event()
        self.lock = threading.RLock()

    def current(self, epoch, generation, action):
        """Dispatch/activate atomically with socket adoption, never via a stale UI."""
        with self.lock:
            if (epoch != self.epoch or generation != self.session_token or self.socket is None
                    or self.cancel.is_set() or self.peer is None or not self.approved(self.peer)):
                return False
            action()
            return True

    def connect(self, host, peer, bounds, port=47871, listen_port=None, listen_host="0.0.0.0"):
        address = ipaddress.ip_address(host)
        if address.is_unspecified or address.is_multicast:
            raise ValueError("Enter the other computer's local IP address.")
        if not 1 <= port <= 65535 or type(port) is not int:
            raise ValueError("Invalid port.")
        epoch, cancel = self._begin(peer)
        try:
            if listen_port is not None: self._listen(epoch, cancel, peer, bounds, listen_host, listen_port)
            threading.Thread(target=self._outbound, args=(epoch, cancel, str(address), port, peer, bounds), daemon=True).start()
        except BaseException:
            self.stop(epoch); raise
        return epoch

    def _begin(self, peer):
        self.stop()
        with self.lock:
            self.epoch += 1; epoch = self.epoch
            cancel = threading.Event(); self.cancel = cancel; self.peer = peer; self.outbound_pending = 0
        return epoch, cancel

    def listen(self, peer, bounds, host="0.0.0.0", port=47871):
        epoch, cancel = self._begin(peer)
        try: self._listen(epoch, cancel, peer, bounds, host, port)
        except BaseException:
            self.stop(epoch); raise
        return epoch

    def _listen(self, epoch, cancel, peer, bounds, host, port):
        address = ipaddress.ip_address(host)
        if address.is_multicast or type(port) is not int or not 0 <= port <= 65535:
            raise ValueError("Invalid listening address or port.")
        listener = None
        try:
            listener = socket.socket(socket.AF_INET6 if address.version == 6 else socket.AF_INET, socket.SOCK_STREAM)
            listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            listener.bind((str(address), port)); listener.listen(4); listener.settimeout(0.5)
            with self.lock:
                self.listener = listener; self.bound_address = listener.getsockname()
            self.inbox.put(epoch, "listening", listener.getsockname())
            threading.Thread(target=self._accept, args=(epoch, cancel, peer, bounds, listener), daemon=True).start()
        except BaseException:
            if listener: listener.close()
            raise

    @staticmethod
    def _prepare_socket(raw):
        raw.settimeout(5)
        raw.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        raw.setsockopt(socket.SOL_SOCKET, socket.SO_KEEPALIVE, 1)
        for option, value in ((socket.TCP_KEEPIDLE, 5), (socket.TCP_KEEPINTVL, 1), (socket.TCP_KEEPCNT, 5)):
            raw.setsockopt(socket.IPPROTO_TCP, option, value)

    def _authenticate(self, raw, context, peer, bounds, server, cancel):
        stream = None
        try:
            self._prepare_socket(raw)
            stream = context.wrap_socket(raw, server_side=server, server_hostname=None if server else "batcave.local")
            verify_peer(stream, peer)
            stream.sendall(hello(self.identity.card, bounds)); stream.settimeout(0.5)
            reader = FrameReader(); deadline = time.monotonic() + 5
            frame = reader.read(stream, lambda: cancel.is_set() or time.monotonic() > deadline, self.generation)
            remote_bounds = read_hello(decode(frame, peer.device_id), peer.device_id)
            return stream, reader, remote_bounds
        except BaseException:
            (stream or raw).close()
            raise

    def _accept(self, epoch, cancel, peer, bounds, listener):
        try:
            context = tls_context(self.identity, peer, server=True)
            while not cancel.is_set():
                try: raw, _ = listener.accept()
                except socket.timeout: continue
                stream = None
                try:
                    stream, reader, remote_bounds = self._authenticate(raw, context, peer, bounds, True, cancel)
                    self._adopt(epoch, cancel, peer, stream, reader, remote_bounds, incoming=True)
                except Exception:
                    if stream is not None: stream.close()
                    # Unknown/unfinished attempts cannot consume the selected peer's approval.
                    continue
        except Exception:
            with self.lock:
                if epoch == self.epoch and not cancel.is_set() and self.socket is None:
                    self.inbox.terminal(epoch, "The secure listener ended. Connect again when ready.")
        finally:
            listener.close()
            with self.lock:
                if self.listener is listener: self.listener = None; self.bound_address = None

    def _outbound(self, epoch, cancel, host, port, peer, bounds):
        with self.lock:
            if epoch != self.epoch or cancel.is_set(): return
            self.outbound_pending += 1
        try:
            raw = socket.create_connection((host, port), timeout=5)
            stream, reader, remote_bounds = self._authenticate(raw, tls_context(self.identity, peer), peer, bounds, False, cancel)
            self._adopt(epoch, cancel, peer, stream, reader, remote_bounds, incoming=False)
        except Exception:
            with self.lock:
                if epoch == self.epoch and not cancel.is_set() and self.socket is None and self.listener is None:
                    self.inbox.terminal(epoch, "Connection ended. Reconnect and enable input again.")
        finally:
            with self.lock:
                if epoch == self.epoch: self.outbound_pending -= 1

    def _adopt(self, epoch, cancel, peer, stream, reader, remote_bounds, incoming):
        # .NET uses ordinal UTF-16 comparison. IDs are ordinarily ASCII, but
        # matching its byte order also makes imported non-ASCII labels deterministic.
        lower = self.identity.card.device_id.encode("utf-16-be") < peer.device_id.encode("utf-16-be")
        preferred = incoming != lower
        wake = socket.socketpair()
        for descriptor in wake: descriptor.setblocking(False)
        with self.lock:
            if cancel.is_set() or epoch != self.epoch or self.peer != peer or not self.approved(peer) or \
                    self.socket is not None and (self.preferred or not preferred):
                stream.close()
                for descriptor in wake: descriptor.close()
                return
            old = self.socket
            self.socket, self.preferred, self.wake = stream, preferred, wake[1]
            self.session_token += 1; token = self.session_token
            self.outgoing.clear(); self.outgoing_bytes = 0
            remote = SessionBounds(remote_bounds, token)
            self.inbox.connected(epoch, remote)
        if old is not None:
            try: old.shutdown(socket.SHUT_RDWR)
            except OSError: pass
            old.close()
        try:
            threading.Thread(target=self._io, args=(epoch, cancel, wake, peer, stream, reader, token), daemon=True).start()
        except BaseException:
            stream.close()
            for descriptor in wake: descriptor.close()
            with self.lock:
                if self.socket is stream:
                    self.socket = self.wake = None
                    self.inbox.terminal(epoch, "The secure connection could not start. Sharing stopped.")
            raise

    def _io(self, epoch, cancel, wake, peer, stream, reader, token):
        try:
            while not cancel.is_set():
                # One worker owns all SSL reads/writes; GUI callbacks never block on network I/O.
                for _ in range(64):
                    with self.lock:
                        if self.socket is not stream: return
                        if not self.outgoing or epoch != self.epoch: break
                        frame_epoch, frame = self.outgoing.popleft(); self.outgoing_bytes -= len(frame)
                    if frame_epoch == epoch: stream.sendall(frame)
                for _ in range(64):
                    frame = reader.take()
                    if frame is None: break
                    message = decode(frame, peer.device_id)
                    if message["type"] == "device.hello": raise ValueError("Unexpected second hello.")
                    with self.lock:
                        if self.socket is not stream: return
                        self.inbox.put(epoch, "message", SessionMessage(message, reader.last_generation, token), len(frame))
                if b"\n" in reader.buffer: continue
                with self.lock: pending = bool(self.outgoing)
                if stream.pending(): readable = [stream]
                else: readable, _, _ = select.select([stream, wake[0]], [], [], 0 if pending else 0.5)
                if wake[0] in readable:
                    try: wake[0].recv(4096)
                    except BlockingIOError: pass
                if stream in readable:
                    generation = self.generation()
                    try: data = stream.recv(min(16384, 256 * 1024 + 1 - len(reader.buffer)))
                    except socket.timeout: continue  # TLS control records need not contain application data.
                    if not data: raise EOFError("Peer disconnected.")
                    reader.feed(data, generation)
        except Exception as error:
            # Never log frames, keys, clipboard, peer addresses or certificate data.
            reason = "Connection ended. Reconnect and enable input again."
            if isinstance(error, ValueError):
                reason = "Invalid or unapproved session. Sharing stopped."
            with self.lock:
                if epoch == self.epoch and self.socket is stream:
                    # The other peer may close the losing socket just before our
                    # preferred handshake finishes. Release ownership immediately,
                    # while allowing that authenticated replacement to complete.
                    kind = "interrupted" if isinstance(error, (OSError, EOFError)) and \
                           self.listener is not None and self.outbound_pending and not self.preferred and \
                           not cancel.is_set() else "disconnected"
                    self.inbox.terminal(epoch, reason, kind)
        finally:
            if stream is not None:
                stream.close()
            for descriptor in wake: descriptor.close()
            with self.lock:
                if epoch == self.epoch and self.socket is stream:
                    self.socket = None
                    self.wake = None
                    self.outgoing.clear(); self.outgoing_bytes = 0

    def send(self, kind, payload, epoch=None, connection_generation=None):
        frame = encode(self.identity.card.device_id, kind, payload)
        with self.lock:
            if epoch is None: epoch = self.epoch
            if epoch != self.epoch or self.socket is None or self.cancel.is_set(): return False
            if connection_generation is not None and connection_generation != self.session_token: return False
            overflow = len(self.outgoing) >= 512 or self.outgoing_bytes + len(frame) > 1048576
            if not overflow:
                self.outgoing.append((epoch, frame)); self.outgoing_bytes += len(frame)
                wake = self.wake
        if overflow:
            self.stop(epoch)
            raise ValueError("Outbound queue overflow; sharing stopped.")
        if wake is not None:
            try: wake.send(b"x")
            except OSError: pass
        return True

    def stop(self, expected_epoch=None):
        with self.lock:
            if expected_epoch is not None and expected_epoch != self.epoch: return
            self.cancel.set()
            stream, self.socket = self.socket, None
            listener, self.listener = self.listener, None
            wake, self.wake = self.wake, None
            self.bound_address = None; self.outgoing.clear(); self.outgoing_bytes = 0
            # Increment even on failure so old workers cannot revive the session.
            self.epoch += 1
        if wake is not None:
            try: wake.send(b"x")
            except OSError: pass
        if listener is not None: listener.close()
        if stream is not None:
            try:
                stream.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            stream.close()


class SessionBounds(Bounds):
    def __init__(self, bounds, generation):
        super().__init__(bounds.left, bounds.top, bounds.width, bounds.height)
        object.__setattr__(self, "connection_generation", generation)


class SessionMessage(dict):
    """Local-only arrival generation. Never serialized as a wire field."""
    def __init__(self, message, generation, connection_generation=0):
        super().__init__(message); self.input_generation = generation
        self.connection_generation = connection_generation
