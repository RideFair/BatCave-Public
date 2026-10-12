"""Public-only nearby bootstrap. This module has no input dispatch path."""
import base64
import hashlib
import hmac
import json
import secrets
import select
import socket
import struct
import threading
import time
import unicodedata
from dataclasses import dataclass

from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec, padding, rsa

from .identity import Card, strict_json

MAX_FRAME = 16384
CARD_FIELDS = {"schema", "deviceId", "displayName", "certificateDerBase64", "certificateSha256"}


def pack(*parts):
    return b"".join(struct.pack("!I", len(part)) + part for part in parts)


def label(value):
    if (not isinstance(value, str) or not 1 <= len(value) <= 128 or value.isspace()
            or any(unicodedata.category(c) in {"Cc", "Cf", "Cs", "Zl", "Zp"} for c in value)
            or len(value.encode("utf-8", "strict")) > 512):
        raise ValueError("Invalid nearby computer name or identity.")
    return value


def port(value):
    if type(value) is not int or not 1 <= value <= 65535:
        raise ValueError("Invalid nearby computer port.")
    return value


def hex_bytes(value, size, lower=False):
    allowed = "0123456789abcdef" if lower else "0123456789ABCDEF"
    if not isinstance(value, str) or len(value) != size * 2 or any(c not in allowed for c in value):
        raise ValueError("Invalid pairing encoding.")
    return bytes.fromhex(value)


def parse(data, limit=MAX_FRAME):
    if not isinstance(data, bytes) or len(data) > limit or b"\r" in data:
        raise ValueError("Invalid pairing frame.")
    # Bound depth before json.loads, including malformed deeply nested input.
    depth = 0; quoted = False; escaped = False
    for byte in data:
        if quoted:
            if escaped: escaped = False
            elif byte == 92: escaped = True
            elif byte == 34: quoted = False
        elif byte == 34: quoted = True
        elif byte in (123, 91):
            depth += 1
            if depth > 8: raise ValueError("Pairing JSON is too deeply nested.")
        elif byte in (125, 93): depth -= 1
    try:
        value = strict_json(data.decode("utf-8", "strict"))
    except (ValueError, UnicodeError, RecursionError) as error:
        raise ValueError("Invalid pairing JSON.") from error
    if not isinstance(value, dict): raise ValueError("Expected a pairing object.")
    def objects(item):
        if isinstance(item, list): raise ValueError("Unexpected pairing array.")
        if isinstance(item, dict):
            for child in item.values(): objects(child)
    objects(value)
    return value


def fields(value, allowed):
    if not isinstance(value, dict) or set(value) != set(allowed):
        raise ValueError("Unexpected pairing fields.")


@dataclass(frozen=True)
class Descriptor:
    card: Card
    input_port: int

    def validate(self):
        label(self.card.device_id); label(self.card.display_name); port(self.input_port)
        Card.read(self.card.serialize())
        key = x509.load_der_x509_certificate(self.card.der).public_key()
        if isinstance(key, rsa.RSAPublicKey) and 3072 <= key.key_size <= 8192: return key
        if isinstance(key, ec.EllipticCurvePublicKey) and key.curve.name in {"secp256r1", "secp384r1"}: return key
        raise ValueError("Unsupported device identity key.")


def identity_bytes(session, role, descriptor, nonce):
    if role not in {"i", "r"} or len(nonce) != 32: raise ValueError("Invalid pairing contribution.")
    descriptor.validate()
    return pack(b"batcave.pair.v1.identity", hex_bytes(session, 16, lower=True), role.encode(),
                descriptor.card.device_id.encode("utf-8"), descriptor.card.display_name.encode("utf-8"),
                descriptor.card.der, str(descriptor.input_port).encode("ascii"), nonce)


def transcript(initiator, responder):
    return hashlib.sha256(pack(b"batcave.pair.v1.transcript", initiator, responder)).digest()


def code(value):
    if len(value) != 32: raise ValueError("Invalid pairing transcript.")
    digest = hashlib.sha256(pack(b"batcave.pair.v1.code", value)).digest()
    digits = f"{int.from_bytes(digest[:4], 'big') % 100000000:08d}"
    return digits[:4] + " " + digits[4:]


