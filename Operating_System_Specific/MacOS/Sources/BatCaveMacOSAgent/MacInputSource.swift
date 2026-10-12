import Foundation
import ApplicationServices

// Native source state is independent of the wire queue. Fixtures use a recording
// sink and synthetic CGEvents; only the agent installs an actual event tap.
final class MacInputSource: @unchecked Sendable {
    typealias WireSink = (String, [String: Any], String, UInt64) -> Void
    private let lock = NSRecursiveLock()
    private let send: WireSink
    private let post: (CGEvent) -> Void
    private var session: LanAuthenticatedSession?
    private var activation: UInt64 = 0
    private var localBounds = DisplayBounds(minX: 0, maxX: 1, minY: 0, maxY: 1)
    private var peerOnRight = true
    private var remote = false
    private var cursor = CGPoint.zero
    private var localCursor = CGPoint.zero
    private var physicalKeys = Set<CGKeyCode>()
    private var physicalButtons = Set<UInt32>()
    private var remoteKeys = Set<CGKeyCode>()
    private var remoteButtons = Set<UInt32>()
    private var keyText: [CGKeyCode: String] = [:]
    private var suppressedReleases = Set<CGKeyCode>()
    private var suppressedButtonReleases = Set<UInt32>()
    private var scrollVerticalRemainder = 0.0
    private var scrollHorizontalRemainder = 0.0
    private let marker: Int64 = 0x4261_7443_6176_6504

    init(send: @escaping WireSink, post: @escaping (CGEvent) -> Void) {
        self.send = send; self.post = post
    }
    var isActive: Bool { lock.withLock { session != nil } }
    var ownsRemoteCursor: Bool { lock.withLock { session != nil && remote } }

    func start(session: LanAuthenticatedSession, activation: UInt64, peerOnRight: Bool,
               localBounds: DisplayBounds, pointer: CGPoint, initialFlags: CGEventFlags = []) {
        lock.withLock {
            pauseLocked(sendStop: false)
            self.session = session; self.activation = activation; self.peerOnRight = peerOnRight
            self.localBounds = localBounds; self.localCursor = localBounds.clamped(pointer)
            remote = false; cursor = .zero
            // Include modifiers already held before the explicit activation.
            let flags = initialFlags
            for pair: (CGKeyCode, CGKeyCode, CGEventFlags) in [(56,60,.maskShift),(59,62,.maskControl),(58,61,.maskAlternate),(55,54,.maskCommand)] {
                if let group = Self.modifierGroup(pair.0), flags.rawValue & group.sideMask != 0 {
                    if modifierIsDown(pair.0, flags) { physicalKeys.insert(pair.0) }
                    if modifierIsDown(pair.1, flags) { physicalKeys.insert(pair.1) }
                } else if flags.contains(pair.2) { physicalKeys.insert(pair.0) }
            }
        }
    }

    func pause(sendStop: Bool = true) { lock.withLock { pauseLocked(sendStop: sendStop) } }
    private func pauseLocked(sendStop: Bool) {
        if remote {
            releaseRemoteSnapshot()
            // Local keys were released when ownership crossed. Swallow their
            // later physical releases after Pause instead of issuing unmatched ups.
            suppressedReleases.formUnion(physicalKeys)
            suppressedButtonReleases.formUnion(physicalButtons)
            postMove(localBounds.clamped(localCursor))
        }
        if sendStop { emit("control.testStop", ["reason": "Mac sharing paused"]) }
        session = nil; remote = false; activation = 0
        physicalKeys.removeAll(); physicalButtons.removeAll(); remoteKeys.removeAll(); remoteButtons.removeAll(); keyText.removeAll()
        scrollVerticalRemainder = 0; scrollHorizontalRemainder = 0
    }

