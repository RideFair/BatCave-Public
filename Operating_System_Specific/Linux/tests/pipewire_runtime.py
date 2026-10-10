import json
import os
import subprocess
import tempfile
import shutil
import time
from pathlib import Path
from unittest.mock import patch

from batcave_linux.audio_pipewire import Playback, graph_outputs, snapshot

prefix = Path(os.environ.get('BATCAVE_PIPEWIRE_TEST_PREFIX', '/'))
binary = prefix / 'usr/bin'
real_popen = subprocess.Popen
def invoke(command, **kwargs):
    if command[0].startswith('/usr/bin/pw-'): command = [str(binary / Path(command[0]).name), *command[1:]]
    if Path(command[0]).name == 'pw-play': kwargs['stderr'] = subprocess.PIPE
    return real_popen(command, **kwargs)

if os.environ.get('BATCAVE_TEST_PIPEWIRE') != '1': raise SystemExit('Run only inside an isolated dbus-run-session with BATCAVE_TEST_PIPEWIRE=1')
os.environ['DBUS_SYSTEM_BUS_ADDRESS'] = os.environ['DBUS_SESSION_BUS_ADDRESS']

with tempfile.TemporaryDirectory(prefix='batcave-private-pipewire-') as directory:
    os.environ['XDG_RUNTIME_DIR'] = directory
    os.environ['XDG_STATE_HOME'] = directory + '/state'
    os.environ['XDG_CONFIG_HOME'] = directory + '/config'
    os.environ['XDG_DATA_HOME'] = directory + '/data'
    # WirePlumber 0.4 fixture: disable all hardware discovery before policy starts.
    config = Path(directory) / 'wireplumber'
    shutil.copytree(prefix/'usr/share/wireplumber', config)
    for subtree in ('main.lua.d', 'bluetooth.lua.d'):
        (config/subtree/'99-batcave-no-hardware.lua').write_text('''
if alsa_monitor then alsa_monitor.enabled = false end
if bluez_monitor then bluez_monitor.enabled = false end
if v4l2_monitor then v4l2_monitor.enabled = false end
if libcamera_monitor then libcamera_monitor.enabled = false end
''')
    os.environ['WIREPLUMBER_CONFIG_DIR'] = str(config)
    os.environ['PIPEWIRE_REMOTE'] = 'pipewire-0'
    daemon = real_popen([str(binary/'pipewire')], stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
    manager = None; playback = None
    try:
        deadline = time.monotonic() + 5
        while not (Path(directory)/'pipewire-0').exists():
            if time.monotonic() > deadline:
                daemon.terminate(); daemon.wait(3)
                raise RuntimeError('Private PipeWire daemon did not start: ' + daemon.stderr.read(4096).decode())
            time.sleep(.02)
        manager = real_popen([str(binary/'wireplumber')], stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        result = subprocess.run([str(binary/'pw-cli'), 'create-node', 'adapter',
            '{ factory.name=support.null-audio-sink node.name=batcave-synthetic-speakers node.description="Synthetic speakers" media.class=Audio/Sink audio.position=[ FL FR ] object.linger=true }'],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=5)
        if result.returncode: raise RuntimeError('Synthetic PipeWire sink creation failed: '+result.stderr.decode())
        subprocess.run([str(binary/'pw-cli'), 'create-node', 'adapter',
            '{ factory.name=support.null-audio-sink node.name=batcave-synthetic-backup media.class=Audio/Sink audio.position=[ FL FR ] object.linger=true }'],
            check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=5)
        with patch('batcave_linux.audio_pipewire.subprocess.Popen', side_effect=invoke):
            deadline = time.monotonic() + 5
            while True:
                graph = snapshot()
                outputs = graph_outputs(graph)
                assert all(o.native_name in {'batcave-synthetic-speakers', 'batcave-synthetic-backup'} for o in outputs), 'Unexpected hardware output in private graph'
                assert not any(n.get('info',{}).get('props',{}).get('media.class')=='Audio/Source' for n in graph), 'Unexpected hardware source in private graph'
                output = next((o for o in outputs if o.native_name=='batcave-synthetic-speakers'), None)
                if output: break
                if time.monotonic() > deadline: raise RuntimeError('Synthetic sink missing')
                time.sleep(.05)
            playback = Playback(output, experimental=True, process_factory=invoke)
            playback.enqueue(bytes(3840)); playback.enqueue(bytes(3840))
            time.sleep(.2)
            if playback.failed:
                manager.terminate(); manager.wait(3)
                raise RuntimeError('Native playback failed: ' + playback.process.stderr.read(4096).decode() + manager.stderr.read(4096).decode())
            graph = snapshot()
            own = [n for n in graph if n.get('type')=='PipeWire:Interface:Node' and n.get('info',{}).get('props',{}).get('application.id')=='dev.batcave.AudioPreview']
            if len(own)!=1: raise RuntimeError('Tagged native playback node missing')
            props = own[0]['info']['props']
            assert str(props['target.object'])==str(output.serial)
            assert props['node.dont-fallback'] in (True, 'true')
            own_id = own[0]['id']
            links = [n['info'] for n in graph if n.get('type')=='PipeWire:Interface:Link']
            assert any(link.get('output-node-id')==own_id and link.get('input-node-id')==output.node_id for link in links), 'Native playback has no link to the selected sink'
            subprocess.run([str(binary/'pw-cli'), 'destroy', str(output.node_id)], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=5)
            assert playback.cancel.wait(3), 'Output loss did not stop native playback'
            playback.stop()
            assert playback.process.poll() is not None
            remaining = snapshot()
            assert any(o.native_name=='batcave-synthetic-backup' for o in graph_outputs(remaining))
            assert not any(n.get('info',{}).get('output-node-id')==own_id for n in remaining if n.get('type')=='PipeWire:Interface:Link'), 'Playback silently relinked to another output'
            print('PASS: real isolated PipeWire/WirePlumber selected-sink playback link, tagged exclusion identity and output-loss teardown; synthetic silence only.')
    finally:
        if playback: playback.stop()
        for process in (manager, daemon):
            if process:
                process.terminate()
                try: process.wait(timeout=3)
                except subprocess.TimeoutExpired: process.kill(); process.wait()
