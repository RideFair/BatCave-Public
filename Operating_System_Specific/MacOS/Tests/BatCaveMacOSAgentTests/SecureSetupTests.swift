import XCTest
import Security
@preconcurrency import Network
@testable import BatCaveMacOSAgent

final class SecureSetupTests: XCTestCase, @unchecked Sendable {
    func testCertificateSignatureCardTamperingAndExpiry() throws {
        let key = try XCTUnwrap(SecKeyCreateRandomKey([kSecAttrKeyType: kSecAttrKeyTypeECSECPrimeRandom,
            kSecAttrKeySizeInBits: 256] as CFDictionary, nil))
        let der = try LocalCertificate.create(key: key, commonName: "Test identity")
        let card = PairingCard(deviceId: "windows:test", displayName: "Test Windows",
            certificateDerBase64: der.base64EncodedString(), certificateSha256: PairingCard.fingerprint(der))
        XCTAssertEqual(try PairingCard.parse(card.encoded()), card)
        XCTAssertThrowsError(try PairingCard.parse(card.encoded(), now: Date().addingTimeInterval(4 * 365 * 86400)))
        let tampered = PairingCard(deviceId: card.deviceId, displayName: card.displayName,
            certificateDerBase64: card.certificateDerBase64, certificateSha256: String(repeating: "0", count: 64))
        XCTAssertThrowsError(try PairingCard.parse(tampered.encoded()))
        XCTAssertThrowsError(try PairingCard.parse(Data(repeating: 65, count: 16 * 1024 + 1)))
        // Check the self-signature with Security's X.509 trust engine.
        let certificate = try XCTUnwrap(SecCertificateCreateWithData(nil, der as CFData))
        var trust: SecTrust?
        XCTAssertEqual(SecTrustCreateWithCertificates(certificate, SecPolicyCreateBasicX509(), &trust), errSecSuccess)
        let value = try XCTUnwrap(trust)
        SecTrustSetAnchorCertificates(value, [certificate] as CFArray)
        SecTrustSetAnchorCertificatesOnly(value, true)
        XCTAssertTrue(SecTrustEvaluateWithError(value, nil))
    }

    func testKeychainIdentityPersistsAndMissingKeyDoesNotRotate() throws {
        let fixture = try IdentityFixture()
        defer { fixture.cleanup() }
        let reloaded = try PairingIdentity.openOrCreate(at: fixture.directory, displayName: "Renamed")
        XCTAssertEqual(reloaded.card, fixture.identity.card)
        XCTAssertEqual(SecItemDelete([kSecClass: kSecClassKey, kSecAttrApplicationTag: fixture.identity.keyTag] as CFDictionary), errSecSuccess)
        XCTAssertThrowsError(try PairingIdentity.openOrCreate(at: fixture.directory, displayName: "New identity"))
        let saved = try PairingCard.parse(Data(contentsOf: fixture.directory.appendingPathComponent("identity.json")))
        XCTAssertEqual(saved.deviceId, fixture.identity.card.deviceId)
    }