def signature_message(value, role, stage):
    if len(value) != 32 or role not in {"i", "r"} or stage not in {"proof", "approve"}:
        raise ValueError("Invalid pairing signature context.")
    return pack(("batcave.pair.v1." + stage).encode(), value, role.encode())


def sign(key, message):
    if isinstance(key, rsa.RSAPrivateKey): return key.sign(message, padding.PKCS1v15(), hashes.SHA256())
    if isinstance(key, ec.EllipticCurvePrivateKey): return key.sign(message, ec.ECDSA(hashes.SHA256()))
    raise ValueError("Device signing key is unavailable.")


def verify(descriptor, message, signature):
    key = descriptor.validate()
    if isinstance(key, rsa.RSAPublicKey): key.verify(signature, message, padding.PKCS1v15(), hashes.SHA256())
    else: key.verify(signature, message, ec.ECDSA(hashes.SHA256()))


class Decision:
    """One immutable session's local choice, never a reusable consent flag."""
    def __init__(self):
        self.lock = threading.Lock(); self.value = None; self.cancelled = False

    def choose(self, value):
        if type(value) is not bool: raise ValueError("Approval must be explicit.")
        with self.lock:
            if self.cancelled or self.value is not None: return False
            self.value = value
            return True

    def close(self):
        with self.lock: self.cancelled = True

    def state(self):
        with self.lock: return self.value, self.cancelled


@dataclass(frozen=True)
class Confirmation:
    peer: Descriptor
    code: str
    initiator: bool
    session: str


class Frames:
    def __init__(self, stream, cancelled):
        self.stream = stream; self.cancelled = cancelled; self.buffer = bytearray()

    def check(self, deadline):
        if self.cancelled.is_set(): raise InterruptedError("Pairing cancelled.")
        if time.monotonic() >= deadline: raise TimeoutError("Pairing expired. Search again to retry.")

    def write(self, value, deadline):
        data = json.dumps(value, ensure_ascii=True, separators=(",", ":"), allow_nan=False).encode()
        if len(data) > MAX_FRAME: raise ValueError("Pairing frame exceeds its limit.")
        view = memoryview(data + b"\n")
        while view:
            self.check(deadline)
            try:
                sent = self.stream.send(view)
                if sent == 0: raise EOFError("Pairing disconnected.")
                view = view[sent:]
            except (BlockingIOError, socket.timeout): select.select([], [self.stream], [], 0.05)

    def poll(self, deadline, wait=0.05):
        self.check(deadline)
        if b"\n" in self.buffer:
            index = self.buffer.index(10)
            if index > MAX_FRAME: raise ValueError("Pairing frame exceeds its limit.")
            data = bytes(self.buffer[:index]); del self.buffer[:index + 1]
            return parse(data)
        if len(self.buffer) > MAX_FRAME: raise ValueError("Pairing frame exceeds its limit.")
        if not select.select([self.stream], [], [], wait)[0]: return None
        try: data = self.stream.recv(min(1024, MAX_FRAME + 1 - len(self.buffer)))
        except (BlockingIOError, socket.timeout): return None
        if not data: raise EOFError("Pairing disconnected. Search again to retry.")
        self.buffer.extend(data)
        if b"\r" in data: raise ValueError("Carriage returns are forbidden in pairing frames.")
        return self.poll(deadline, wait=0)

    def read(self, deadline):
        while True:
            frame = self.poll(deadline)
            if frame is not None: return frame


