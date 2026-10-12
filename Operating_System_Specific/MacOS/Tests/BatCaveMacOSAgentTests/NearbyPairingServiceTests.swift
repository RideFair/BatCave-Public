import XCTest
import Security
import Darwin
@preconcurrency import Network
@testable import BatCaveMacOSAgent

final class NearbyPairingServiceTests: XCTestCase, @unchecked Sendable {
    func testIncomingAttemptLimiterPersistsCooldownAndRollingMinute() {
        var limiter = NearbyPairingAttemptLimiter(); let now = Date()
        XCTAssertTrue(limiter.accept(at: now))
        XCTAssertFalse(limiter.accept(at: now.addingTimeInterval(4.99)))
        for second in stride(from: 5, through: 25, by: 5) { XCTAssertTrue(limiter.accept(at: now.addingTimeInterval(Double(second)))) }
        XCTAssertFalse(limiter.accept(at: now.addingTimeInterval(30)))
        XCTAssertFalse(limiter.accept(at: now.addingTimeInterval(60)))
        XCTAssertTrue(limiter.accept(at: now.addingTimeInterval(61)))
    }
    func testDeadlineChecksAfterSigningRejectBeforeTimersRun() throws {
        for advance in [121.0, 181.0] {
            let clock = NearbyManualClock()
            let first = try NearbyServiceFixture(id: "macos:sign-deadline", monotonicNow: { clock.now },
                onSign: { bytes in
                    if bytes.range(of: Data("batcave.pair.v1.approve".utf8)) != nil { clock.advance(advance) }
                })
            let second = try NearbyServiceFixture(id: "windows:sign-deadline")
            defer { first.service.dispose(); second.service.dispose() }
            let ready = expectation(description: "Ready"); ready.expectedFulfillmentCount = 2
            first.recorder.listening = ready; second.recorder.listening = ready
            first.service.openWindow(port: 0, address: "127.0.0.1"); second.service.openWindow(port: 0, address: "127.0.0.1")
            wait(for: [ready], timeout: 5)
            let codes = expectation(description: "Comparisons"); codes.expectedFulfillmentCount = 2
            first.recorder.comparison = codes; second.recorder.comparison = codes
            first.service.link(second.discovered()); wait(for: [codes], timeout: 5)
            let firstID = try XCTUnwrap(first.recorder.state?.confirmation?.sessionID)
            let secondID = try XCTUnwrap(second.recorder.state?.confirmation?.sessionID)
            let cleared = expectation(description: "Late signing rejected"); cleared.expectedFulfillmentCount = 2
            first.recorder.cleared = cleared; second.recorder.cleared = cleared
            second.service.approve(sessionID: secondID)
            first.service.approve(sessionID: firstID)
            wait(for: [cleared], timeout: 5)
            XCTAssertNil(first.recorder.result); XCTAssertNil(second.recorder.result)
            XCTAssertNil(first.recorder.state?.confirmation)
            if advance > 180 { XCTAssertFalse(first.recorder.state?.isOpen ?? true) }
        }
    }
    func testHandshakeDeadlineCheckedAfterProofSigning() throws {
        let clock = NearbyManualClock()
        let first = try NearbyServiceFixture(id: "macos:proof-deadline", monotonicNow: { clock.now },
            onSign: { bytes in if bytes.range(of: Data("batcave.pair.v1.proof".utf8)) != nil { clock.advance(11) } })
        let second = try NearbyServiceFixture(id: "windows:proof-deadline")
        defer { first.service.dispose(); second.service.dispose() }
        let ready = expectation(description: "Ready"); ready.expectedFulfillmentCount = 2
        first.recorder.listening = ready; second.recorder.listening = ready
        first.service.openWindow(port: 0, address: "127.0.0.1"); second.service.openWindow(port: 0, address: "127.0.0.1")
        wait(for: [ready], timeout: 5)
        let failure = expectation(description: "Late proof rejected"); first.recorder.failed = failure
        first.service.link(second.discovered()); wait(for: [failure], timeout: 5)
        XCTAssertNil(first.recorder.state?.confirmation); XCTAssertNil(first.recorder.result)
        XCTAssertNil(second.recorder.result)
    }
    func testBothApprovalsRequiredAndObservedEndpointReturned() throws {
        let first = try NearbyServiceFixture(id: "macos:fixture-one")
        let second = try NearbyServiceFixture(id: "windows:fixture-two")
        defer { first.service.dispose(); second.service.dispose() }
        let firstReady = expectation(description: "First listener ready")
        let secondReady = expectation(description: "Second listener ready")
        first.recorder.listening = firstReady; second.recorder.listening = secondReady
        first.service.openWindow(port: 0, address: "127.0.0.1")
        second.service.openWindow(port: 0, address: "127.0.0.1")
        wait(for: [firstReady, secondReady], timeout: 5)
        let firstCode = expectation(description: "First comparison")
        let secondCode = expectation(description: "Second comparison")
        first.recorder.comparison = firstCode; second.recorder.comparison = secondCode
        first.service.link(second.discovered())
        wait(for: [firstCode, secondCode], timeout: 5)
        let local = try XCTUnwrap(first.recorder.state?.confirmation)
        let remote = try XCTUnwrap(second.recorder.state?.confirmation)
        XCTAssertEqual(local.code, remote.code); XCTAssertTrue(local.initiator); XCTAssertFalse(remote.initiator)
        XCTAssertEqual(local.code.count, 9)
        XCTAssertNil(first.recorder.result); XCTAssertNil(second.recorder.result)
        first.service.approve(sessionID: local.sessionID)
        // Give the local signature time to reach the other screen; completing
        // one screen cannot save either peer's identity.
        Thread.sleep(forTimeInterval: 0.15)
        XCTAssertNil(first.recorder.result); XCTAssertNil(second.recorder.result)
        let firstPaired = expectation(description: "First completed")
        let secondPaired = expectation(description: "Second completed")
        first.recorder.paired = firstPaired; second.recorder.paired = secondPaired
        second.service.approve(sessionID: remote.sessionID)
        wait(for: [firstPaired, secondPaired], timeout: 5)
        XCTAssertEqual(first.recorder.result?.sessionID, local.sessionID)
        XCTAssertEqual(second.recorder.result?.sessionID, remote.sessionID)
        XCTAssertEqual(first.recorder.result?.address, "127.0.0.1")
        XCTAssertEqual(first.recorder.result?.peer.card.deviceId, second.descriptor.card.deviceId)
        XCTAssertTrue(first.recorder.result?.initiator ?? false)
        XCTAssertFalse(second.recorder.result?.initiator ?? true)
    }

