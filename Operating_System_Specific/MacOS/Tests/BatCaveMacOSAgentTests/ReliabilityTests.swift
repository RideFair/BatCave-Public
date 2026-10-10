import ApplicationServices
import Foundation
@preconcurrency import Network
import XCTest
@testable import BatCaveMacOSAgent

private final class EventRecorder: @unchecked Sendable {
    private let lock = NSLock()
    private var events: [CGEvent] = []
    var onEvent: ((CGEvent) -> Void)?
    func record(_ event: CGEvent) {
        lock.lock()
        events.append(event)
        lock.unlock()
        onEvent?(event)
    }
    var snapshot: [CGEvent] {
        lock.lock()
        defer { lock.unlock() }
        return events
    }
}

final class ReliabilityTests: XCTestCase {
    private let windows = "windows:TEST"

    private func handoff(to target: String) -> CursorHandoffMessage {
        CursorHandoffMessage(linkId: "test", segmentPosition: 0.5, sourceDisplayId: "test", sourceEdge: "right", sourcePosition: 0.5, targetDeviceId: target, targetDisplayId: "test", targetEdge: "left", targetPosition: 0.5, targetX: 0, targetY: 0)
    }

    private func key(_ vk: Int, up: Bool = false, text: String? = nil) -> KeyInputMessage {
        KeyInputMessage(virtualKey: vk, scanCode: 0, isKeyUp: up, isExtended: false, text: text)
    }

    private func holdInput(_ agent: BatCaveMacOSAgent, _ lan: LanBridgePeer) {
        agent.lanPeer(lan, didReceiveMouseButton: MouseButtonMessage(button: "left", isDown: true), sourceDeviceId: windows)
        agent.lanPeer(lan, didReceiveKeyInput: key(0x10), sourceDeviceId: windows)
        agent.lanPeer(lan, didReceiveKeyInput: key(0x25), sourceDeviceId: windows)
    }

    func testReturnHandoffReleasesHeldInputAndCanReenter() {
        let lan = LanBridgePeer(), output = EventRecorder()
        let agent = BatCaveMacOSAgent(lan: lan, showHUD: false, eventPoster: output.record)
        agent.lanPeer(lan, didReceiveCursorHandoff: handoff(to: lan.localDeviceId), sourceDeviceId: windows)
        holdInput(agent, lan)
        XCTAssertTrue(output.snapshot.last!.flags.contains(.maskShift))
        agent.lanPeer(lan, didReceiveCursorHandoff: handoff(to: windows), sourceDeviceId: windows)
        XCTAssertFalse(agent.isRemoteControlActive)
        XCTAssertEqual(output.snapshot.suffix(3).map(\.type), [.leftMouseUp, .keyUp, .flagsChanged])
        let releaseCodes = output.snapshot.suffix(2).map { $0.getIntegerValueField(.keyboardEventKeycode) }
        XCTAssertFalse(output.snapshot.last!.flags.contains(.maskShift))
        XCTAssertEqual(releaseCodes, [123, 56]) // Left arrow before Shift.
        let count = output.snapshot.count
        agent.lanPeer(lan, didReceiveKeyInput: key(0x25), sourceDeviceId: windows)
        XCTAssertEqual(output.snapshot.count, count)
        agent.lanPeer(lan, didReceiveCursorHandoff: handoff(to: lan.localDeviceId), sourceDeviceId: windows)
        XCTAssertTrue(agent.isRemoteControlActive)
    }

    func testDisconnectAndStopReleaseExactlyOnce() {
        for reason in ["disconnect", "stop", "remoteStop"] {
            let lan = LanBridgePeer(), output = EventRecorder()
            let agent = BatCaveMacOSAgent(lan: lan, showHUD: false, eventPoster: output.record)
            agent.lanPeer(lan, didReceiveCursorHandoff: handoff(to: lan.localDeviceId), sourceDeviceId: windows)
            holdInput(agent, lan)
            if reason == "disconnect" { agent.lanPeer(lan, didDisconnect: "test FIN") }
            else if reason == "stop" { agent.stop(reason: "test") }
            else { agent.lanPeer(lan, didReceiveTestStop: RemoteTestStopMessage(reason: "test"), sourceDeviceId: windows) }
            XCTAssertFalse(agent.isRemoteControlActive)
            XCTAssertEqual(output.snapshot.suffix(3).map(\.type), [.leftMouseUp, .keyUp, .flagsChanged])
            let count = output.snapshot.count
            agent.stop(reason: "again")
            XCTAssertEqual(output.snapshot.count, count)
        }
    }

