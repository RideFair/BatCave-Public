import XCTest
import ApplicationServices
@testable import BatCaveMacOSAgent

private final class SourceRecordingSink {
    var frames: [(String, [String: Any], String, UInt64)] = []
    var events: [CGEvent] = []
    lazy var source = MacInputSource(send: { self.frames.append(($0,$1,$2,$3)) }, post: { self.events.append($0) })
    let bounds = DisplayBounds(minX: 0, maxX: 800, minY: 0, maxY: 600)
    func start(right: Bool = true, session: String = "session-1", activation: UInt64 = 4, flags: CGEventFlags = []) {
        source.localDeviceId = "macos:source"
        source.start(session: LanAuthenticatedSession(sessionId: session, deviceId: "linux:peer",
            fingerprint: String(repeating: "A", count: 64), peerBounds: LanDesktopBounds(left: 0, top: 0, width: 1000, height: 700), role: .source),
            activation: activation, peerOnRight: right, localBounds: bounds, pointer: CGPoint(x:400,y:300), initialFlags:flags)
    }
    func move(x: CGFloat, y: CGFloat = 300, dx: Int64, dy: Int64 = 0) -> Bool {
        let e = CGEvent(mouseEventSource: nil, mouseType: .mouseMoved, mouseCursorPosition: CGPoint(x:x,y:y), mouseButton: .left)!
        e.setIntegerValueField(.mouseEventDeltaX, value: dx); e.setIntegerValueField(.mouseEventDeltaY, value: dy)
        return source.handle(type:.mouseMoved,event:e)
    }
    func key(_ code: CGKeyCode, up: Bool = false, flags: CGEventFlags = [], text: String? = nil) -> Bool {
        let e = CGEvent(keyboardEventSource:nil, virtualKey:code,keyDown:!up)!
        if MacInputSource.isModifier(code) { e.type = .flagsChanged }
        e.flags = flags
        if let text { let utf = Array(text.utf16); e.keyboardSetUnicodeString(stringLength: utf.count, unicodeString:utf) }
        return source.handle(type:e.type,event:e)
    }
    func button(down: Bool) -> Bool {
        let type: CGEventType = down ? .leftMouseDown : .leftMouseUp
        let e = CGEvent(mouseEventSource:nil,mouseType:type,mouseCursorPosition:CGPoint(x:799,y:300),mouseButton:.left)!
        return source.handle(type:type,event:e)
    }
}

final class MacSourceTests: XCTestCase {
    func testPersistentBothWaysCrossingWithoutTestStart() {
        for right in [true,false] {
            let sink = SourceRecordingSink(); sink.start(right:right)
            XCTAssertFalse(sink.move(x:400,dx:5))
            XCTAssertTrue(sink.move(x:right ? 799 : 0,dx:right ? 5 : -5))
            XCTAssertTrue(sink.source.ownsRemoteCursor)
            XCTAssertEqual(sink.frames.first?.0,"cursor.handoff")
            XCTAssertEqual(sink.frames.first?.1["targetDeviceId"] as? String,"linux:peer")
            XCTAssertEqual(sink.frames.first?.1["targetEdge"] as? String,right ? "left":"right")
            XCTAssertTrue(sink.move(x:799,dx:right ? 20 : -20))
            XCTAssertEqual(sink.frames.last?.0,"input.mouseDelta")
            XCTAssertTrue(sink.move(x:799,dx:right ? -100 : 100))
            XCTAssertFalse(sink.source.ownsRemoteCursor)
            XCTAssertTrue(sink.source.isActive)
            XCTAssertEqual(sink.frames.last?.0,"cursor.handoff")
            XCTAssertEqual(sink.frames.last?.1["targetDeviceId"] as? String,"macos:source")
            XCTAssertEqual(sink.frames.last?.1["targetEdge"] as? String,right ? "right":"left")
            XCTAssertFalse(sink.frames.contains { $0.0 == "control.testStart" })
        }
    }

