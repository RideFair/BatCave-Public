"""Visible link-and-share approval, usable once on the first pinned connection."""
import time


class InitialConsent:
    def __init__(self, peer, source, session, now=None):
        self.peer_id = peer.device_id; self.fingerprint = peer.fingerprint
        self.source = source; self.session = session
        self.expires = (time.monotonic() if now is None else now) + 30
        self.generation = None; self.closed = False

    def bind(self, peer, source, generation, now=None):
        if not self.valid(peer, source, now) or self.generation is not None:
            self.closed = True; return False
        self.generation = generation
        return True

    def valid(self, peer, source, now=None):
        return (not self.closed and (time.monotonic() if now is None else now) < self.expires
                and peer is not None and peer.device_id == self.peer_id and peer.fingerprint == self.fingerprint
                and type(source) is bool and source == self.source)

    def take(self, peer, source, generation, now=None):
        if not self.valid(peer, source, now) or self.generation != generation or self.generation is None: return False
        self.closed = True
        return True

    def close(self): self.closed = True
