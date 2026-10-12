import XCTest
import Foundation
import ApplicationServices
import Security
@testable import BatCaveMacOSAgent

private final class PeerIdentityFixture {
    let directory: URL
    let identity: PairingIdentity
    init() throws {
        directory = FileManager.default.temporaryDirectory.appendingPathComponent("BatCave-role-fixture-" + UUID().uuidString)
        identity = try PairingIdentity.openOrCreate(at: directory, displayName: "Fixture computer")
    }
    func cleanup() {
        if let der = Data(base64Encoded: identity.card.certificateDerBase64), let cert = SecCertificateCreateWithData(nil, der as CFData) {
            SecItemDelete([kSecClass:kSecClassCertificate,kSecValueRef:cert] as CFDictionary)
        }
        SecItemDelete([kSecClass:kSecClassKey,kSecAttrApplicationTag:identity.keyTag] as CFDictionary)
        try? FileManager.default.removeItem(at:directory)
    }
}

private final class RoleEventRecorder: @unchecked Sendable {
    private let lock = NSLock()
    private var events: [CGEvent] = []
    func append(_ event: CGEvent) { lock.withLock { events.append(event) } }
    var snapshot: [CGEvent] { lock.withLock { events } }
}

final class PeerRolesTests: XCTestCase, @unchecked Sendable {
    private func eventually(_ description: String, timeout: Double = 5, _ check: () -> Bool) {
        let limit = Date().addingTimeInterval(timeout)
        while !check(), Date() < limit { Thread.sleep(forTimeInterval:0.01) }
        XCTAssertTrue(check(),description)
    }
    private let bounds = DisplayBounds(minX:0,maxX:800,minY:0,maxY:600)
    private func prepare(_ peer: LanBridgePeer, card: PairingCard, role: LanInputRole) {
        peer.approve(card); peer.configureRole(role); peer.updateLocalBounds(bounds); peer.startListening(port:0)
        eventually("Listener bound") { peer.listeningPort != nil }
    }
    private func sendHandoff(_ source: LanBridgePeer, session: LanAuthenticatedSession, activation: UInt64, target: String) {
        source.publishSource("cursor.handoff",payload:["targetDeviceId":target,"targetDisplayId":target + ":desktop",
            "targetEdge":"left","targetPosition":0.5,"targetX":999_999,"targetY":-999_999],session:session.sessionId,activation:activation)
    }
    private func sendHeld(_ source: LanBridgePeer, session: LanAuthenticatedSession, activation: UInt64) {
        source.publishSource("input.mouseButton",payload:["button":"left","isDown":true],session:session.sessionId,activation:activation)
        source.publishSource("input.key",payload:["virtualKey":0xA0,"scanCode":0,"isKeyUp":false,"isExtended":false],session:session.sessionId,activation:activation)
        source.publishSource("input.key",payload:["virtualKey":0x25,"scanCode":0,"isKeyUp":false,"isExtended":true],session:session.sessionId,activation:activation)
    }

    func testNativePeersAuthenticateBothDirectionsAndRequireEnablePlusTarget() throws {
        for firstConnects in [true,false] {
            let a = try PeerIdentityFixture(), b = try PeerIdentityFixture()
            defer { a.cleanup(); b.cleanup() }
            let source = LanBridgePeer(secureIdentity:a.identity), receiver = LanBridgePeer(secureIdentity:b.identity)
            let recording = RoleEventRecorder(), agent = BatCaveMacOSAgent(lan:receiver,showHUD:false,eventPoster:recording.append)
            defer { source.dispose(); receiver.dispose(); agent.stop(reason:"Fixture finished") }
            prepare(source,card:b.identity.card,role:.source); prepare(receiver,card:a.identity.card,role:.receiver)
            if firstConnects { source.connect(host:"127.0.0.1",port:sourcePort(receiver)) }
            else { receiver.connect(host:"127.0.0.1",port:sourcePort(source)) }
            eventually("Both mutual TLS sessions authenticated") { source.isAuthenticated && receiver.isAuthenticated }
            let session = try XCTUnwrap(source.authenticatedSession), activation = try XCTUnwrap(source.activateSource(forSession:session.sessionId))
            sendHandoff(source,session:session,activation:activation,target:receiver.localDeviceId)
            sendHeld(source,session:session,activation:activation)
            Thread.sleep(forTimeInterval:0.1); XCTAssertTrue(recording.snapshot.isEmpty)
            receiver.enableSecureInput(true,forSession:receiver.authenticatedSession!.sessionId)
            eventually("Receiver enabled") { receiver.isSecureInputEnabled }
            sendHeld(source,session:session,activation:activation)
            Thread.sleep(forTimeInterval:0.1); XCTAssertTrue(recording.snapshot.isEmpty,"Enable alone grants no ownership")
            sendHandoff(source,session:session,activation:activation,target:receiver.localDeviceId)
            sendHeld(source,session:session,activation:activation)
            eventually("Held keyboard and mouse reached recording sink") { recording.snapshot.count == 4 }
            XCTAssertEqual(recording.snapshot.map(\.type),[.mouseMoved,.leftMouseDown,.flagsChanged,.keyDown])
            XCTAssertTrue(recording.snapshot.last!.flags.contains(.maskShift))
            sendHandoff(source,session:session,activation:activation,target:source.localDeviceId)
            eventually("Target-away handoff released held input") { recording.snapshot.count == 7 }
            XCTAssertEqual(recording.snapshot.suffix(3).map(\.type),[.leftMouseUp,.keyUp,.flagsChanged])
            XCTAssertFalse(agent.isRemoteControlActive)
            XCTAssertTrue(receiver.isSecureInputEnabled,"Crossing keeps persistent explicit sharing available")
        }
    }
    private func sourcePort(_ peer: LanBridgePeer) -> UInt16 { peer.listeningPort! }

