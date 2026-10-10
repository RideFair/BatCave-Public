import Foundation
@preconcurrency import Network
import Security

private let batCaveProtocolName = "batcave.input.v0"

private struct JSONPayload: @unchecked Sendable {
    let value: [String: Any]
}

protocol LanBridgeRemoteInputReceiver: AnyObject {
    func lanPeer(_ peer: LanBridgePeer, didDisconnect reason: String)
    func lanPeer(_ peer: LanBridgePeer, didReceiveTestStart message: RemoteTestStartMessage, sourceDeviceId: String)
    func lanPeer(_ peer: LanBridgePeer, didReceiveTestStop message: RemoteTestStopMessage, sourceDeviceId: String)
    func lanPeer(_ peer: LanBridgePeer, didReceiveCursorHandoff message: CursorHandoffMessage, sourceDeviceId: String)
    func lanPeer(_ peer: LanBridgePeer, didReceiveMouseDelta message: MouseDeltaMessage, sourceDeviceId: String)
    func lanPeer(_ peer: LanBridgePeer, didReceiveMouseButton message: MouseButtonMessage, sourceDeviceId: String)
    func lanPeer(_ peer: LanBridgePeer, didReceiveMouseWheel message: MouseWheelMessage, sourceDeviceId: String)
    func lanPeer(_ peer: LanBridgePeer, didReceiveKeyInput message: KeyInputMessage, sourceDeviceId: String)
    func lanPeer(_ peer: LanBridgePeer, didReceiveShortcutInput message: ShortcutInputMessage, sourceDeviceId: String)
    func lanPeer(_ peer: LanBridgePeer, didReceiveClipboardText message: ClipboardTextMessage, sourceDeviceId: String)
}

struct RemoteTestStartMessage {
    let durationSeconds: Int
}

struct RemoteTestStopMessage {
    let reason: String
}

struct CursorHandoffMessage {
    let linkId: String
    let segmentPosition: Double
    let sourceDisplayId: String
    let sourceEdge: String
    let sourcePosition: Double
    let targetDeviceId: String
    let targetDisplayId: String
    let targetEdge: String
    let targetPosition: Double
    let targetX: Int
    let targetY: Int
}

struct MouseDeltaMessage {
    let deltaX: Int
    let deltaY: Int
}

struct MouseButtonMessage {
    let button: String
    let isDown: Bool
}

struct MouseWheelMessage {
    let verticalDelta: Int
    let horizontalDelta: Int
}

struct KeyInputMessage {
    let virtualKey: Int
    let scanCode: Int
    let isKeyUp: Bool
    let isExtended: Bool
    let text: String?
}

struct ShortcutInputMessage {
    let action: String
}

struct ClipboardTextMessage {
    let format: String
    let text: String
}

private struct ScreenBoundsPayload {
    let left: Int
    let top: Int
    let width: Int
    let height: Int
    let right: Int
    let bottom: Int

    init(_ bounds: DisplayBounds) {
        left = Int(bounds.minX.rounded())
        top = Int(bounds.minY.rounded())
        width = Int(bounds.width.rounded())
        height = Int(bounds.height.rounded())
        right = Int(bounds.maxX.rounded())
        bottom = Int(bounds.maxY.rounded())
    }

    var dictionary: [String: Any] {
        [
            "left": left,
            "top": top,
            "width": width,
            "height": height,
            "right": right,
            "bottom": bottom
        ]
    }
}

final class LanBridgePeer: @unchecked Sendable {
    static let defaultPort: UInt16 = 47870
    private let allowInsecureLoopback: Bool
    private let allowLegacyClipboard: Bool

    private let queue = DispatchQueue(label: "BatCaveMacOSAgent.LanBridgePeer")
    private let deviceName = {
        let hostName = ProcessInfo.processInfo.hostName
        if !hostName.isEmpty {
            return hostName
        }

        return Host.current().localizedName ?? "macOS"
    }()
    private let shutdownLock = NSLock()
    private var listener: NWListener?
    private var connection: NWConnection?
    private var peerLabel: String?
    private var listenPort: UInt16?
    private var localBounds = ScreenBoundsPayload(DisplayBounds(minX: 0, maxX: 0, minY: 0, maxY: 0))
    private var receiveBuffer = Data()
    private var disposed = false
    private var reconnectEndpoint: (host: String, port: UInt16)?
    private var reconnectTask: DispatchWorkItem?
    private var reconnectAttempt = 0
    private var connectionReady = false
    let secureIdentity: PairingIdentity?
    private var approvedCard: PairingCard?
    private var authenticatedDeviceId: String?
    private var secureReceiveEnabled = false
    private var secureOwnership = false
    private var clipboardEnabled = false
    private var outgoing = [Data]()
    private var outgoingBytes = 0
    private var sending = false
    private var lastFailure: String?
    private var sessionId = UUID().uuidString
    weak var remoteInputReceiver: LanBridgeRemoteInputReceiver?

