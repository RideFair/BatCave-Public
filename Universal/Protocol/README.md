# BatCave Protocol v0

BatCave agents exchange newline-delimited JSON messages. Version `v0` is intentionally small: it describes device identity, topology, and input within an authenticated session. A hello message is not authentication.

Every message is a JSON object with this envelope:

```json
{
  "protocol": "batcave.input.v0",
  "type": "device.hello",
  "messageId": "7c0a7e4a30a749e48e2f408c7f8f15ac",
  "sourceDeviceId": "windows:DESKTOP-EXAMPLE",
  "sentAtUtc": "2026-05-16T18:22:40.1234567+00:00",
  "payload": {}
}
```

## Message Types

### `device.hello`

Announces a local agent and its current virtual desktop bounds.

```json
{
  "deviceId": "windows:DESKTOP-EXAMPLE",
  "deviceName": "DESKTOP-EXAMPLE",
  "operatingSystem": "Windows",
  "agentName": "BatCave.WindowsAgent",
  "virtualDesktopBounds": {
    "left": -2560,
    "top": -2160,
    "width": 5120,
    "height": 3600,
    "right": 2559,
    "bottom": 1439
  }
}
```

### `cursor.edgeHandoff`

Reports that the local virtual cursor crossed an outer desktop edge. This remains useful telemetry and a fallback for agents that do not yet understand topology-aware handoffs.

```json
{
  "edge": "right",
  "virtualX": 2565,
  "virtualY": 657,
  "deltaX": 6,
  "deltaY": 0,
  "localBounds": {
    "left": -2560,
    "top": -2160,
    "width": 5120,
    "height": 3600,
    "right": 2559,
    "bottom": 1439
  }
}
```

### `cursor.handoff`

Reports that the local virtual cursor crossed a configured topology bridge range. The crossing is mapped by normalized position along the source bridge segment, then projected onto the target bridge segment. The target OS should materialize the target point using its own coordinate space before it takes cursor ownership.

```json
{
  "linkId": "059a07157599444bb04d2a5d32ddb5fa",
  "segmentPosition": 0.42,
  "sourceDisplayId": "windows:example:screen:1",
  "sourceEdge": "right",
  "sourcePosition": 0.59,
  "sourceX": 2310,
  "sourceY": -240,
  "targetDeviceId": "macos:example",
  "targetDisplayId": "macos:example:screen:1",
  "targetEdge": "left",
  "targetPosition": 0.42,
  "targetX": 3267,
  "targetY": -522
}
```

### `input.mouseDelta`

Represents physical mouse movement after cursor ownership has moved to a remote device.

```json
{
  "deltaX": 4,
  "deltaY": -2
}
```

### `input.mouseButton`

Represents a physical mouse button event after cursor ownership has moved to a remote device.

```json
{
  "button": "left",
  "isDown": true
}
```

Known button names are `left`, `right`, `middle`, `x1`, and `x2`.

### `input.mouseWheel`

Represents physical wheel movement after cursor ownership has moved to a remote device. Windows wheel deltas are sent in native `120`-unit wheel increments; receivers convert them to local scroll events.

```json
{
  "verticalDelta": -120,
  "horizontalDelta": 0
}
```

### `input.key`

Represents a physical keyboard event captured by an OS-specific backend.

```json
{
  "virtualKey": 65,
  "scanCode": 30,
  "isKeyUp": false,
  "isExtended": false,
  "text": "a"
}
```

### `input.shortcut`

Represents a platform-neutral editing shortcut captured by an OS-specific backend. The receiver maps the action to its local platform convention.

```json
{
  "action": "paste"
}
```

Known actions are `copy`, `paste`, `cut`, and `selectAll`.

### `clipboard.text`

Represents a text clipboard update from one device. Receivers should apply it to the local system clipboard without re-publishing the same content back to the source.

```json
{
  "format": "text/plain",
  "text": "Copied text"
}
```

### `control.testStart`

Announces a timed inter-device handoff test.

```json
{
  "durationSeconds": 60
}
```

### `control.testStop`

Announces that the timed inter-device handoff test has ended.

```json
{
  "reason": "timer expired"
}
```

### `file.offer`

Reserved for tray handoff. The final file protocol will need transfer IDs, file metadata, receiver decisions, progress, and cancellation.

```json
{
  "transferId": "5d3b5d35aeb940799949a27ca6bb94dc",
  "fileName": "example.txt",
  "byteLength": 1024
}
```

## Transport and authorization

The Windows secure profile accepts newline-delimited UTF-8 JSON inside mutual
TLS 1.3 on port 47871. Both certificates must be explicitly approved by leaf
SHA-256 fingerprint. The source device ID and hello payload must match the
identity approved for the certificate; a hello must arrive within five seconds.
Protocol fields and network addresses cannot independently grant control.

Frames are limited to 256 KiB excluding the newline, and decoded text clipboard
updates to 64 KiB. Windows outbound queues are bounded and fail closed on
overflow. Input and clipboard actions require a current session and active
sharing, and text clipboard sharing requires the user's opt-in.

The Mac authenticated transport remains pending. Its network listener is
disabled in this snapshot. Source-only plaintext development connections are
restricted to loopback and must not be used for cross-device deployment.