    func testPauseInvalidatesQueuedSourceActivationAndRemoteHeldInput() throws {
        let a = try PeerIdentityFixture(), b = try PeerIdentityFixture()
        defer { a.cleanup(); b.cleanup() }
        let source = LanBridgePeer(secureIdentity:a.identity), receiver = LanBridgePeer(secureIdentity:b.identity)
        let recording = RoleEventRecorder(), agent = BatCaveMacOSAgent(lan:receiver,showHUD:false,eventPoster:recording.append)
        defer { source.dispose(); receiver.dispose(); agent.stop(reason:"Fixture finished") }
        prepare(source,card:b.identity.card,role:.source); prepare(receiver,card:a.identity.card,role:.receiver)
        source.connect(host:"127.0.0.1",port:sourcePort(receiver),automaticallyReconnect:true)
        eventually("Authenticated") { source.isAuthenticated && receiver.isAuthenticated }
        let session = try XCTUnwrap(source.authenticatedSession), oldActivation = try XCTUnwrap(source.activateSource(forSession:session.sessionId))
        receiver.enableSecureInput(true)
        eventually("Receiver enabled") { receiver.isSecureInputEnabled }
        sendHandoff(source,session:session,activation:oldActivation,target:receiver.localDeviceId); sendHeld(source,session:session,activation:oldActivation)
        eventually("Held") { agent.isRemoteControlActive && recording.snapshot.count == 4 }
        let staleReceiver = try XCTUnwrap(receiver.authenticatedSession)
        receiver.pauseSharing(reason:"Fixture Pause")
        eventually("Source and receiver both paused") { !source.isSecureInputEnabled && !receiver.isSecureInputEnabled }
        eventually("Releases") { recording.snapshot.count == 7 }
        XCTAssertEqual(recording.snapshot.suffix(3).map(\.type),[.leftMouseUp,.keyUp,.flagsChanged])
        let count = recording.snapshot.count
        receiver.enableSecureInput(true,forSession:staleReceiver.sessionId,forActivation:staleReceiver.activationGeneration)
        Thread.sleep(forTimeInterval:0.1)
        XCTAssertFalse(receiver.isSecureInputEnabled,"A delayed activation captured before Pause must be refused")
        XCTAssertNil(source.activateSource(forSession:session.sessionId,forActivation:session.activationGeneration),"Stale source activation must be refused")
        let freshReceiver = try XCTUnwrap(receiver.authenticatedSession)
        receiver.enableSecureInput(true,forSession:freshReceiver.sessionId,forActivation:freshReceiver.activationGeneration)
        let newActivation = try XCTUnwrap(source.activateSource(forSession:session.sessionId))
        XCTAssertNotEqual(newActivation,oldActivation)
        sendHandoff(source,session:session,activation:oldActivation,target:receiver.localDeviceId); sendHeld(source,session:session,activation:oldActivation)
        Thread.sleep(forTimeInterval:0.2); XCTAssertEqual(recording.snapshot.count,count,"Pre-Pause events cannot replay after Resume")
        source.disconnect(reason:"Fixture lost connection")
        eventually("Disconnected") { !source.isAuthenticated && !receiver.isAuthenticated }
        eventually("Automatic reconnect",timeout:8) { source.isAuthenticated && receiver.isAuthenticated }
        XCTAssertFalse(source.isSecureInputEnabled); XCTAssertFalse(receiver.isSecureInputEnabled)
        XCTAssertNotEqual(source.authenticatedSession?.sessionId,session.sessionId)
    }