    func testCancelInvalidatesComparisonAndStaleApproveCannotComplete() throws {
        let first = try NearbyServiceFixture(id: "macos:cancel-one")
        let second = try NearbyServiceFixture(id: "windows:cancel-two")
        defer { first.service.dispose(); second.service.dispose() }
        let ready = expectation(description: "Ready"); ready.expectedFulfillmentCount = 2
        first.recorder.listening = ready; second.recorder.listening = ready
        first.service.openWindow(port: 0, address: "127.0.0.1"); second.service.openWindow(port: 0, address: "127.0.0.1")
        wait(for: [ready], timeout: 5)
        let codes = expectation(description: "Both comparisons"); codes.expectedFulfillmentCount = 2
        first.recorder.comparison = codes; second.recorder.comparison = codes
        first.service.link(second.discovered()); wait(for: [codes], timeout: 5)
        let old = try XCTUnwrap(first.recorder.state?.confirmation)
        let other = try XCTUnwrap(second.recorder.state?.confirmation)
        let cancelled = expectation(description: "Local comparison cleared")
        let disconnected = expectation(description: "Remote comparison cleared")
        first.recorder.cleared = cancelled; second.recorder.cleared = disconnected
        first.service.cancel()
        wait(for: [cancelled, disconnected], timeout: 5)
        first.service.approve(sessionID: old.sessionID); second.service.approve(sessionID: other.sessionID)
        Thread.sleep(forTimeInterval: 0.1)
        XCTAssertNil(first.recorder.result); XCTAssertNil(second.recorder.result)
        XCTAssertNil(first.recorder.state?.confirmation); XCTAssertNil(second.recorder.state?.confirmation)
    }