    func testMutualTLSHelloAndOwnershipGate() throws {
        let clientIdentity = try IdentityFixture(), serverIdentity = try IdentityFixture()
        defer { clientIdentity.cleanup(); serverIdentity.cleanup() }
        let received = expectation(description: "Authorized input after ownership")
        let recorder = SecureRecorder(input: received)
        let client = LanBridgePeer(secureIdentity: clientIdentity.identity)
        client.remoteInputReceiver = recorder
        let server = try TLSServer(identity: serverIdentity.identity, approved: clientIdentity.identity.card)
        defer { client.dispose(); server.close() }
        let ready = expectation(description: "TLS server listening")
        server.listener.stateUpdateHandler = { if case .ready = $0 { ready.fulfill() } }
        server.listener.start(queue: server.queue)
        wait(for: [ready], timeout: 5)
        client.approve(serverIdentity.identity.card)
        client.connect(host: "127.0.0.1", port: try XCTUnwrap(server.listener.port).rawValue)
        let authenticated = expectation(description: "Hello authenticated")
        server.onReady = { connection in
            connection.send(content: self.frame("device.hello", source: serverIdentity.identity.card.deviceId,
                payload: ["deviceId": serverIdentity.identity.card.deviceId, "virtualDesktopBounds": ["left": 0,"top": 0,"width": 800,"height": 600,"right": 799,"bottom": 599]]), completion: .idempotent)
        }
        // No native input is installed or posted in this test.
        server.queue.asyncAfter(deadline: .now() + 1) {
            if client.isAuthenticated { authenticated.fulfill() }
        }
        wait(for: [authenticated], timeout: 5)
        XCTAssertFalse(client.isSecureInputEnabled)
        let source = serverIdentity.identity.card.deviceId
        let key: [String: Any] = ["virtualKey": 37, "scanCode": 75, "isKeyUp": false, "isExtended": true]
        server.send(frame("input.key", source: source, payload: key))
        Thread.sleep(forTimeInterval: 0.1)
        XCTAssertEqual(recorder.count, 0)
        client.enableSecureInput(true)
        XCTAssertTrue(client.isSecureInputEnabled)
        Thread.sleep(forTimeInterval: 0.1)
        server.send(frame("cursor.handoff", source: source, payload: ["targetDeviceId": client.localDeviceId,
            "targetDisplayId": client.localDeviceId + ":desktop", "targetEdge": "left", "targetPosition": 0.5, "targetX": 0, "targetY": 200]))
        server.send(frame("input.key", source: source, payload: key))
        wait(for: [received], timeout: 5)
        XCTAssertEqual(recorder.count, 1)
        XCTAssertTrue(server.mutualTLSVerified)
        client.connect(host: "127.0.0.1", port: try XCTUnwrap(server.listener.port).rawValue)
        XCTAssertFalse(client.isSecureInputEnabled, "Replacing a connection must also reset the UI's input-enabled state")
    }

    func testPartialInputFromBeforePauseCannotReplayAfterResume() throws {
        let a = try IdentityFixture(), b = try IdentityFixture()
        defer { a.cleanup(); b.cleanup() }
        let received = expectation(description:"Only fresh input received")
        let recorder = SecureRecorder(input:received), client = LanBridgePeer(secureIdentity:a.identity)
        client.remoteInputReceiver = recorder
        client.updateLocalBounds(DisplayBounds(minX:0,maxX:800,minY:0,maxY:600))
        let server = try TLSServer(identity:b.identity,approved:a.identity.card)
        defer { client.dispose(); server.close() }
        let ready = expectation(description:"TLS listener ready"), authenticated = expectation(description:"Authenticated")
        server.listener.stateUpdateHandler = { if case .ready = $0 { ready.fulfill() } }
        let source = b.identity.card.deviceId
        server.onReady = { connection in
            connection.send(content:self.frame("device.hello",source:source,payload:["deviceId":source,
                "virtualDesktopBounds":["left":0,"top":0,"width":800,"height":600,"right":800,"bottom":600]]),completion:.idempotent)
        }
        client.onAuthenticated = { _ in authenticated.fulfill() }
        server.listener.start(queue:server.queue); wait(for:[ready],timeout:5)
        client.approve(b.identity.card); client.connect(host:"127.0.0.1",port:try XCTUnwrap(server.listener.port).rawValue)
        wait(for:[authenticated],timeout:5)
        client.enableSecureInput(true)
        XCTAssertTrue(client.isSecureInputEnabled)
        let stale = frame("input.key",source:source,payload:["virtualKey":0x25,"scanCode":0,"isKeyUp":false,"isExtended":true])
        server.send(Data(stale.dropLast()))
        let buffered = expectation(description:"Partial input buffered")
        server.queue.asyncAfter(deadline:.now()+0.1) {
            if client.pendingReceiveBytes == stale.count - 1 { buffered.fulfill() }
        }
        wait(for:[buffered],timeout:5)
        let old = try XCTUnwrap(client.authenticatedSession)
        client.pauseSharing(reason:"Fixture Pause")
        XCTAssertFalse(client.isSecureInputEnabled)
        let fresh = try XCTUnwrap(client.authenticatedSession)
        XCTAssertNotEqual(old.activationGeneration,fresh.activationGeneration)
        client.enableSecureInput(true,forSession:fresh.sessionId,forActivation:fresh.activationGeneration)
        XCTAssertTrue(client.isSecureInputEnabled)
        server.send(Data([10]))
        let drained = expectation(description:"Stale partial input drained")
        server.queue.asyncAfter(deadline:.now()+0.1) { if client.pendingReceiveBytes == 0 { drained.fulfill() } }
        wait(for:[drained],timeout:5)
        XCTAssertEqual(recorder.count,0)
        server.send(frame("cursor.handoff",source:source,payload:["targetDeviceId":client.localDeviceId,
            "targetDisplayId":client.localDeviceId + ":desktop","targetEdge":"left","targetPosition":0.5,"targetX":0,"targetY":300]))
        server.send(stale)
        wait(for:[received],timeout:5)
        XCTAssertEqual(recorder.count,1)
    }