    init(secureIdentity: PairingIdentity? = nil, allowInsecureLoopback: Bool = false, allowLegacyClipboard: Bool = false) {
        self.secureIdentity = secureIdentity
        self.allowInsecureLoopback = allowInsecureLoopback
        self.allowLegacyClipboard = allowLegacyClipboard
    }

    var localDeviceId: String {
        secureIdentity?.card.deviceId ?? "macos:\(deviceName)"
    }

    var statusDescription: String {
        queue.sync {
            if secureIdentity != nil {
                if authenticatedDeviceId != nil { return secureReceiveEnabled ? "Ready for sharing" : "Connected securely · input paused" }
                if let lastFailure { return lastFailure }
                return connection == nil ? "Not connected" : "Connecting securely…"
            }
            if connectionReady { return "Connected: \(peerLabel ?? "peer")" }
            if let endpoint = reconnectEndpoint { return "Waiting for Windows: \(endpoint.host):\(endpoint.port)" }
            return connection == nil ? "Disconnected" : "Connecting: \(peerLabel ?? "peer")"
        }
    }

    var isAuthenticated: Bool { queue.sync { authenticatedDeviceId != nil } }
    var isSecureInputEnabled: Bool { queue.sync { authenticatedDeviceId != nil && secureReceiveEnabled } }

    func approve(_ card: PairingCard?) {
        queue.async {
            self.reconnectEndpoint = nil
            self.reconnectTask?.cancel(); self.reconnectTask = nil
            if let connection = self.connection { self.clearConnectionIfCurrent(connection, reason: "Pairing changed", reconnect: false) }
            self.approvedCard = card
            self.lastFailure = nil
        }
    }

    func enableSecureInput(_ enabled: Bool, clipboard: Bool = false) {
        queue.async {
            self.secureReceiveEnabled = enabled
            self.clipboardEnabled = clipboard
            if !enabled {
                self.secureOwnership = false
                self.remoteInputReceiver?.lanPeer(self, didDisconnect: "Sharing paused")
            }
        }
    }

    func stopConnecting() {
        queue.async {
            self.reconnectEndpoint = nil
            self.reconnectTask?.cancel(); self.reconnectTask = nil
            if let connection = self.connection { self.clearConnectionIfCurrent(connection, reason: "Disconnected", reconnect: false) }
        }
    }

    // Called from the UI/clipboard thread, never while this transport queue is held.
    func clipboardSession() -> String? {
        queue.sync { clipboardSessionOnReceiverQueue() }
    }

    // Input receiver callbacks execute on the serial connection queue.
    func clipboardSessionOnReceiverQueue() -> String? {
        guard secureIdentity != nil else { return allowLegacyClipboard && connectionReady ? sessionId : nil }
        return clipboardEnabled && secureReceiveEnabled && secureOwnership && authenticatedDeviceId != nil ? sessionId : nil
    }

    func updateLocalBounds(_ bounds: DisplayBounds) {
        let payload = ScreenBoundsPayload(bounds)
        queue.async {
            self.localBounds = payload
        }
    }

    func printState() {
        queue.async {
            let listening = self.listenPort.map { "ON:\($0)" } ?? "OFF"
            let peer = self.peerLabel ?? "none"
            emit("LAN state: listening=\(listening) peer=\(peer) deviceId=\(self.localDeviceId)")
        }
    }

    func startListening(port: UInt16 = LanBridgePeer.defaultPort) {
        emit("Network listeners are disabled in the Mac preview; use the approved TLS connector.")
    }

    func connect(host: String, port: UInt16 = LanBridgePeer.defaultPort, automaticallyReconnect: Bool = false) {
        guard secureIdentity != nil || (allowInsecureLoopback && Self.isLiteralLoopback(host)) else {
            emit("Connection refused: use secure pairing, or explicit loopback development only.")
            return
        }
        // Do not let a modified localhost resolver route plaintext off-device.
        let target = secureIdentity == nil && host.lowercased() == "localhost" ? "127.0.0.1" : host
        queue.async {
            guard !self.disposed else {
                emit("LAN connect unavailable: peer disposed")
                return
            }

            self.reconnectTask?.cancel()
            self.reconnectTask = nil
            self.reconnectAttempt = 0
            self.reconnectEndpoint = automaticallyReconnect ? (target, port) : nil
            self.connectOnQueue(host: target, port: port)
        }
    }

    private static func isLiteralLoopback(_ host: String) -> Bool {
        ["localhost", "127.0.0.1", "::1"].contains(host.lowercased())
    }