    func testListenerDeadlineAndCloseActuallyUnbind() throws {
        let fixture = try NearbyServiceFixture(id: "macos:deadline", timings: .init(window: 0.25, handshake: 0.1, approval: 0.2))
        defer { fixture.service.dispose() }
        let ready = expectation(description: "Listener ready"); fixture.recorder.listening = ready
        fixture.service.openWindow(port: 0, address: "127.0.0.1"); wait(for: [ready], timeout: 5)
        let port = try XCTUnwrap(fixture.recorder.state?.listeningPort)
        let closed = expectation(description: "Window expired"); fixture.recorder.closed = closed
        wait(for: [closed], timeout: 3)
        XCTAssertFalse(fixture.recorder.state?.isOpen ?? true)
        XCTAssertEqual(fixture.recorder.state?.listeningPort, 0)
        try assertPortCanBind(port)

        let reopened = expectation(description: "Reopened"); fixture.recorder.listening = reopened
        fixture.service.openWindow(port: port, address: "127.0.0.1"); wait(for: [reopened], timeout: 3)
        fixture.service.closeWindow()
        try assertPortCanBind(port)
        XCTAssertNil(fixture.recorder.result)
    }

    func testApprovalDeadlineInvalidatesBothComparisonScreens() throws {
        let first = try NearbyServiceFixture(id: "macos:expire-one", timings: .init(window: 4, handshake: 1, approval: 0.3))
        let second = try NearbyServiceFixture(id: "windows:expire-two", timings: .init(window: 4, handshake: 1, approval: 0.3))
        defer { first.service.dispose(); second.service.dispose() }
        let ready = expectation(description: "Ready"); ready.expectedFulfillmentCount = 2
        first.recorder.listening = ready; second.recorder.listening = ready
        first.service.openWindow(port: 0, address: "127.0.0.1"); second.service.openWindow(port: 0, address: "127.0.0.1")
        wait(for: [ready], timeout: 5)
        let codes = expectation(description: "Comparisons"); codes.expectedFulfillmentCount = 2
        first.recorder.comparison = codes; second.recorder.comparison = codes
        first.service.link(second.discovered()); wait(for: [codes], timeout: 3)
        let stale = try XCTUnwrap(first.recorder.state?.confirmation)
        first.service.approve(sessionID: stale.sessionID)
        let expired = expectation(description: "Comparisons cleared"); expired.expectedFulfillmentCount = 2
        first.recorder.cleared = expired; second.recorder.cleared = expired
        wait(for: [expired], timeout: 3)
        first.service.approve(sessionID: stale.sessionID)
        XCTAssertNil(first.recorder.result); XCTAssertNil(second.recorder.result)
    }

    func testSilentPeerHandshakeDeadlineAndClosingScreenCancelSession() throws {
        for close in [false, true] {
            let fixture = try NearbyServiceFixture(id: "macos:handshake", timings: .init(window: 4, handshake: 0.25, approval: 2))
            defer { fixture.service.dispose() }
            let ready = expectation(description: "Ready"); fixture.recorder.listening = ready
            fixture.service.openWindow(port: 0, address: "127.0.0.1"); wait(for: [ready], timeout: 5)
            let busy = expectation(description: "Handshake started"); fixture.recorder.started = busy
            let port = try XCTUnwrap(NWEndpoint.Port(rawValue: fixture.recorder.state!.listeningPort))
            let connection = NWConnection(host: "127.0.0.1", port: port, using: .tcp)
            defer { connection.cancel() }
            connection.start(queue: DispatchQueue(label: "BatCave.Nearby.silent-fixture"))
            wait(for: [busy], timeout: 3)
            let ended = expectation(description: "Session ended"); fixture.recorder.cleared = ended
            if close { fixture.service.closeWindow() }
            wait(for: [ended], timeout: 3)
            XCTAssertNil(fixture.recorder.state?.confirmation); XCTAssertNil(fixture.recorder.result)
            XCTAssertFalse(fixture.recorder.state?.busy ?? true)
            if close {
                try assertPortCanBind(port.rawValue)
                let reopened = expectation(description: "Same TCP port reopened")
                fixture.recorder.listening = reopened
                fixture.service.openWindow(port: port.rawValue, address: "127.0.0.1")
                wait(for: [reopened], timeout: 3)
            }
        }
    }
    func testOccupiedListenerFailsVisiblyAndRefreshKeepsActualPort() throws {
        let first = try NearbyServiceFixture(id: "macos:bind-first"), second = try NearbyServiceFixture(id: "macos:bind-second")
        defer { first.service.dispose(); second.service.dispose() }
        let ready = expectation(description: "First ready"); first.recorder.listening = ready
        first.service.openWindow(port: 0, address: "127.0.0.1"); wait(for: [ready], timeout: 5)
        let port = try XCTUnwrap(first.recorder.state?.listeningPort)
        let failed = expectation(description: "Occupied pairing port rejected"); second.recorder.failed = failed
        second.service.openWindow(port: port, address: "127.0.0.1"); wait(for: [failed], timeout: 5)
        XCTAssertEqual(second.recorder.state?.listeningPort, 0)
        XCTAssertFalse(second.recorder.state?.isOpen ?? true)
        let refreshed = expectation(description: "Window refreshed"); first.recorder.listening = refreshed
        first.service.openWindow(port: 0, address: "127.0.0.1"); wait(for: [refreshed], timeout: 3)
        XCTAssertEqual(first.recorder.state?.listeningPort, port)
    }

