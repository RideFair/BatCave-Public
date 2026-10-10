"""Bounded, unauthenticated LAN hints. A hint can never replace a saved pin."""
import ctypes
import ipaddress
import json
import select
import socket
import struct
import threading
import time
from dataclasses import dataclass

from .pairing import fields, hex_bytes, label, parse, port

GROUP = "239.255.47.87"
DISCOVERY_PORT = 47872
PAIRING_PORT = 47873
MAX_PACKET = 1024
CACHE_LIMIT = 64
EXPIRY = 12


def eligible_address(value):
    try: address = ipaddress.ip_address(value)
    except ValueError: return False
    return address.version == 4 and not address.is_loopback and 0 < address.packed[0] < 224 and str(address) != "255.255.255.255"


@dataclass(frozen=True)
class Interface:
    index: int
    address: str
    mask: str

    def accepts(self, address):
        if not eligible_address(address): return False
        network = ipaddress.IPv4Network((self.address, self.mask), strict=False)
        value = ipaddress.IPv4Address(address)
        return network.prefixlen > 0 and value in network and (network.prefixlen >= 31 or value not in {network.network_address, network.broadcast_address})


class _Address(ctypes.Structure):
    _fields_ = [("family", ctypes.c_ushort), ("port", ctypes.c_ushort), ("address", ctypes.c_ubyte * 4), ("padding", ctypes.c_ubyte * 8)]


class _Ifaddrs(ctypes.Structure): pass
_Ifaddrs._fields_ = [("next", ctypes.POINTER(_Ifaddrs)), ("name", ctypes.c_char_p), ("flags", ctypes.c_uint),
                    ("address", ctypes.POINTER(_Address)), ("mask", ctypes.POINTER(_Address)),
                    ("destination", ctypes.c_void_p), ("data", ctypes.c_void_p)]


def interfaces():
    """Linux getifaddrs ABI; no subprocess, privilege or extra daemon."""
    libc = ctypes.CDLL(None, use_errno=True)
    libc.getifaddrs.argtypes = [ctypes.POINTER(ctypes.POINTER(_Ifaddrs))]; libc.getifaddrs.restype = ctypes.c_int
    libc.freeifaddrs.argtypes = [ctypes.POINTER(_Ifaddrs)]; libc.freeifaddrs.restype = None
    first = ctypes.POINTER(_Ifaddrs)()
    if libc.getifaddrs(ctypes.byref(first)): raise OSError(ctypes.get_errno(), "LAN interfaces are unavailable.")
    result = set()
    try:
        pointer = first; count = 0
        while pointer:
            count += 1
            if count > 1024: raise ValueError("Too many network interfaces.")
            value = pointer.contents; pointer = value.next
            if not value.flags & 1 or not value.flags & 4096 or value.flags & (8 | 16): continue
            if not value.address or not value.mask or value.address.contents.family != socket.AF_INET: continue
            address = socket.inet_ntoa(bytes(value.address.contents.address)); mask = socket.inet_ntoa(bytes(value.mask.contents.address))
            if not eligible_address(address): continue
            try:
                network = Interface(socket.if_nametoindex(value.name.decode("utf-8", "strict")), address, mask)
                if ipaddress.IPv4Network((address, mask), strict=False).prefixlen: result.add(network)
            except (ValueError, OSError, UnicodeError): continue  # Interface vanished or changed during enumeration.
    finally: libc.freeifaddrs(first)
    return tuple(sorted(result, key=lambda item: (item.index, item.address)))


@dataclass(frozen=True)
class Computer:
    device_id: str
    display_name: str
    platform: str
    fingerprint: str
    address: str
    input_port: int
    pairing_port: int
    pairing: bool
    seen: float
    interface: int = 0