    // Returns true only when the native event should be suppressed.
    func handle(type: CGEventType, event: CGEvent) -> Bool {
        lock.withLock {
            if event.getIntegerValueField(.eventSourceUserData) == marker { return false }
            let key = CGKeyCode(truncatingIfNeeded: event.getIntegerValueField(.keyboardEventKeycode))
            if (type == .keyUp || (type == .flagsChanged && !modifierIsDown(key, event.flags))),
               suppressedReleases.remove(key) != nil {
                releasePhysicalKey(key, flags: event.flags)
                return true
            }
            if let button = buttonChange(type: type, event: event), !button.down,
               suppressedButtonReleases.remove(button.number) != nil {
                physicalButtons.remove(button.number)
                if remote, remoteButtons.remove(button.number) != nil { emitButton(button.number, down: false) }
                return true
            }
            guard session != nil else { return false }
            if type == .keyDown && key == 111 { pauseLocked(sendStop: true); return true } // F12
            if type == .keyDown || type == .keyUp || type == .flagsChanged {
                let down = type == .flagsChanged ? modifierIsDown(key, event.flags) : type == .keyDown
                if !down {
                    releasePhysicalKey(key, flags: event.flags)
                    return remote
                }
                physicalKeys.insert(key)
                if !remote { return false }
                guard let vk = Self.windowsVirtualKey(key) else { return false }
                let text: String? = type == .keyDown && !Self.isModifier(key) ? unicodeText(event) : nil
                remoteKeys.insert(key); if let text { keyText[key] = text }
                emitKey(vk, up: false, text: text)
                return true
            }
            if let button = buttonChange(type: type, event: event) {
                if button.down { physicalButtons.insert(button.number) } else { physicalButtons.remove(button.number) }
                if remote {
                    if button.down { remoteButtons.insert(button.number) } else { remoteButtons.remove(button.number) }
                    emitButton(button.number, down: button.down)
                    return true
                }
                return false
            }
            if type == .scrollWheel {
                guard remote else { return false }
                // Quartz line scrolling to the existing 120-units-per-notch wire.
                // Accumulate fractional trackpad lines rather than losing them.
                scrollVerticalRemainder += event.getDoubleValueField(.scrollWheelEventFixedPtDeltaAxis1) * 120
                scrollHorizontalRemainder += event.getDoubleValueField(.scrollWheelEventFixedPtDeltaAxis2) * 120
                let v = Int(scrollVerticalRemainder.rounded(.towardZero)), h = Int(scrollHorizontalRemainder.rounded(.towardZero))
                scrollVerticalRemainder -= Double(v); scrollHorizontalRemainder -= Double(h)
                if v != 0 || h != 0 { emit("input.mouseWheel", ["verticalDelta": v, "horizontalDelta": h]) }
                return true
            }
            guard [.mouseMoved, .leftMouseDragged, .rightMouseDragged, .otherMouseDragged].contains(type) else { return false }
            let dx = boundedDelta(event.getIntegerValueField(.mouseEventDeltaX))
            let dy = boundedDelta(event.getIntegerValueField(.mouseEventDeltaY))
            guard dx != 0 || dy != 0 else { return remote }
            if remote {
                let desired = CGPoint(x: cursor.x + CGFloat(dx), y: cursor.y + CGFloat(dy))
                guard let session else { return false }
                let bounds = session.peerBounds.displayBounds
                let crossedBack = peerOnRight ? (desired.x < bounds.minX && dx < 0) : (desired.x >= bounds.maxX && dx > 0)
                if crossedBack { returnLocal(position: normalizedY(desired, bounds)); return true }
                cursor = bounds.clamped(desired)
                emit("input.mouseDelta", ["deltaX": dx, "deltaY": dy])
                return true
            }
            localCursor = localBounds.clamped(event.location)
            let atEdge = peerOnRight ? (event.location.x >= localBounds.maxX - 1 && dx > 0)
                : (event.location.x <= localBounds.minX + 1 && dx < 0)
            if atEdge { enterRemote(position: normalizedY(event.location, localBounds)); return true }
            return false
        }
    }