    private static func tcpParameters() -> NWParameters {
        let tcp = NWProtocolTCP.Options()
        tcp.noDelay = true
        tcp.enableKeepalive = true
        tcp.keepaliveIdle = 5
        tcp.keepaliveInterval = 2
        tcp.keepaliveCount = 3
        tcp.connectionTimeout = 5
        tcp.connectionDropTime = 10
        return NWParameters(tls: nil, tcp: tcp)
    }

    private func connectOnQueue(host: String, port: UInt16) {
        guard !disposed, let nwPort = NWEndpoint.Port(rawValue: port), port > 0 else { return }
        guard secureIdentity != nil || (allowInsecureLoopback && Self.isLiteralLoopback(host)) else { return }
        var parameters = Self.tcpParameters()
        if let identity = secureIdentity {
            guard let approvedCard, let localIdentity = sec_identity_create(identity.identity) else {
                lastFailure = "Approve the Windows pairing card first"
                return
            }
            let tls = NWProtocolTLS.Options()
            sec_protocol_options_set_min_tls_protocol_version(tls.securityProtocolOptions, .TLSv13)
            sec_protocol_options_set_max_tls_protocol_version(tls.securityProtocolOptions, .TLSv13)
            sec_protocol_options_set_local_identity(tls.securityProtocolOptions, localIdentity)
            sec_protocol_options_set_verify_block(tls.securityProtocolOptions, { _, trust, complete in
                let ref = sec_trust_copy_ref(trust).takeRetainedValue()
                guard let chain = SecTrustCopyCertificateChain(ref) as? [SecCertificate], let leaf = chain.first else { complete(false); return }
                complete(approvedCard.matches(leaf))
            }, queue)
            parameters = NWParameters(tls: tls, tcp: parameters.defaultProtocolStack.transportProtocol as? NWProtocolTCP.Options ?? .init())
        }
        lastFailure = nil
        emit("LAN connecting to \(host):\(port)...")
        let candidate = NWConnection(host: NWEndpoint.Host(host), port: nwPort, using: parameters)
        replaceConnection(candidate, peerLabel: "\(host):\(port)")
        // NWConnection can remain in .waiting indefinitely, even with TCP timeout options.
        queue.asyncAfter(deadline: .now() + 8) { [weak self, weak candidate] in
            guard let self, let candidate, self.connection === candidate, !self.connectionReady else { return }
            self.clearConnectionIfCurrent(candidate, reason: "connection attempt timed out")
        }
    }

    private func scheduleReconnect() {
        guard !disposed, let endpoint = reconnectEndpoint, reconnectTask == nil else { return }
        let delay = min(pow(2.0, Double(min(reconnectAttempt, 4))), 10.0)
        reconnectAttempt += 1
        emit("LAN reconnect in \(Int(delay))s to \(endpoint.host):\(endpoint.port)")
        let task = DispatchWorkItem { [weak self] in
            guard let self, !self.disposed else { return }
            self.reconnectTask = nil
            self.connectOnQueue(host: endpoint.host, port: endpoint.port)
        }
        reconnectTask = task
        queue.asyncAfter(deadline: .now() + delay, execute: task)
    }

    func publishEdgeHandoff(edge: String, virtualX: Int, virtualY: Int, deltaX: Int, deltaY: Int, bounds: DisplayBounds) {
        let payload: [String: Any] = [
            "edge": edge.lowercased(),
            "virtualX": virtualX,
            "virtualY": virtualY,
            "deltaX": deltaX,
            "deltaY": deltaY,
            "localBounds": ScreenBoundsPayload(bounds).dictionary
        ]

        sendMessage(type: "cursor.edgeHandoff", payload: payload)
    }

    func publishClipboardText(_ text: String) {
        guard text.utf8.count <= 64 * 1024 else { return }
        let payload: [String: Any] = [
            "format": "text/plain",
            "text": text
        ]

        sendMessage(type: "clipboard.text", payload: payload)
    }

    func sendDeviceHello() {
        queue.async {
            self.sendDeviceHelloOnQueue()
        }
    }

    func disconnect(reason: String) {
        queue.async {
            if let connection = self.connection {
                self.clearConnectionIfCurrent(connection, reason: reason)
            }
        }
    }

    func dispose() {
        shutdownLock.lock()
        defer {
            shutdownLock.unlock()
        }

        queue.async {
            self.disposed = true
            self.reconnectEndpoint = nil
            self.reconnectTask?.cancel()
            self.reconnectTask = nil
            self.listener?.cancel()
            self.listener = nil
            if let connection = self.connection {
                self.clearConnectionIfCurrent(connection, reason: "transport disposed")
            }
            self.peerLabel = nil
            self.listenPort = nil
            self.receiveBuffer.removeAll(keepingCapacity: false)
        }
    }