    func testReplacementHandoffReleasesPreviousKeys() {
        let lan = LanBridgePeer(), output = EventRecorder()
        let agent = BatCaveMacOSAgent(lan: lan, showHUD: false, eventPoster: output.record)
        agent.lanPeer(lan, didReceiveCursorHandoff: handoff(to: lan.localDeviceId), sourceDeviceId: windows)
        holdInput(agent, lan)
        agent.lanPeer(lan, didReceiveCursorHandoff: handoff(to: lan.localDeviceId), sourceDeviceId: windows)
        XCTAssertEqual(output.snapshot.suffix(4).map(\.type), [.leftMouseUp, .keyUp, .flagsChanged, .mouseMoved])
    }

    func testInputRequiresHandoffAndCurrentSource() {
        let lan = LanBridgePeer(), output = EventRecorder()
        let agent = BatCaveMacOSAgent(lan: lan, showHUD: false, eventPoster: output.record)
        holdInput(agent, lan)
        XCTAssertTrue(output.snapshot.isEmpty)
        agent.lanPeer(lan, didReceiveCursorHandoff: handoff(to: lan.localDeviceId), sourceDeviceId: windows)
        agent.lanPeer(lan, didReceiveKeyInput: key(0x25), sourceDeviceId: "windows:OTHER")
        agent.lanPeer(lan, didReceiveMouseDelta: MouseDeltaMessage(deltaX: 9, deltaY: 9), sourceDeviceId: "windows:OTHER")
        agent.lanPeer(lan, didReceiveTestStop: RemoteTestStopMessage(reason: "other"), sourceDeviceId: "windows:OTHER")
        XCTAssertEqual(output.snapshot.count, 1)
        XCTAssertTrue(agent.isRemoteControlActive)
    }

    func testUnicodeKeyUpDoesNotProduceUnmatchedNativeRelease() {
        let lan = LanBridgePeer(), output = EventRecorder()
        let agent = BatCaveMacOSAgent(lan: lan, showHUD: false, eventPoster: output.record)
        agent.lanPeer(lan, didReceiveCursorHandoff: handoff(to: lan.localDeviceId), sourceDeviceId: windows)
        agent.lanPeer(lan, didReceiveKeyInput: key(0x41, text: "a"), sourceDeviceId: windows)
        agent.lanPeer(lan, didReceiveKeyInput: key(0x41, up: true), sourceDeviceId: windows)
        XCTAssertEqual(output.snapshot.map(\.type), [.mouseMoved, .keyDown, .keyUp])
        agent.stop(reason: "test")
        XCTAssertEqual(output.snapshot.count, 3)
    }

    func testDragEventsAndTaggedPassthrough() throws {
        let lan = LanBridgePeer(), output = EventRecorder()
        let agent = BatCaveMacOSAgent(lan: lan, showHUD: false, eventPoster: output.record)
        agent.lanPeer(lan, didReceiveCursorHandoff: handoff(to: lan.localDeviceId), sourceDeviceId: windows)
        agent.lanPeer(lan, didReceiveMouseButton: MouseButtonMessage(button: "left", isDown: true), sourceDeviceId: windows)
        agent.lanPeer(lan, didReceiveMouseDelta: MouseDeltaMessage(deltaX: 8, deltaY: 3), sourceDeviceId: windows)
        let drag = try XCTUnwrap(output.snapshot.last)
        XCTAssertEqual(drag.type, .leftMouseDragged)
        XCTAssertNotEqual(drag.getIntegerValueField(.eventSourceUserData), 0)
        XCTAssertNotNil(agent.handle(type: drag.type, event: drag))
        agent.stop(reason: "test")
    }