    func testSelectedFingerprintMismatchRejectsBeforeComparison() throws {
        let first = try NearbyServiceFixture(id: "macos:select-one"), second = try NearbyServiceFixture(id: "linux:select-two")
        defer { first.service.dispose(); second.service.dispose() }
        let ready = expectation(description: "Ready"); ready.expectedFulfillmentCount = 2
        first.recorder.listening = ready; second.recorder.listening = ready
        first.service.openWindow(port: 0, address: "127.0.0.1"); second.service.openWindow(port: 0, address: "127.0.0.1")
        wait(for: [ready], timeout: 5)
        let failure = expectation(description: "Mismatch rejected"); first.recorder.failed = failure
        let original = second.discovered()
        first.service.link(NearbyComputer(deviceID: original.deviceID, displayName: original.displayName,
            platform: original.platform, fingerprint: String(repeating: "0", count: 64), address: original.address,
            inputPort: original.inputPort, pairingPort: original.pairingPort, pairing: true, seen: Date()))
        wait(for: [failure], timeout: 5)
        XCTAssertNil(first.recorder.state?.confirmation); XCTAssertNil(first.recorder.result)
    }

    func testOutOfOrderAndOversizeIncomingFramesNeverPresentCode() throws {
        for payload in [Data("{\"type\":\"pair.approve\",\"version\":1,\"session\":\"00000000000000000000000000000000\",\"signature\":\"AA==\"}\n".utf8),
                        Data(repeating: 65, count: 16385)] {
            let fixture = try NearbyServiceFixture(id: "macos:invalid")
            defer { fixture.service.dispose() }
            let ready = expectation(description: "Ready"); fixture.recorder.listening = ready
            fixture.service.openWindow(port: 0, address: "127.0.0.1"); wait(for: [ready], timeout: 5)
            let failure = expectation(description: "Bad frame rejected"); fixture.recorder.failed = failure
            let port = try XCTUnwrap(NWEndpoint.Port(rawValue: fixture.recorder.state!.listeningPort))
            let connection = NWConnection(host: "127.0.0.1", port: port, using: .tcp)
            defer { connection.cancel() }
            connection.stateUpdateHandler = { state in if case .ready = state { connection.send(content: payload, completion: .idempotent) } }
            connection.start(queue: DispatchQueue(label: "BatCave.Nearby.invalid-fixture"))
            wait(for: [failure], timeout: 5)
            XCTAssertNil(fixture.recorder.state?.confirmation); XCTAssertNil(fixture.recorder.result)
        }
    }

    private func assertPortCanBind(_ port: UInt16) throws {
        var success = false
        for _ in 0..<20 {
            let socket = Darwin.socket(AF_INET, SOCK_STREAM, IPPROTO_TCP)
            guard socket >= 0 else { throw PairingError("Fixture socket unavailable.") }
            // Closed TCP flows can retain TIME_WAIT; SO_REUSEADDR permits that
            // harmless kernel state but never shares an active listening socket.
            var reuse: Int32 = 1
            _ = setsockopt(socket, SOL_SOCKET, SO_REUSEADDR, &reuse, socklen_t(MemoryLayout<Int32>.size))
            var address = sockaddr_in(); address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
            address.sin_family = UInt8(AF_INET); address.sin_port = port.bigEndian
            _ = "127.0.0.1".withCString { inet_pton(AF_INET, $0, &address.sin_addr) }
            success = withUnsafePointer(to: &address) { pointer in
                pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) { Darwin.bind(socket, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) == 0 }
            }
            if success { success = Darwin.listen(socket, 1) == 0 }
            Darwin.close(socket)
            if success { break }; Thread.sleep(forTimeInterval: 0.05)
        }
        XCTAssertTrue(success, "The pairing listener must release its actual TCP port")
    }
}

