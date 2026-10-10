import dataclasses
import json
import socket
import tempfile
import threading
import time
import unittest
from pathlib import Path

from batcave_linux.discovery import (Cache, Computer, Discovery, Interface, announcement, eligible_address,
                                     interfaces, read_announcement)
from batcave_linux.identity import Approvals, Identity
from batcave_linux.initial_consent import InitialConsent
from batcave_linux.nearby_service import Attempts, PairingService
from batcave_linux.pairing import Descriptor
from batcave_linux.storage import StateDirectory
from test_core import MemoryVault


class NearbyTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.states = [StateDirectory(Path(self.temporary.name) / str(i)) for i in range(2)]
        self.identities = [Identity.open(state, MemoryVault(), "Test computer " + str(i)) for i, state in enumerate(self.states)]
        self.services = []

    def tearDown(self):
        for service in self.services: service.close()
        for state in self.states: state.close()
        self.temporary.cleanup()

    def test_announcement_source_types_labels_and_exact_fields(self):
        data = announcement(self.identities[0].card, 47870, 47873, True)
        value = read_announcement(data, "192.0.2.10", 10, 2)
        self.assertEqual(value.address, "192.0.2.10"); self.assertEqual(value.interface, 2)
        for key, invalid in (("version", True), ("inputPort", True), ("pairing", 1), ("pairingPort", 47870),
                             ("deviceId", "\u2028"), ("displayName", "\u202eName"), ("platform", ["linux"]),
                             ("fingerprint", self.identities[0].card.fingerprint.lower())):
            record = json.loads(data); record[key] = invalid
            with self.subTest(key=key), self.assertRaises((ValueError, TypeError)):
                read_announcement(json.dumps(record).encode(), "192.0.2.10", 10)
        record = json.loads(data); record["address"] = "203.0.113.10"
        with self.assertRaises(ValueError): read_announcement(json.dumps(record).encode(), "192.0.2.10", 10)
        for source in ("0.0.0.0", "127.0.0.1", "224.0.0.1", "240.0.0.1", "255.255.255.255", "::1"):
            self.assertFalse(eligible_address(source))
            with self.assertRaises(ValueError): read_announcement(data, source, 10)
        with self.assertRaises(ValueError): read_announcement(data + b" " * 1024, "192.0.2.10", 10)

    def test_cache_is_bounded_expires_and_ignores_own_identity(self):
        card = self.identities[0].card; cache = Cache(card.device_id)
        value = read_announcement(announcement(card, 47870, 47873, True), "192.0.2.10", 10)
        cache.remember(value, 10); self.assertEqual(cache.snapshot(10), ())
        for i in range(65): cache.remember(dataclasses.replace(value, device_id="linux:test-" + str(i)), 10)
        self.assertEqual(len(cache.snapshot(10)), 64)
        self.assertEqual(cache.snapshot(23), ())
        cache.remember(dataclasses.replace(value, device_id="linux:other"), 10)
        self.assertEqual(cache.snapshot(9), ())

    def test_linux_interface_enumeration_and_subnet_boundary(self):
        for value in interfaces():
            self.assertGreater(value.index, 0); self.assertTrue(eligible_address(value.address)); self.assertTrue(value.accepts(value.address))
        network = Interface(2, "192.0.2.10", "255.255.255.0")
        self.assertTrue(network.accepts("192.0.2.20"))
        for source in ("192.0.2.255", "192.0.2.0", "192.0.3.1", "127.0.0.1", "224.0.0.1"):
            self.assertFalse(network.accepts(source))

    def test_membership_is_per_interface_and_address_change_expires_candidates(self):
        networks = (Interface(2, "192.0.2.10", "255.255.255.0"), Interface(2, "198.51.100.10", "255.255.255.0"))
        class SocketFixture:
            def __init__(self): self.calls = []
            def setsockopt(self, *args): self.calls.append(args)
        discovery = Discovery("linux:own", lambda: b"", interface_provider=lambda: networks)
        discovery.stream = SocketFixture(); discovery._refresh()
        self.assertEqual(len(discovery.stream.calls), 1); self.assertEqual(discovery.joined, {2})
        self.assertTrue(discovery.on_lan("192.0.2.20", 2)); self.assertTrue(discovery.on_lan("198.51.100.20", 2))
        generation = discovery.network_generation; discovery._refresh()
        self.assertEqual(discovery.network_generation, generation); self.assertEqual(len(discovery.stream.calls), 1)
        card = self.identities[0].card
        discovery.cache.remember(read_announcement(announcement(card, 47870, 47873, True), "192.0.2.20", 10), 10)
        networks = (Interface(2, "203.0.113.10", "255.255.255.0"),)
        discovery._refresh()
        self.assertEqual(discovery.cache.snapshot(10), ()); self.assertGreater(discovery.network_generation, generation)
        self.assertFalse(discovery.on_lan("192.0.2.20", 2)); self.assertTrue(discovery.on_lan("203.0.113.20", 2))

    def test_incoming_attempt_limit_and_monotonic_window(self):
        attempts = Attempts(); self.assertTrue(attempts.admit(0)); self.assertFalse(attempts.admit(4.99))
        for now in (5, 10, 15, 20, 25): self.assertTrue(attempts.admit(now))
        self.assertFalse(attempts.admit(30)); self.assertTrue(attempts.admit(60))

    def service(self, index):
        identity = self.identities[index]
        service = PairingService(identity, Descriptor(identity.card, 47870 + index), lambda _peer: True,
                                 lambda address: address == "127.0.0.1", pairing_port=0, host="127.0.0.1")
        service.open_window(); self.services.append(service); return service

    def wait(self, callback, timeout=2):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            value = callback()
            if value: return value
            time.sleep(0.01)
        self.fail("Nearby fixture timed out")

    def test_service_saves_nothing_until_both_local_choices_and_completion_ack(self):
        services = [self.service(i) for i in range(2)]
        card = self.identities[1].card
        computer = Computer(card.device_id, card.display_name, "linux", card.fingerprint, "127.0.0.1", 47871,
                            services[1].port, True, time.monotonic())
        services[0].link(computer)
        prompts = [self.wait(lambda service=service: next((value for _, kind, value in service.drain() if kind == "confirm"), None)) for service in services]
        self.assertEqual(prompts[0][0].code, prompts[1][0].code)
        prompts[0][1].choose(True); time.sleep(0.08)
        self.assertEqual(services[0].drain(), []); self.assertEqual(services[1].drain(), [])
        prompts[1][1].choose(True)
        for index, service in enumerate(services):
            generation, kind, result = self.wait(lambda service=service: next(iter(service.drain()), None))
            self.assertEqual(kind, "completed"); self.assertEqual(result.peer.card, self.identities[1 - index].card)
            self.assertTrue(service.busy); self.assertTrue(service.acknowledge(generation)); self.assertFalse(service.busy)

    def test_close_unbinds_and_invalidates_old_confirmation(self):
        service = self.service(1); port = service.port; old_generation = service.generation
        service.close_window()
        self.assertFalse(service.is_open); self.assertGreater(service.generation, old_generation)
        with self.assertRaises(OSError): socket.create_connection(("127.0.0.1", port), timeout=0.2)
        service._event(old_generation, "confirm", None); self.assertEqual(service.drain(), [])
        service.open_window(seconds=0.05)
        self.wait(lambda: not service.is_open)
        event = self.wait(lambda: next(iter(service.drain()), None))
        self.assertEqual(event[1], "expired")
        with self.assertRaises(OSError): socket.create_connection(("127.0.0.1", port), timeout=0.2)

    def test_stale_discovery_and_overlapping_link_are_rejected(self):
        service = self.service(0); card = self.identities[1].card
        computer = Computer(card.device_id, card.display_name, "linux", card.fingerprint, "127.0.0.1", 47871, 47873,
                            True, time.monotonic() - 13)
        with self.assertRaises(ValueError): service.link(computer)
        service.close_window()
        with self.assertRaises(ValueError): service.link(dataclasses.replace(computer, seen=time.monotonic()))

    def test_initial_share_consent_first_connection_role_pin_and_expiry(self):
        peer = self.identities[1].card
        consent = InitialConsent(peer, True, "00" * 16, now=10)
        self.assertFalse(consent.take(peer, True, 1, now=11))
        self.assertTrue(consent.bind(peer, True, 1, now=12))
        self.assertFalse(consent.take(peer, False, 1, now=12))
        self.assertFalse(consent.take(peer, True, 2, now=12))
        self.assertFalse(consent.take(self.identities[0].card, True, 1, now=12))
        self.assertTrue(consent.take(peer, True, 1, now=13))
        self.assertFalse(consent.take(peer, True, 1, now=14))
        consent = InitialConsent(peer, False, "00" * 16, now=10)
        self.assertFalse(consent.bind(peer, False, 1, now=40))
        consent = InitialConsent(peer, False, "00" * 16, now=10)
        self.assertTrue(consent.bind(peer, False, 1, now=11))
        self.assertFalse(consent.bind(peer, False, 2, now=12)); self.assertFalse(consent.take(peer, False, 1, now=12))
        consent = InitialConsent(peer, False, "00" * 16, now=10); consent.close()
        self.assertFalse(consent.bind(peer, False, 1, now=11))

    def test_existing_pin_cannot_be_replaced_through_another_approval(self):
        approvals = Approvals(self.states[0]); previous = self.identities[1].card
        approvals.approve(previous)
        changed = dataclasses.replace(self.identities[0].card, device_id=previous.device_id)
        with self.assertRaises(ValueError): approvals.approve(changed)
        self.assertEqual(Approvals(self.states[0]).cards[previous.device_id], previous)
