"""User-owned, bounded, symlink-resistant state and atomic replacement."""

import json
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
            raise PermissionError("The preview state directory must be owned by you with mode 0700.")
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
                raise PermissionError("Preview state must be an owned regular file with mode 0600.")
            data = stream.read(limit + 1)
            if len(data) > limit:
                raise ValueError("Preview state exceeds its size limit.")
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

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()


def profile_path():
    root = os.environ.get("XDG_DATA_HOME")
    if root and not os.path.isabs(root):
        raise ValueError("XDG_DATA_HOME must be an absolute path.")
    return Path(root or Path.home() / ".local" / "share") / "batcave" / "secure-preview"
