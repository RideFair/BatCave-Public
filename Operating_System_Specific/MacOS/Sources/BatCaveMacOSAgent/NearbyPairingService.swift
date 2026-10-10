import Foundation
import CryptoKit
@preconcurrency import Network

struct NearbyPairingConfirmation: Sendable {
    let sessionID: UUID
    let peer: NearbyPairingProtocol.Descriptor
    let code: String
    let initiator: Bool
}
struct NearbyPairingResult: Sendable {
    let sessionID: UUID
    let peer: NearbyPairingProtocol.Descriptor
    let address: String
    let initiator: Bool
}
struct NearbyPairingState: Sendable {
    let isOpen: Bool
    let listeningPort: UInt16
    let busy: Bool
    let confirmation: NearbyPairingConfirmation?
}

struct NearbyPairingAttemptLimiter {
    private var attempts = [Date]()
    mutating func accept(at now: Date) -> Bool {
        attempts.removeAll { now.timeIntervalSince($0) > 60 }
        guard attempts.count < 6, attempts.last.map({ now.timeIntervalSince($0) >= 5 }) ?? true else { return false }
        attempts.append(now); return true
    }
}

// A foreground-only public identity exchange. This service has no pin store,
// TLS input transport, clipboard, or OS input access. Completion means both
// explicit approvals verified for this exact session; the caller saves trust.
final class NearbyPairingService: @unchecked Sendable {
    static let defaultPort: UInt16 = 47873
    struct Timings: Sendable {
        var window: TimeInterval = 180
        var handshake: TimeInterval = 10
        var approval: TimeInterval = 120
    }
    private enum Phase { case commit, reveal, proof, approval }
    private final class Session: @unchecked Sendable {
        let id = UUID()
        let connection: NWConnection
        let initiator: Bool
        let address: String
        let expected: NearbyComputer?
        var wireID: String = ""
        let nonce: Data
        var localBytes = Data()
        var remoteCommitment = Data()
        var transcript = Data()
        var peer: NearbyPairingProtocol.Descriptor?
        var phase = Phase.commit
        var buffer = Data()
        var readFrames = 0
        var localApproved = false
        var localApprovalSent = false
        var remoteApproved = false
        var confirmation: NearbyPairingConfirmation?
        var handshakeTimer: DispatchWorkItem?
        var lifetimeTimer: DispatchWorkItem?
        let handshakeDeadline: TimeInterval
        let approvalDeadline: TimeInterval
        init(connection: NWConnection, initiator: Bool, address: String, expected: NearbyComputer?, nonce: Data,
             handshakeDeadline: TimeInterval, approvalDeadline: TimeInterval) {
            self.connection = connection; self.initiator = initiator; self.address = address
            self.expected = expected; self.nonce = nonce
            self.handshakeDeadline = handshakeDeadline; self.approvalDeadline = approvalDeadline
        }
        var role: NearbyPairingProtocol.Role { initiator ? .initiator : .responder }
        var otherRole: NearbyPairingProtocol.Role { initiator ? .responder : .initiator }
    }
    private let queue = DispatchQueue(label: "BatCave.NearbyPairing")
    private let local: NearbyPairingProtocol.Descriptor
    private let sign: @Sendable (Data) throws -> Data
    private let acceptPeer: @Sendable (NearbyPairingProtocol.Descriptor) -> Bool
    private let allowAddress: @Sendable (String) -> Bool
    private let onUpdate: @MainActor @Sendable (NearbyPairingState) -> Void
    private let onPaired: @MainActor @Sendable (NearbyPairingResult) -> Void
    private let onFailure: @MainActor @Sendable (String) -> Void
    private let timings: Timings
    private let monotonicNow: @Sendable () -> TimeInterval
    private var listener: NWListener?
    private var session: Session?
    private var open = false
    private var port: UInt16 = 0
    private var windowTimer: DispatchWorkItem?
    private var windowID: UUID?
    private var requestedPort: UInt16 = 0
    private var requestedAddress: String?
    private var windowDeadline: TimeInterval = 0
    private var generation: UInt64 = 0
    private var revision: UInt64 = 0
    private var attemptLimiter = NearbyPairingAttemptLimiter()
    private var completedSession: UUID?
    private var disposed = false