    func testTapDisableReleasesRemoteInput() throws {
        let lan = LanBridgePeer(), output = EventRecorder()
        let agent = BatCaveMacOSAgent(lan: lan, showHUD: false, eventPoster: output.record)
        agent.lanPeer(lan, didReceiveCursorHandoff: handoff(to: lan.localDeviceId), sourceDeviceId: windows)
        holdInput(agent, lan)
        let event = try XCTUnwrap(CGEvent(source: nil))
        _ = agent.handle(type: .tapDisabledByTimeout, event: event)
        XCTAssertFalse(agent.isRemoteControlActive)
        XCTAssertEqual(output.snapshot.suffix(3).map(\.type), [.leftMouseUp, .keyUp, .flagsChanged])
    }

    func testTCPDisconnectReleasesInputAndReconnectsWithFreshHello() throws {
        let listener = try NWListener(using: .tcp, on: .any)
        let server = LoopbackWindowsServer(listener: listener, test: self)
        let ready = expectation(description: "listener ready")
        listener.stateUpdateHandler = { state in if case .ready = state { ready.fulfill() } }
        listener.newConnectionHandler = server.accept
        listener.start(queue: server.queue)
        wait(for: [ready], timeout: 5)
        let lan = LanBridgePeer(allowInsecureLoopback: true), output = EventRecorder()
        let agent = BatCaveMacOSAgent(lan: lan, showHUD: false, eventPoster: output.record)
        let released = expectation(description: "disconnect releases held key")
        output.onEvent = { event in if event.type == .keyUp { released.fulfill() } }
        lan.connect(host: "127.0.0.1", port: try XCTUnwrap(listener.port).rawValue, automaticallyReconnect: true)
        wait(for: [released, server.reconnected], timeout: 12)
        XCTAssertFalse(agent.isRemoteControlActive)
        XCTAssertEqual(output.snapshot.map(\.type), [.mouseMoved, .keyDown, .keyUp])
        lan.dispose()
        server.close()
    }
}

private final class LoopbackWindowsServer: @unchecked Sendable {
    let queue = DispatchQueue(label: "BatCave.tests.server")
    let reconnected: XCTestExpectation
    private let listener: NWListener
    private var connections: [NWConnection] = []
    init(listener: NWListener, test: XCTestCase) {
        self.listener = listener
        reconnected = test.expectation(description: "reconnect sends fresh device.hello")
    }
    func accept(_ connection: NWConnection) {
        connections.append(connection)
        let generation = connections.count
        connection.start(queue: queue)
        receiveHello(connection, generation: generation, buffer: Data())
    }
    private func receiveHello(_ connection: NWConnection, generation: Int, buffer: Data) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 4096) { data, _, _, _ in
            var collected = buffer
            if let data { collected.append(data) }
            guard let newline = collected.firstIndex(of: 10) else {
                self.receiveHello(connection, generation: generation, buffer: collected)
                return
            }
            guard let envelope = try? JSONSerialization.jsonObject(with: collected[..<newline]) as? [String: Any],
                  envelope["type"] as? String == "device.hello",
                  let source = envelope["sourceDeviceId"] as? String else { return }
            if generation > 1 { self.reconnected.fulfill(); return }
            let messages: [[String: Any]] = [
                ["type": "cursor.handoff", "payload": ["targetDeviceId": source, "targetEdge": "left", "targetPosition": 0.5]],
                ["type": "input.key", "payload": ["virtualKey": 37, "isKeyUp": false]]
            ]
            var bytes = Data()
            for message in messages {
                var envelope = message
                envelope["protocol"] = "batcave.input.v0"
                envelope["sourceDeviceId"] = "windows:TEST"
                bytes.append(try! JSONSerialization.data(withJSONObject: envelope))
                bytes.append(10)
            }
            // Split a JSON line across receives and coalesce the next line.
            let rest = Data(bytes.dropFirst(9))
            connection.send(content: Data(bytes.prefix(9)), completion: .contentProcessed { _ in
                connection.send(content: rest, completion: .contentProcessed { _ in
                    connection.send(content: nil, contentContext: .finalMessage, isComplete: true, completion: .contentProcessed { _ in })
                })
            })
        }
    }
    func close() {
        queue.sync {
            connections.forEach { $0.cancel() }
            listener.cancel()
        }
    }
}
