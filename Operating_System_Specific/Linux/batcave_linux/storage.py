"""User-owned, bounded, symlink-resistant state and atomic replacement."""

import json
import fcntl
import os
import re
import stat
import uuid
from pathlib import Path


class StateDirectory:
    def __init__(self, path):
        path = Path(path)
        path.mkdir(parents=True, mode=0o700, exist_ok=True)
        self.fd = os.open(path, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
        info = os.fstat(self.fd)
        if info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) != 0o700:
            os.close(self.fd)
            raise PermissionError("The BatCave state directory must be owned by you with mode 0700.")
        self.path = path

    @staticmethod
    def _name(name):
        if not re.fullmatch(r"[a-zA-Z0-9_-]+\.[a-zA-Z0-9_-]+", name):
            raise ValueError("Invalid state filename.")
        return name

    def read(self, name, limit=65536):
        try:
            fd = os.open(self._name(name), os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK, dir_fd=self.fd)
        except FileNotFoundError:
            return None
        with os.fdopen(fd, "rb") as stream:
            info = os.fstat(stream.fileno())
            if not stat.S_ISREG(info.st_mode) or info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) != 0o600:
                raise PermissionError("BatCave state must be an owned regular file with mode 0600.")
            data = stream.read(limit + 1)
            if len(data) > limit:
                raise ValueError("BatCave state exceeds its size limit.")
            return data

    def write(self, name, data, replace=True):
        self._name(name)
        if not isinstance(data, bytes):
            data = json.dumps(data, ensure_ascii=True, allow_nan=False).encode()
        temporary = "pending-" + uuid.uuid4().hex + ".tmp"
        fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC,
                     0o600, dir_fd=self.fd)
        try:
            with os.fdopen(fd, "wb") as stream:
                stream.write(data)
                stream.flush()
                os.fsync(stream.fileno())
            if replace:
                os.replace(temporary, name, src_dir_fd=self.fd, dst_dir_fd=self.fd)
            else:
                os.link(temporary, name, src_dir_fd=self.fd, dst_dir_fd=self.fd, follow_symlinks=False)
                os.unlink(temporary, dir_fd=self.fd)
            os.fsync(self.fd)
        finally:
            try:
                os.unlink(temporary, dir_fd=self.fd)
            except FileNotFoundError:
                pass

    def close(self):
        if self.fd >= 0:
            os.close(self.fd)
            self.fd = -1

    def lock_for_application(self):
        try:
            fcntl.flock(self.fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise RuntimeError("This setup is open in another BatCave session. Quit that copy first.") from error
        try:
            # An fd opened before another process's reset may now refer to its
            # backup. Never unlock that old identity as the current profile.
            parent = _open_profile_parent(self.path.absolute())
            try:
                current = os.stat(self.path.name, dir_fd=parent, follow_symlinks=False)
                opened = os.fstat(self.fd)
                if not stat.S_ISDIR(current.st_mode) or (current.st_dev, current.st_ino) != (opened.st_dev, opened.st_ino):
                    raise RuntimeError("The setup folder changed. Reopen BatCave; no identity was replaced.")
            finally:
                os.close(parent)
        except Exception as error:
            fcntl.flock(self.fd, fcntl.LOCK_UN)
            raise RuntimeError("The setup folder changed or its location is not private. Reopen BatCave; no identity was replaced.") from error

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()


def profile_path():
    root = os.environ.get("XDG_DATA_HOME")
    if root and not os.path.isabs(root):
        raise ValueError("XDG_DATA_HOME must be an absolute path.")
    return Path(root or Path.home() / ".local" / "share") / "batcave" / "secure-preview"


def _open_profile_parent(path):
    """Walk protected ancestry without following links; return its pinned fd."""
    if not path.is_absolute() or ".." in path.parts:
        raise ValueError("Unexpected setup path. Nothing was moved.")
    fd = os.open(path.anchor, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
    try:
        for component in path.parent.parts[1:]:
            next_fd = os.open(component, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=fd)
            os.close(fd); fd = next_fd
            info = os.fstat(fd)
            # Root-owned sticky ancestors such as /tmp are safe above a private
            # parent: other users cannot replace our owned child there.
            sticky_root = info.st_uid == 0 and bool(info.st_mode & stat.S_ISVTX)
            if info.st_uid not in {0, os.getuid()} or info.st_mode & 0o022 and not sticky_root:
                raise PermissionError("The setup location permits other-user changes. Nothing was moved.")
        parent = os.fstat(fd)
        if parent.st_uid != os.getuid() or parent.st_mode & 0o022:
            raise PermissionError("The setup parent must be owned by you and protected from other-user changes. Nothing was moved.")
        return fd
    except Exception:
        os.close(fd)
        raise


def backup_profile(state):
    """Rename a stopped app's locked profile; keep its keyring secret untouched."""
    path = state.path.absolute()
    if path.name != "secure-preview" or path.parent == Path(path.anchor):
        raise ValueError("Unexpected BatCave profile. Nothing was moved.")
    for component in (path, *path.parents):
        info = component.lstat()
        if not stat.S_ISDIR(info.st_mode):
            raise ValueError("The setup path uses a link or is not a folder. Nothing was moved.")
    current, opened = path.stat(), os.fstat(state.fd)
    if current.st_uid != os.getuid() or stat.S_IMODE(current.st_mode) != 0o700:
        raise PermissionError("The setup folder must be private and owned by you. Nothing was moved.")
    if (current.st_dev, current.st_ino) != (opened.st_dev, opened.st_ino):
        raise ValueError("The setup folder changed. Nothing was moved.")
    state.lock_for_application()
    destination = path.with_name("secure-preview-backup-" + uuid.uuid4().hex)
    # Linux renameat2 refuses any existing destination, including a racing name.
    # A single rename preserves file modes and cannot leave a partial backup.
    import ctypes
    library = ctypes.CDLL(None, use_errno=True)
    rename = library.renameat2
    rename.argtypes = [ctypes.c_int, ctypes.c_char_p, ctypes.c_int, ctypes.c_char_p, ctypes.c_uint]
    rename.restype = ctypes.c_int
    parent = _open_profile_parent(path)
    try:
        current = os.stat(path.name, dir_fd=parent, follow_symlinks=False)
        if not stat.S_ISDIR(current.st_mode) or (current.st_dev, current.st_ino) != (opened.st_dev, opened.st_ino):
            raise ValueError("The setup folder changed. Nothing was moved.")
        if rename(parent, os.fsencode(path.name), parent, os.fsencode(destination.name), 1) != 0:
            raise OSError(ctypes.get_errno(), "Could not back up setup; the original folder was kept.")
    finally:
        os.close(parent)
    return destination