    func testDragAndModifierSnapshotsReleaseAndRestoreBothSides() {
        let sink = SourceRecordingSink(); sink.start()
        XCTAssertFalse(sink.key(56,flags:.maskShift))
        XCTAssertFalse(sink.button(down:true))
        XCTAssertTrue(sink.move(x:799,dx:5))
        XCTAssertEqual(sink.events.map(\.type),[.leftMouseUp,.flagsChanged])
        let downs = sink.frames.filter { $0.0 == "input.key" || $0.0 == "input.mouseButton" }
        XCTAssertEqual(downs.count,2)
        XCTAssertEqual(downs[0].1["virtualKey"] as? Int,0xA0)
        XCTAssertEqual(downs[0].1["isKeyUp"] as? Bool,false)
        XCTAssertEqual(downs[1].1["isDown"] as? Bool,true)
        XCTAssertTrue(sink.move(x:799,dx:-30))
        let releases = sink.frames.filter { $0.0 == "input.key" || $0.0 == "input.mouseButton" }.suffix(2)
        XCTAssertEqual(releases.first?.0,"input.mouseButton")
        XCTAssertEqual(releases.last?.1["isKeyUp"] as? Bool,true)
        XCTAssertEqual(sink.events.suffix(3).map(\.type),[.mouseMoved,.flagsChanged,.leftMouseDown])
        XCTAssertFalse(sink.key(56,up:true))
        XCTAssertFalse(sink.button(down:false))
    }

    func testPauseBalancesHeldStateAndDoesNotResumeOnReplacement() {
        let sink = SourceRecordingSink(); sink.start()
        _ = sink.move(x:799,dx:5); _ = sink.key(59,flags:.maskControl); _ = sink.key(0,flags:.maskControl); _ = sink.button(down:true)
        sink.source.pause()
        XCTAssertFalse(sink.source.isActive)
        XCTAssertEqual(sink.frames.suffix(4).map(\.0),["input.mouseButton","input.key","input.key","control.testStop"])
        XCTAssertTrue(sink.key(0,up:true))
        XCTAssertTrue(sink.key(59,up:true))
        XCTAssertTrue(sink.button(down:false))
        let count = sink.frames.count
        _ = sink.move(x:799,dx:5)
        XCTAssertEqual(sink.frames.count,count)
        sink.source.pause()
        XCTAssertEqual(sink.frames.count,count)
        sink.start(session:"session-2",activation:9)
        XCTAssertFalse(sink.source.ownsRemoteCursor)
        _ = sink.move(x:799,dx:5)
        XCTAssertEqual(sink.frames.last?.2,"session-2")
        XCTAssertEqual(sink.frames.last?.3,9)
    }

    func testModifierFamiliesIgnoreUnrelatedLowBitsAndPreserveRightSides() {
        for (key, flag, bit, vk): (CGKeyCode,CGEventFlags,UInt64,Int) in [
            (60,.maskShift,0x4,0xA1),(62,.maskControl,0x2000,0xA3),(61,.maskAlternate,0x40,0xA5),(54,.maskCommand,0x10,0x5C)] {
            let sink = SourceRecordingSink()
            sink.start(flags:[flag,CGEventFlags(rawValue:bit)])
            _ = sink.move(x:799,dx:5)
            XCTAssertEqual(sink.frames.last?.1["virtualKey"] as? Int,vk)
            _ = sink.key(key,up:true)
            XCTAssertEqual(sink.frames.last?.1["virtualKey"] as? Int,vk)
            XCTAssertEqual(sink.frames.last?.1["isKeyUp"] as? Bool,true)
        }
        let sink = SourceRecordingSink()
        sink.start(flags:[.maskShift,.maskControl,.maskNonCoalesced,CGEventFlags(rawValue:1)])
        _ = sink.move(x:799,dx:5)
        let pressed = sink.frames.filter { $0.0 == "input.key" }.compactMap { $0.1["virtualKey"] as? Int }
        XCTAssertEqual(Set(pressed),[0xA0,0xA2])
    }

