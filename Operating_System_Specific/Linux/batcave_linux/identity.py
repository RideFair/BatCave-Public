"""Encrypted PKCS8 identity; its unlock secret belongs to the desktop keyring."""

import base64
import datetime as dt
import hashlib
import hmac
import json
import os
import re
import secrets
import ssl
import uuid
import unicodedata
from dataclasses import dataclass

from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec, rsa, padding
from cryptography.x509.oid import ExtendedKeyUsageOID, NameOID

CARD_SCHEMA = "batcave.pairing-card.v1"


def strict_json(data):
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise ValueError("Duplicate JSON property.")
            result[key] = value
        return result

    def invalid(_):
        raise ValueError("Non-finite JSON number.")

    return json.loads(data, object_pairs_hook=pairs, parse_constant=invalid)


def label(value):
    if not isinstance(value, str) or not 1 <= len(value) <= 128 or any(unicodedata.category(c) in {"Cc", "Cf", "Cs"} for c in value):
        raise ValueError("Invalid device name or ID.")
    return value


def certificate_current(cert):
    now = dt.datetime.now(dt.timezone.utc).replace(tzinfo=None)
    if not cert.not_valid_before <= now <= cert.not_valid_after:
        raise ValueError("The pairing certificate is expired or not yet valid.")


@dataclass(frozen=True)
class Card:
    device_id: str
    display_name: str
    der: bytes
    fingerprint: str

    @classmethod
    def read(cls, data, require_current=True):
        if isinstance(data, str):
            data = data.encode("utf-8")
        if len(data) > 16384:
            raise ValueError("Pairing card exceeds 16 KiB.")
        card = strict_json(data)
        if not isinstance(card, dict) or card.get("schema") != CARD_SCHEMA:
            raise ValueError("Unsupported pairing card.")
        device_id, display_name = label(card.get("deviceId")), label(card.get("displayName"))
        expected = card.get("certificateSha256")
        if not isinstance(expected, str) or not re.fullmatch(r"[a-fA-F0-9]{64}", expected):
            raise ValueError("Invalid certificate fingerprint.")
        der = base64.b64decode(card.get("certificateDerBase64", ""), validate=True)
        cert = x509.load_der_x509_certificate(der)
        if require_current:
            certificate_current(cert)
        fingerprint = hashlib.sha256(der).hexdigest().upper()
        if not hmac.compare_digest(fingerprint, expected.upper()):
            raise ValueError("Pairing certificate does not match its fingerprint.")
        # All agents currently use directly pinned, self-signed TLS leaf identities.
        if cert.issuer != cert.subject:
            raise ValueError("A self-signed device identity is required.")
        key = cert.public_key()
        if isinstance(key, rsa.RSAPublicKey) and 3072 <= key.key_size <= 8192:
            key.verify(cert.signature, cert.tbs_certificate_bytes, padding.PKCS1v15(), cert.signature_hash_algorithm)
        elif isinstance(key, ec.EllipticCurvePublicKey) and key.curve.name in {"secp256r1", "secp384r1"}:
            key.verify(cert.signature, cert.tbs_certificate_bytes, ec.ECDSA(cert.signature_hash_algorithm))
        else:
            raise ValueError("Unsupported or weak device identity key.")
        if not isinstance(cert.signature_hash_algorithm, (hashes.SHA256, hashes.SHA384, hashes.SHA512)):
            raise ValueError("Unsupported certificate signature algorithm.")
        eku = cert.extensions.get_extension_for_class(x509.ExtendedKeyUsage).value
        if ExtendedKeyUsageOID.SERVER_AUTH not in eku or ExtendedKeyUsageOID.CLIENT_AUTH not in eku:
            raise ValueError("Certificate is not valid for mutual TLS.")
        return cls(device_id, display_name, der, fingerprint)

    def dictionary(self):
        return dict(schema=CARD_SCHEMA, deviceId=self.device_id, displayName=self.display_name,
                    certificateDerBase64=base64.b64encode(self.der).decode(), certificateSha256=self.fingerprint)

    def serialize(self):
        return json.dumps(self.dictionary(), indent=2).encode()

    def pem(self):
        return x509.load_der_x509_certificate(self.der).public_bytes(serialization.Encoding.PEM)


class SecretVault:
    """No file, environment-variable or plaintext fallback if the keyring fails."""

    def __init__(self):
        import gi
        gi.require_version("Secret", "1")
        from gi.repository import Secret
        self.api = Secret
        self.schema = Secret.Schema.new("dev.batcave.Linux.SecurePreview", Secret.SchemaFlags.NONE,
                                        {"identity": Secret.SchemaAttributeType.STRING})

    def get(self, secret_id):
        return self.api.password_lookup_sync(self.schema, {"identity": secret_id}, None)

    def put(self, secret_id, value):
        if not self.api.password_store_sync(self.schema, {"identity": secret_id}, self.api.COLLECTION_DEFAULT,
                                            "BatCave identity", value, None):
            raise RuntimeError("The desktop keyring did not store the identity secret.")

    def remove(self, secret_id):
        self.api.password_clear_sync(self.schema, {"identity": secret_id}, None)