    private func replaceConnection(_ newConnection: NWConnection, peerLabel: String) {
        reconnectTask?.cancel()
        reconnectTask = nil
        if let connection {
            clearConnectionIfCurrent(connection, reason: "peer replaced", reconnect: false)
        }
        connectionReady = false
        sessionId = UUID().uuidString
        authenticatedDeviceId = nil
        secureReceiveEnabled = false
        secureOwnership = false
        receiveBuffer.removeAll(keepingCapacity: true)

        connection = newConnection
        self.peerLabel = peerLabel

        newConnection.stateUpdateHandler = { [weak self, weak newConnection] state in
            guard let self, let newConnection else {
                return
            }

            self.queue.async {
                self.handleConnectionState(state, connection: newConnection, peerLabel: peerLabel)
            }
        }

        receiveLoop(newConnection, peerLabel: peerLabel)
        newConnection.start(queue: queue)
    }

    private func handleConnectionState(_ state: NWConnection.State, connection: NWConnection, peerLabel: String) {
        guard self.connection === connection else {
            return
        }

        switch state {
        case .ready:
            connectionReady = true
            reconnectAttempt = 0
            emit("LAN peer connected: \(peerLabel)")
            sendDeviceHelloOnQueue()
            if secureIdentity != nil {
                queue.asyncAfter(deadline: .now() + 5) { [weak self, weak connection] in
                    guard let self, let connection, self.connection === connection, self.authenticatedDeviceId == nil else { return }
                    self.clearConnectionIfCurrent(connection, reason: "Windows did not send an authenticated hello")
                }
            }
        case .failed(let error):
            emit("LAN peer failed \(peerLabel): \(error)")
            clearConnectionIfCurrent(connection, reason: "connection failed: \(error)")
        case .cancelled:
            clearConnectionIfCurrent(connection, reason: "connection cancelled")
        case .waiting(let error):
            clearConnectionIfCurrent(connection, reason: "connection waiting: \(error)")
        default:
            break
        }
    }

    private func receiveLoop(_ activeConnection: NWConnection, peerLabel: String) {
        activeConnection.receive(minimumIncompleteLength: 1, maximumLength: 4096) { [weak self, weak activeConnection] data, _, isComplete, error in
            guard let self, let activeConnection else {
                return
            }

            self.queue.async {
                guard self.connection === activeConnection else {
                    return
                }

                if let data, !data.isEmpty {
                    self.receiveBuffer.append(data)
                    self.drainReceiveBuffer()
                    guard self.connection === activeConnection else { return }
                }

                if let error {
                    emit("LAN receive failed from \(peerLabel): \(error)")
                    self.clearConnectionIfCurrent(activeConnection, reason: "receive failed: \(error)")
                    return
                }

                if isComplete {
                    emit("LAN peer disconnected: \(peerLabel)")
                    self.clearConnectionIfCurrent(activeConnection, reason: "peer disconnected")
                    return
                }

                self.receiveLoop(activeConnection, peerLabel: peerLabel)
            }
        }
    }

    private func drainReceiveBuffer() {
        while let newlineIndex = receiveBuffer.firstIndex(of: 0x0A) {
            let lineData = receiveBuffer[..<newlineIndex]
            if lineData.count > 256 * 1024 { rejectFrame("Message exceeds 256 KB"); return }
            receiveBuffer.removeSubrange(...newlineIndex)

            guard !lineData.isEmpty else {
                continue
            }

            if let line = String(data: lineData, encoding: .utf8) {
                logReceivedMessage(line)
            } else {
                emit("LAN RX invalid utf8 bytes=\(lineData.count)")
                rejectFrame("Invalid UTF-8 message"); return
            }
            if connection == nil { return }
        }
        if receiveBuffer.count > 256 * 1024 { rejectFrame("Message exceeds 256 KB") }
    }

    private func rejectFrame(_ reason: String) {
        if let connection { clearConnectionIfCurrent(connection, reason: reason) }
    }

    private func sendDeviceHelloOnQueue() {
        let payload: [String: Any] = [
            "deviceId": localDeviceId,
            "deviceName": secureIdentity?.card.displayName ?? deviceName,
            "operatingSystem": "macOS",
            "agentName": "BatCave.MacOSAgent",
            "virtualDesktopBounds": localBounds.dictionary
        ]

        sendMessageOnQueue(type: "device.hello", payload: payload)
    }

    private func sendMessage(type: String, payload: [String: Any]) {
        let payload = JSONPayload(value: payload)
        queue.async {
            self.sendMessageOnQueue(type: type, payload: payload.value)
        }
    }