    func testWrongServerPinRejectsTLS() throws {
        let clientIdentity = try IdentityFixture(), serverIdentity = try IdentityFixture()
        defer { clientIdentity.cleanup(); serverIdentity.cleanup() }
        let client = LanBridgePeer(secureIdentity: clientIdentity.identity)
        let server = try TLSServer(identity: serverIdentity.identity, approved: clientIdentity.identity.card)
        defer { client.dispose(); server.close() }
        let ready = expectation(description: "TLS server listening")
        server.listener.stateUpdateHandler = { if case .ready = $0 { ready.fulfill() } }
        server.listener.start(queue: server.queue)
        wait(for: [ready], timeout: 5)
        client.approve(clientIdentity.identity.card) // Correct format, wrong certificate.
        client.connect(host: "127.0.0.1", port: try XCTUnwrap(server.listener.port).rawValue)
        let rejected = expectation(description: "TLS pin rejected")
        server.queue.asyncAfter(deadline: .now() + 2) {
            if !client.isAuthenticated, client.statusDescription.contains("Connection lost") { rejected.fulfill() }
        }
        wait(for: [rejected], timeout: 5)
        XCTAssertFalse(client.isAuthenticated)
    }

    func testWrongHelloAndOversizeFrameCloseAuthenticatedTransport() throws {
        for oversized in [false, true] {
            let clientIdentity = try IdentityFixture(), serverIdentity = try IdentityFixture()
            defer { clientIdentity.cleanup(); serverIdentity.cleanup() }
            let client = LanBridgePeer(secureIdentity: clientIdentity.identity)
            let server = try TLSServer(identity: serverIdentity.identity, approved: clientIdentity.identity.card)
            defer { client.dispose(); server.close() }
            let ready = expectation(description: "TLS server listening")
            server.listener.stateUpdateHandler = { if case .ready = $0 { ready.fulfill() } }
            let source = serverIdentity.identity.card.deviceId
            server.onReady = { connection in
                let invalid = oversized ? Data(repeating: 65, count: 256 * 1024 + 1)
                    : self.frame("device.hello", source: source, payload: ["deviceId": "windows:impostor"])
                connection.send(content: invalid, completion: .idempotent)
            }
            server.listener.start(queue: server.queue)
            wait(for: [ready], timeout: 5)
            client.approve(serverIdentity.identity.card)
            client.connect(host: "127.0.0.1", port: try XCTUnwrap(server.listener.port).rawValue)
            let rejected = expectation(description: "Invalid authenticated message rejected")
            server.queue.asyncAfter(deadline: .now() + 1) {
                if !client.isAuthenticated, client.statusDescription.contains("Connection lost") { rejected.fulfill() }
            }
            wait(for: [rejected], timeout: 5)
        }
    }

    private func frame(_ type: String, source: String, payload: [String: Any]) -> Data {
        var data = try! JSONSerialization.data(withJSONObject: ["protocol": "batcave.input.v0", "type": type,
            "sourceDeviceId": source, "payload": payload])
        data.append(10); return data
    }
}