    func receivedHandoff(_ handoff: CursorHandoffMessage) {
        lock.withLock {
            guard let session, !lanExactIdentity(handoff.targetDeviceId, session.deviceId), remote else { return }
            // A source only accepts ownership returning to this local identity;
            // the transport already restricts targets to the two authenticated IDs.
            returnLocal(position: handoff.targetPosition, sendHandoff: false)
        }
    }

    private func enterRemote(position: Double) {
        guard let session else { return }
        let bounds = session.peerBounds.displayBounds
        cursor = CGPoint(x: peerOnRight ? bounds.minX + 2 : bounds.maxX - 2,
                         y: bounds.minY + CGFloat(position) * max(0, bounds.height - 1))
        emitHandoff(target: session.deviceId, targetBounds: bounds,
                    edge: peerOnRight ? "left" : "right", position: position)
        releaseLocalSnapshot()
        remote = true
        remoteKeys = physicalKeys; remoteButtons = physicalButtons
        for key in remoteKeys.sorted() { if let vk = Self.windowsVirtualKey(key) { emitKey(vk, up: false) } }
        for button in remoteButtons.sorted() { emitButton(button, down: true) }
    }

    private func returnLocal(position: Double, sendHandoff: Bool = true) {
        guard session != nil else { return }
        releaseRemoteSnapshot()
        if sendHandoff { emitHandoff(target: "", targetBounds: localBounds, edge: peerOnRight ? "right" : "left", position: position) }
        remote = false
        localCursor = CGPoint(x: peerOnRight ? localBounds.maxX - 3 : localBounds.minX + 3,
                              y: localBounds.minY + CGFloat(min(max(position, 0), 1)) * max(0, localBounds.height - 1))
        postMove(localCursor)
        restoreLocalSnapshot()
    }

