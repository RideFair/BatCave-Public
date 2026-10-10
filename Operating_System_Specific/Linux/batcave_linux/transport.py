"""Client-only TLS connection; queue overflow invalidates the entire session."""

import collections
import ipaddress
import socket
import threading
import time

from .identity import tls_context, verify_peer
from .protocol import FrameReader, decode, hello, read_hello


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

    def terminal(self, epoch, reason):
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
            self.items.append((epoch, "disconnected", reason, 0))

    def drain(self, count=64):
        with self.lock:
            result = []
            while self.items and len(result) < count:
                epoch, kind, value, size = self.items.popleft()
                self.bytes -= size
                result.append((epoch, kind, value))
            return result


class Connection:
    def __init__(self, identity, inbox):
        self.identity = identity
        self.inbox = inbox
        self.epoch = 0
        self.socket = None
        self.cancel = threading.Event()
        self.lock = threading.Lock()

    def connect(self, host, peer, bounds, port=47871):
        address = ipaddress.ip_address(host)
        if address.is_unspecified or address.is_multicast:
            raise ValueError("Enter the other computer's local IP address.")
        if not 1 <= port <= 65535 or type(port) is not int:
            raise ValueError("Invalid port.")
        self.stop()
        self.epoch += 1
        epoch = self.epoch
        cancel = threading.Event()
        self.cancel = cancel
        worker = threading.Thread(target=self._run, args=(epoch, cancel, str(address), port, peer, bounds), daemon=True)
        worker.start()
        return epoch

    def _run(self, epoch, cancel, host, port, peer, bounds):
        stream = None
        try:
            context = tls_context(self.identity, peer)
            raw = socket.create_connection((host, port), timeout=5)
            try:
                raw.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
                raw.setsockopt(socket.SOL_SOCKET, socket.SO_KEEPALIVE, 1)
                for option, value in ((socket.TCP_KEEPIDLE, 5), (socket.TCP_KEEPINTVL, 1), (socket.TCP_KEEPCNT, 5)):
                    raw.setsockopt(socket.IPPROTO_TCP, option, value)
                stream = context.wrap_socket(raw, server_hostname="batcave.local")
            except BaseException:
                raw.close()
                raise
            verify_peer(stream, peer)
            with self.lock:
                if cancel.is_set() or epoch != self.epoch:
                    return
                self.socket = stream
            stream.sendall(hello(self.identity.card, bounds))
            stream.settimeout(0.5)
            reader = FrameReader()
            deadline = time.monotonic() + 5
            frame = reader.read(stream, lambda: cancel.is_set() or time.monotonic() > deadline)
            message = decode(frame, peer.device_id)
            remote_bounds = read_hello(message, peer.device_id)
            self.inbox.put(epoch, "connected", remote_bounds)
            while not cancel.is_set():
                frame = reader.read(stream, cancel.is_set)
                message = decode(frame, peer.device_id)
                if message["type"] == "device.hello":
                    raise ValueError("Unexpected second hello.")
                self.inbox.put(epoch, "message", message, len(frame))
        except Exception as error:
            # Never log frames, keys, clipboard, peer addresses or certificate data.
            reason = "Connection ended. Reconnect and enable input again."
            if isinstance(error, ValueError):
                reason = "Invalid or unapproved session. Sharing stopped."
            if epoch == self.epoch:
                self.inbox.terminal(epoch, reason)
        finally:
            if stream is not None:
                stream.close()
            with self.lock:
                if epoch == self.epoch:
                    self.socket = None

    def stop(self):
        self.cancel.set()
        with self.lock:
            stream, self.socket = self.socket, None
        if stream is not None:
            try:
                stream.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            stream.close()
        # Increment even on a failed connection so late callbacks cannot revive it.
        self.epoch += 1
