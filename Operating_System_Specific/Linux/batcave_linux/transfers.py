"""Local transfer staging. Wire integration waits for the shared capability contract."""

import binascii
import hashlib
import hmac
import os
import re
import stat
import struct
import unicodedata
import uuid
import zlib
from dataclasses import dataclass

MAX_CHUNK = 32768
MAX_FILE = 1024 * 1024 * 1024
MAX_ICON = 32768
MAX_BATCH = 4 * MAX_FILE


def leaf_name(value):
    if not isinstance(value, str) or not value or len(value.encode("utf-8")) > 240:
        raise ValueError("Invalid file name.")
    if value != unicodedata.normalize("NFC", value) or value in {".", ".."} or value.endswith((" ", ".")):
        raise ValueError("The file name is not portable.")
    if any(c in '/\\:*?"<>|' or unicodedata.category(c) in {"Cc", "Cf", "Cs"} for c in value):
        raise ValueError("File offers must contain a plain leaf name, not a path.")
    stem = value.split(".", 1)[0].rstrip(" ").upper()
    if stem in {"CON", "PRN", "AUX", "NUL"} or re.fullmatch(r"(?:COM|LPT)[1-9¹²³]", stem):
        raise ValueError("Reserved file name.")
    return value


def icon_preview(data):
    """Accept small static 8-bit PNG; discard all ancillary metadata before decode."""
    if not isinstance(data, bytes) or not 45 <= len(data) <= MAX_ICON or data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("Icon preview must be a PNG no larger than 32 KiB.")
    offset, chunks, compressed, dimensions, ended = 8, [], bytearray(), None, False
    while offset < len(data):
        if offset + 12 > len(data):
            raise ValueError("Truncated PNG chunk.")
        size = struct.unpack_from(">I", data, offset)[0]
        kind = data[offset + 4:offset + 8]
        if size > MAX_ICON or offset + size + 12 > len(data):
            raise ValueError("Invalid PNG chunk size.")
        payload = data[offset + 8:offset + 8 + size]
        crc = struct.unpack_from(">I", data, offset + size + 8)[0]
        if binascii.crc32(kind + payload) & 0xffffffff != crc:
            raise ValueError("Invalid PNG checksum.")
        if kind == b"IHDR":
            if dimensions is not None or chunks or size != 13:
                raise ValueError("Invalid PNG header order.")
            width, height, depth, color, compression, filtering, interlace = struct.unpack(">IIBBBBB", payload)
            if not 1 <= width <= 128 or not 1 <= height <= 128 or depth != 8 or color not in (0, 2, 4, 6) or (compression, filtering, interlace) != (0, 0, 0):
                raise ValueError("Icon preview must be a static 8-bit PNG at most 128 by 128.")
            dimensions = width, height, {0: 1, 2: 3, 4: 2, 6: 4}[color]
        elif dimensions is None:
            raise ValueError("PNG header must come first.")
        elif kind == b"IDAT":
            if ended:
                raise ValueError("PNG data after end.")
            compressed.extend(payload)
        elif kind == b"IEND":
            if size or not compressed or offset + 12 != len(data):
                raise ValueError("Invalid PNG ending.")
            ended = True
        elif kind in (b"acTL", b"fcTL", b"fdAT") or not kind[0] & 32:
            raise ValueError("Unsupported PNG format.")
        if kind in (b"IHDR", b"IDAT", b"IEND"):
            chunks.append(data[offset:offset + size + 12])
        offset += size + 12
    if not ended:
        raise ValueError("PNG has no end marker.")
    width, height, channels = dimensions
    expected = height * (width * channels + 1)
    decoder = zlib.decompressobj()
    pixels = decoder.decompress(bytes(compressed), expected + 1)
    if len(pixels) != expected or not decoder.eof or decoder.unused_data or decoder.unconsumed_tail:
        raise ValueError("Invalid or oversized PNG pixel data.")
    if any(pixels[row * (width * channels + 1)] > 4 for row in range(height)):
        raise ValueError("Invalid PNG row filter.")
    return b"\x89PNG\r\n\x1a\n" + b"".join(chunks)


@dataclass(frozen=True)
class Offer:
    transfer_id: str
    name: str
    size: int
    sha256: str
    icon: bytes | None = None

    def validate(self):
        if not isinstance(self.transfer_id, str) or not re.fullmatch(r"[a-f0-9]{8}-[a-f0-9]{4}-4[a-f0-9]{3}-[89ab][a-f0-9]{3}-[a-f0-9]{12}", self.transfer_id):
            raise ValueError("Invalid transfer ID.")
        leaf_name(self.name)
        if type(self.size) is not int or not 0 <= self.size <= MAX_FILE:
            raise ValueError("The preview file limit is 1 GiB.")
        if not isinstance(self.sha256, str) or not re.fullmatch(r"[a-f0-9]{64}", self.sha256):
            raise ValueError("Invalid file digest.")
        if self.icon is not None:
            icon_preview(self.icon)