    private func sendMessageOnQueue(type: String, payload: [String: Any]) {
        guard let connection, connectionReady else {
            return
        }
        if secureIdentity != nil && type != "device.hello" {
            guard authenticatedDeviceId != nil else { return }
            if type == "clipboard.text" && !(clipboardEnabled && secureReceiveEnabled && secureOwnership) { return }
        }
        if type == "clipboard.text", clipboardSessionOnReceiverQueue() == nil { return }

        let envelope: [String: Any] = [
            "protocol": batCaveProtocolName,
            "type": type,
            "messageId": UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased(),
            "sourceDeviceId": localDeviceId,
            "sentAtUtc": currentTimestamp(),
            "payload": payload
        ]

        do {
            var data = try JSONSerialization.data(withJSONObject: envelope)
            guard data.count <= 256 * 1024 else {
                clearConnectionIfCurrent(connection, reason: "Outgoing message exceeds 256 KB"); return
            }
            data.append(0x0A)
            if secureIdentity != nil {
                guard data.count <= 256 * 1024 + 1, outgoing.count < 4096, outgoingBytes + data.count <= 1024 * 1024 else {
                    clearConnectionIfCurrent(connection, reason: "Outgoing message limit exceeded"); return
                }
                outgoing.append(data); outgoingBytes += data.count
                sendNext(connection)
                return
            }

            if type == "clipboard.text" {
                guard allowLegacyClipboard && connectionReady else { return }
                guard readString(payload, "text").utf8.count <= 64 * 1024 else { rejectFrame("Clipboard exceeds 64 KB"); return }
            }
            connection.send(content: data, completion: .contentProcessed { [weak self, weak connection] error in
                guard let self, let connection else { return }
                self.queue.async {
                    guard self.connection === connection else { return }
                    if let error {
                        emit("LAN send \(type) failed: \(error)")
                        self.clearConnectionIfCurrent(connection, reason: "send failed: \(error)")
                    } else {
                        emit("LAN TX \(type) to \(self.peerLabel ?? "peer")")
                    }
                }
            })
        } catch {
            emit("LAN serialize \(type) failed: \(error.localizedDescription)")
        }
    }

    private func sendNext(_ candidate: NWConnection) {
        guard connection === candidate, !sending, let data = outgoing.first else { return }
        sending = true
        candidate.send(content: data, completion: .contentProcessed { [weak self, weak candidate] error in
            guard let self, let candidate else { return }
            self.queue.async {
                guard self.connection === candidate else { return }
                if let error { self.clearConnectionIfCurrent(candidate, reason: "Send failed: \(error)"); return }
                self.outgoing.removeFirst(); self.outgoingBytes -= data.count; self.sending = false
                self.sendNext(candidate)
            }
        })
    }

