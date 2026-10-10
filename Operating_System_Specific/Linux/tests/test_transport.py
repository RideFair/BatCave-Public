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