    func testPauseThenResumeWhileShiftHeldStillReleasesRemoteModifier() {
        let sink = SourceRecordingSink(); sink.start(); _ = sink.move(x:799,dx:5)
        _ = sink.key(56,flags:.maskShift)
        sink.source.pause()
        sink.start(session:"resumed",activation:8,flags:.maskShift)
        _ = sink.move(x:799,dx:5)
        XCTAssertEqual(sink.frames.last?.1["virtualKey"] as? Int,0xA0)
        XCTAssertEqual(sink.frames.last?.1["isKeyUp"] as? Bool,false)
        XCTAssertTrue(sink.key(56,up:true))
        XCTAssertEqual(sink.frames.last?.1["virtualKey"] as? Int,0xA0)
        XCTAssertEqual(sink.frames.last?.1["isKeyUp"] as? Bool,true)
        let count = sink.frames.count
        sink.source.pause()
        XCTAssertEqual(sink.frames.count,count + 1,"Only stop remains; Shift was already released")
    }

    func testInitialRightModifierAndAggregateFallbackReleaseBalanced() {
        for flags: CGEventFlags in [[.maskShift,CGEventFlags(rawValue:4)], .maskShift] {
            let sink = SourceRecordingSink(); sink.start(flags:flags); _ = sink.move(x:799,dx:5)
            let expected = flags.rawValue & 4 != 0 ? 0xA1 : 0xA0
            XCTAssertEqual(sink.frames.last?.1["virtualKey"] as? Int,expected)
            XCTAssertTrue(sink.key(60,up:true))
            XCTAssertEqual(sink.frames.last?.1["virtualKey"] as? Int,expected)
            XCTAssertEqual(sink.frames.last?.1["isKeyUp"] as? Bool,true)
            let count = sink.frames.count
            sink.source.pause()
            XCTAssertEqual(sink.frames.count,count + 1)
        }
    }

    func testEscapeIsForwardedAndF12RemainsTheSourceSafetyKey() {
        let sink = SourceRecordingSink(); sink.start(); _ = sink.move(x:799,dx:5)
        XCTAssertTrue(sink.key(53)); XCTAssertTrue(sink.key(53,up:true))
        let escape = sink.frames.suffix(2)
        XCTAssertEqual(escape.map { $0.1["virtualKey"] as? Int },[0x1B,0x1B])
        XCTAssertEqual(escape.map { $0.1["isKeyUp"] as? Bool },[false,true])
        XCTAssertTrue(sink.source.isActive)
        XCTAssertTrue(sink.key(111))
        XCTAssertFalse(sink.source.isActive)
        XCTAssertEqual(sink.frames.last?.0,"control.testStop")
    }

    func testUnicodeAndModifiersUseExistingWindowsPayload() {
        let sink = SourceRecordingSink(); sink.start(); _ = sink.move(x:799,dx:5)
        XCTAssertTrue(sink.key(0,text:"é")); XCTAssertTrue(sink.key(0,up:true))
        let keys = sink.frames.filter { $0.0 == "input.key" }
        XCTAssertEqual(keys[0].1["virtualKey"] as? Int,0x41)
        XCTAssertEqual(keys[0].1["text"] as? String,"é")
        XCTAssertEqual(keys[1].1["isKeyUp"] as? Bool,true)
        XCTAssertNil(keys[1].1["text"])
        XCTAssertTrue(sink.key(60,flags:[.maskShift,CGEventFlags(rawValue:4)]))
        XCTAssertEqual(sink.frames.last?.1["virtualKey"] as? Int,0xA1)
    }

    func testRemoteReturnAndTaggedInjectionNeverRecapture() {
        let sink = SourceRecordingSink(); sink.start(); _ = sink.move(x:799,dx:5)
        sink.source.receivedHandoff(CursorHandoffMessage(linkId:"link",segmentPosition:0.4,sourceDisplayId:"peer:desktop",
            sourceEdge:"left",sourcePosition:0.4,targetDeviceId:"macos:source",targetDisplayId:"macos:source:desktop",
            targetEdge:"right",targetPosition:0.4,targetX:99999,targetY:-99999))
        XCTAssertFalse(sink.source.ownsRemoteCursor)
        let e = sink.events.last!
        XCTAssertFalse(sink.source.handle(type:e.type,event:e))
        XCTAssertEqual(e.location.x,797)
        XCTAssertEqual(e.location.y,239.6,accuracy:0.01)
    }
}
