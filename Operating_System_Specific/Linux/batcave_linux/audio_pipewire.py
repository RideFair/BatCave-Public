"""Experimental PipeWire playback adapter. No capture or wire capability enabled.

The audio owner retains transport/routing/mixing. Feed only its already accepted,
mixed stereo PCM to this adapter, after an explicit local Start decision.
"""
import collections
import fcntl
import hashlib
import json
import math
import os
import selectors
import struct
import subprocess
import sys
import threading
import time
import unicodedata
from dataclasses import dataclass

APP_ID = "dev.batcave.AudioPreview"
PCM_BYTES = 960 * 4
MAX_GRAPH_BYTES = 4 * 1024 * 1024


def native_id(value):
    if isinstance(value, str) and value.isascii() and value.isdecimal(): value = int(value)
    if type(value) is not int or not 0 < value < 2 ** 53: raise ValueError("Invalid PipeWire identity")
    return value


@dataclass(frozen=True)
class Output:
    endpoint_id: str
    display_name: str
    kind: int
    node_id: int
    serial: int
    # Local-only native node name; never put this object directly on the wire.
    native_name: str

    def public(self):
        return {"endpointId": self.endpoint_id, "displayName": self.display_name, "kind": self.kind,
                "isConnected": True, "isActive": True, "isDefault": False, "canReceiveStream": False}


def graph_outputs(graph):
    if not isinstance(graph, list) or len(graph) > 4096: raise ValueError("Unsupported PipeWire graph")
    result = []
    seen = set()
    for node in graph:
        if not isinstance(node, dict) or node.get("type") != "PipeWire:Interface:Node": continue
        props = node.get("info", {}).get("props", {})
        if props.get("media.class") != "Audio/Sink": continue
        name = props.get("node.name")
        if not isinstance(name, str) or not name or len(name) > 512: continue
        node_id, serial = native_id(node.get("id")), native_id(props.get("object.serial"))
        endpoint = "pw-" + hashlib.sha256(name.encode()).hexdigest()
        if endpoint in seen: raise ValueError("Ambiguous PipeWire endpoint")
        seen.add(endpoint)
        label = props.get("node.description", "Linux audio output")
        if not isinstance(label, str) or not 1 <= len(label) <= 160 or any(unicodedata.category(c) in {"Cc", "Cf", "Cs"} for c in label):
            label = "Linux audio output"
        # Device names containing 'bluetooth' are not evidence of transport type.
        kind = 1 if props.get("device.api") == "bluez5" else 0
        result.append(Output(endpoint, label, kind, node_id, serial, name))
    if len(result) > 32: raise ValueError("Audio output inventory exceeds 32 endpoints")
    return result


def capture_candidates(graph, output, owned_client_ids=()):
    """Review-only per-application tap plan. Never record a mixed sink monitor.

    This does not establish safe capture; microphone/source nodes are ineligible.
    A future native tap must validate exclusion and unchanged local links live.
    """
    if output not in graph_outputs(graph): raise ValueError("Selected output changed")
    connected = set()
    for item in graph:
        if item.get("type") == "PipeWire:Interface:Link":
            info = item.get("info", {})
            if info.get("input-node-id") == output.node_id: connected.add(info.get("output-node-id"))
    candidates = []
    for item in graph:
        if item.get("type") != "PipeWire:Interface:Node" or item.get("id") not in connected: continue
        props = item.get("info", {}).get("props", {})
        if props.get("media.class") != "Stream/Output/Audio": continue
        if (props.get("application.id") == APP_ID or str(props.get("node.name", "")).startswith("batcave-")
                or props.get("client.id") in owned_client_ids): continue
        candidates.append(native_id(props.get("object.serial")))
    return tuple(candidates)