    private func logReceivedMessage(_ json: String) {
        guard let data = json.data(using: .utf8) else {
            emit("LAN RX invalid utf8")
            return
        }

        do {
            guard let root = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
                emit("LAN RX invalid json bytes=\(data.count)")
                return
            }

            let protocolName = readString(root, "protocol")
            let type = readString(root, "type")
            let source = readString(root, "sourceDeviceId")

            guard protocolName == batCaveProtocolName else {
                if secureIdentity != nil { rejectFrame("Unsupported protocol"); return }
                emit("LAN RX unsupported protocol=\(protocolName) type=\(type) from \(source)")
                return
            }

            guard let payload = root["payload"] as? [String: Any] else {
                if secureIdentity != nil { rejectFrame("Missing message payload"); return }
                emit("LAN RX \(type) from \(source) missing payload")
                return
            }

            if secureIdentity != nil {
                guard let approvedCard, source == approvedCard.deviceId else { rejectFrame("Peer identity mismatch"); return }
                if type == "device.hello" {
                    guard readString(payload, "deviceId") == source else { rejectFrame("Peer hello identity mismatch"); return }
                    authenticatedDeviceId = source
                    lastFailure = nil
                    return
                }
                guard authenticatedDeviceId == source else { rejectFrame("Hello required before messages"); return }
                guard validSecurePayload(type: type, payload: payload) else { rejectFrame("Invalid message fields"); return }
                if type == "control.testStop" {
                    secureOwnership = false
                } else if type == "cursor.handoff" {
                    guard secureReceiveEnabled else { return }
                    secureOwnership = readString(payload, "targetDeviceId") == localDeviceId
                } else if type.hasPrefix("input.") {
                    guard secureReceiveEnabled && secureOwnership else { return }
                } else if type == "clipboard.text" {
                    guard clipboardEnabled && secureReceiveEnabled && secureOwnership else { return }
                    guard readString(payload, "text").utf8.count <= 64 * 1024 else { rejectFrame("Clipboard exceeds 64 KB"); return }
                } else if type == "control.testStart" {
                    guard secureReceiveEnabled else { return }
                }
                // Routine secure logs never record key codes, text, or clipboard data.
                handleActionMessage(type: type, sourceDeviceId: source, payload: payload)
                return
            }

            if type == "clipboard.text" {
                guard allowLegacyClipboard && connectionReady else { return }
                guard readString(payload, "text").utf8.count <= 64 * 1024 else { rejectFrame("Clipboard exceeds 64 KB"); return }
            }
            if shouldLogMessage(type) {
                emit("LAN RX \(type) from \(source): \(describePayload(type: type, payload: payload))")
            }
            handleActionMessage(type: type, sourceDeviceId: source, payload: payload)
        } catch {
            emit("LAN RX invalid json bytes=\(data.count)")
            if secureIdentity != nil { rejectFrame("Invalid JSON message") }
        }
    }

    private func shouldLogMessage(_ type: String) -> Bool {
        type != "input.mouseDelta" && type != "input.key"
    }

    private func validSecurePayload(type: String, payload: [String: Any]) -> Bool {
        func integer(_ key: String, limit: Int = 1_000_000) -> Bool {
            guard let value = payload[key] as? NSNumber, CFGetTypeID(value) != CFBooleanGetTypeID() else { return false }
            let n = value.doubleValue
            return n.isFinite && n.rounded() == n && abs(n) <= Double(limit)
        }
        func boolean(_ key: String) -> Bool {
            guard let value = payload[key] as? NSNumber else { return false }
            return CFGetTypeID(value) == CFBooleanGetTypeID()
        }
        func label(_ key: String, max: Int = 256) -> Bool {
            guard let value = payload[key] as? String else { return false }
            return !value.isEmpty && value.utf8.count <= max && !value.unicodeScalars.contains { CharacterSet.controlCharacters.contains($0) }
        }
        switch type {
        case "input.mouseDelta": return integer("deltaX") && integer("deltaY")
        case "input.mouseWheel": return integer("verticalDelta") && integer("horizontalDelta")
        case "input.mouseButton":
            return ["left", "right", "middle", "center", "x1", "x2"].contains(readString(payload, "button")) && boolean("isDown")
        case "input.key":
            return integer("virtualKey", limit: 65535) && readInt(payload, "virtualKey") >= 0
                && integer("scanCode", limit: 65535) && readInt(payload, "scanCode") >= 0
                && boolean("isKeyUp") && boolean("isExtended")
                && (payload["text"] == nil || payload["text"] is NSNull || (payload["text"] as? String).map { $0.utf8.count <= 8192 } == true)
        case "input.shortcut": return ["selectAll", "copy", "paste", "cut"].contains(readString(payload, "action"))
        case "cursor.handoff":
            guard label("targetDeviceId", max: 128), label("targetDisplayId"), integer("targetX"), integer("targetY"),
                  let position = payload["targetPosition"] as? NSNumber else { return false }
            return ["left", "right", "top", "bottom"].contains(readString(payload, "targetEdge"))
                && position.doubleValue.isFinite && (0...1).contains(position.doubleValue)
        case "clipboard.text": return readString(payload, "format") == "text/plain" && payload["text"] is String && readString(payload, "text").utf8.count <= 64 * 1024
        case "control.testStart": return integer("durationSeconds", limit: 86400) && readInt(payload, "durationSeconds") > 0
        case "control.testStop": return label("reason", max: 1024)
        default: return true // Unknown capabilities are ignored, never injected.
        }
    }

    private func handleActionMessage(type: String, sourceDeviceId: String, payload: [String: Any]) {
        guard let receiver = remoteInputReceiver else {
            return
        }

        switch type {
        case "control.testStart":
            receiver.lanPeer(
                self,
                didReceiveTestStart: RemoteTestStartMessage(durationSeconds: readInt(payload, "durationSeconds")),
                sourceDeviceId: sourceDeviceId
            )
        case "control.testStop":
            receiver.lanPeer(
                self,
                didReceiveTestStop: RemoteTestStopMessage(reason: readString(payload, "reason")),
                sourceDeviceId: sourceDeviceId
            )
        case "cursor.handoff":
            receiver.lanPeer(
                self,
                didReceiveCursorHandoff: CursorHandoffMessage(
                    linkId: readString(payload, "linkId"),
                    segmentPosition: readDouble(payload, "segmentPosition"),
                    sourceDisplayId: readString(payload, "sourceDisplayId"),
                    sourceEdge: readString(payload, "sourceEdge"),
                    sourcePosition: readDouble(payload, "sourcePosition"),
                    targetDeviceId: readString(payload, "targetDeviceId"),
                    targetDisplayId: readString(payload, "targetDisplayId"),
                    targetEdge: readString(payload, "targetEdge"),
                    targetPosition: readDouble(payload, "targetPosition"),
                    targetX: readInt(payload, "targetX"),
                    targetY: readInt(payload, "targetY")
                ),
                sourceDeviceId: sourceDeviceId
            )
        case "input.mouseDelta":
            receiver.lanPeer(
                self,
                didReceiveMouseDelta: MouseDeltaMessage(
                    deltaX: readInt(payload, "deltaX"),
                    deltaY: readInt(payload, "deltaY")
                ),
                sourceDeviceId: sourceDeviceId
            )
        case "input.mouseButton":
            receiver.lanPeer(
                self,
                didReceiveMouseButton: MouseButtonMessage(
                    button: readString(payload, "button"),
                    isDown: readBool(payload, "isDown")
                ),
                sourceDeviceId: sourceDeviceId
            )
        case "input.mouseWheel":
            receiver.lanPeer(
                self,
                didReceiveMouseWheel: MouseWheelMessage(
                    verticalDelta: readInt(payload, "verticalDelta"),
                    horizontalDelta: readInt(payload, "horizontalDelta")
                ),
                sourceDeviceId: sourceDeviceId
            )
        case "input.key":
            receiver.lanPeer(
                self,
                didReceiveKeyInput: KeyInputMessage(
                    virtualKey: readInt(payload, "virtualKey"),
                    scanCode: readInt(payload, "scanCode"),
                    isKeyUp: readBool(payload, "isKeyUp"),
                    isExtended: readBool(payload, "isExtended"),
                    text: readOptionalString(payload, "text")
                ),
                sourceDeviceId: sourceDeviceId
            )
        case "input.shortcut":
            receiver.lanPeer(
                self,
                didReceiveShortcutInput: ShortcutInputMessage(action: readString(payload, "action")),
                sourceDeviceId: sourceDeviceId
            )
        case "clipboard.text":
            receiver.lanPeer(
                self,
                didReceiveClipboardText: ClipboardTextMessage(
                    format: readString(payload, "format"),
                    text: readString(payload, "text")
                ),
                sourceDeviceId: sourceDeviceId
            )
        default:
            break
        }
    }

    private func describePayload(type: String, payload: [String: Any]) -> String {
        switch type {
        case "device.hello":
            return describeDeviceHello(payload)
        case "cursor.edgeHandoff":
            return describeEdgeHandoff(payload)
        case "cursor.handoff":
            return describeCursorHandoff(payload)
        case "input.mouseDelta":
            return describeMouseDelta(payload)
        case "input.mouseButton":
            return describeMouseButton(payload)
        case "input.mouseWheel":
            return describeMouseWheel(payload)
        case "input.key":
            return describeKeyInput(payload)
        case "input.shortcut":
            return describeShortcutInput(payload)
        case "clipboard.text":
            return describeClipboardText(payload)
        case "control.testStart":
            return "duration=\(readInt(payload, "durationSeconds"))s"
        case "control.testStop":
            return "reason=\(readString(payload, "reason"))"
        case "file.offer":
            return describeFileOffer(payload)
        default:
            return "unhandled payload bytes=\(payload.description.utf8.count)"
        }
    }

    private func describeDeviceHello(_ payload: [String: Any]) -> String {
        let bounds = payload["virtualDesktopBounds"] as? [String: Any]
        let boundsDescription = bounds.map(describeBounds) ?? "bounds=missing"

        return "deviceId=\(readString(payload, "deviceId")) name=\(readString(payload, "deviceName")) os=\(readString(payload, "operatingSystem")) agent=\(readString(payload, "agentName")) \(boundsDescription)"
    }

    private func describeEdgeHandoff(_ payload: [String: Any]) -> String {
        "edge=\(readString(payload, "edge")) virtual=(\(readInt(payload, "virtualX")),\(readInt(payload, "virtualY"))) delta=(\(readInt(payload, "deltaX")),\(readInt(payload, "deltaY")))"
    }

    private func describeCursorHandoff(_ payload: [String: Any]) -> String {
        let linkId = readString(payload, "linkId")
        let segmentPosition = readDouble(payload, "segmentPosition")
        let sourceDisplayId = readString(payload, "sourceDisplayId")
        let sourceEdge = readString(payload, "sourceEdge")
        let sourcePosition = readDouble(payload, "sourcePosition")
        let targetDeviceId = readString(payload, "targetDeviceId")
        let targetDisplayId = readString(payload, "targetDisplayId")
        let targetEdge = readString(payload, "targetEdge")
        let targetPosition = readDouble(payload, "targetPosition")
        let targetX = readInt(payload, "targetX")
        let targetY = readInt(payload, "targetY")

        return String(
            format: "link=%@ segment=%.3f source=%@ %@ %.3f target=%@/%@ %@ %.3f point=(%d,%d)",
            linkId,
            segmentPosition,
            sourceDisplayId,
            sourceEdge,
            sourcePosition,
            targetDeviceId,
            targetDisplayId,
            targetEdge,
            targetPosition,
            targetX,
            targetY
        )
    }

    private func describeMouseDelta(_ payload: [String: Any]) -> String {
        "delta=(\(readInt(payload, "deltaX")),\(readInt(payload, "deltaY")))"
    }

    private func describeMouseButton(_ payload: [String: Any]) -> String {
        "button=\(readString(payload, "button")) down=\(readBool(payload, "isDown"))"
    }

    private func describeMouseWheel(_ payload: [String: Any]) -> String {
        "wheel=(\(readInt(payload, "horizontalDelta")),\(readInt(payload, "verticalDelta")))"
    }

    private func describeKeyInput(_ payload: [String: Any]) -> String {
        let virtualKey = readInt(payload, "virtualKey")
        let scanCode = readInt(payload, "scanCode")
        let isKeyUp = readBool(payload, "isKeyUp")
        let isExtended = readBool(payload, "isExtended")
        let textLength = readOptionalString(payload, "text")?.count ?? 0

        return String(format: "vk=0x%X scan=0x%X up=%@ extended=%@ textLength=%d", virtualKey, scanCode, String(isKeyUp), String(isExtended), textLength)
    }

    private func describeShortcutInput(_ payload: [String: Any]) -> String {
        "action=\(readString(payload, "action"))"
    }

    private func describeClipboardText(_ payload: [String: Any]) -> String {
        let format = readString(payload, "format")
        let text = readString(payload, "text")
        let length = text == "missing" ? 0 : text.count
        return "format=\(format) textLength=\(length)"
    }

    private func describeFileOffer(_ payload: [String: Any]) -> String {
        "transferId=\(readString(payload, "transferId")) fileName=\(readString(payload, "fileName")) byteLength=\(readInt(payload, "byteLength"))"
    }

    private func describeBounds(_ payload: [String: Any]) -> String {
        "bounds=left=\(readInt(payload, "left")),top=\(readInt(payload, "top")),width=\(readInt(payload, "width")),height=\(readInt(payload, "height")),right=\(readInt(payload, "right")),bottom=\(readInt(payload, "bottom"))"
    }

    private func readString(_ payload: [String: Any], _ key: String) -> String {
        guard let value = payload[key], !(value is NSNull) else {
            return "missing"
        }

        return "\(value)"
    }

    private func readOptionalString(_ payload: [String: Any], _ key: String) -> String? {
        guard let value = payload[key], !(value is NSNull) else {
            return nil
        }

        let text = "\(value)"
        return text == "missing" ? nil : text
    }

    private func readInt(_ payload: [String: Any], _ key: String) -> Int {
        if let value = payload[key] as? Int {
            return value
        }

        if let value = payload[key] as? NSNumber {
            return value.intValue
        }

        return 0
    }

    private func readDouble(_ payload: [String: Any], _ key: String) -> Double {
        if let value = payload[key] as? Double {
            return value
        }

        if let value = payload[key] as? NSNumber {
            return value.doubleValue
        }

        return 0
    }

    private func readBool(_ payload: [String: Any], _ key: String) -> Bool {
        if let value = payload[key] as? Bool {
            return value
        }

        if let value = payload[key] as? NSNumber {
            return value.boolValue
        }

        return false
    }

    private func escapeForLog(_ text: String) -> String {
        text
            .replacingOccurrences(of: "\r", with: "\\r")
            .replacingOccurrences(of: "\n", with: "\\n")
            .replacingOccurrences(of: "\t", with: "\\t")
    }

    private func clearConnectionIfCurrent(_ activeConnection: NWConnection, reason: String, reconnect: Bool = true) {
        guard connection === activeConnection else {
            return
        }

        connection = nil
        connectionReady = false
        authenticatedDeviceId = nil
        secureReceiveEnabled = false
        secureOwnership = false
        outgoing.removeAll(); outgoingBytes = 0; sending = false
        lastFailure = secureIdentity == nil ? nil : "Connection lost · \(reason)"
        peerLabel = nil
        receiveBuffer.removeAll(keepingCapacity: true)
        activeConnection.cancel()
        remoteInputReceiver?.lanPeer(self, didDisconnect: reason)
        if reconnect { scheduleReconnect() }
    }

    private func currentTimestamp() -> String {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter.string(from: Date())
    }

    private static func remoteEndpointLabel(for connection: NWConnection) -> String {
        switch connection.endpoint {
        case .hostPort(let host, let port):
            return "\(host):\(port)"
        default:
            return "\(connection.endpoint)"
        }
    }
}
