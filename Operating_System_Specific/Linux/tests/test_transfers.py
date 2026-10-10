import binascii
import hashlib
import os
import struct
import tempfile
import unittest
import uuid
import zlib
from pathlib import Path

from batcave_linux.transfers import IncomingFile, MAX_CHUNK, Offer, icon_preview, leaf_name


def png_chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", binascii.crc32(kind + data) & 0xffffffff)


def png(width=1, height=1, pixels=b"\0\x80\x80\x80\xff", metadata=True):
    return (b"\x89PNG\r\n\x1a\n" + png_chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
            + (png_chunk(b"tEXt", b"Comment\0not retained") if metadata else b"")
            + png_chunk(b"IDAT", zlib.compress(pixels)) + png_chunk(b"IEND", b""))


class TransferTests(unittest.TestCase):
    def offer(self, data=b"file bytes", name="example.txt", digest=None):
        return Offer(str(uuid.uuid4()), name, len(data), digest or hashlib.sha256(data).hexdigest())

    def test_verified_atomic_completion_and_exact_bytes(self):
        with tempfile.TemporaryDirectory() as destination:
            data = os.urandom(MAX_CHUNK + 17)
            transfer = IncomingFile(self.offer(data), destination)
            self.assertFalse((Path(destination) / "example.txt").exists())
            transfer.append(0, data[:MAX_CHUNK]); transfer.append(MAX_CHUNK, data[MAX_CHUNK:])
            self.assertEqual(transfer.complete(), "example.txt")
            self.assertEqual((Path(destination) / "example.txt").read_bytes(), data)
            self.assertEqual(os.stat(Path(destination) / "example.txt").st_mode & 0o777, 0o600)
            self.assertEqual(len(list(Path(destination).iterdir())), 1)

    def test_cancel_hash_mismatch_out_of_order_and_oversize_leave_no_file(self):
        for failure in ("cancel", "digest", "offset", "oversize"):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as destination:
                transfer = IncomingFile(self.offer(digest="0" * 64 if failure == "digest" else None), destination)
                if failure == "cancel": transfer.cancel()
                else:
                    with self.assertRaises(ValueError):
                        if failure == "digest": transfer.append(0, b"file bytes"); transfer.complete()
                        elif failure == "offset": transfer.append(1, b"a")
                        else: transfer.append(0, b"a" * (MAX_CHUNK + 1))
                self.assertEqual(list(Path(destination).iterdir()), [])

    def test_existing_file_or_symlink_is_never_overwritten(self):
        for symlink in (False, True):
            with self.subTest(symlink=symlink), tempfile.TemporaryDirectory() as destination:
                target = Path(destination) / "example.txt"
                if symlink: target.symlink_to("other.txt")
                else: target.write_bytes(b"original")
                transfer = IncomingFile(self.offer(), destination); transfer.append(0, b"file bytes")
                with self.assertRaises(FileExistsError): transfer.complete()
                self.assertEqual(target.is_symlink(), symlink)
                if not symlink: self.assertEqual(target.read_bytes(), b"original")
                self.assertEqual(len(list(Path(destination).iterdir())), 1)

    def test_empty_file_and_unsafe_names_destinations(self):
        with tempfile.TemporaryDirectory() as destination:
            transfer = IncomingFile(self.offer(b""), destination); transfer.complete()
            self.assertEqual((Path(destination) / "example.txt").read_bytes(), b"")
        for name in ("../escape", "a/b", "a\\b", "CON.txt", "LPT1", "COM¹.txt", "CON .txt", "a:stream", "foo.", "x\u202ey", "..", "", "a" * 241):
            with self.subTest(name=name), self.assertRaises(ValueError): leaf_name(name)
        with tempfile.TemporaryDirectory() as destination:
            os.chmod(destination, 0o777)
            with self.assertRaises(PermissionError): IncomingFile(self.offer(), destination)

    def test_icon_bounds_checksum_decompression_and_metadata_removal(self):
        cleaned = icon_preview(png())
        self.assertNotIn(b"not retained", cleaned)
        self.assertEqual(cleaned, png(metadata=False))
        for data in (png(width=129), png(pixels=b"x" * 10000), b"svg", png()[:-1], png() + b"extra"):
            with self.subTest(size=len(data)), self.assertRaises(ValueError): icon_preview(data)

    def test_stored_content_and_staging_name_substitution_rejected(self):
        for attack in ("bytes", "replace", "link", "permissions"):
            with self.subTest(attack=attack), tempfile.TemporaryDirectory() as destination:
                transfer = IncomingFile(self.offer(), destination)
                transfer.append(0, b"file bytes")
                stage = Path(destination) / transfer.staging_name
                if attack == "bytes": os.pwrite(transfer.fd, b"bad!", 0)
                elif attack == "replace":
                    (stage / "content").rename(stage / "original")
                    (stage / "content").write_bytes(b"other data")
                elif attack == "link": os.link(stage / "content", stage / "extra")
                else: os.chmod(stage / "content", 0o644)
                with self.assertRaises(ValueError): transfer.complete()
                self.assertFalse((Path(destination) / "example.txt").exists())
                if attack == "replace": self.assertEqual((stage / "content").read_bytes(), b"other data")


if __name__ == "__main__": unittest.main()