    private func releasePhysicalKey(_ key: CGKeyCode, flags: CGEventFlags) {
        var released: Set<CGKeyCode> = [key]
        if let group = Self.modifierGroup(key), !flags.contains(group.flag) {
            // An aggregate-only initial snapshot may have used the left key.
            // Releasing the last physical side must clear that fallback too.
            released.formUnion(physicalKeys.intersection(group.keys))
        }
        physicalKeys.subtract(released)
        for releasedKey in released.sorted() {
            keyText.removeValue(forKey: releasedKey)
            if remote, remoteKeys.remove(releasedKey) != nil, let vk = Self.windowsVirtualKey(releasedKey) { emitKey(vk, up: true) }
        }
    }
    private static func modifierGroup(_ key: CGKeyCode) -> (keys: Set<CGKeyCode>, flag: CGEventFlags, sideMask: UInt64)? {
        switch key {
        case 56,60: return ([56,60],.maskShift,0x6)
        case 59,62: return ([59,62],.maskControl,0x2001)
        case 58,61: return ([58,61],.maskAlternate,0x60)
        case 54,55: return ([54,55],.maskCommand,0x18)
        default: return nil
        }
    }
    private func releaseRemoteSnapshot() {
        for button in remoteButtons.sorted() { emitButton(button, down: false) }
        for key in remoteKeys.sorted(by: Self.releaseOrder) { if let vk = Self.windowsVirtualKey(key) { emitKey(vk, up: true) } }
        remoteButtons.removeAll(); remoteKeys.removeAll(); keyText.removeAll()
    }
    private func releaseLocalSnapshot() {
        for button in physicalButtons.sorted() { postButton(button, down: false) }
        for key in physicalKeys.sorted(by: Self.releaseOrder) { postKey(key, down: false, flags: []) }
    }
    private func restoreLocalSnapshot() {
        for key in physicalKeys.sorted(by: { Self.releaseOrder($1, $0) }) { postKey(key, down: true, flags: flagsForKeys()) }
        for button in physicalButtons.sorted() { postButton(button, down: true) }
    }
    private func flagsForKeys() -> CGEventFlags {
        var flags: CGEventFlags = []
        if !physicalKeys.isDisjoint(with: [56,60]) { flags.insert(.maskShift) }
        if !physicalKeys.isDisjoint(with: [59,62]) { flags.insert(.maskControl) }
        if !physicalKeys.isDisjoint(with: [58,61]) { flags.insert(.maskAlternate) }
        if !physicalKeys.isDisjoint(with: [54,55]) { flags.insert(.maskCommand) }
        return flags
    }
    private func emit(_ type: String, _ payload: [String: Any]) {
        guard let session else { return }; send(type, payload, session.sessionId, activation)
    }
    private func emitKey(_ vk: Int, up: Bool, text: String? = nil) {
        var payload: [String: Any] = ["virtualKey": vk, "scanCode": 0, "isKeyUp": up,
                                     "isExtended": [0xA3,0xA5,0x5B,0x5C,0x21,0x22,0x23,0x24,0x25,0x26,0x27,0x28,0x2E].contains(vk)]
        if let text, !text.isEmpty { payload["text"] = text }
        emit("input.key", payload)
    }
    private func emitButton(_ number: UInt32, down: Bool) {
        guard let name = [UInt32(0): "left", 1: "right", 2: "middle", 3: "x1", 4: "x2"][number] else { return }
        emit("input.mouseButton", ["button": name, "isDown": down])
    }
    private func emitHandoff(target: String, targetBounds: DisplayBounds, edge: String, position: Double) {
        guard let session else { return }
        // The source local ID is supplied by the transport when target is empty.
        let id = target.isEmpty ? localDeviceId : target
        emit("cursor.handoff", ["linkId": "default-horizontal", "segmentPosition": position,
            "sourceDisplayId": (target.isEmpty ? session.deviceId : localDeviceId) + ":desktop",
            "sourceEdge": edge == "left" ? "right" : "left", "sourcePosition": position,
            "targetDeviceId": id, "targetDisplayId": id + ":desktop", "targetEdge": edge, "targetPosition": position,
            "targetX": Int(edge == "left" ? targetBounds.minX + 2 : targetBounds.maxX - 2),
            "targetY": Int(targetBounds.minY + CGFloat(position) * max(0, targetBounds.height - 1))])
    }
    var localDeviceId = ""
    private func normalizedY(_ point: CGPoint, _ bounds: DisplayBounds) -> Double {
        Double(min(max((point.y - bounds.minY) / max(1, bounds.height - 1), 0), 1))
    }
    private func boundedDelta(_ n: Int64) -> Int { Int(min(max(n, -1_000_000), 1_000_000)) }
    private func unicodeText(_ event: CGEvent) -> String? {
        guard !event.flags.contains(.maskCommand), !event.flags.contains(.maskControl), !event.flags.contains(.maskAlternate) else { return nil }
        var chars = [UniChar](repeating: 0, count: 4096), length = 0
        event.keyboardGetUnicodeString(maxStringLength: chars.count, actualStringLength: &length, unicodeString: &chars)
        guard length > 0 && length <= chars.count else { return nil }
        let text = String(utf16CodeUnits: chars, count: length)
        return text.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) ? nil : text
    }
    private func buttonChange(type: CGEventType, event: CGEvent) -> (number: UInt32, down: Bool)? {
        switch type {
        case .leftMouseDown: return (0,true)
        case .leftMouseUp: return (0,false)
        case .rightMouseDown: return (1,true)
        case .rightMouseUp: return (1,false)
        case .otherMouseDown: return (UInt32(clamping: event.getIntegerValueField(.mouseEventButtonNumber)),true)
        case .otherMouseUp: return (UInt32(clamping: event.getIntegerValueField(.mouseEventButtonNumber)),false)
        default: return nil
        }
    }
    private func modifierIsDown(_ key: CGKeyCode, _ flags: CGEventFlags) -> Bool {
        // Device-dependent masks distinguish the two physical modifier keys.
        let side: [CGKeyCode: UInt64] = [56:0x2,60:0x4,59:0x1,62:0x2000,58:0x20,61:0x40,55:0x8,54:0x10]
        if let bit = side[key], let group = Self.modifierGroup(key), flags.rawValue & group.sideMask != 0 { return flags.rawValue & bit != 0 }
        switch key {
        case 56,60: return flags.contains(.maskShift)
        case 59,62: return flags.contains(.maskControl)
        case 58,61: return flags.contains(.maskAlternate)
        case 54,55: return flags.contains(.maskCommand)
        default: return false
        }
    }
    static func isModifier(_ key: CGKeyCode) -> Bool { [54,55,56,58,59,60,61,62].contains(key) }
    private static func releaseOrder(_ a: CGKeyCode, _ b: CGKeyCode) -> Bool {
        isModifier(a) == isModifier(b) ? a < b : !isModifier(a)
    }
    private func postMove(_ point: CGPoint) {
        if let event = CGEvent(mouseEventSource: nil, mouseType: .mouseMoved, mouseCursorPosition: point, mouseButton: .left) { tagAndPost(event) }
    }
    private func postButton(_ number: UInt32, down: Bool) {
        let type: CGEventType = number == 0 ? (down ? .leftMouseDown : .leftMouseUp)
            : number == 1 ? (down ? .rightMouseDown : .rightMouseUp) : (down ? .otherMouseDown : .otherMouseUp)
        if let event = CGEvent(mouseEventSource: nil, mouseType: type, mouseCursorPosition: localCursor,
                              mouseButton: CGMouseButton(rawValue: number) ?? .center) { tagAndPost(event) }
    }
    private func postKey(_ key: CGKeyCode, down: Bool, flags: CGEventFlags) {
        if let event = CGEvent(keyboardEventSource: nil, virtualKey: key, keyDown: down) {
            if Self.isModifier(key) { event.type = .flagsChanged }
            event.flags = flags; tagAndPost(event)
        }
    }
    private func tagAndPost(_ event: CGEvent) { event.setIntegerValueField(.eventSourceUserData, value: marker); post(event) }

    static func windowsVirtualKey(_ key: CGKeyCode) -> Int? {
        let map: [CGKeyCode: Int] = [
            0:0x41,1:0x53,2:0x44,3:0x46,4:0x48,5:0x47,6:0x5A,7:0x58,8:0x43,9:0x56,11:0x42,
            12:0x51,13:0x57,14:0x45,15:0x52,16:0x59,17:0x54,18:0x31,19:0x32,20:0x33,21:0x34,
            22:0x36,23:0x35,24:0xBB,25:0x39,26:0x37,27:0xBD,28:0x38,29:0x30,30:0xDD,31:0x4F,
            32:0x55,33:0xDB,34:0x49,35:0x50,37:0x4C,38:0x4A,39:0xDE,40:0x4B,41:0xBA,42:0xDC,
            43:0xBC,44:0xBF,45:0x4E,46:0x4D,47:0xBE,36:0x0D,48:0x09,49:0x20,50:0xC0,51:0x08,
            53:0x1B,54:0x5C,55:0x5B,56:0xA0,60:0xA1,59:0xA2,62:0xA3,58:0xA4,61:0xA5,
            65:0x6E,67:0x6A,69:0x6B,71:0x90,75:0x6F,76:0x0D,78:0x6D,82:0x60,83:0x61,84:0x62,
            85:0x63,86:0x64,87:0x65,88:0x66,89:0x67,91:0x68,92:0x69,
            122:0x70,120:0x71,99:0x72,118:0x73,96:0x74,97:0x75,98:0x76,100:0x77,101:0x78,
            109:0x79,103:0x7A,111:0x7B,116:0x21,121:0x22,119:0x23,115:0x24,117:0x2E,
            123:0x25,126:0x26,124:0x27,125:0x28]
        return map[key]
    }
}