def announcement(card, input_port, pairing_port, pairing):
    label(card.device_id); label(card.display_name); hex_bytes(card.fingerprint, 32)
    port(input_port); port(pairing_port)
    if input_port == pairing_port or type(pairing) is not bool: raise ValueError("Invalid advertised pairing state.")
    data = json.dumps(dict(type="batcave.nearby", version=1, deviceId=card.device_id, displayName=card.display_name,
                          platform="linux", fingerprint=card.fingerprint, inputPort=input_port,
                          pairingPort=pairing_port, pairing=pairing), ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    if len(data) > MAX_PACKET: raise ValueError("Nearby announcement exceeds its limit.")
    return data


def read_announcement(data, source, now, interface=0):
    if not eligible_address(source): raise ValueError("Invalid discovery source.")
    value = parse(data, MAX_PACKET)
    fields(value, {"type", "version", "deviceId", "displayName", "platform", "fingerprint", "inputPort", "pairingPort", "pairing"})
    if value["type"] != "batcave.nearby" or type(value["version"]) is not int or value["version"] != 1:
        raise ValueError("Unsupported nearby version.")
    label(value["deviceId"]); label(value["displayName"]); hex_bytes(value["fingerprint"], 32)
    if value["platform"] not in {"windows", "macos", "linux"} or type(value["pairing"]) is not bool:
        raise ValueError("Invalid nearby computer type.")
    input_port, pairing_port = port(value["inputPort"]), port(value["pairingPort"])
    if input_port == pairing_port: raise ValueError("Advertised ports must be distinct.")
    return Computer(value["deviceId"], value["displayName"], value["platform"], value["fingerprint"],
                    str(ipaddress.IPv4Address(source)), input_port, pairing_port, value["pairing"], now, interface)


class Cache:
    def __init__(self, own_id): self.own_id = own_id; self.items = {}; self.lock = threading.Lock()

    def _expire(self, now):
        self.items = {key: value for key, value in self.items.items() if 0 <= now - value.seen <= EXPIRY}

    def remember(self, value, now):
        if value.device_id == self.own_id: return
        with self.lock:
            self._expire(now)
            if len(self.items) >= CACHE_LIMIT and value.device_id not in self.items: return
            self.items[value.device_id] = value

    def snapshot(self, now=None):
        with self.lock:
            self._expire(time.monotonic() if now is None else now)
            return tuple(sorted(self.items.values(), key=lambda value: (value.display_name.casefold(), value.device_id)))

    def clear(self):
        with self.lock: self.items.clear()


class Discovery:
    def __init__(self, own_id, advertise, interface_provider=interfaces):
        self.cache = Cache(own_id); self.advertise = advertise; self.interface_provider = interface_provider
        self.networks = (); self.network_generation = 0; self.error = None
        self.joined = set()
        self.stop_event = threading.Event(); self.thread = None; self.stream = None

    def on_lan(self, address, index=0):
        return any((not index or network.index == index) and network.accepts(address) for network in self.networks)

    def start(self):
        stream = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            stream.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            stream.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 65536)
            stream.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_TTL, 1)
            stream.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_LOOP, 0)
            # IP_PKTINFO = 8 on Linux, even when Python omits the constant.
            stream.setsockopt(socket.IPPROTO_IP, getattr(socket, "IP_PKTINFO", 8), 1)
            stream.bind(("0.0.0.0", DISCOVERY_PORT)); stream.setblocking(False)
            self.stream = stream
            self.thread = threading.Thread(target=self._worker, daemon=True); self.thread.start()
        except BaseException: stream.close(); raise

    def _refresh(self):
        next_networks = self.interface_provider()
        if next_networks != self.networks:
            self.cache.clear(); self.network_generation += 1
        next_indices = {network.index for network in next_networks}
        for index in self.joined - next_indices:
            try: self.stream.setsockopt(socket.IPPROTO_IP, socket.IP_DROP_MEMBERSHIP,
                    struct.pack("=4s4sI", socket.inet_aton(GROUP), bytes(4), index))
            except OSError: pass
        joined = self.joined & next_indices
        for index in next_indices - joined:
            try:
                self.stream.setsockopt(socket.IPPROTO_IP, socket.IP_ADD_MEMBERSHIP,
                    struct.pack("=4s4sI", socket.inet_aton(GROUP), bytes(4), index))
                joined.add(index)
            except OSError: self.error = "Nearby discovery could not join this network. Reconnect Wi-Fi and search again."
        self.joined = joined
        self.networks = tuple(network for network in next_networks if network.index in joined)
        if not self.networks: self.error = "No local IPv4 network is available. Connect both computers to the same network, then search again."

    def _worker(self):
        next_send = 0; second = time.monotonic(); count = 0
        try:
            while not self.stop_event.is_set():
                now = time.monotonic()
                if now >= next_send:
                    self.error = None; self._refresh(); data = self.advertise()
                    if len(data) > MAX_PACKET: raise ValueError("Nearby announcement exceeds its limit.")
                    for network in self.networks:
                        try:
                            self.stream.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_IF, socket.inet_aton(network.address))
                            self.stream.sendto(data, (GROUP, DISCOVERY_PORT))
                        except OSError: self.error = "Nearby discovery is blocked. Allow BatCave on your private network, then search again."
                    next_send = now + 3
                if now - second >= 1: second = now; count = 0
                if count >= 64:
                    self.stop_event.wait(min(0.05, max(0, second + 1 - now))); continue
                if not select.select([self.stream], [], [], 0.1)[0]: continue
                try: data, ancillary, flags, source = self.stream.recvmsg(MAX_PACKET + 1, socket.CMSG_SPACE(12))
                except BlockingIOError: continue
                count += 1
                if flags & (socket.MSG_TRUNC | socket.MSG_CTRUNC) or len(data) > MAX_PACKET: continue
                index = 0
                for level, kind, value in ancillary:
                    if level == socket.IPPROTO_IP and kind == getattr(socket, "IP_PKTINFO", 8) and len(value) >= 12:
                        index = struct.unpack("=I", value[:4])[0]
                if not index or not self.on_lan(source[0], index): continue
                try: self.cache.remember(read_announcement(data, source[0], time.monotonic(), index), time.monotonic())
                except (ValueError, TypeError, UnicodeError): continue
        except (OSError, ValueError):
            if not self.stop_event.is_set(): self.error = "Nearby discovery stopped. Search again to retry."

    def close(self):
        self.stop_event.set()
        if self.stream: self.stream.close()
        self.cache.clear()
