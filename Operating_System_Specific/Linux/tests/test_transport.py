"""Real socket failures must never authorize input or downgrade encryption."""
import socket
import ssl
import tempfile
import threading
import time
import unittest

from batcave_linux.identity import Identity, tls_context
from batcave_linux.protocol import Bounds, FrameReader, hello
from batcave_linux.storage import StateDirectory
from batcave_linux.transport import Connection, Inbox
from test_core import MemoryVault


class TransportTests(unittest.TestCase):
    def test_activation_and_dispatch_are_atomic_with_connection_stop(self):
        with tempfile.TemporaryDirectory() as a, tempfile.TemporaryDirectory() as b, StateDirectory(a) as sa, StateDirectory(b) as sb:
            first = Identity.open(sa, MemoryVault(), "Source fixture")
            second = Identity.open(sb, MemoryVault(), "Receiver fixture")
            incoming = Inbox(); receiver = Connection(second, incoming); sender = Connection(first, Inbox())
            release = threading.Event(); entered = threading.Event(); attempted = threading.Event(); stopped = threading.Event()
            workers = []; dispatched = []
            try:
                epoch = receiver.listen(first.card, Bounds(0, 0, 800, 600), "127.0.0.1", 0)
                sender.connect("127.0.0.1", second.card, Bounds(0, 0, 800, 600), receiver.bound_address[1])
                self.await_events(incoming, "connected"); token = receiver.session_token
                self.assertFalse(receiver.current(epoch, token + 1, lambda: dispatched.append("stale")))
                receiver.approved = lambda _peer: False
                self.assertFalse(receiver.current(epoch, token, lambda: dispatched.append("revoked")))
                receiver.approved = lambda _peer: True
                def action():
                    entered.set(); release.wait(2)
                    # Reentrant send is needed by source return-handoff dispatch.
                    self.assertTrue(receiver.send("control.testStop", {}, epoch, connection_generation=token))
                    dispatched.append(token)
                dispatcher = threading.Thread(target=lambda: receiver.current(epoch, token, action), daemon=True)
                def stop(): attempted.set(); receiver.stop(); stopped.set()
                stopper = threading.Thread(target=stop, daemon=True); workers = [dispatcher, stopper]
                dispatcher.start(); self.assertTrue(entered.wait(2)); stopper.start(); self.assertTrue(attempted.wait(2))
                self.assertFalse(stopped.wait(0.05)); release.set()
                for worker in workers: worker.join(2)
                self.assertTrue(stopped.is_set()); self.assertEqual(dispatched, [token])
                self.assertFalse(receiver.current(epoch, token, lambda: dispatched.append("late")))
            finally:
                release.set()
                for worker in workers: worker.join(2)
                receiver.stop(); sender.stop()

    @staticmethod
    def await_events(inbox, kind, timeout=5):
        events = []; deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            events.extend(inbox.drain())
            if any(event[1] == kind for event in events): return events
            time.sleep(0.005)
        raise AssertionError('Expected transport event: ' + kind)

    def test_linux_listener_and_connector_bidirectional_and_fresh_reconnect(self):
        with tempfile.TemporaryDirectory() as first_dir, tempfile.TemporaryDirectory() as second_dir, \
                StateDirectory(first_dir) as first_state, StateDirectory(second_dir) as second_state:
            first = Identity.open(first_state, MemoryVault(), 'Linux source')
            second = Identity.open(second_state, MemoryVault(), 'Linux receiver')
            first_inbox, second_inbox = Inbox(), Inbox()
            source, receiver = Connection(first, first_inbox), Connection(second, second_inbox)
            try:
                for _ in range(2):
                    receive_epoch = receiver.listen(first.card, Bounds(0, 0, 1920, 1080), '127.0.0.1', 0)
                    port = receiver.bound_address[1]
                    source_epoch = source.connect('127.0.0.1', second.card, Bounds(0, 0, 1600, 900), port)
                    self.assertTrue(any(event[:2] == (source_epoch, 'connected') for event in self.await_events(first_inbox, 'connected')))
                    self.assertTrue(any(event[:2] == (receive_epoch, 'connected') for event in self.await_events(second_inbox, 'connected')))
                    self.assertTrue(source.send('input.mouseDelta', {'deltaX': 4, 'deltaY': -3}, source_epoch))
                    messages = self.await_events(second_inbox, 'message')
                    message = next(value for _, kind, value in messages if kind == 'message')
                    self.assertEqual(message['sourceDeviceId'], first.card.device_id)
                    self.assertEqual(message['payload'], {'deltaX': 4, 'deltaY': -3})
                    self.assertTrue(receiver.send('control.testStop', {'reason': 'Local pause'}, receive_epoch))
                    message = next(value for _, kind, value in self.await_events(first_inbox, 'message') if kind == 'message')
                    self.assertEqual(message['type'], 'control.testStop')
                    self.assertEqual(message['sourceDeviceId'], second.card.device_id)
                    source.stop()
                    self.assertFalse(source.send('input.key', {}, source_epoch))
                    self.await_events(second_inbox, 'disconnected')
                    receiver.stop()
                # The same approved pair may reverse which computer accepts the socket.
                source_epoch = source.listen(second.card, Bounds(0, 0, 1600, 900), '127.0.0.1', 0)
                receive_epoch = receiver.connect('127.0.0.1', first.card, Bounds(0, 0, 1920, 1080), source.bound_address[1])
                self.await_events(first_inbox, 'connected'); self.await_events(second_inbox, 'connected')
                self.assertTrue(receiver.send('control.testStop', {'reason': 'Local pause'}, receive_epoch))
                self.await_events(first_inbox, 'message')
            finally:
                source.stop(); receiver.stop()

    def test_listener_rejects_unapproved_identity_without_losing_wait_state(self):
        with tempfile.TemporaryDirectory() as first_dir, tempfile.TemporaryDirectory() as second_dir, \
                tempfile.TemporaryDirectory() as outsider_dir, StateDirectory(first_dir) as first_state, \
                StateDirectory(second_dir) as second_state, StateDirectory(outsider_dir) as outsider_state:
            first = Identity.open(first_state, MemoryVault())
            second = Identity.open(second_state, MemoryVault())
            outsider = Identity.open(outsider_state, MemoryVault())
            first_inbox, second_inbox, outsider_inbox = Inbox(), Inbox(), Inbox()
            source, receiver, unknown = Connection(first, first_inbox), Connection(second, second_inbox), Connection(outsider, outsider_inbox)
            try:
                epoch = receiver.listen(first.card, Bounds(0, 0, 100, 100), '127.0.0.1', 0)
                port = receiver.bound_address[1]
                unknown.connect('127.0.0.1', second.card, Bounds(0, 0, 100, 100), port)
                self.await_events(outsider_inbox, 'disconnected')
                self.assertFalse(any(event[1] == 'connected' for event in second_inbox.drain()))
                self.assertEqual(receiver.epoch, epoch)
                source.connect('127.0.0.1', second.card, Bounds(0, 0, 100, 100), port)
                self.await_events(first_inbox, 'connected'); self.await_events(second_inbox, 'connected')
            finally:
                source.stop(); receiver.stop(); unknown.stop()

    def test_repeated_simultaneous_outgoing_connections_converge_without_old_socket_replay(self):
        with tempfile.TemporaryDirectory() as first_dir, tempfile.TemporaryDirectory() as second_dir, \
                StateDirectory(first_dir) as first_state, StateDirectory(second_dir) as second_state:
            first = Identity.open(first_state, MemoryVault()); second = Identity.open(second_state, MemoryVault())
            inbox_a, inbox_b = Inbox(), Inbox()
            a, b = Connection(first, inbox_a), Connection(second, inbox_b)
            try:
                for _ in range(8):
                    # Bind both before starting either outgoing attempt.
                    epoch_a = a.listen(second.card, Bounds(0, 0, 1000, 800), "127.0.0.1", 0)
                    epoch_b = b.listen(first.card, Bounds(0, 0, 1000, 800), "127.0.0.1", 0)
                    args_a = (epoch_a, a.cancel, "127.0.0.1", b.bound_address[1], second.card, Bounds(0, 0, 1000, 800))
                    args_b = (epoch_b, b.cancel, "127.0.0.1", a.bound_address[1], first.card, Bounds(0, 0, 1000, 800))
                    threads = [threading.Thread(target=a._outbound, args=args_a), threading.Thread(target=b._outbound, args=args_b)]
                    for worker in threads: worker.start()
                    for worker in threads: worker.join(3); self.assertFalse(worker.is_alive())
                    deadline = time.monotonic() + 3
                    while time.monotonic() < deadline and not (a.socket and b.socket and a.preferred and b.preferred): time.sleep(0.005)
                    self.assertTrue(a.socket and b.socket and a.preferred and b.preferred)
                    # The preferred socket must still relay both ways after loser workers exit.
                    token_a, token_b = a.session_token, b.session_token
                    inbox_a.drain(512); inbox_b.drain(512)
                    self.assertFalse(a.send("input.mouseDelta", {"deltaX": 99, "deltaY": 99}, epoch_a, token_a - 1))
                    self.assertTrue(a.send("input.mouseDelta", {"deltaX": 4, "deltaY": -3}, epoch_a, token_a))
                    self.assertTrue(b.send("control.testStop", {"reason": "Pause"}, epoch_b, token_b))
                    events_a = self.await_events(inbox_a, "message"); events_b = self.await_events(inbox_b, "message")
                    self.assertFalse(any(kind == "disconnected" for _, kind, _ in events_a + events_b))
                    self.assertTrue(all(value.connection_generation == token_a for _, kind, value in events_a if kind == "message"))
                    self.assertTrue(all(value.connection_generation == token_b for _, kind, value in events_b if kind == "message"))
                    a.stop(); b.stop(); inbox_a.drain(512); inbox_b.drain(512)
            finally: a.stop(); b.stop()

    def test_losing_socket_closure_releases_ownership_without_canceling_delayed_preferred_handshake(self):
        with tempfile.TemporaryDirectory() as first_dir, tempfile.TemporaryDirectory() as second_dir, \
                StateDirectory(first_dir) as first_state, StateDirectory(second_dir) as second_state:
            identities = sorted((Identity.open(first_state, MemoryVault()), Identity.open(second_state, MemoryVault())),
                                key=lambda identity: identity.card.device_id)
            low, high = identities; inbox_low, inbox_high = Inbox(), Inbox()
            a, b = Connection(low, inbox_low), Connection(high, inbox_high)
            try:
                epoch_a = a.listen(high.card, Bounds(0, 0, 1000, 800), "127.0.0.1", 0)
                epoch_b = b.connect("127.0.0.1", low.card, Bounds(0, 0, 1000, 800), a.bound_address[1],
                                    listen_port=0, listen_host="127.0.0.1")
                self.await_events(inbox_low, "connected"); self.await_events(inbox_high, "connected")
                self.assertFalse(a.preferred or b.preferred)
                original = a._authenticate
                completed = threading.Event(); resume = threading.Event()
                def delayed(*args, **kwargs):
                    result = original(*args, **kwargs); completed.set()
                    if not resume.wait(2): raise RuntimeError("Test handshake did not resume")
                    return result
                a._authenticate = delayed
                worker = threading.Thread(target=a._outbound, args=(epoch_a, a.cancel, "127.0.0.1", b.bound_address[1], high.card, Bounds(0, 0, 1000, 800)))
                worker.start(); self.assertTrue(completed.wait(2))
                events = self.await_events(inbox_low, "interrupted")
                self.assertFalse(any(kind == "disconnected" for _, kind, _ in events))
                self.assertFalse(a.cancel.is_set())
                resume.set(); worker.join(2); self.assertFalse(worker.is_alive())
                self.await_events(inbox_low, "connected")
                self.assertTrue(a.preferred and b.preferred)
                self.assertTrue(a.send("control.testStop", {"reason": "Paused"}, epoch_a))
                self.await_events(inbox_high, "message")
            finally: a.stop(); b.stop()

    def test_plaintext_tls12_and_forged_hello_never_connect(self):
        for failure in ("plaintext", "tls12", "hello"):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as client_dir, \
                    tempfile.TemporaryDirectory() as host_dir, StateDirectory(client_dir) as client_state, StateDirectory(host_dir) as host_state:
                client = Identity.open(client_state, MemoryVault())
                host = Identity.open(host_state, MemoryVault())
                context = tls_context(host, client.card, server=True)
                if failure == "tls12": context.minimum_version = context.maximum_version = ssl.TLSVersion.TLSv1_2
                listener = socket.socket(); listener.bind(("127.0.0.1", 0)); listener.listen()
                port = listener.getsockname()[1]; outcomes = []
                def serve():
                    try:
                        raw, _ = listener.accept(); raw.settimeout(2)
                        if failure == "plaintext":
                            with raw:
                                raw.recv(16384); raw.sendall(b'{"protocol":"batcave.input.v0"}\n')
                        else:
                            try:
                                with context.wrap_socket(raw, server_side=True) as stream:
                                    FrameReader().read(stream)
                                    # Correct certificate and envelope; wrong hello payload identity.
                                    data = hello(host.card, Bounds(0, 0, 100, 100))
                                    import json
                                    value = json.loads(data); value["payload"]["deviceId"] = "forged"
                                    stream.sendall(json.dumps(value).encode() + b"\n")
                            finally: raw.close()
                    except (OSError, EOFError): pass
                    except BaseException as error: outcomes.append(error)
                    finally: listener.close()
                worker = threading.Thread(target=serve, daemon=True); worker.start()
                inbox = Inbox(); connection = Connection(client, inbox)
                epoch = connection.connect("127.0.0.1", host.card, Bounds(0, 0, 100, 100), port)
                events = []; deadline = time.monotonic() + 4
                while time.monotonic() < deadline and not any(e[1] == "disconnected" for e in events):
                    events.extend(inbox.drain()); time.sleep(0.01)
                connection.stop(); worker.join(3)
                self.assertFalse(worker.is_alive())
                self.assertEqual(outcomes, [])
                self.assertEqual([event[:2] for event in events], [(epoch, "disconnected")])


if __name__ == "__main__": unittest.main()