    init(local: NearbyPairingProtocol.Descriptor, sign: @escaping @Sendable (Data) throws -> Data,
         acceptPeer: @escaping @Sendable (NearbyPairingProtocol.Descriptor) -> Bool,
         allowAddress: @escaping @Sendable (String) -> Bool = { NearbyDiscovery.isOnLAN($0, interfaces: NearbyDiscovery.interfaces()) },
         timings: Timings = Timings(),
         monotonicNow: @escaping @Sendable () -> TimeInterval = { ProcessInfo.processInfo.systemUptime },
         onUpdate: @escaping @MainActor @Sendable (NearbyPairingState) -> Void,
         onPaired: @escaping @MainActor @Sendable (NearbyPairingResult) -> Void,
         onFailure: @escaping @MainActor @Sendable (String) -> Void) {
        self.local = local; self.sign = sign; self.acceptPeer = acceptPeer; self.allowAddress = allowAddress
        self.timings = timings; self.monotonicNow = monotonicNow
        self.onUpdate = onUpdate; self.onPaired = onPaired; self.onFailure = onFailure
    }

    func openWindow(port: UInt16 = NearbyPairingService.defaultPort, address: String? = nil) {
        queue.async { [weak self] in self?.openOnQueue(port: port, address: address) }
    }
    func closeWindow() { queue.sync { closeOnQueue() } }
    func cancel() { queue.async { [weak self] in self?.cancelOnQueue() } }
    func dispose() { queue.sync { disposed = true; closeOnQueue() } }
    func link(_ computer: NearbyComputer) {
        queue.async { [weak self] in
            guard let self else { return }
            guard self.open, self.monotonicNow() < self.windowDeadline, self.port > 0, self.session == nil, computer.pairing,
                  Date().timeIntervalSince(computer.seen) <= 12, self.allowAddress(computer.address),
                  let address = IPv4Address(computer.address), let port = NWEndpoint.Port(rawValue: computer.pairingPort) else {
                self.failure("Open BatCave on both computers and search again."); return
            }
            let parameters = Self.tcpParameters()
            let connection = NWConnection(host: .ipv4(address), port: port, using: parameters)
            self.begin(connection: connection, initiator: true, address: computer.address, expected: computer)
        }
    }
    func approve(sessionID: UUID) {
        queue.async { [weak self] in
            guard let self, let current = self.session, current.id == sessionID,
                  current.phase == .approval, current.confirmation != nil, !current.localApproved,
                  let peer = current.peer else { return }
            guard self.ensureLive(current) else { return }
            guard self.acceptPeer(peer) else {
                self.end(current, error: "This computer's approval changed. Cancel and select it again."); return
            }
            do {
                current.localApproved = true
                let signature = try self.sign(NearbyPairingProtocol.signatureMessage(transcript: current.transcript, role: current.role, stage: .approve))
                guard self.ensureLive(current) else { return }
                guard self.acceptPeer(peer) else { self.end(current, error: "This computer's approval changed. Select it again to retry."); return }
                try self.send(.approve(session: current.wireID, signature: signature), current: current) {
                    current.localApprovalSent = true; self.completeIfApproved(current)
                }
                self.publish()
            } catch { self.end(current, error: "Could not approve this computer. Select it again to retry.") }
        }
    }