private final class NearbyServiceFixture: @unchecked Sendable {
    let key: SecKey
    let descriptor: NearbyPairingProtocol.Descriptor
    let recorder = NearbyServiceRecorder()
    let service: NearbyPairingService
    init(id: String, timings: NearbyPairingService.Timings = .init(),
         monotonicNow: @escaping @Sendable () -> TimeInterval = { ProcessInfo.processInfo.systemUptime },
         onSign: @escaping @Sendable (Data) -> Void = { _ in }) throws {
        // No permanent attribute, certificate import, Keychain entry or profile.
        key = try XCTUnwrap(SecKeyCreateRandomKey([kSecAttrKeyType: kSecAttrKeyTypeECSECPrimeRandom,
            kSecAttrKeySizeInBits: 256, kSecAttrIsPermanent: false] as CFDictionary, nil))
        let der = try LocalCertificate.create(key: key, commonName: "Nearby fixture")
        descriptor = .init(card: PairingCard(deviceId: id, displayName: "Fixture computer",
            certificateDerBase64: der.base64EncodedString(), certificateSha256: PairingCard.fingerprint(der)), inputPort: 47871)
        let signer = NearbyTestSigner(key: key), recorder = self.recorder
        service = NearbyPairingService(local: descriptor, sign: { onSign($0); return try signer.sign($0) }, acceptPeer: { _ in true },
            allowAddress: { $0 == "127.0.0.1" }, timings: timings, monotonicNow: monotonicNow,
            onUpdate: { recorder.update($0) }, onPaired: { recorder.complete($0) }, onFailure: { recorder.fail($0) })
    }
    func discovered() -> NearbyComputer {
        NearbyComputer(deviceID: descriptor.card.deviceId, displayName: descriptor.card.displayName,
            platform: "macos", fingerprint: descriptor.card.certificateSha256, address: "127.0.0.1",
            inputPort: UInt16(descriptor.inputPort), pairingPort: recorder.state?.listeningPort ?? 0, pairing: true, seen: Date())
    }
}
private final class NearbyManualClock: @unchecked Sendable {
    private let lock = NSLock()
    private var value = ProcessInfo.processInfo.systemUptime
    var now: TimeInterval { lock.withLock { value } }
    func advance(_ seconds: TimeInterval) { lock.withLock { value += seconds } }
}
private final class NearbyTestSigner: @unchecked Sendable {
    let key: SecKey
    init(key: SecKey) { self.key = key }
    func sign(_ message: Data) throws -> Data { try NearbyPairingProtocol.sign(key: key, message: message) }
}
private final class NearbyServiceRecorder: @unchecked Sendable {
    private let lock = NSLock()
    private var storedState: NearbyPairingState?
    private var storedResult: NearbyPairingResult?
    var listening: XCTestExpectation?
    var comparison: XCTestExpectation?
    var paired: XCTestExpectation?
    var cleared: XCTestExpectation?
    var closed: XCTestExpectation?
    var failed: XCTestExpectation?
    var started: XCTestExpectation?
    var state: NearbyPairingState? { lock.withLock { storedState } }
    var result: NearbyPairingResult? { lock.withLock { storedResult } }
    func update(_ value: NearbyPairingState) {
        lock.withLock { storedState = value }
        if value.isOpen && value.listeningPort > 0, let ready = listening { listening = nil; ready.fulfill() }
        if value.confirmation != nil, let code = comparison { comparison = nil; code.fulfill() }
        if value.busy, let active = started { started = nil; active.fulfill() }
        if !value.busy && value.confirmation == nil, let empty = cleared { cleared = nil; empty.fulfill() }
        if !value.isOpen, let stopped = closed { closed = nil; stopped.fulfill() }
    }
    func complete(_ value: NearbyPairingResult) {
        lock.withLock { storedResult = value }
        paired?.fulfill(); paired = nil
    }
    func fail(_ message: String) { failed?.fulfill(); failed = nil }
}