class IncomingFile:
    """Construct only after the local user selects a destination and accepts."""

    def __init__(self, offer, destination):
        offer.validate()
        self.offer, self.offset = offer, 0
        self.digest = hashlib.sha256()
        self.directory = self.stage = self.fd = -1
        self.stage_identity = self.file_identity = None
        self.finished = False
        self.staging_name = ".batcave-incoming-" + uuid.uuid4().hex
        try:
            self.directory = os.open(destination, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
            info = os.fstat(self.directory)
            if info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) & 0o022:
                raise PermissionError("Choose a destination owned by you without group or public write access.")
            space = os.fstatvfs(self.directory)
            if space.f_bavail * space.f_frsize < offer.size:
                raise OSError("Insufficient destination space.")
            os.mkdir(self.staging_name, 0o700, dir_fd=self.directory)
            self.stage = os.open(self.staging_name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=self.directory)
            self.stage_identity = self._identity(os.fstat(self.stage))
            self.fd = os.open("content", os.O_RDWR | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC,
                              0o600, dir_fd=self.stage)
            self.file_identity = self._identity(os.fstat(self.fd))
        except BaseException:
            self.cancel()
            raise

    def append(self, offset, chunk):
        if self.finished or self.fd < 0:
            raise ValueError("Transfer is no longer active.")
        if type(offset) is not int or offset != self.offset or not isinstance(chunk, bytes) or not 1 <= len(chunk) <= MAX_CHUNK:
            self.cancel()
            raise ValueError("Transfer chunks must be bounded and strictly ordered.")
        if self.offset + len(chunk) > self.offer.size:
            self.cancel()
            raise ValueError("Transfer exceeds the accepted file length.")
        try:
            remaining = memoryview(chunk)
            while remaining:
                written = os.write(self.fd, remaining)
                if written <= 0:
                    raise OSError("File write made no progress.")
                remaining = remaining[written:]
            self.digest.update(chunk)
            self.offset += len(chunk)
        except BaseException:
            self.cancel()
            raise

    def complete(self):
        if self.finished or self.fd < 0:
            raise ValueError("Transfer is no longer active.")
        try:
            if self.offset != self.offer.size or not hmac.compare_digest(self.digest.hexdigest(), self.offer.sha256.lower()):
                raise ValueError("Transferred file failed its length or SHA-256 check.")
            before = self._validate_staging()
            if before.st_size != self.offer.size:
                raise ValueError("Stored file length changed during transfer.")
            os.lseek(self.fd, 0, os.SEEK_SET)
            stored = hashlib.sha256()
            while True:
                chunk = os.read(self.fd, MAX_CHUNK)
                if not chunk: break
                stored.update(chunk)
            if not hmac.compare_digest(stored.hexdigest(), self.offer.sha256.lower()):
                raise ValueError("Stored file failed its independent SHA-256 check.")
            after = self._validate_staging()
            if (before.st_size, before.st_mtime_ns, before.st_ctime_ns) != (after.st_size, after.st_mtime_ns, after.st_ctime_ns):
                raise ValueError("Stored file changed during verification.")
            os.fsync(self.fd)
            # Link the verified descriptor, not a replaceable staging pathname.
            # Linux linkat follows /proc/self/fd to that inode and fails on collision.
            os.link("/proc/self/fd/" + str(self.fd), self.offer.name,
                    dst_dir_fd=self.directory, follow_symlinks=True)
            os.fsync(self.directory)
            self.finished = True
            return self.offer.name
        finally:
            self.cancel()

    @staticmethod
    def _identity(info):
        return info.st_dev, info.st_ino

    def _validate_staging(self):
        opened = os.fstat(self.fd)
        named = os.stat("content", dir_fd=self.stage, follow_symlinks=False)
        stage = os.stat(self.staging_name, dir_fd=self.directory, follow_symlinks=False)
        if (self._identity(opened) != self.file_identity or self._identity(named) != self.file_identity
                or self._identity(stage) != self.stage_identity or not stat.S_ISREG(opened.st_mode)
                or opened.st_uid != os.getuid() or stat.S_IMODE(opened.st_mode) != 0o600 or opened.st_nlink != 1):
            raise ValueError("Transfer staging identity changed.")
        return opened

    def cancel(self):
        if self.fd >= 0:
            os.close(self.fd); self.fd = -1
        if self.stage >= 0:
            try:
                if self._identity(os.stat("content", dir_fd=self.stage, follow_symlinks=False)) == self.file_identity:
                    os.unlink("content", dir_fd=self.stage)
            except FileNotFoundError: pass
            os.close(self.stage); self.stage = -1
        if self.directory >= 0:
            try:
                if self._identity(os.stat(self.staging_name, dir_fd=self.directory, follow_symlinks=False)) == self.stage_identity:
                    os.rmdir(self.staging_name, dir_fd=self.directory)
            except FileNotFoundError: pass
            except OSError: pass  # Unexpected contents are not this transfer's to delete.
            os.close(self.directory); self.directory = -1
