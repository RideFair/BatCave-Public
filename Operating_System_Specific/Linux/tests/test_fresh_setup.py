import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from types import SimpleNamespace

from batcave_linux.storage import StateDirectory, backup_profile
from batcave_linux.identity import Identity, Approvals
from test_core import MemoryVault


class FreshSetupTests(unittest.TestCase):
    def test_startup_cannot_lock_a_stale_fd_after_reset(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "secure-preview"
            with StateDirectory(path) as first, StateDirectory(path) as delayed:
                first.lock_for_application()
                first.write("identity.json", b"synthetic old identity")
                backup = backup_profile(first)
                first.close()
                with self.assertRaises(RuntimeError): delayed.lock_for_application()
                with StateDirectory(path) as replacement:
                    replacement.lock_for_application()
                    with self.assertRaises(RuntimeError): delayed.lock_for_application()
                self.assertEqual((backup / "identity.json").read_bytes(), b"synthetic old identity")

    def test_other_user_writable_parent_is_rejected(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "secure-preview"
            with StateDirectory(path) as state:
                state.lock_for_application()
                os.chmod(root, 0o777)
                try:
                    with self.assertRaises(RuntimeError): backup_profile(state)
                    self.assertTrue(path.is_dir())
                    self.assertEqual(list(Path(root).iterdir()), [path])
                finally: os.chmod(root, 0o700)

    def test_backup_restore_keeps_original_identity_and_keyring(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "secure-preview"
            vault = MemoryVault()
            with StateDirectory(path) as state:
                state.lock_for_application()
                identity = Identity.open(state, vault, "Synthetic desktop")
                original = state.read("identity.json")
                Approvals(state).approve(identity.card)
                backup = backup_profile(state)
            self.assertFalse(path.exists())
            self.assertEqual(backup.stat().st_mode & 0o777, 0o700)
            self.assertEqual((backup / "identity.json").read_bytes(), original)
            with StateDirectory(path) as fresh:
                second = Identity.open(fresh, vault, "New synthetic desktop")
                self.assertNotEqual(identity.card, second.card)
                other_backup = backup_profile(fresh)
            self.assertNotEqual(backup, other_backup)
            backup.rename(path)
            with StateDirectory(path) as restored:
                self.assertEqual(Identity.open(restored, vault).card, identity.card)
                self.assertIn(identity.card.device_id, Approvals(restored).cards)
            self.assertEqual(len(vault.values), 2)

    def test_collision_and_second_session_preserve_profile(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "secure-preview"
            with StateDirectory(path) as first, StateDirectory(path) as second:
                first.lock_for_application()
                with self.assertRaisesRegex(RuntimeError, "another BatCave"):
                    second.lock_for_application()
                collision = Path(root) / "secure-preview-backup-existing"
                collision.mkdir()
                with patch("batcave_linux.storage.uuid.uuid4", return_value=SimpleNamespace(hex="existing")):
                    with self.assertRaises(OSError): backup_profile(first)
                self.assertTrue(path.is_dir())
                self.assertTrue(collision.is_dir())

    def test_symlinks_and_replaced_profile_are_rejected(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "secure-preview"
            with StateDirectory(path) as state:
                actual = Path(root) / "original"
                path.rename(actual)
                path.symlink_to(actual, target_is_directory=True)
                with self.assertRaises(ValueError): backup_profile(state)
                path.unlink()
                path.mkdir(mode=0o700)
                with self.assertRaisesRegex(ValueError, "changed"): backup_profile(state)
            linked_parent = Path(root) / "linked"
            linked_parent.symlink_to(root, target_is_directory=True)
            with StateDirectory(linked_parent / "secure-preview") as state:
                with self.assertRaises(ValueError): backup_profile(state)