@dataclass
class Identity:
    card: Card
    key: object

    @classmethod
    def open(cls, state, vault, display_name="Linux computer"):
        data = state.read("identity.json", 16384)
        if data is not None:
            record = strict_json(data)
            card = Card.read(json.dumps(record["card"]).encode())
            secret = vault.get(record["secretId"])
            if not secret:
                raise RuntimeError("The identity unlock secret is unavailable. Unlock the keyring; identity was not replaced.")
            key = serialization.load_pem_private_key(base64.b64decode(record["encryptedKey"], validate=True), secret.encode())
            if key.public_key().public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo) != \
                    x509.load_der_x509_certificate(card.der).public_key().public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo):
                raise ValueError("Stored identity key and certificate do not match.")
            return cls(card, key)

        device_id, secret_id = "linux:" + uuid.uuid4().hex, uuid.uuid4().hex
        secret = secrets.token_urlsafe(48)
        key = ec.generate_private_key(ec.SECP256R1())
        name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "BatCave " + device_id)])
        now = dt.datetime.now(dt.timezone.utc)
        cert = (x509.CertificateBuilder().subject_name(name).issuer_name(name).public_key(key.public_key())
                .serial_number(x509.random_serial_number()).not_valid_before(now - dt.timedelta(minutes=5))
                .not_valid_after(now + dt.timedelta(days=1095))
                .add_extension(x509.BasicConstraints(ca=False, path_length=None), critical=True)
                .add_extension(x509.KeyUsage(digital_signature=True, content_commitment=False, key_encipherment=False,
                                           data_encipherment=False, key_agreement=False, key_cert_sign=False,
                                           crl_sign=False, encipher_only=False, decipher_only=False), critical=True)
                .add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.SERVER_AUTH, ExtendedKeyUsageOID.CLIENT_AUTH]), critical=False)
                .sign(key, hashes.SHA256()))
        der = cert.public_bytes(serialization.Encoding.DER)
        card = Card(device_id, label(display_name), der, hashlib.sha256(der).hexdigest().upper())
        encrypted = key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8,
                                      serialization.BestAvailableEncryption(secret.encode()))
        vault.put(secret_id, secret)
        try:
            state.write("identity.json", {"card": card.dictionary(), "secretId": secret_id,
                                          "encryptedKey": base64.b64encode(encrypted).decode()}, replace=False)
        except BaseException:
            vault.remove(secret_id)
            raise
        return cls(card, key)

    def configure(self, context):
        """OpenSSL loads the key from anonymous RAM, never a plaintext disk file."""
        key = self.key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption())
        fd = os.memfd_create("batcave-tls-identity", os.MFD_CLOEXEC)
        try:
            with os.fdopen(os.dup(fd), "wb") as stream:
                stream.write(self.card.pem() + key)
            context.load_cert_chain("/proc/self/fd/" + str(fd))
        finally:
            os.close(fd)


class Approvals:
    def __init__(self, state):
        self.state = state
        self.cards = {}
        data = state.read("approved-peers.json", 1048576)
        if data is not None:
            records = strict_json(data)
            if not isinstance(records, list) or len(records) > 64:
                raise ValueError("Invalid peer store.")
            for record in records:
                card = Card.read(json.dumps(record).encode(), require_current=False)
                if card.device_id in self.cards:
                    raise ValueError("Duplicate approved identity.")
                self.cards[card.device_id] = card

    def approve(self, card):
        Card.read(card.serialize())
        previous = self.cards.get(card.device_id)
        if previous is not None and previous.fingerprint != card.fingerprint:
            raise ValueError("Remove the previously approved computer before replacing its identity.")
        if len(self.cards) >= 64 and card.device_id not in self.cards:
            raise ValueError("The approved peer limit is 64.")
        updated = dict(self.cards, **{card.device_id: card})
        self.state.write("approved-peers.json", [value.dictionary() for value in updated.values()])
        self.cards = updated

    def remove(self, device_id):
        updated = {key: value for key, value in self.cards.items() if key != device_id}
        self.state.write("approved-peers.json", [value.dictionary() for value in updated.values()])
        self.cards = updated


def tls_context(identity, expected, server=False):
    Card.read(expected.serialize())
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER if server else ssl.PROTOCOL_TLS_CLIENT)
    context.minimum_version = context.maximum_version = ssl.TLSVersion.TLSv1_3
    context.check_hostname = False  # Trust is the explicitly approved leaf, not a DNS name.
    context.verify_mode = ssl.CERT_REQUIRED
    context.verify_flags |= ssl.VERIFY_X509_PARTIAL_CHAIN
    context.load_verify_locations(cadata=expected.pem().decode())
    identity.configure(context)
    return context


def verify_peer(stream, expected):
    if stream.version() != "TLSv1.3":
        raise ValueError("Mutual TLS 1.3 is required.")
    der = stream.getpeercert(binary_form=True)
    if not der or not hmac.compare_digest(hashlib.sha256(der).hexdigest().upper(), expected.fingerprint):
        raise ValueError("Peer certificate does not match the approved fingerprint.")
    certificate_current(x509.load_der_x509_certificate(der))