    func testSyntheticMacCaptureCrossesNativeTLSAndPauseReturnsLocalControl() throws {
        let a = try PeerIdentityFixture(), b = try PeerIdentityFixture()
        defer { a.cleanup(); b.cleanup() }
        let source = LanBridgePeer(secureIdentity:a.identity), receiver = LanBridgePeer(secureIdentity:b.identity)
        let receiving = RoleEventRecorder(), local = RoleEventRecorder()
        let agent = BatCaveMacOSAgent(lan:receiver,showHUD:false,eventPoster:receiving.append)
        let capture = MacInputSource(send:{ type,payload,session,activation in
            source.publishSource(type,payload:payload,session:session,activation:activation)
        },post:local.append)
        source.onSessionInvalidated = { _,_ in capture.pause(sendStop:false) }
        defer { source.dispose(); receiver.dispose(); agent.stop(reason:"Fixture finished") }
        prepare(source,card:b.identity.card,role:.source); prepare(receiver,card:a.identity.card,role:.receiver)
        source.connect(host:"127.0.0.1",port:sourcePort(receiver))
        eventually("Authenticated") { source.isAuthenticated && receiver.isAuthenticated }
        let sourceSession = try XCTUnwrap(source.authenticatedSession)
        let activation = try XCTUnwrap(source.activateSource(forSession:sourceSession.sessionId,forActivation:sourceSession.activationGeneration))
        capture.localDeviceId = source.localDeviceId
        capture.start(session:sourceSession,activation:activation,peerOnRight:true,localBounds:bounds,pointer:CGPoint(x:400,y:300))
        let receiverSession = try XCTUnwrap(receiver.authenticatedSession)
        receiver.enableSecureInput(true,forSession:receiverSession.sessionId,forActivation:receiverSession.activationGeneration)
        eventually("Explicit receiver activation") { receiver.isSecureInputEnabled }
        let shift = try XCTUnwrap(CGEvent(keyboardEventSource:nil,virtualKey:60,keyDown:true))
        shift.type = .flagsChanged; shift.flags = [.maskShift,CGEventFlags(rawValue:4)]
        XCTAssertFalse(capture.handle(type:.flagsChanged,event:shift))
        let click = try XCTUnwrap(CGEvent(mouseEventSource:nil,mouseType:.leftMouseDown,mouseCursorPosition:CGPoint(x:799,y:300),mouseButton:.left))
        XCTAssertFalse(capture.handle(type:.leftMouseDown,event:click))
        let cross = try XCTUnwrap(CGEvent(mouseEventSource:nil,mouseType:.leftMouseDragged,mouseCursorPosition:CGPoint(x:799,y:300),mouseButton:.left))
        cross.setIntegerValueField(.mouseEventDeltaX,value:5)
        XCTAssertTrue(capture.handle(type:.leftMouseDragged,event:cross))
        eventually("Synthetic Mac tap events traverse actual TLS into native receiving sink") { receiving.snapshot.count == 3 }
        XCTAssertEqual(receiving.snapshot.map(\.type),[.mouseMoved,.flagsChanged,.leftMouseDown])
        XCTAssertEqual(receiving.snapshot[1].getIntegerValueField(.keyboardEventKeycode),60)
        XCTAssertTrue(capture.ownsRemoteCursor)
        receiver.pauseSharing(reason:"Fixture receiver paused")
        eventually("Pause restores local source control") { !capture.isActive && !capture.ownsRemoteCursor }
        eventually("Pause releases actual remote held state") { receiving.snapshot.count == 5 }
        XCTAssertEqual(receiving.snapshot.suffix(2).map(\.type),[.leftMouseUp,.flagsChanged])
        XCTAssertFalse(source.isSourceSharingEnabled)
        XCTAssertEqual(local.snapshot.last?.type,.mouseMoved)
    }

