import base64
import hashlib
import json
import socket
import tempfile
import threading
import time
import unittest
from pathlib import Path

from cryptography.exceptions import InvalidSignature

from batcave_linux.identity import Card, Identity
from batcave_linux.storage import StateDirectory
from batcave_linux.pairing import (Confirmation, Decision, Descriptor, Frames, code, identity_bytes,
                                  parse, run, signature_message, transcript, verify)
from test_core import MemoryVault


class PairingTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.states = [StateDirectory(Path(self.temporary.name) / str(i)) for i in range(2)]
        self.identities = [Identity.open(state, MemoryVault(), "Test computer " + str(i)) for i, state in enumerate(self.states)]
        self.descriptors = [Descriptor(identity.card, 47870 + i) for i, identity in enumerate(self.identities)]
        self.sockets = []; self.workers = []

    def tearDown(self):
        for stream in self.sockets: stream.close()
        for worker in self.workers: worker.join(2)
        self.assertTrue(all(not worker.is_alive() for worker in self.workers))
        for state in self.states: state.close()
        self.temporary.cleanup()

    def start(self, index, stream, results, confirm, **kwargs):
        def worker():
            try:
                results[index] = run(stream, self.identities[index], self.descriptors[index], index == 0,
                                     confirm, kwargs.pop("accept_peer", lambda _peer: True),
                                     kwargs.pop("cancelled", threading.Event()), **kwargs)
            except Exception as error: results[index] = error
            finally: stream.close()
        thread = threading.Thread(target=worker, daemon=True); self.workers.append(thread); thread.start()

    def pair(self):
        streams = socket.socketpair(); self.sockets.extend(streams)
        return streams

    def test_shared_vectors_and_separate_signature_domains(self):
        path = Path(__file__).resolve().parents[3] / "Universal/Protocol/pairing-vectors.json"
        vectors = json.loads(path.read_text())
        descriptors = [Descriptor(Card.read(json.dumps(vectors[key]["card"]).encode()), vectors[key]["inputPort"])
                       for key in ("initiator", "responder")]
        contributions = [identity_bytes(vectors["session"], role, descriptor, bytes.fromhex(vectors[key + "Nonce"]))
                         for role, key, descriptor in zip(("i", "r"), ("initiator", "responder"), descriptors)]
        for key, contribution in zip(("initiator", "responder"), contributions):
            self.assertEqual(hashlib.sha256(contribution).hexdigest().upper(), vectors[key + "Commitment"])
        value = transcript(*contributions)
        self.assertEqual(value.hex().upper(), vectors["transcript"])
        self.assertEqual(code(value), vectors["code"])
        for role, descriptor in zip(("i", "r"), descriptors):
            for stage in ("proof", "approve"):
                signature = base64.b64decode(vectors["signatures"][role + "-" + stage], validate=True)
                verify(descriptor, signature_message(value, role, stage), signature)
                with self.assertRaises(InvalidSignature):
                    verify(descriptor, signature_message(value, role, "approve" if stage == "proof" else "proof"), signature)
                with self.assertRaises(InvalidSignature):
                    verify(descriptor, signature_message(value, "r" if role == "i" else "i", stage), signature)
                with self.assertRaises(InvalidSignature):
                    verify(descriptor, signature_message(bytes(32), role, stage), signature)

    def test_two_local_approvals_have_same_code_and_descriptor(self):
        streams = self.pair(); results = {}; prompts = []
        def confirm(value):
            self.assertIsInstance(value, Confirmation); prompts.append(value)
            choice = Decision(); choice.choose(True); return choice
        for i, stream in enumerate(streams): self.start(i, stream, results, confirm)
        for worker in self.workers: worker.join(3)
        self.assertEqual(results, {0: self.descriptors[1], 1: self.descriptors[0]})
        self.assertEqual(len(prompts), 2); self.assertEqual(prompts[0].code, prompts[1].code)
        self.assertEqual(prompts[0].session, prompts[1].session)
        self.assertEqual(len(prompts[0].code), 9)

    def test_one_approval_does_not_complete_other_and_disconnect_invalidates(self):
        streams = self.pair(); results = {}; decision = Decision(); prompt = threading.Event()
        def pending(_value): prompt.set(); return decision
        def approved(_value): choice = Decision(); choice.choose(True); return choice
        self.start(0, streams[0], results, approved)
        self.start(1, streams[1], results, pending)
        self.assertTrue(prompt.wait(2)); time.sleep(0.1); self.assertEqual(results, {})
        streams[0].shutdown(socket.SHUT_RDWR)
        for worker in self.workers: worker.join(2)
        self.assertTrue(all(isinstance(item, Exception) for item in results.values()))
        self.assertTrue(decision.state()[1]); self.assertFalse(decision.choose(True))

    def test_decline_and_expiry_invalidate_session_without_approval(self):
        for decline in (True, False):
            with self.subTest(decline=decline):
                streams = self.pair(); results = {}; decisions = []
                def confirm(_value):
                    choice = Decision(); decisions.append(choice)
                    if decline: choice.choose(False)
                    return choice
                before = len(self.workers)
                for i, stream in enumerate(streams): self.start(i, stream, results, confirm, lifetime_seconds=0.2)
                for worker in self.workers[before:]: worker.join(2)
                self.assertEqual(len(results), 2)
                self.assertTrue(all(isinstance(item, Exception) for item in results.values()))
                self.assertTrue(all(choice.state()[1] and not choice.choose(True) for choice in decisions))

    def test_cancel_and_changed_pin_abort_before_trust(self):
        streams = self.pair(); results = {}; cancellation = threading.Event(); entered = threading.Event()
        def confirm(_value): entered.set(); cancellation.set(); return Decision()
        self.start(0, streams[0], results, confirm, cancelled=cancellation)
        self.start(1, streams[1], results, lambda _value: Decision())
        self.assertTrue(entered.wait(2))
        for worker in self.workers: worker.join(2)
        self.assertTrue(all(isinstance(item, Exception) for item in results.values()))
        streams = self.pair(); results = {}; entered.clear()
        for i, stream in enumerate(streams):
            self.start(i, stream, results, lambda _value: entered.set(), accept_peer=lambda _peer: False)
        for worker in self.workers[-2:]: worker.join(2)
        self.assertFalse(entered.is_set()); self.assertEqual(len(results), 2)

    def test_selected_discovery_identity_cannot_be_substituted(self):
        streams = self.pair(); results = {}; prompted = threading.Event()
        self.start(0, streams[0], results, lambda _value: prompted.set(), expected=self.identities[0].card)
        self.start(1, streams[1], results, lambda _value: Decision())
        for worker in self.workers: worker.join(2)
        self.assertFalse(prompted.is_set()); self.assertTrue(all(isinstance(item, Exception) for item in results.values()))

    def test_reveal_before_commit_and_input_are_rejected(self):
        for kind in ("pair.reveal", "input"):
            streams = self.pair(); results = {}
            self.start(1, streams[1], results, lambda _value: self.fail("Prompt before proof"))
            streams[0].sendall(json.dumps(dict(type=kind, version=1, session="00" * 16, commitment="00" * 32)).encode() + b"\n")
            self.workers[-1].join(2)
            self.assertIsInstance(results[1], ValueError)

    def test_malformed_frames_are_bounded_and_strict(self):
        for data in (b'{"a":1,"a":2}', b'{"a":{"x":1,"x":2}}', b'{"a":[]} ', b'{}\r',
                     b'{"a":NaN}', b'\xff', b'[' + b'[' * 5000, b' ' * 16385, b'null'):
            with self.subTest(data=data[:30]), self.assertRaises(ValueError): parse(data)
        self.assertEqual(parse(b'{"a":"{[\\\"test"}'), {"a": '{["test'})
        streams = self.pair(); streams[1].setblocking(False)
        streams[0].sendall(b"x" * 16385)
        with self.assertRaises(ValueError): Frames(streams[1], threading.Event()).read(time.monotonic() + 1)

    def test_contribution_cannot_change_identity_port_or_nonce(self):
        descriptor = self.descriptors[0]; session = "00" * 16; nonce = bytes(32)
        original = identity_bytes(session, "i", descriptor, nonce)
        variants = [identity_bytes("01" * 16, "i", descriptor, nonce), identity_bytes(session, "r", descriptor, nonce),
                    identity_bytes(session, "i", Descriptor(descriptor.card, 47872), nonce),
                    identity_bytes(session, "i", descriptor, bytes([1]) * 32),
                    identity_bytes(session, "i", self.descriptors[1], nonce)]
        self.assertTrue(all(hashlib.sha256(item).digest() != hashlib.sha256(original).digest() for item in variants))


if __name__ == "__main__": unittest.main()