def run(stream, identity, local, initiator, confirm, accept_peer, cancelled, expected=None,
        handshake_seconds=10, lifetime_seconds=120):
    """Run on a worker. confirm returns a Decision; no GUI is touched here.

    Durations are injectable only for isolated expiry tests, never network fields.
    The caller closes the socket and persists trust only after a successful return.
    """
    local_key = local.validate()
    if (local.card != identity.card or identity.key.public_key().public_bytes(serialization.Encoding.DER,
            serialization.PublicFormat.SubjectPublicKeyInfo) != local_key.public_bytes(serialization.Encoding.DER,
            serialization.PublicFormat.SubjectPublicKeyInfo)):
        raise ValueError("Local pairing identity mismatch.")
    stream.setblocking(False)
    started = time.monotonic(); lifetime = started + lifetime_seconds
    handshake = min(lifetime, started + handshake_seconds)
    frames = Frames(stream, cancelled)
    session = secrets.token_hex(16) if initiator else None
    role, other = ("i", "r") if initiator else ("r", "i")

    def read(kind, deadline, *extra):
        value = frames.read(deadline)
        check(value, kind, *extra)
        return value

    def check(value, kind, *extra):
        fields(value, {"type", "version", "session", *extra})
        if value["type"] != kind or type(value["version"]) is not int or value["version"] != 1 or value["session"] != session:
            raise ValueError("Unexpected pairing message.")

    def write(kind, deadline, **extra):
        frames.write(dict(type=kind, version=1, session=session, **extra), deadline)

    if not initiator:
        first = frames.read(handshake)
        fields(first, {"type", "version", "session", "commitment"})
        session = first["session"]; hex_bytes(session, 16, lower=True)
        check(first, "pair.commit", "commitment")
        remote_commitment = hex_bytes(first["commitment"], 32)
    nonce = secrets.token_bytes(32)
    local_bytes = identity_bytes(session, role, local, nonce)
    write("pair.commit", handshake, commitment=hashlib.sha256(local_bytes).hexdigest().upper())

    def reveal():
        write("pair.reveal", handshake, nonce=nonce.hex().upper(), card=local.card.dictionary(), inputPort=local.input_port)

    if initiator:
        remote_commitment = hex_bytes(read("pair.commit", handshake, "commitment")["commitment"], 32)
        reveal()
    value = read("pair.reveal", handshake, "nonce", "card", "inputPort")
    fields(value["card"], CARD_FIELDS)
    peer = Descriptor(Card.read(json.dumps(value["card"]).encode()), port(value["inputPort"]))
    peer_bytes = identity_bytes(session, other, peer, hex_bytes(value["nonce"], 32))
    if not hmac.compare_digest(remote_commitment, hashlib.sha256(peer_bytes).digest()):
        raise ValueError("Pairing commitment changed.")
    if (peer.card.device_id == local.card.device_id or peer.card.fingerprint == local.card.fingerprint
            or expected is not None and (peer.card.device_id != expected.device_id or peer.card.fingerprint != expected.fingerprint)
            or not accept_peer(peer)):
        raise ValueError("The computer's identity changed. Cancel and choose it again.")
    if not initiator: reveal()
    value = transcript(local_bytes, peer_bytes) if initiator else transcript(peer_bytes, local_bytes)

    def send_signature(stage, deadline):
        write("pair." + stage, deadline, signature=base64.b64encode(sign(identity.key, signature_message(value, role, stage))).decode())

    def check_signature(frame, stage):
        check(frame, "pair." + stage, "signature")
        encoded = frame["signature"]
        if not isinstance(encoded, str) or len(encoded) > 1400: raise ValueError("Invalid pairing signature size.")
        signature = base64.b64decode(encoded, validate=True)
        if base64.b64encode(signature).decode() != encoded: raise ValueError("Invalid pairing signature encoding.")
        verify(peer, signature_message(value, other, stage), signature)

    send_signature("proof", handshake)
    check_signature(read("pair.proof", handshake, "signature"), "proof")
    decision = confirm(Confirmation(peer, code(value), initiator, session))
    if not isinstance(decision, Decision): raise ValueError("Missing local approval decision.")
    sent = received = False
    try:
        while True:
            frames.check(lifetime)
            choice, closed = decision.state()
            if closed or choice is False: raise InterruptedError("Pairing cancelled.")
            if choice is True and not sent:
                if not accept_peer(peer): raise ValueError("Approval changed during pairing.")
                send_signature("approve", lifetime); sent = True
            if sent and received:
                if frames.buffer: raise ValueError("Unexpected data after pairing approval.")
                if not accept_peer(peer): raise ValueError("Approval changed during pairing.")
                peer.validate(); frames.check(lifetime)
                return peer
            frame = frames.poll(lifetime)
            if frame is not None:
                if received: raise ValueError("Duplicate pairing approval.")
                check_signature(frame, "approve"); received = True
    finally:
        decision.close()