    func testIncomingWrongPinAndUnpairNeverRetainHeldInput() throws {
        let a = try PeerIdentityFixture(), b = try PeerIdentityFixture(), wrong = try PeerIdentityFixture()
        defer { a.cleanup(); b.cleanup(); wrong.cleanup() }
        let source = LanBridgePeer(secureIdentity:a.identity), receiver = LanBridgePeer(secureIdentity:b.identity)
        let intruder = LanBridgePeer(secureIdentity:wrong.identity)
        let recording = RoleEventRecorder(), agent = BatCaveMacOSAgent(lan:receiver,showHUD:false,eventPoster:recording.append)
        defer { source.dispose(); receiver.dispose(); intruder.dispose(); agent.stop(reason:"Fixture finished") }
        prepare(source,card:b.identity.card,role:.source); prepare(receiver,card:a.identity.card,role:.receiver)
        intruder.approve(b.identity.card); intruder.updateLocalBounds(bounds)
        intruder.connect(host:"127.0.0.1",port:sourcePort(receiver))
        eventually("Incoming unexpected client certificate refused") { intruder.statusDescription.contains("Connection lost") }
        XCTAssertFalse(receiver.isAuthenticated); XCTAssertFalse(intruder.isAuthenticated)
        source.connect(host:"127.0.0.1",port:sourcePort(receiver))
        eventually("Correct peer authenticated") { source.isAuthenticated && receiver.isAuthenticated }
        let session = try XCTUnwrap(source.authenticatedSession), activation = try XCTUnwrap(source.activateSource(forSession:session.sessionId))
        receiver.enableSecureInput(true)
        eventually("Enabled") { receiver.isSecureInputEnabled }
        sendHandoff(source,session:session,activation:activation,target:receiver.localDeviceId); sendHeld(source,session:session,activation:activation)
        eventually("Held") { recording.snapshot.count == 4 }
        receiver.approve(nil)
        eventually("Unpair closes both connections") { !source.isAuthenticated && !receiver.isAuthenticated }
        eventually("Unpair releases held keys and buttons") { recording.snapshot.count == 7 }
        XCTAssertFalse(agent.isRemoteControlActive)
        XCTAssertFalse(receiver.isSecureInputEnabled)
        XCTAssertEqual(recording.snapshot.suffix(3).map(\.type),[.leftMouseUp,.keyUp,.flagsChanged])
    }

    func testSimultaneousConnectionTieBreakStabilizesAndNeverRearms() throws {
        let a = try PeerIdentityFixture(), b = try PeerIdentityFixture()
        defer { a.cleanup(); b.cleanup() }
        let first = LanBridgePeer(secureIdentity:a.identity), second = LanBridgePeer(secureIdentity:b.identity)
        defer { first.dispose(); second.dispose() }
        prepare(first,card:b.identity.card,role:.source); prepare(second,card:a.identity.card,role:.receiver)
        for _ in 0..<8 {
            first.stopConnecting(); second.stopConnecting()
            eventually("Disconnected before duplicate run") { !first.isAuthenticated && !second.isAuthenticated }
            first.connect(host:"127.0.0.1",port:sourcePort(second)); second.connect(host:"127.0.0.1",port:sourcePort(first))
            eventually("Dual connection adopted") { first.isAuthenticated && second.isAuthenticated }
            Thread.sleep(forTimeInterval:0.25)
            XCTAssertTrue(first.isAuthenticated); XCTAssertTrue(second.isAuthenticated)
            XCTAssertTrue(first.usesPreferredConnection); XCTAssertTrue(second.usesPreferredConnection)
            XCTAssertFalse(first.isSecureInputEnabled); XCTAssertFalse(second.isSecureInputEnabled)
            let id = first.authenticatedSession?.sessionId
            Thread.sleep(forTimeInterval:0.1)
            XCTAssertEqual(first.authenticatedSession?.sessionId,id)
        }
    }

    func testBoundsAndRawIdentityPolicyRejectsAmbiguity() {
        XCTAssertNotNil(LanBridgePeer.parseBounds(["left":-800,"top":0,"width":800,"height":600,"right":-1,"bottom":599]))
        XCTAssertNotNil(LanBridgePeer.parseBounds(["left":0,"top":0,"width":800,"height":600,"right":800,"bottom":600]))
        XCTAssertNil(LanBridgePeer.parseBounds(["left":0,"top":0,"width":800,"height":600,"right":900,"bottom":600]))
        XCTAssertNil(LanBridgePeer.parseBounds(["left":false,"top":0,"width":800,"height":600,"right":800,"bottom":600]))
        XCTAssertNil(LanBridgePeer.parseBounds(["left":0,"top":0,"width":800,"height":0,"right":800,"bottom":0]))
        XCTAssertFalse(lanExactIdentity("macos:é","macos:e\u{301}"))
        XCTAssertTrue(LanBridgePeer.prefersOutgoing(local:"macos:a",peer:"macos:b"))
        XCTAssertFalse(LanBridgePeer.prefersOutgoing(local:"macos:b",peer:"macos:a"))
    }
}