private final class IdentityFixture: @unchecked Sendable {
    let directory: URL
    let identity: PairingIdentity
    init() throws {
        directory = FileManager.default.temporaryDirectory.appendingPathComponent("BatCave-identity-test-" + UUID().uuidString)
        identity = try PairingIdentity.openOrCreate(at: directory, displayName: "Test computer")
    }
    func cleanup() {
        if let der = Data(base64Encoded: identity.card.certificateDerBase64), let cert = SecCertificateCreateWithData(nil, der as CFData) {
            SecItemDelete([kSecClass: kSecClassCertificate, kSecValueRef: cert] as CFDictionary)
        }
        SecItemDelete([kSecClass: kSecClassKey, kSecAttrApplicationTag: identity.keyTag] as CFDictionary)
        try? FileManager.default.removeItem(at: directory)
    }
}

private final class TLSServer: @unchecked Sendable {
    let queue = DispatchQueue(label: "BatCave.TLS.test")
    let listener: NWListener
    var connection: NWConnection?
    var onReady: (@Sendable (NWConnection) -> Void)?
    var mutualTLSVerified = false
    init(identity: PairingIdentity, approved: PairingCard) throws {
        let tls = NWProtocolTLS.Options()
        sec_protocol_options_set_min_tls_protocol_version(tls.securityProtocolOptions, .TLSv13)
        sec_protocol_options_set_max_tls_protocol_version(tls.securityProtocolOptions, .TLSv13)
        sec_protocol_options_set_local_identity(tls.securityProtocolOptions, try XCTUnwrap(sec_identity_create(identity.identity)))
        sec_protocol_options_set_peer_authentication_required(tls.securityProtocolOptions, true)
        sec_protocol_options_set_verify_block(tls.securityProtocolOptions, { _, trust, done in
            let value = sec_trust_copy_ref(trust).takeRetainedValue()
            let chain = SecTrustCopyCertificateChain(value) as? [SecCertificate]
            done(chain?.first.map { approved.matches($0) } ?? false)
        }, queue)
        listener = try NWListener(using: NWParameters(tls: tls, tcp: NWProtocolTCP.Options()), on: .any)
        listener.newConnectionHandler = { [weak self] connection in
            guard let self else { return }
            self.connection = connection
            connection.stateUpdateHandler = { state in
                if case .ready = state {
                    self.mutualTLSVerified = true
                    self.onReady?(connection)
                }
            }
            connection.start(queue: self.queue)
        }
    }
    func send(_ data: Data) { queue.async { self.connection?.send(content: data, completion: .idempotent) } }
    func close() { queue.sync { connection?.cancel(); listener.cancel() } }
}

private final class SecureRecorder: LanBridgeRemoteInputReceiver, @unchecked Sendable {
    private let lock = NSLock()
    private var keys = 0
    let input: XCTestExpectation
    init(input: XCTestExpectation) { self.input = input }
    var count: Int { lock.withLock { keys } }
    func lanPeer(_ peer: LanBridgePeer, didReceiveKeyInput message: KeyInputMessage, sourceDeviceId: String) {
        lock.withLock { keys += 1 }; input.fulfill()
    }
    func lanPeer(_ peer: LanBridgePeer, didDisconnect reason: String) {}
    func lanPeer(_ peer: LanBridgePeer, didReceiveTestStart message: RemoteTestStartMessage, sourceDeviceId: String) {}
    func lanPeer(_ peer: LanBridgePeer, didReceiveTestStop message: RemoteTestStopMessage, sourceDeviceId: String) {}
    func lanPeer(_ peer: LanBridgePeer, didReceiveCursorHandoff message: CursorHandoffMessage, sourceDeviceId: String) {}
    func lanPeer(_ peer: LanBridgePeer, didReceiveMouseDelta message: MouseDeltaMessage, sourceDeviceId: String) {}
    func lanPeer(_ peer: LanBridgePeer, didReceiveMouseButton message: MouseButtonMessage, sourceDeviceId: String) {}
    func lanPeer(_ peer: LanBridgePeer, didReceiveMouseWheel message: MouseWheelMessage, sourceDeviceId: String) {}
    func lanPeer(_ peer: LanBridgePeer, didReceiveShortcutInput message: ShortcutInputMessage, sourceDeviceId: String) {}
    func lanPeer(_ peer: LanBridgePeer, didReceiveClipboardText message: ClipboardTextMessage, sourceDeviceId: String) {}
}
