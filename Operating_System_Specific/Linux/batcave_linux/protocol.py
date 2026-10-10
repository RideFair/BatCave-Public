"""Strict bounded v0 framing; transport identity is supplied separately."""

import datetime as dt
import math
import socket
import uuid
from dataclasses import dataclass

from .identity import strict_json

PROTOCOL = "batcave.input.v0"
MAX_FRAME = 256 * 1024


def integer(value, low, high):
    if type(value) is not int or not low <= value <= high:
        raise ValueError("Invalid integer input field.")
    return value


def number(value, low, high):
    if type(value) not in (int, float) or not math.isfinite(value) or not low <= value <= high:
        raise ValueError("Invalid numeric input field.")
    return value


def boolean(value):
    if type(value) is not bool:
        raise ValueError("Invalid boolean input field.")
    return value


@dataclass(frozen=True)
class Bounds:
    left: int
    top: int
    width: int
    height: int

    @classmethod
    def read(cls, value):
        if not isinstance(value, dict):
            raise ValueError("Missing screen bounds.")
        bounds = cls(integer(value.get("left"), -100000, 100000), integer(value.get("top"), -100000, 100000),
                     integer(value.get("width"), 1, 100000), integer(value.get("height"), 1, 100000))
        right = integer(value.get("right"), -100000, 200000)
        bottom = integer(value.get("bottom"), -100000, 200000)
        # CGRect peers may advertise exclusive endpoints; derive our own inclusive geometry.
        if right not in (bounds.right, bounds.right + 1) or bottom not in (bounds.bottom, bounds.bottom + 1):
            raise ValueError("Inconsistent screen bounds.")
        return bounds

    @property
    def right(self):
        return self.left + self.width - 1

    @property
    def bottom(self):
        return self.top + self.height - 1

    def dictionary(self):
        return dict(left=self.left, top=self.top, width=self.width, height=self.height, right=self.right, bottom=self.bottom)

    def point(self, edge, position):
        number(position, 0, 1)
        inset_x, inset_y = min(2, self.width - 1), min(2, self.height - 1)
        if edge in {"left", "right"}:
            return (self.left + inset_x if edge == "left" else self.right - inset_x,
                    self.top + position * (self.height - 1))
        if edge in {"top", "bottom"}:
            return (self.left + position * (self.width - 1),
                    self.top + inset_y if edge == "top" else self.bottom - inset_y)
        raise ValueError("Unknown handoff edge.")


def encode(source, kind, payload):
    import json
    envelope = dict(protocol=PROTOCOL, type=kind, messageId=uuid.uuid4().hex, sourceDeviceId=source,
                    sentAtUtc=dt.datetime.now(dt.timezone.utc).isoformat(), payload=payload)
    data = json.dumps(envelope, allow_nan=False, ensure_ascii=True, separators=(",", ":")).encode()
    if len(data) > MAX_FRAME:
        raise ValueError("Outbound frame exceeds 256 KiB.")
    return data + b"\n"


def decode(data, peer_id):
    if not data or len(data) > MAX_FRAME:
        raise ValueError("Invalid frame size.")
    try:
        value = strict_json(data.decode("utf-8", errors="strict"))
    except (RecursionError, UnicodeError) as error:
        raise ValueError("Invalid JSON frame.") from error
    if not isinstance(value, dict) or value.get("protocol") != PROTOCOL or value.get("sourceDeviceId") != peer_id:
        raise ValueError("Frame does not match its authenticated identity and protocol.")
    if not isinstance(value.get("type"), str) or not isinstance(value.get("payload"), dict):
        raise ValueError("Invalid message envelope.")
    return value


class FrameReader:
    def __init__(self):
        self.buffer = bytearray()

    def read(self, stream, cancelled=lambda: False):
        while not cancelled():
            newline = self.buffer.find(b"\n")
            if newline >= 0:
                if newline > MAX_FRAME:
                    raise ValueError("Inbound frame exceeds 256 KiB.")
                result = bytes(self.buffer[:newline])
                del self.buffer[:newline + 1]
                return result
            if len(self.buffer) > MAX_FRAME:
                raise ValueError("Inbound frame exceeds 256 KiB.")
            try:
                data = stream.recv(min(16384, MAX_FRAME + 1 - len(self.buffer)))
            except socket.timeout:
                continue
            if not data:
                raise EOFError("Peer disconnected.")
            self.buffer.extend(data)
        raise EOFError("Connection stopped.")


def hello(identity, bounds):
    return encode(identity.device_id, "device.hello", dict(deviceId=identity.device_id, deviceName=identity.display_name,
                  operatingSystem="Linux", agentName="BatCave.LinuxAgent", virtualDesktopBounds=bounds.dictionary()))


def read_hello(message, peer_id):
    payload = message["payload"]
    if message["type"] != "device.hello" or payload.get("deviceId") != peer_id:
        raise ValueError("Expected an identity-bound device hello.")
    return Bounds.read(payload.get("virtualDesktopBounds"))
