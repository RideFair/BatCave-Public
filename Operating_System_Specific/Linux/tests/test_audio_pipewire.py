import json
import os
import struct
import threading
import time
import unittest

from batcave_linux.audio_pipewire import APP_ID, PCM_BYTES, Playback, capture_candidates, graph_outputs, playback_command


def node(identifier, serial, name, media_class="Audio/Sink", **properties):
    return {"id": identifier, "type": "PipeWire:Interface:Node", "info": {"props": dict(properties,
        **{"object.serial": serial, "node.name": name, "media.class": media_class})}}


class PipeConsumer:
    """Real bounded OS pipe, paced synthetic consumer; no PipeWire/hardware claim."""
    def __init__(self, command, **_):
        read, write = os.pipe(); self.stdin = os.fdopen(write, "wb", buffering=0)
        self.read = read; self.command = command; self.returncode = None; self.received = bytearray()
        self.thread = threading.Thread(target=self.consume, daemon=True); self.thread.start()
    def consume(self):
        try:
            while self.returncode is None:
                chunk = os.read(self.read, PCM_BYTES)
                if not chunk: break
                self.received.extend(chunk); time.sleep(0.01)
        finally: os.close(self.read)
    def poll(self): return self.returncode
    def terminate(self): self.returncode = 0
    def kill(self): self.returncode = -9
    def wait(self, timeout=None): return self.returncode


class AudioPipeWireTests(unittest.TestCase):
    def setUp(self):
        self.graph = [node(10, "100", "synthetic-speakers", **{"node.description": "Speakers"}),
                      node(11, "101", "synthetic-bt", **{"device.api": "bluez5"})]
        self.output = graph_outputs(self.graph)[0]

    def test_inventory_only_sinks_opaque_ids_and_os_bluetooth(self):
        self.graph += [node(12, 102, "microphone", "Audio/Source"), node(13, 103, "bluetooth-name-only")]
        outputs = graph_outputs(self.graph)
        self.assertEqual([output.kind for output in outputs], [0, 1, 0])
        public = json.dumps([output.public() for output in outputs])
        self.assertNotIn("synthetic-", public)
        self.assertNotIn("serial", public)
        self.assertTrue(all(not output.public()["canReceiveStream"] for output in outputs))
        self.assertEqual(len(outputs[0].endpoint_id), 67)

    def test_capture_plan_excludes_microphones_own_playback_and_mixed_monitor(self):
        self.graph += [node(20, 200, "other-app", "Stream/Output/Audio"),
                       node(21, 201, "batcave-received", "Stream/Output/Audio"),
                       node(22, 202, "generic-name", "Stream/Output/Audio", **{"application.id": APP_ID}),
                       node(23, 203, "microphone", "Audio/Source"),
                       node(24, 204, "owned-client", "Stream/Output/Audio", **{"client.id": 500})]
        for identifier in range(20, 25):
            self.graph.append({"type": "PipeWire:Interface:Link", "info": {"input-node-id": 10, "output-node-id": identifier}})
        self.assertEqual(capture_candidates(self.graph, self.output, (500,)), (200,))
        self.assertFalse(Playback.supports_system_capture or Playback.excludes_playback_from_capture)

    def test_target_serial_no_fallback_and_release_gate(self):
        command = playback_command(self.output)
        self.assertIn("--target=100", command)
        properties = json.loads(next(argument.split("=", 1)[1] for argument in command if argument.startswith("--properties=")))
        for key in ("node.dont-fallback", "node.dont-reconnect", "node.dont-move"): self.assertIs(properties[key], True)
        with self.assertRaises(RuntimeError): Playback(self.output)
        with self.assertRaises(ValueError): graph_outputs([node(10, True, "bad")])
        with self.assertRaises(ValueError): graph_outputs(self.graph + [self.graph[0]])

    def test_bounded_pcm_prefill_stale_drop_and_stop(self):
        consumer = []
        def factory(*args, **kwargs): consumer.append(PipeConsumer(*args, **kwargs)); return consumer[-1]
        playback = Playback(self.output, experimental=True, graph_reader=lambda: self.graph, process_factory=factory)
        try:
            for pcm in (bytes(4), struct.pack("<960f", *([float("nan")] * 960)), struct.pack("<960f", *([2.0] * 960))):
                with self.assertRaises(ValueError): playback.enqueue(pcm)
            block = struct.pack("<960f", *([0.25] * 960))
            playback.enqueue(block); time.sleep(0.02); self.assertFalse(consumer[0].received)
            with playback.lock:
                playback.queue.append((time.monotonic() - 1, block))
                for _ in range(20): playback.queue.append((time.monotonic(), block))
                self.assertEqual(len(playback.queue), 8)
            deadline = time.monotonic() + 1
            while not consumer[0].received and time.monotonic() < deadline: time.sleep(0.01)
            self.assertTrue(consumer[0].received)
        finally: playback.stop()
        self.assertFalse(playback.worker.is_alive() or playback.monitor.is_alive() or playback.queue)
        self.assertFalse(playback.enqueue(bytes(PCM_BYTES)))

    def test_selected_node_loss_or_serial_reuse_stops_without_other_output(self):
        for replace in (False, True):
            with self.subTest(replace=replace):
                graph = [*self.graph]
                playback = Playback(self.output, experimental=True, graph_reader=lambda: graph, process_factory=PipeConsumer)
                try:
                    graph[0] = node(10, 999, "synthetic-speakers") if replace else node(10, 999, "other-output")
                    self.assertTrue(playback.cancel.wait(2))
                    self.assertTrue(playback.failed)
                finally: playback.stop()
                self.assertIn("--target=100", playback.process.command)


if __name__ == "__main__": unittest.main()