    private static func tcpParameters() -> NWParameters {
        let tcp = NWProtocolTCP.Options(); tcp.noDelay = true; tcp.connectionTimeout = 10
        let value = NWParameters(tls: nil, tcp: tcp); value.includePeerToPeer = false
        return value
    }
    private func openOnQueue(port requestedPort: UInt16, address: String?) {
        guard !disposed else { return }
        if open, listener != nil, self.requestedPort == requestedPort, requestedAddress == address {
            // Refresh the foreground deadline without racing cancellation of a
            // working listener against an immediate bind to its occupied port.
            cancelOnQueue(); scheduleWindowDeadline(); publish(); return
        }
        closeOnQueue(); generation &+= 1; let currentGeneration = generation
        do {
            try NearbyPairingProtocol.validateDescriptor(local)
            let parameters = Self.tcpParameters()
            let bindHost: NWEndpoint.Host
            if let address {
                guard let ipv4 = IPv4Address(address) else { throw PairingError("Invalid local address.") }
                bindHost = .ipv4(ipv4)
            } else { bindHost = .ipv4(.any) }
            parameters.requiredLocalEndpoint = .hostPort(host: bindHost, port: NWEndpoint.Port(rawValue: requestedPort) ?? .any)
            // Exclusive binding: never fall back to the authenticated input
            // listener, and report a bind error instead of pretending to pair.
            let candidate = try NWListener(using: parameters)
            listener = candidate; open = true; self.requestedPort = requestedPort; requestedAddress = address
            candidate.stateUpdateHandler = { [weak self, weak candidate] state in
                guard let self, let candidate, self.generation == currentGeneration, self.listener === candidate else { return }
                switch state {
                case .ready: self.port = candidate.port?.rawValue ?? 0; self.publish()
                case .failed: self.closeOnQueue(); self.failure("Could not open nearby pairing. Close another BatCave pairing window and search again.")
                case .cancelled: break
                default: break
                }
            }
            candidate.newConnectionHandler = { [weak self] connection in
                guard let self, self.generation == currentGeneration, self.open, self.monotonicNow() < self.windowDeadline,
                      let remote = Self.observedAddress(connection), self.allowAddress(remote),
                      self.session == nil, self.attemptLimiter.accept(at: Date()) else { connection.cancel(); return }
                self.begin(connection: connection, initiator: false, address: remote, expected: nil)
            }
            candidate.start(queue: queue)
            scheduleWindowDeadline(); publish()
        } catch {
            closeOnQueue(); failure("Could not open nearby pairing. Check this computer's identity and search again.")
        }
    }
    private func scheduleWindowDeadline() {
        windowTimer?.cancel(); let identity = UUID(); windowID = identity
        windowDeadline = monotonicNow() + timings.window
        let timer = DispatchWorkItem { [weak self] in
            guard let self, self.windowID == identity, self.open else { return }
            self.closeOnQueue(); self.failure("Nearby pairing timed out. Search again when both computers are ready.")
        }
        windowTimer = timer; queue.asyncAfter(deadline: .now() + timings.window, execute: timer)
    }
    private static func observedAddress(_ connection: NWConnection) -> String? {
        let endpoint = connection.currentPath?.remoteEndpoint ?? connection.endpoint
        guard case .hostPort(let host, _) = endpoint, case .ipv4(let address) = host else { return nil }
        return String(describing: address)
    }
    private func begin(connection: NWConnection, initiator: Bool, address: String, expected: NearbyComputer?) {
        do {
            let current = Session(connection: connection, initiator: initiator, address: address, expected: expected,
                nonce: try NearbyPairingProtocol.randomNonce(), handshakeDeadline: monotonicNow() + timings.handshake,
                approvalDeadline: monotonicNow() + timings.approval)
            if initiator { current.wireID = try NearbyPairingProtocol.randomSession() }
            session = current; completedSession = nil
            let handshake = DispatchWorkItem { [weak self, weak current] in
                guard let self, let current, self.session === current, current.phase != .approval else { return }
                self.end(current, error: "Could not link this computer. Select it again when BatCave is open on both.")
            }
            current.handshakeTimer = handshake; queue.asyncAfter(deadline: .now() + timings.handshake, execute: handshake)
            let lifetime = DispatchWorkItem { [weak self, weak current] in
                guard let self, let current, self.session === current else { return }
                self.end(current, error: "Code comparison timed out. Select the computer again to retry.")
            }
            current.lifetimeTimer = lifetime; queue.asyncAfter(deadline: .now() + timings.approval, execute: lifetime)
            connection.stateUpdateHandler = { [weak self, weak current] state in
                guard let self, let current, self.session === current else { return }
                switch state {
                case .ready:
                    do {
                        guard self.ensureLive(current) else { return }
                        if current.initiator { try self.sendCommit(current) }
                        self.receive(current)
                    } catch { self.end(current, error: "Could not start pairing. Select this computer again to retry.") }
                case .failed, .cancelled: self.end(current, error: "Connection lost. Select this computer again to retry.")
                default: break
                }
            }
            connection.start(queue: queue); publish()
        } catch { connection.cancel(); failure("Could not start pairing. Select this computer again to retry.") }
    }
    private func sendCommit(_ current: Session) throws {
        current.localBytes = try NearbyPairingProtocol.identityBytes(session: current.wireID, role: current.role,
            descriptor: local, nonce: current.nonce)
        try send(.commit(session: current.wireID, commitment: Data(SHA256.hash(data: current.localBytes))), current: current)
    }
    private func sendReveal(_ current: Session) throws {
        try send(.reveal(session: current.wireID, nonce: current.nonce, descriptor: local), current: current)
    }
    private func send(_ frame: NearbyPairingProtocol.Frame, current: Session, completed: (@Sendable () -> Void)? = nil) throws {
        guard ensureLive(current) else { return }
        var bytes = try NearbyPairingProtocol.encodeFrame(frame); bytes.append(10)
        current.connection.send(content: bytes, completion: .contentProcessed { [weak self, weak current] error in
            guard let self, let current, self.session === current else { return }
            guard self.ensureLive(current) else { return }
            if error != nil { self.end(current, error: "Connection lost. Select this computer again to retry."); return }
            completed?()
        })
    }
    private func receive(_ current: Session) {
        current.connection.receive(minimumIncompleteLength: 1, maximumLength: 4096) { [weak self, weak current] bytes, _, done, error in
            guard let self, let current, self.session === current else { return }
            guard self.ensureLive(current) else { return }
            do {
                if let bytes { current.buffer.append(bytes) }
                while let newline = current.buffer.firstIndex(of: 10) {
                    guard newline <= 16384, current.readFrames < 4 else { throw PairingError("Pairing frame limit exceeded.") }
                    let data = Data(current.buffer.prefix(upTo: newline))
                    current.buffer.removeSubrange(...newline); current.readFrames += 1
                    guard current.readFrames != 4 || current.buffer.isEmpty else { throw PairingError("Unexpected extra pairing data.") }
                    let frame = try NearbyPairingProtocol.parseFrame(data)
                    try self.handle(frame, current: current)
                    if self.session !== current { return }
                }
                guard current.buffer.count <= 16384 else { throw PairingError("Pairing frame limit exceeded.") }
                if error != nil || done { self.end(current, error: "Connection lost. Select this computer again to retry.") }
                else { self.receive(current) }
            } catch { self.end(current, error: "Could not verify this computer. Cancel and choose it again.") }
        }
    }
    private func handle(_ frame: NearbyPairingProtocol.Frame, current: Session) throws {
        guard ensureLive(current) else { return }
        if current.wireID.isEmpty {
            guard !current.initiator, case .commit(let wireID, _) = frame else { throw PairingError("Commitment required.") }
            current.wireID = wireID
        }
        guard frame.session == current.wireID else { throw PairingError("Pairing session changed.") }
        switch (current.phase, frame) {
        case (.commit, .commit(_, let commitment)):
            current.remoteCommitment = commitment
            if !current.initiator { try sendCommit(current) }
            current.phase = .reveal
            if current.initiator { try sendReveal(current) }
        case (.reveal, .reveal(_, let nonce, let peer)):
            let peerBytes = try NearbyPairingProtocol.identityBytes(session: current.wireID, role: current.otherRole,
                descriptor: peer, nonce: nonce)
            guard NearbyPairingProtocol.constantTimeEqual(current.remoteCommitment, Data(SHA256.hash(data: peerBytes))),
                  Data(peer.card.deviceId.utf8) != Data(local.card.deviceId.utf8), peer.card.certificateSha256 != local.card.certificateSha256,
                  acceptPeer(peer) else { throw PairingError("Identity or commitment changed.") }
            if let expected = current.expected {
                guard Data(peer.card.deviceId.utf8) == Data(expected.deviceID.utf8), peer.card.certificateSha256 == expected.fingerprint else {
                    throw PairingError("The selected computer's identity changed.")
                }
            }
            current.peer = peer
            if !current.initiator { try sendReveal(current) }
            current.transcript = current.initiator
                ? NearbyPairingProtocol.transcript(initiator: current.localBytes, responder: peerBytes)
                : NearbyPairingProtocol.transcript(initiator: peerBytes, responder: current.localBytes)
            current.phase = .proof
            let signature = try sign(NearbyPairingProtocol.signatureMessage(transcript: current.transcript, role: current.role, stage: .proof))
            try send(.proof(session: current.wireID, signature: signature), current: current)
        case (.proof, .proof(_, let signature)):
            guard let peer = current.peer,
                  try NearbyPairingProtocol.verify(descriptor: peer,
                    message: NearbyPairingProtocol.signatureMessage(transcript: current.transcript, role: current.otherRole, stage: .proof),
                    signature: signature), acceptPeer(peer) else { throw PairingError("Identity proof failed.") }
            guard ensureLive(current) else { return }
            current.handshakeTimer?.cancel(); current.handshakeTimer = nil; current.phase = .approval
            current.confirmation = NearbyPairingConfirmation(sessionID: current.id, peer: peer,
                code: try NearbyPairingProtocol.code(current.transcript), initiator: current.initiator)
            publish()
        case (.approval, .approve(_, let signature)):
            guard !current.remoteApproved, let peer = current.peer,
                  try NearbyPairingProtocol.verify(descriptor: peer,
                    message: NearbyPairingProtocol.signatureMessage(transcript: current.transcript, role: current.otherRole, stage: .approve),
                    signature: signature), acceptPeer(peer) else { throw PairingError("Approval proof failed.") }
            guard ensureLive(current) else { return }
            current.remoteApproved = true; completeIfApproved(current)
        default: throw PairingError("Pairing message arrived out of order.")
        }
    }
    private func completeIfApproved(_ current: Session) {
        guard session === current, current.localApproved, current.localApprovalSent, current.remoteApproved,
              let peer = current.peer, open else { return }
        guard ensureLive(current) else { return }
        guard acceptPeer(peer) else {
            end(current, error: "This computer's approval changed. Cancel and select it again."); return
        }
        let result = NearbyPairingResult(sessionID: current.id, peer: peer, address: current.address, initiator: current.initiator)
        completedSession = current.id; let currentGeneration = generation
        end(current, error: nil)
        DispatchQueue.main.async { [weak self] in
            guard let self, self.queue.sync(execute: { self.generation == currentGeneration && self.completedSession == result.sessionID && self.open && self.monotonicNow() < self.windowDeadline }) else { return }
            self.onPaired(result)
        }
    }
    private func ensureLive(_ current: Session) -> Bool {
        guard session === current else { return false }
        let now = monotonicNow()
        if !open || now >= windowDeadline {
            closeOnQueue(); failure("Nearby pairing timed out. Search again when both computers are ready."); return false
        }
        if now >= current.approvalDeadline || (current.phase != .approval && now >= current.handshakeDeadline) {
            end(current, error: "Pairing timed out. Select this computer again to retry."); return false
        }
        return true
    }
    private func cancelOnQueue() {
        completedSession = nil
        if let current = session { end(current, error: nil) }
        else { publish() }
    }
    private func end(_ current: Session, error: String?) {
        guard session === current else { return }
        session = nil; current.handshakeTimer?.cancel(); current.lifetimeTimer?.cancel()
        current.confirmation = nil; current.connection.stateUpdateHandler = nil; current.connection.cancel()
        publish(); if let error { failure(error) }
    }
    private func closeOnQueue() {
        generation &+= 1; open = false; port = 0; completedSession = nil
        windowDeadline = 0
        windowTimer?.cancel(); windowTimer = nil; windowID = nil
        listener?.newConnectionHandler = nil; listener?.stateUpdateHandler = nil; listener?.cancel(); listener = nil
        if let current = session { end(current, error: nil) }
        else { publish() }
    }
    private func publish() {
        revision &+= 1; let currentRevision = revision
        let value = NearbyPairingState(isOpen: open, listeningPort: port, busy: session != nil, confirmation: session?.confirmation)
        DispatchQueue.main.async { [weak self] in
            guard let self, self.queue.sync(execute: { self.revision == currentRevision }) else { return }
            self.onUpdate(value)
        }
    }
    private func failure(_ message: String) {
        let currentGeneration = generation
        DispatchQueue.main.async { [weak self] in
            guard let self, self.queue.sync(execute: { self.generation == currentGeneration }) else { return }
            self.onFailure(message)
        }
    }
    deinit { windowTimer?.cancel(); listener?.cancel(); session?.connection.cancel() }
}