def snapshot(timeout=2):
    """Bound pw-dump before parsing; no shell, raw graph logging or default changes."""
    process = subprocess.Popen(["/usr/bin/pw-dump"], stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
    data = bytearray(); deadline = time.monotonic() + timeout
    try:
        os.set_blocking(process.stdout.fileno(), False)
        with selectors.DefaultSelector() as selector:
            selector.register(process.stdout, selectors.EVENT_READ)
            while True:
                if time.monotonic() >= deadline: raise TimeoutError("PipeWire inventory timed out")
                if not selector.select(min(0.05, deadline - time.monotonic())): continue
                chunk = os.read(process.stdout.fileno(), min(65536, MAX_GRAPH_BYTES + 1 - len(data)))
                if not chunk: break
                data.extend(chunk)
                if len(data) > MAX_GRAPH_BYTES: raise ValueError("PipeWire inventory is too large")
        if process.wait(timeout=max(0.01, deadline-time.monotonic())) != 0: raise RuntimeError("PipeWire inventory unavailable")
        # Strict JSON helpers are the Linux input preview's existing bounded parser.
        from .identity import strict_json
        graph = strict_json(data.decode("utf-8", errors="strict")); graph_outputs(graph)
        return graph
    finally:
        if process.poll() is None: process.kill()
        process.wait(); process.stdout.close()


def playback_command(output):
    native_id(output.serial)
    properties = {"application.id": APP_ID, "application.name": "BatCave Audio Preview",
        "node.name": "batcave-audio-preview", "node.dont-fallback": True,
        "node.dont-reconnect": True, "node.dont-move": True, "state.restore-props": False,
        "target.object": str(output.serial)}
    # PipeWire 1.0.x treats '-' as raw stdin; --raw was introduced later.
    return ["/usr/bin/pw-play", "--rate=48000", "--channels=2", "--channel-map=FL,FR",
            "--format=f32", "--latency=480", "--target=" + str(output.serial),
            "--properties=" + json.dumps(properties, separators=(",", ":")), "-"]


class Playback:
    """Disabled by default; hardware policy/clock checks precede any release.

    A control/transport adapter must call stop on pause, disconnect, lock and sleep.
    No input hook, UI callback or realtime thread may call snapshot or enqueue.
    """
    supports_system_capture = False
    excludes_playback_from_capture = False
    can_receive_stream = False

    def __init__(self, output, *, experimental=False, graph_reader=snapshot, process_factory=subprocess.Popen):
        if not experimental: raise RuntimeError("Linux audio playback is not enabled for release")
        if sys.byteorder != "little": raise RuntimeError("Only little-endian native PCM is supported")
        if output not in graph_outputs(graph_reader()): raise RuntimeError("Selected output is unavailable")
        self.output, self.graph_reader = output, graph_reader
        self.queue = collections.deque(maxlen=8)
        self.lock = threading.Lock(); self.cancel = threading.Event(); self.failed = False
        self.process = process_factory(playback_command(output), stdin=subprocess.PIPE,
                                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, bufsize=0)
        try:
            os.set_blocking(self.process.stdin.fileno(), False)
            # Limit the kernel pipe to approximately one PCM packet.
            fcntl.fcntl(self.process.stdin.fileno(), fcntl.F_SETPIPE_SZ, 4096)
            self.worker = threading.Thread(target=self._run, daemon=True); self.worker.start()
            self.monitor = threading.Thread(target=self._monitor, daemon=True); self.monitor.start()
        except BaseException:
            self.process.kill(); self.process.wait(); self.process.stdin.close(); raise

    def enqueue(self, pcm):
        if not isinstance(pcm, bytes) or len(pcm) != PCM_BYTES: raise ValueError("Expected 10 ms stereo PCM")
        if any(not math.isfinite(value) or not -1 <= value <= 1 for value in struct.unpack("<960f", pcm)):
            raise ValueError("PCM must contain finite normalized samples")
        with self.lock:
            if self.cancel.is_set(): return False
            self.queue.append((time.monotonic(), pcm)); return True

    def _run(self):
        started = False
        try:
            while not self.cancel.is_set():
                now = time.monotonic()
                if self.process.poll() is not None: raise RuntimeError("Selected output stream ended")
                with self.lock:
                    while self.queue and now - self.queue[0][0] > 0.15: self.queue.popleft()
                    if not started and len(self.queue) >= 2: started = True
                    packet = self.queue.popleft() if started and self.queue else None
                if packet is None:
                    if not started:
                        self.cancel.wait(0.005); continue
                    packet = (time.monotonic(), bytes(PCM_BYTES))
                timestamp, pcm = packet; offset = 0
                while offset < len(pcm) and not self.cancel.is_set():
                    if time.monotonic() - timestamp > 0.15: raise TimeoutError("Native playback stalled")
                    try: written = os.write(self.process.stdin.fileno(), pcm[offset:])
                    except BlockingIOError: self.cancel.wait(0.002); continue
                    if written <= 0: raise RuntimeError("Native playback made no progress")
                    offset += written
        except Exception:
            self.failed = True
        finally:
            self.cancel.set()
            with self.lock: self.queue.clear()
            if self.process.poll() is None: self.process.terminate()
            try: self.process.wait(timeout=1)
            except subprocess.TimeoutExpired: self.process.kill(); self.process.wait()
            self.process.stdin.close()

    def _monitor(self):
        try:
            while not self.cancel.wait(0.25):
                if self.output not in graph_outputs(self.graph_reader()):
                    raise RuntimeError("Selected output changed")
        except Exception:
            self.failed = True; self.cancel.set()

    def stop(self):
        self.cancel.set()
        if threading.current_thread() is not self.worker: self.worker.join(4)
        if self.worker.is_alive():
            self.process.kill(); self.worker.join(1)
        if threading.current_thread() is not self.monitor: self.monitor.join(3)
        with self.lock: self.queue.clear()

    def capture(self):
        raise RuntimeError("Safe per-application capture exclusion has not been verified")
