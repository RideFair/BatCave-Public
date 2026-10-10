import ApplicationServices
@preconcurrency import AppKit
import Dispatch
import Darwin
import Foundation

// Computed constants also initialize correctly when SwiftPM imports the executable for tests.
nonisolated private var batCaveEventMarker: Int64 { 0x4261_7443_6176_6504 }
nonisolated private var sessionTap: CGEventTapLocation { CGEventTapLocation(rawValue: 1)! }
nonisolated private var f12KeyCode: Int64 { 111 }
nonisolated private var escapeKeyCode: Int64 { 53 }
nonisolated private var commandKeyCode: CGKeyCode { 55 }

func emit(_ line: String = "") {
    guard !containsInputDetails(line) else { return }
    if setupPreviewRequested() { return }
    FileHandle.standardOutput.write(Data((line + "\n").utf8))
}

private func containsInputDetails(_ line: String) -> Bool {
    ["keyCode=", "vk=", "scan=", "text="].contains { line.contains($0) }
}

func emitPrompt(_ text: String) {
    FileHandle.standardOutput.write(Data(text.utf8))
}

func emitError(_ line: String) {
    guard !containsInputDetails(line) else { return }
    FileHandle.standardError.write(Data((line + "\n").utf8))
}

private final class InstructionPanel: NSPanel {
    override var canBecomeKey: Bool {
        false
    }

    override var canBecomeMain: Bool {
        false
    }
}

private final class InstructionHUD: @unchecked Sendable {
    private let enabled: Bool
    private var panel: InstructionPanel?
    private var titleField: NSTextField?
    private var bodyField: NSTextField?
    private var generation = 0

    init(enabled: Bool = true) { self.enabled = enabled }

    func show(title: String, lines: [String], hideAfter: Double? = nil) {
        guard enabled else { return }
        Task { @MainActor in
            self.showOnMain(title: title, lines: lines, hideAfter: hideAfter)
        }
    }

    func hide() {
        guard enabled else { return }
        Task { @MainActor in
            self.generation += 1
            self.panel?.orderOut(nil)
        }
    }

    @MainActor
    private func configureIfNeeded() -> (InstructionPanel, NSTextField, NSTextField) {
        if let panel, let titleField, let bodyField {
            return (panel, titleField, bodyField)
        }

        let screenFrame = NSScreen.main?.visibleFrame ?? NSRect(x: 0, y: 0, width: 900, height: 600)
        let width = min(760, max(360, screenFrame.width - 32))
        let height: CGFloat = 180
        let frame = NSRect(
            x: screenFrame.midX - (width / 2),
            y: screenFrame.maxY - height - 24,
            width: width,
            height: height
        )

        let panel = InstructionPanel(
            contentRect: frame,
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        let titleField = NSTextField(labelWithString: "")
        let bodyField = NSTextField(labelWithString: "")

        panel.isReleasedWhenClosed = false
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = true
        panel.ignoresMouseEvents = true
        panel.level = .screenSaver
        panel.collectionBehavior = [
            .canJoinAllSpaces,
            .fullScreenAuxiliary,
            .ignoresCycle,
            .stationary
        ]

        let contentView = NSView(frame: panel.contentView?.bounds ?? .zero)
        contentView.autoresizingMask = [.width, .height]
        contentView.wantsLayer = true
        contentView.layer?.backgroundColor = NSColor(calibratedWhite: 0.05, alpha: 0.92).cgColor
        contentView.layer?.cornerRadius = 8
        contentView.layer?.borderWidth = 1
        contentView.layer?.borderColor = NSColor(calibratedWhite: 1.0, alpha: 0.18).cgColor

        titleField.font = .systemFont(ofSize: 18, weight: .semibold)
        titleField.textColor = .white
        titleField.lineBreakMode = .byTruncatingTail

        bodyField.font = .systemFont(ofSize: 14, weight: .medium)
        bodyField.textColor = NSColor(calibratedWhite: 0.94, alpha: 1)
        bodyField.lineBreakMode = .byWordWrapping
        bodyField.maximumNumberOfLines = 0

        titleField.translatesAutoresizingMaskIntoConstraints = false
        bodyField.translatesAutoresizingMaskIntoConstraints = false
        contentView.addSubview(titleField)
        contentView.addSubview(bodyField)

        NSLayoutConstraint.activate([
            titleField.leadingAnchor.constraint(equalTo: contentView.leadingAnchor, constant: 20),
            titleField.trailingAnchor.constraint(equalTo: contentView.trailingAnchor, constant: -20),
            titleField.topAnchor.constraint(equalTo: contentView.topAnchor, constant: 16),

            bodyField.leadingAnchor.constraint(equalTo: contentView.leadingAnchor, constant: 20),
            bodyField.trailingAnchor.constraint(equalTo: contentView.trailingAnchor, constant: -20),
            bodyField.topAnchor.constraint(equalTo: titleField.bottomAnchor, constant: 10),
            bodyField.bottomAnchor.constraint(lessThanOrEqualTo: contentView.bottomAnchor, constant: -16)
        ])

        panel.contentView = contentView

        self.panel = panel
        self.titleField = titleField
        self.bodyField = bodyField
        return (panel, titleField, bodyField)
    }

    @MainActor
    private func showOnMain(title: String, lines: [String], hideAfter: Double?) {
        let (panel, titleField, bodyField) = configureIfNeeded()
        generation += 1
        let currentGeneration = generation

        titleField.stringValue = title
        bodyField.stringValue = lines.map { "- \($0)" }.joined(separator: "\n")

        let screenFrame = NSScreen.main?.visibleFrame ?? NSRect(x: 0, y: 0, width: 900, height: 600)
        let width = min(760, max(360, screenFrame.width - 32))
        let estimatedHeight = CGFloat(92 + (lines.count * 30))
        let height = min(max(150, estimatedHeight), max(150, screenFrame.height - 48))
        bodyField.preferredMaxLayoutWidth = width - 40

        panel.setFrame(
            NSRect(
                x: screenFrame.midX - (width / 2),
                y: screenFrame.maxY - height - 24,
                width: width,
                height: height
            ),
            display: true
        )
        panel.orderFrontRegardless()

        guard let hideAfter else {
            return
        }

        DispatchQueue.main.asyncAfter(deadline: .now() + hideAfter) {
            guard self.generation == currentGeneration else {
                return
            }
            self.panel?.orderOut(nil)
        }
    }
}

private enum BridgeMode: String {
    case off = "OFF"
    case mouse = "MOUSE"
    case keyboard = "KEYBOARD"
    case bridge = "BRIDGE"

    var capturesMouse: Bool {
        self == .mouse || self == .bridge
    }

    var capturesKeyboard: Bool {
        self == .keyboard || self == .bridge
    }
}

struct DisplayBounds {
    let minX: CGFloat
    let maxX: CGFloat
    let minY: CGFloat
    let maxY: CGFloat

    var width: CGFloat {
        max(0, maxX - minX)
    }

    var height: CGFloat {
        max(0, maxY - minY)
    }

    func clamped(_ point: CGPoint) -> CGPoint {
        CGPoint(
            x: min(max(point.x, minX), maxX),
            y: min(max(point.y, minY), maxY)
        )
    }

    func edgeName(for point: CGPoint) -> String? {
        if point.x < minX {
            return "LEFT"
        }
        if point.x > maxX {
            return "RIGHT"
        }
        if point.y < minY {
            return "UP"
        }
        if point.y > maxY {
            return "DOWN"
        }
        return nil
    }

    func isSafelyInside(_ point: CGPoint) -> Bool {
        let margin: CGFloat = 24
        return point.x > minX + margin
            && point.x < maxX - margin
            && point.y > minY + margin
            && point.y < maxY - margin
    }

    var description: String {
        String(
            format: "minX=%.1f minY=%.1f maxX=%.1f maxY=%.1f width=%.1f height=%.1f",
            Double(minX),
            Double(minY),
            Double(maxX),
            Double(maxY),
            Double(width),
            Double(height)
        )
    }
}

private struct StagedPhase {
    let number: Int
    let title: String
    let mode: BridgeMode
    let startDelay: Double
    let duration: Double
    let prepLines: [String]
}

final class BatCaveMacOSAgent: @unchecked Sendable {
    private let lock = NSRecursiveLock()
    private let hud: InstructionHUD
    private let lan: LanBridgePeer
    private let eventPoster: (CGEvent) -> Void
    private var bounds = currentDisplayBounds()
    private let inputSource: MacInputSource
    private var mode: BridgeMode = .off
    private var runToken: UInt64 = 0
    private var virtualCursor = currentPointerLocation()
    private var edgeArmed = true
    private var mouseEvents: UInt64 = 0
    private var keyEvents: UInt64 = 0
    private var injectedPassed: UInt64 = 0
    private var eventTap: CFMachPort?
    private var remoteControlActive = false
    private var remoteControlSourceDeviceId: String?
    private var remoteControlToken: UInt64 = 0
    private var remoteControlSafetyTimeoutSeconds: Double?
    private var remoteButtonsDown = Set<UInt32>()
    private var remoteKeysDown = Set<CGKeyCode>()
    private var unicodeVirtualKeys = Set<Int>()
    private var clipboardTimer: Timer?
    private var lastPasteboardChangeCount = NSPasteboard.general.changeCount
    private var lastClipboardText: String?

    init(lan: LanBridgePeer, showHUD: Bool = true, eventPoster: @escaping (CGEvent) -> Void = { $0.post(tap: sessionTap) }) {
        self.lan = lan
        self.hud = InstructionHUD(enabled: showHUD)
        self.eventPoster = eventPoster
        self.inputSource = MacInputSource(send: { [weak lan] type, payload, session, activation in
            lan?.publishSource(type, payload: payload, session: session, activation: activation)
        }, post: eventPoster)
        lan.remoteInputReceiver = self
        lan.updateLocalBounds(bounds)
    }

    @discardableResult
    func startSourceSharing(session: LanAuthenticatedSession, peerOnRight: Bool = true) -> Bool {
        guard eventTap != nil, session.role == .source else { return false }
        return lan.withSourceActivation(session: session) { activation in
            lock.withLock {
                stopRemoteControlLocked(reason: "Using this Mac's keyboard and mouse", notify: false)
                mode = .off
                inputSource.localDeviceId = lan.localDeviceId
                inputSource.start(session: session, activation: activation, peerOnRight: peerOnRight,
                    localBounds: bounds, pointer: currentPointerLocation(), initialFlags: CGEventSource.flagsState(.combinedSessionState))
            }
        }
    }

    var isSourceSharingActive: Bool { inputSource.isActive }
    var localDisplayBounds: DisplayBounds { lock.withLock { bounds } }

    func install(exitOnFailure: Bool = true) -> Bool {
        if eventTap != nil { return true }
        bounds = currentDisplayBounds()
        lan.updateLocalBounds(bounds)
        let eventTypes: [CGEventType] = [
            .mouseMoved, .leftMouseDragged, .rightMouseDragged, .otherMouseDragged,
            .leftMouseDown, .leftMouseUp, .rightMouseDown, .rightMouseUp,
            .otherMouseDown, .otherMouseUp, .scrollWheel, .keyDown, .keyUp, .flagsChanged
        ]
        let eventMask = eventTypes.reduce(CGEventMask(0)) { mask, type in
            mask | (CGEventMask(1) << type.rawValue)
        }

        let pointer = Unmanaged.passUnretained(self).toOpaque()
        guard let tap = CGEvent.tapCreate(
            tap: sessionTap,
            place: .headInsertEventTap,
            options: .defaultTap,
            eventsOfInterest: eventMask,
            callback: { _, type, event, userInfo in
                guard let userInfo else {
                    return Unmanaged.passUnretained(event)
                }

                let agent = Unmanaged<BatCaveMacOSAgent>.fromOpaque(userInfo).takeUnretainedValue()
                return agent.handle(type: type, event: event)
            },
            userInfo: pointer
        ) else {
            emitError("failed to create active session event tap")
            emitError("grant Input Monitoring and Accessibility permissions, then restart BatCave")
            emitError("try again with: swift run BatCaveMacOSAgent --request-permission")
            hud.show(
                title: "BATCAVE PERMISSION NEEDED",
                lines: [
                    "Grant Input Monitoring and Accessibility permissions to BatCave.",
                    "Then quit and reopen BatCave."
                ],
                hideAfter: nil
            )
            if exitOnFailure {
                exit(1)
            }
            return false
        }

        eventTap = tap
        let runLoopSource = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
        CFRunLoopAddSource(CFRunLoopGetCurrent(), runLoopSource, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
        return true
    }

    func startClipboardSync() {
        guard clipboardTimer == nil else {
            return
        }

        let pasteboard = NSPasteboard.general
        lastPasteboardChangeCount = pasteboard.changeCount
        lastClipboardText = pasteboard.string(forType: .string)
        clipboardTimer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
            self?.pollClipboard()
        }
        emit("Clipboard sync: text polling active.")
    }

    func showAppModeStatus(host: String?, port: UInt16, inputReady: Bool = true) {
        let connectionLine: String
        if !inputReady {
            connectionLine = "Permissions needed before connecting."
        } else if host != nil {
            connectionLine = lan.statusDescription
        } else {
            connectionLine = "No Windows host configured yet."
        }

        let secondLine: String
        if inputReady {
            secondLine = "Clipboard sync is active while a LAN peer is connected."
        } else {
            secondLine = "Grant Input Monitoring and Accessibility, then reopen BatCave."
        }

        hud.show(
            title: "BATCAVE RUNNING",
            lines: [
                connectionLine,
                secondLine,
                "Escape or F12 stops active remote-control mode."
            ],
            hideAfter: 6
        )
    }

    func startMouseTest(duration: Double) {
        start(mode: .mouse, duration: duration, reason: "mouse suppression + relay test")
    }

    func startKeyboardTest(duration: Double) {
        start(mode: .keyboard, duration: duration, reason: "keyboard suppression + relay test")
    }

    func startBridge(duration: Double) {
        start(mode: .bridge, duration: duration, reason: "combined mouse + keyboard bridge test")
    }

    func startStagedTest() {
        lock.lock()
        runToken += 1
        let token = runToken
        lock.unlock()

        let phases = [
            StagedPhase(
                number: 1,
                title: "Mouse suppression + relay",
                mode: .mouse,
                startDelay: 5,
                duration: 8,
                prepLines: [
                    "Mouse stage starts in 5 seconds.",
                    "Move the mouse in circles, then push into screen edges.",
                    "Expected: pointer follows BatCave relay, MOUSE logs appear, and edge pushes print WOULD_HANDOFF_*."
                ]
            ),
            StagedPhase(
                number: 2,
                title: "Keyboard suppression + relay",
                mode: .keyboard,
                startDelay: 18,
                duration: 8,
                prepLines: [
                    "Keyboard stage starts in 5 seconds.",
                    "Focus a text field now if you want to see replayed typing.",
                    "During the stage, type normal letters.",
                    "Expected: HOOKKEY logs appear and text reaches the focused field through BatCave replay."
                ]
            ),
            StagedPhase(
                number: 3,
                title: "Combined mouse + keyboard bridge",
                mode: .bridge,
                startDelay: 31,
                duration: 15,
                prepLines: [
                    "Combined bridge starts in 5 seconds.",
                    "Keep a text field focused if testing keyboard replay.",
                    "During the stage, move the mouse, push edges, and type letters.",
                    "Expected: mouse and keyboard both relay through BatCave; edge pushes print WOULD_HANDOFF_*."
                ]
            )
        ]

        emit()
        emit("STAGED TEST queued. Timeline:")
        for phase in phases {
            emit("  T+\(Int(phase.startDelay))s: Stage \(phase.number), \(phase.title), runs \(Int(phase.duration))s")
        }
        emit("Escape or F12 stops the active stage.")
        emit()

        for phase in phases {
            schedulePhasePreparation(token: token, phase: phase)
            DispatchQueue.main.asyncAfter(deadline: .now() + phase.startDelay) {
                self.startIfToken(
                    token,
                    mode: phase.mode,
                    duration: phase.duration,
                    reason: "stage \(phase.number): \(phase.title)"
                )
            }
            DispatchQueue.main.asyncAfter(deadline: .now() + phase.startDelay + phase.duration) {
                self.stopIfToken(token, reason: "stage \(phase.number) complete")
            }
        }

        let totalDuration = phases.map { $0.startDelay + $0.duration }.max() ?? 0
        DispatchQueue.main.asyncAfter(deadline: .now() + totalDuration + 0.5) {
            self.stopIfToken(token, reason: "staged test complete")
        }
    }

    func stop(reason: String) {
        inputSource.pause(sendStop: false)
        lock.lock()
        runToken += 1
        remoteControlSafetyTimeoutSeconds = nil
        stopLocked(reason: reason)
        stopRemoteControlLocked(reason: reason)
        lock.unlock()
        if reason == "safety key" || reason == "event tap disabled" {
            lan.disconnect(reason: reason)
        }
    }

    var isRemoteControlActive: Bool {
        lock.lock()
        defer { lock.unlock() }
        return remoteControlActive
    }

    func resetVirtualCursor() {
        lock.lock()
        virtualCursor = currentPointerLocation()
        edgeArmed = true
        let cursor = virtualCursor
        lock.unlock()
        lan.updateLocalBounds(bounds)

        emit(String(format: "Virtual cursor reset to x=%.1f y=%.1f", Double(cursor.x), Double(cursor.y)))
    }

    private func pollClipboard() {
        guard lan.clipboardSession() != nil else { return }
        let pasteboard = NSPasteboard.general
        let changeCount = pasteboard.changeCount

        lock.lock()
        guard changeCount != lastPasteboardChangeCount else {
            lock.unlock()
            return
        }
        lastPasteboardChangeCount = changeCount
        lock.unlock()

        guard let text = pasteboard.string(forType: .string), !text.isEmpty else {
            return
        }

        lock.lock()
        guard text != lastClipboardText else {
            lock.unlock()
            return
        }
        lastClipboardText = text
        lock.unlock()

        lan.publishClipboardText(text)
        emit("CLIPBOARD TX text length=\(text.count)")
    }

    private func applyRemoteClipboardText(_ message: ClipboardTextMessage, sourceDeviceId: String) {
        guard message.text.utf8.count <= 64 * 1024 else { return }
        guard message.format.caseInsensitiveCompare("text/plain") == .orderedSame else {
            emit("CLIPBOARD RX ignored from \(sourceDeviceId): unsupported format=\(message.format)")
            return
        }

        let text = message.text
        let pasteboard = NSPasteboard.general
        if pasteboard.string(forType: .string) == text {
            lock.lock()
            lastPasteboardChangeCount = pasteboard.changeCount
            lastClipboardText = text
            lock.unlock()
            emit("CLIPBOARD RX already current from \(sourceDeviceId) length=\(text.count)")
            return
        }

        pasteboard.clearContents()
        guard pasteboard.setString(text, forType: .string) else {
            emit("CLIPBOARD RX failed to apply from \(sourceDeviceId) length=\(text.count)")
            return
        }

        lock.lock()
        lastPasteboardChangeCount = pasteboard.changeCount
        lastClipboardText = text
        lock.unlock()
        emit("CLIPBOARD RX applied from \(sourceDeviceId) length=\(text.count)")
    }

    func printState(reason: String = "command") {
        lock.lock()
        let currentMode = mode
        let cursor = virtualCursor
        let real = currentPointerLocation()
        let mouseCount = mouseEvents
        let keyCount = keyEvents
        let injectedCount = injectedPassed
        let remoteActive = remoteControlActive
        let remoteSource = remoteControlSourceDeviceId ?? "none"
        lock.unlock()

        emit(
            String(
                format: "STATE %@: mode=%@ remote=%@ source=%@ real=(%.1f,%.1f) virtual=(%.1f,%.1f) mouseEvents=%llu keyEvents=%llu injectedPassed=%llu bounds=%@",
                reason,
                currentMode.rawValue,
                String(remoteActive),
                remoteSource,
                Double(real.x),
                Double(real.y),
                Double(cursor.x),
                Double(cursor.y),
                mouseCount,
                keyCount,
                injectedCount,
                bounds.description
            )
        )
    }

    func handle(type: CGEventType, event: CGEvent) -> Unmanaged<CGEvent>? {
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            emitError("event tap disabled: \(type.rawValue)")
            stop(reason: "event tap disabled")
            if let eventTap {
                CGEvent.tapEnable(tap: eventTap, enable: true)
            }
            return Unmanaged.passUnretained(event)
        }

        let injected = event.getIntegerValueField(.eventSourceUserData) == batCaveEventMarker

        lock.lock()
        let currentMode = mode
        let remoteActive = remoteControlActive
        lock.unlock()

        if injected {
            lock.lock()
            injectedPassed += 1
            lock.unlock()
            return Unmanaged.passUnretained(event)
        }

        if inputSource.isActive, type == .keyDown,
           event.getIntegerValueField(.keyboardEventKeycode) == f12KeyCode {
            stop(reason: "safety key")
            return nil
        }
        if inputSource.handle(type: type, event: event) { return nil }

        if remoteActive, isKeyboardEvent(type), isStopKey(event: event, type: type) {
            stop(reason: "safety key")
            return nil
        }

        if currentMode == .off {
            return Unmanaged.passUnretained(event)
        }

        if isKeyboardEvent(type), isStopKey(event: event, type: type) {
            stop(reason: "safety key")
            return nil
        }

        if currentMode.capturesMouse, isMouseEvent(type) {
            handlePhysicalMouse(type: type, event: event)
            return nil
        }

        if currentMode.capturesKeyboard, isKeyboardEvent(type) {
            handlePhysicalKeyboard(type: type, event: event)
            return nil
        }

        return Unmanaged.passUnretained(event)
    }

    private func start(mode newMode: BridgeMode, duration: Double, reason: String) {
        lock.lock()
        runToken += 1
        let token = runToken
        startLocked(mode: newMode, duration: duration, reason: reason)
        lock.unlock()

        DispatchQueue.main.asyncAfter(deadline: .now() + duration) {
            self.stopIfToken(token, reason: "timer expired")
        }
        scheduleModeReminders(token: token, mode: newMode, duration: duration)
    }

    private func startIfToken(_ token: UInt64, mode newMode: BridgeMode, duration: Double, reason: String) {
        lock.lock()
        guard token == runToken else {
            lock.unlock()
            return
        }
        startLocked(mode: newMode, duration: duration, reason: reason)
        lock.unlock()
        scheduleModeReminders(token: token, mode: newMode, duration: duration)
    }

    private func stopIfToken(_ token: UInt64, reason: String) {
        lock.lock()
        guard token == runToken else {
            lock.unlock()
            return
        }
        stopLocked(reason: reason)
        lock.unlock()
    }

    private func startLocked(mode newMode: BridgeMode, duration: Double, reason: String) {
        virtualCursor = currentPointerLocation()
        edgeArmed = true
        mode = newMode
        lan.updateLocalBounds(bounds)

        emit()
        emit("\(newMode.rawValue) ON for \(String(format: "%.1f", duration)) seconds (\(reason)).")
        emit("Physical input for this mode is suppressed locally and replayed as tagged BatCave events.")
        emit("Escape or F12 stops the active mode. Auto-stop is enabled.")
        emit(String(format: "Virtual cursor starts at x=%.1f y=%.1f", Double(virtualCursor.x), Double(virtualCursor.y)))
        emit()
        emitModeInstructions(mode: newMode, remaining: duration)
    }

    private func stopLocked(reason: String) {
        guard mode != .off else {
            return
        }

        let previousMode = mode
        emit()
        emit("\(previousMode.rawValue) OFF (\(reason)).")
        hud.show(
            title: "\(previousMode.rawValue) OFF",
            lines: [
                reason,
                "Stand by for the next phase or command."
            ],
            hideAfter: 2
        )
        mode = .off
        edgeArmed = true
        emit()
    }

    private func handlePhysicalMouse(type: CGEventType, event: CGEvent) {
        let source = makeEventSource()
        let dx = event.getIntegerValueField(.mouseEventDeltaX)
        let dy = event.getIntegerValueField(.mouseEventDeltaY)

        lock.lock()
        mouseEvents += 1

        if isMouseMovementEvent(type), dx != 0 || dy != 0 {
            let desired = CGPoint(x: virtualCursor.x + CGFloat(dx), y: virtualCursor.y + CGFloat(dy))
            reportEdgeCrossingIfNeeded(desired: desired, dx: dx, dy: dy)
            virtualCursor = bounds.clamped(desired)
            let cursor = virtualCursor
            lock.unlock()

            postMouse(type: type, at: cursor, button: button(for: type, event: event), source: source)
            maybePrintMouseStatus(dx: dx, dy: dy, cursor: cursor)
            return
        }

        let cursor = virtualCursor
        lock.unlock()

        if type == .scrollWheel {
            postScroll(from: event, source: source)
        } else {
            postMouse(type: type, at: cursor, button: button(for: type, event: event), source: source)
        }
    }

    private func handlePhysicalKeyboard(type: CGEventType, event: CGEvent) {
        lock.lock()
        keyEvents += 1
        let keyCount = keyEvents
        lock.unlock()

        let keyCode = event.getIntegerValueField(.keyboardEventKeycode)
        let action = keyboardActionName(for: type)
        emit(String(format: "HOOKKEY %@ keyCode=%4lld keyEvents=%llu", action, keyCode, keyCount))

        guard let replay = event.copy() else {
            emitError("failed to copy keyboard event keyCode=\(keyCode)")
            return
        }

        mark(replay)
        replay.post(tap: sessionTap)
    }

    private func reportEdgeCrossingIfNeeded(desired: CGPoint, dx: Int64, dy: Int64) {
        if bounds.isSafelyInside(desired) {
            edgeArmed = true
            return
        }

        guard edgeArmed, let edge = bounds.edgeName(for: desired) else {
            return
        }

        edgeArmed = false
        emit()
        emit(String(format: "WOULD_HANDOFF_%@ virtual=(%.1f,%.1f) bounds=%@", edge, Double(desired.x), Double(desired.y), bounds.description))
        emit()
        lan.publishEdgeHandoff(
            edge: edge,
            virtualX: Int(desired.x.rounded()),
            virtualY: Int(desired.y.rounded()),
            deltaX: Int(dx),
            deltaY: Int(dy),
            bounds: bounds
        )
    }

    private func maybePrintMouseStatus(dx: Int64, dy: Int64, cursor: CGPoint) {
        guard mouseEvents % 20 == 0 || abs(dx) > 80 || abs(dy) > 80 else {
            return
        }

        emit(String(format: "MOUSE dx=%5lld dy=%5lld virtual=(%.1f,%.1f) mouseEvents=%llu", dx, dy, Double(cursor.x), Double(cursor.y), mouseEvents))
    }

    private func schedulePhasePreparation(token: UInt64, phase: StagedPhase) {
        let prepDelay = max(0, phase.startDelay - 5)
        DispatchQueue.main.asyncAfter(deadline: .now() + prepDelay) {
            guard self.isActiveToken(token) else {
                return
            }

            self.emitInstructionBanner(
                title: "PREP Stage \(phase.number): \(phase.title)",
                lines: phase.prepLines
            )
        }

        for secondsBeforeStart in [3, 1] {
            let delay = max(0, phase.startDelay - Double(secondsBeforeStart))
            DispatchQueue.main.asyncAfter(deadline: .now() + delay) {
                guard self.isActiveToken(token) else {
                    return
                }

                self.emitInstructionBanner(
                    title: "COUNTDOWN Stage \(phase.number)",
                    lines: [
                        "\(phase.title) starts in \(secondsBeforeStart) second\(secondsBeforeStart == 1 ? "" : "s").",
                        "Next action: \(self.shortInstruction(for: phase.mode))"
                    ]
                )
            }
        }
    }

    private func scheduleModeReminders(token: UInt64, mode expectedMode: BridgeMode, duration: Double) {
        let interval = duration <= 10 ? 3.0 : 5.0
        var offset = interval

        while offset < duration {
            let remaining = max(0, duration - offset)
            DispatchQueue.main.asyncAfter(deadline: .now() + offset) {
                guard self.isActiveToken(token, mode: expectedMode) else {
                    return
                }

                self.emitModeInstructions(mode: expectedMode, remaining: remaining)
            }
            offset += interval
        }
    }

    private func isActiveToken(_ token: UInt64, mode expectedMode: BridgeMode? = nil) -> Bool {
        lock.lock()
        defer {
            lock.unlock()
        }

        guard token == runToken else {
            return false
        }

        if let expectedMode {
            return mode == expectedMode
        }

        return true
    }

    private func emitModeInstructions(mode: BridgeMode, remaining: Double) {
        var lines = instructions(for: mode)
        lines.insert("About \(Int(ceil(remaining))) seconds remain in this phase.", at: 0)
        emitInstructionBanner(title: "DO THIS NOW: \(mode.rawValue)", lines: lines)
    }

    private func emitInstructionBanner(title: String, lines: [String]) {
        emit()
        emit("=== \(title) ===")
        for line in lines {
            emit("- \(line)")
        }
        emit("==============================")
        emit()
        hud.show(title: title, lines: lines)
    }

    private func shortInstruction(for mode: BridgeMode) -> String {
        switch mode {
        case .mouse:
            return "move the mouse, then push into edges"
        case .keyboard:
            return "focus a text field and type letters"
        case .bridge:
            return "move the mouse and type letters"
        case .off:
            return "stand by"
        }
    }

    private func instructions(for mode: BridgeMode) -> [String] {
        switch mode {
        case .mouse:
            return [
                "Move the physical mouse now.",
                "Push into each screen edge if possible.",
                "Expected: the pointer moves by BatCave replay, MOUSE logs appear, and edge pushes log WOULD_HANDOFF_*.",
                "Do not judge the test from quiet output unless you are actively moving the mouse."
            ]
        case .keyboard:
            return [
                "Type normal letters now.",
                "Use a focused text field if you want visible replay.",
                "Expected: HOOKKEY logs appear and characters reach the focused field through BatCave replay.",
                "Escape or F12 stops the active phase."
            ]
        case .bridge:
            return [
                "Move the mouse and type letters now.",
                "Push screen edges to look for WOULD_HANDOFF_* logs.",
                "Use a focused text field if you want to verify keyboard replay.",
                "Expected: mouse and keyboard are both suppressed locally and replayed as tagged BatCave events."
            ]
        case .off:
            return [
                "No active test phase."
            ]
        }
    }
}

extension BatCaveMacOSAgent: LanBridgeRemoteInputReceiver {
    func lanPeer(_ peer: LanBridgePeer, didDisconnect reason: String) {
        stop(reason: "LAN disconnected: \(reason)")
    }

    func lanPeer(_ peer: LanBridgePeer, didReceiveTestStart message: RemoteTestStartMessage, sourceDeviceId: String) {
        lock.lock()
        remoteControlSafetyTimeoutSeconds = Double(max(message.durationSeconds, 1)) + 5
        lock.unlock()

        emit()
        emit("REMOTE HANDOFF TEST armed by \(sourceDeviceId) for \(message.durationSeconds)s.")
        emit("Move the Windows cursor through the configured bridge; macOS will take remote input after cursor.handoff.")
        emit()
        hud.show(
            title: "REMOTE HANDOFF READY",
            lines: [
                "Windows test is running for \(message.durationSeconds) seconds.",
                "When the cursor crosses the bridge, macOS will receive mouse and keyboard input.",
                "Escape or F12 stops the test."
            ],
            hideAfter: 5
        )
    }

    func lanPeer(_ peer: LanBridgePeer, didReceiveTestStop message: RemoteTestStopMessage, sourceDeviceId: String) {
        lock.lock()
        defer { lock.unlock() }
        guard remoteControlSourceDeviceId == nil || remoteControlSourceDeviceId.map({ lanExactIdentity($0, sourceDeviceId) }) == true else { return }
        inputSource.pause(sendStop: false)
        remoteControlSafetyTimeoutSeconds = nil
        stopRemoteControlLocked(reason: "remote stop from \(sourceDeviceId): \(message.reason)")
    }

    func lanPeer(_ peer: LanBridgePeer, didReceiveCursorHandoff message: CursorHandoffMessage, sourceDeviceId: String) {
        if inputSource.isActive {
            guard lanExactIdentity(message.targetDeviceId, lan.localDeviceId) else { return }
            inputSource.receivedHandoff(message)
            return
        }
        guard peer.secureIdentity == nil || (peer.inputRole == .receiver && peer.isSecureInputEnabled) else { return }
        lock.lock()
        defer { lock.unlock() }
        guard lanExactIdentity(message.targetDeviceId, lan.localDeviceId) else {
            if remoteControlSourceDeviceId.map({ lanExactIdentity($0, sourceDeviceId) }) == true {
                stopRemoteControlLocked(reason: "cursor handoff to \(message.targetDeviceId)", notify: false)
            }
            emit("REMOTE HANDOFF ignored: targetDeviceId=\(message.targetDeviceId) localDeviceId=\(lan.localDeviceId)")
            return
        }

        let target = pointForRemoteHandoff(message)
        releaseRemoteInputLocked()
        remoteControlActive = true
        remoteControlSourceDeviceId = sourceDeviceId
        remoteControlToken += 1
        let token = remoteControlToken
        let safetyTimeoutSeconds = remoteControlSafetyTimeoutSeconds
        virtualCursor = bounds.clamped(target)
        let cursor = virtualCursor
        mode = .off
        postMouse(type: .mouseMoved, at: cursor, button: .left, source: makeEventSource(), post: eventPoster)
        emit(String(format: "REMOTE CONTROL ON from %@ at x=%.1f y=%.1f targetEdge=%@ targetPosition=%.3f", sourceDeviceId, Double(cursor.x), Double(cursor.y), message.targetEdge, message.targetPosition))

        guard let safetyTimeoutSeconds else {
            return
        }

        hud.show(
            title: "REMOTE CONTROL ON",
            lines: [
                "Windows now controls this Mac.",
                "Move and type from the Windows keyboard/mouse.",
                "Escape or F12 stops the test."
            ],
            hideAfter: 4
        )

        DispatchQueue.main.asyncAfter(deadline: .now() + safetyTimeoutSeconds) {
            self.lock.lock()
            guard self.remoteControlToken == token else {
                self.lock.unlock()
                return
            }
            self.stopRemoteControlLocked(reason: "remote safety timeout")
            self.lock.unlock()
        }
    }

    func lanPeer(_ peer: LanBridgePeer, didReceiveMouseDelta message: MouseDeltaMessage, sourceDeviceId: String) {
        lock.lock()
        defer { lock.unlock() }
        guard remoteControlActive, remoteControlSourceDeviceId.map({ lanExactIdentity($0, sourceDeviceId) }) == true else { return }

        mouseEvents += 1
        let desired = CGPoint(
            x: virtualCursor.x + CGFloat(message.deltaX),
            y: virtualCursor.y + CGFloat(message.deltaY)
        )
        virtualCursor = bounds.clamped(desired)
        let cursor = virtualCursor
        let count = mouseEvents
        let dragButton = remoteDragButtonLocked()

        postMouse(
            type: remoteMouseMoveEventType(dragButton: dragButton),
            at: cursor,
            button: dragButton ?? .left,
            source: makeEventSource(),
            post: eventPoster
        )
        if count % 20 == 0 || abs(message.deltaX) > 80 || abs(message.deltaY) > 80 {
            emit(String(format: "REMOTE MOUSE dx=%5d dy=%5d cursor=(%.1f,%.1f) events=%llu", message.deltaX, message.deltaY, Double(cursor.x), Double(cursor.y), count))
        }
    }

    func lanPeer(_ peer: LanBridgePeer, didReceiveMouseButton message: MouseButtonMessage, sourceDeviceId: String) {
        lock.lock()
        defer { lock.unlock() }
        guard remoteControlActive, remoteControlSourceDeviceId.map({ lanExactIdentity($0, sourceDeviceId) }) == true else { return }

        let button = remoteMouseButton(message.button)
        if message.isDown {
            remoteButtonsDown.insert(button.rawValue)
        } else {
            remoteButtonsDown.remove(button.rawValue)
        }
        let cursor = virtualCursor

        let type = remoteMouseEventType(button: message.button, isDown: message.isDown)
        postMouse(type: type, at: cursor, button: button, source: makeEventSource(), post: eventPoster)
    }

    func lanPeer(_ peer: LanBridgePeer, didReceiveMouseWheel message: MouseWheelMessage, sourceDeviceId: String) {
        lock.lock()
        defer { lock.unlock() }
        guard remoteControlActive, remoteControlSourceDeviceId.map({ lanExactIdentity($0, sourceDeviceId) }) == true else { return }

        postRemoteScroll(verticalDelta: message.verticalDelta, horizontalDelta: message.horizontalDelta, source: makeEventSource(), post: eventPoster)
    }

    func lanPeer(_ peer: LanBridgePeer, didReceiveKeyInput message: KeyInputMessage, sourceDeviceId: String) {
        lock.lock()
        defer { lock.unlock() }
        guard remoteControlActive, remoteControlSourceDeviceId.map({ lanExactIdentity($0, sourceDeviceId) }) == true else { return }

        keyEvents += 1
        let count = keyEvents

        if !message.isKeyUp, let text = message.text, !text.isEmpty {
            unicodeVirtualKeys.insert(message.virtualKey)
            postUnicodeText(text, source: makeEventSource(), post: eventPoster)
            emit("REMOTE KEY textLength=\(text.count) keyEvents=\(count)")
            return
        }
        // Unicode text is emitted as a complete down/up pair, so its physical
        // key-up must not become an unrelated native key-up.
        if message.isKeyUp, unicodeVirtualKeys.remove(message.virtualKey) != nil { return }

        guard let keyCode = macKeyCode(forWindowsVirtualKey: message.virtualKey) else {
            return
        }

        if message.isKeyUp { remoteKeysDown.remove(keyCode) } else { remoteKeysDown.insert(keyCode) }
        postKeyboard(keyCode: keyCode, keyDown: !message.isKeyUp, source: makeEventSource(), flags: remoteModifierFlagsLocked(), post: eventPoster)
    }

    func lanPeer(_ peer: LanBridgePeer, didReceiveShortcutInput message: ShortcutInputMessage, sourceDeviceId: String) {
        lock.lock()
        defer { lock.unlock() }
        guard remoteControlActive, remoteControlSourceDeviceId.map({ lanExactIdentity($0, sourceDeviceId) }) == true else { return }

        keyEvents += 1
        let count = keyEvents

        guard let keyCode = macShortcutKeyCode(forAction: message.action) else {
            emit("REMOTE SHORTCUT unsupported action=\(message.action) keyEvents=\(count)")
            return
        }

        postCommandShortcut(keyCode: keyCode, source: makeEventSource(), post: eventPoster)
        emit("REMOTE SHORTCUT action=\(message.action) keyEvents=\(count)")
    }

    func lanPeer(_ peer: LanBridgePeer, didReceiveClipboardText message: ClipboardTextMessage, sourceDeviceId: String) {
        let session = peer.clipboardSessionOnReceiverQueue()
        DispatchQueue.main.async {
            guard let session, peer.clipboardSession() == session else { return }
            self.applyRemoteClipboardText(message, sourceDeviceId: sourceDeviceId)
        }
    }

    private func stopRemoteControlLocked(reason: String, notify: Bool = true) {
        releaseRemoteInputLocked()
        guard remoteControlActive else {
            return
        }

        remoteControlToken += 1
        remoteControlActive = false
        remoteControlSourceDeviceId = nil
        remoteControlSafetyTimeoutSeconds = nil
        emit()
        emit("REMOTE CONTROL OFF (\(reason)).")
        emit()
        guard notify else {
            hud.hide()
            return
        }
        hud.show(
            title: "REMOTE CONTROL OFF",
            lines: [reason],
            hideAfter: 3
        )
    }

    private func releaseRemoteInputLocked() {
        guard !remoteButtonsDown.isEmpty || !remoteKeysDown.isEmpty || !unicodeVirtualKeys.isEmpty else { return }
        let source = makeEventSource()
        for raw in remoteButtonsDown.sorted() {
            let button = CGMouseButton(rawValue: raw) ?? .center
            let type: CGEventType = button == .left ? .leftMouseUp : (button == .right ? .rightMouseUp : .otherMouseUp)
            postMouse(type: type, at: virtualCursor, button: button, source: source, post: eventPoster)
        }
        // Release non-modifiers first, then modifiers, to avoid altered key-ups.
        let modifiers: Set<CGKeyCode> = [54, 55, 56, 58, 59, 60, 61, 62]
        let keys = remoteKeysDown.sorted { a, b in
            if modifiers.contains(a) != modifiers.contains(b) { return !modifiers.contains(a) }
            return a < b
        }
        for key in keys {
            remoteKeysDown.remove(key)
            postKeyboard(keyCode: key, keyDown: false, source: source, flags: remoteModifierFlagsLocked(), post: eventPoster)
        }
        emit("REMOTE INPUT RELEASE buttons=\(remoteButtonsDown.count) keys=\(keys.count)")
        remoteButtonsDown.removeAll()
        remoteKeysDown.removeAll()
        unicodeVirtualKeys.removeAll()
    }

    private func remoteModifierFlagsLocked() -> CGEventFlags {
        var flags: CGEventFlags = []
        if !remoteKeysDown.isDisjoint(with: [56, 60]) { flags.insert(.maskShift) }
        if !remoteKeysDown.isDisjoint(with: [59, 62]) { flags.insert(.maskControl) }
        if !remoteKeysDown.isDisjoint(with: [58, 61]) { flags.insert(.maskAlternate) }
        if !remoteKeysDown.isDisjoint(with: [54, 55]) { flags.insert(.maskCommand) }
        return flags
    }

    private func pointForRemoteHandoff(_ message: CursorHandoffMessage) -> CGPoint {
        let position = min(max(CGFloat(message.targetPosition), 0), 1)
        let inset: CGFloat = 2
        switch message.targetEdge.lowercased() {
        case "left":
            return CGPoint(x: bounds.minX + inset, y: bounds.minY + (bounds.height * position))
        case "right":
            return CGPoint(x: bounds.maxX - inset, y: bounds.minY + (bounds.height * position))
        case "top":
            return CGPoint(x: bounds.minX + (bounds.width * position), y: bounds.minY + inset)
        case "bottom":
            return CGPoint(x: bounds.minX + (bounds.width * position), y: bounds.maxY - inset)
        default:
            return bounds.clamped(CGPoint(x: CGFloat(message.targetX), y: CGFloat(message.targetY)))
        }
    }

    private func remoteDragButtonLocked() -> CGMouseButton? {
        if remoteButtonsDown.contains(CGMouseButton.left.rawValue) {
            return .left
        }

        if remoteButtonsDown.contains(CGMouseButton.right.rawValue) {
            return .right
        }

        if let rawButton = remoteButtonsDown.first {
            return CGMouseButton(rawValue: rawButton) ?? .center
        }

        return nil
    }
}

private func currentPointerLocation() -> CGPoint {
    CGEvent(source: nil)?.location ?? .zero
}

private func currentDisplayBounds() -> DisplayBounds {
    var displayCount: UInt32 = 0
    let countResult = CGGetActiveDisplayList(0, nil, &displayCount)

    guard countResult == .success && displayCount > 0 else {
        emitError("warning: active display list unavailable; using main display bounds")
        return displayBounds(for: CGMainDisplayID())
    }

    var displays = [CGDirectDisplayID](repeating: 0, count: Int(displayCount))
    let listResult = CGGetActiveDisplayList(displayCount, &displays, &displayCount)

    guard listResult == .success else {
        emitError("warning: active display list failed; using main display bounds")
        return displayBounds(for: CGMainDisplayID())
    }

    let rects = displays.prefix(Int(displayCount)).map { CGDisplayBounds($0) }
    let union = rects.reduce(CGRect.null) { partial, rect in
        partial.union(rect)
    }

    guard !union.isNull && !union.isInfinite else {
        emitError("warning: active display bounds invalid; using main display bounds")
        return displayBounds(for: CGMainDisplayID())
    }

    return displayBounds(for: union)
}

private func displayBounds(for displayID: CGDirectDisplayID) -> DisplayBounds {
    displayBounds(for: CGDisplayBounds(displayID))
}

private func displayBounds(for rect: CGRect) -> DisplayBounds {
    DisplayBounds(minX: rect.minX, maxX: rect.maxX, minY: rect.minY, maxY: rect.maxY)
}

private func makeEventSource() -> CGEventSource {
    let source =
        CGEventSource(stateID: CGEventSourceStateID(rawValue: 1)!)
        ?? CGEventSource(stateID: CGEventSourceStateID(rawValue: -1)!)

    guard let source else {
        emitError("failed to create CGEventSource")
        exit(1)
    }

    source.userData = batCaveEventMarker
    source.localEventsSuppressionInterval = 0
    return source
}

private func mark(_ event: CGEvent) {
    event.setIntegerValueField(.eventSourceUserData, value: batCaveEventMarker)
}

private func postMouse(type: CGEventType, at point: CGPoint, button: CGMouseButton, source: CGEventSource, post: (CGEvent) -> Void = { $0.post(tap: sessionTap) }) {
    guard let event = CGEvent(
        mouseEventSource: source,
        mouseType: type,
        mouseCursorPosition: point,
        mouseButton: button
    ) else {
        emitError("failed to create mouse event \(type.rawValue)")
        return
    }

    mark(event)
    post(event)
}

private func postScroll(from sourceEvent: CGEvent, source: CGEventSource) {
    let axis1 = Int32(sourceEvent.getIntegerValueField(.scrollWheelEventDeltaAxis1))
    let axis2 = Int32(sourceEvent.getIntegerValueField(.scrollWheelEventDeltaAxis2))

    guard axis1 != 0 || axis2 != 0 else {
        return
    }

    guard let event = CGEvent(
        scrollWheelEvent2Source: source,
        units: .line,
        wheelCount: 2,
        wheel1: axis1,
        wheel2: axis2,
        wheel3: 0
    ) else {
        emitError("failed to create scroll event")
        return
    }

    mark(event)
    event.post(tap: sessionTap)
}

private func postRemoteScroll(verticalDelta: Int, horizontalDelta: Int, source: CGEventSource, post: (CGEvent) -> Void = { $0.post(tap: sessionTap) }) {
    let vertical = wheelClicks(verticalDelta)
    let horizontal = wheelClicks(horizontalDelta)
    guard vertical != 0 || horizontal != 0 else {
        return
    }

    guard let event = CGEvent(
        scrollWheelEvent2Source: source,
        units: .line,
        wheelCount: 2,
        wheel1: Int32(vertical),
        wheel2: Int32(horizontal),
        wheel3: 0
    ) else {
        emitError("failed to create remote scroll event")
        return
    }

    mark(event)
    post(event)
}

private func wheelClicks(_ delta: Int) -> Int {
    guard delta != 0 else {
        return 0
    }

    let clicks = delta / 120
    if clicks != 0 {
        return clicks
    }

    return delta > 0 ? 1 : -1
}

private func postUnicodeText(_ text: String, source: CGEventSource, post: (CGEvent) -> Void = { $0.post(tap: sessionTap) }) {
    for scalar in text.unicodeScalars {
        let utf16 = Array(String(scalar).utf16)
        guard !utf16.isEmpty else {
            continue
        }

        guard let down = CGEvent(keyboardEventSource: source, virtualKey: 0, keyDown: true),
              let up = CGEvent(keyboardEventSource: source, virtualKey: 0, keyDown: false) else {
            emitError("failed to create unicode key event")
            return
        }

        utf16.withUnsafeBufferPointer { buffer in
            down.keyboardSetUnicodeString(stringLength: utf16.count, unicodeString: buffer.baseAddress)
            up.keyboardSetUnicodeString(stringLength: utf16.count, unicodeString: buffer.baseAddress)
        }
        mark(down)
        mark(up)
        post(down)
        post(up)
    }
}

private func postKeyboard(keyCode: CGKeyCode, keyDown: Bool, source: CGEventSource, flags: CGEventFlags? = nil, post: (CGEvent) -> Void = { $0.post(tap: sessionTap) }) {
    guard let event = CGEvent(keyboardEventSource: source, virtualKey: keyCode, keyDown: keyDown) else {
        emitError("failed to create keyboard event keyCode=\(keyCode)")
        return
    }

    mark(event)
    if let flags { event.flags = flags }
    post(event)
}

private func postCommandShortcut(keyCode: CGKeyCode, source: CGEventSource, post: (CGEvent) -> Void = { $0.post(tap: sessionTap) }) {
    let commandFlags: CGEventFlags = .maskCommand
    guard let commandDown = CGEvent(keyboardEventSource: source, virtualKey: commandKeyCode, keyDown: true),
          let keyDown = CGEvent(keyboardEventSource: source, virtualKey: keyCode, keyDown: true),
          let keyUp = CGEvent(keyboardEventSource: source, virtualKey: keyCode, keyDown: false),
          let commandUp = CGEvent(keyboardEventSource: source, virtualKey: commandKeyCode, keyDown: false) else {
        emitError("failed to create command shortcut keyCode=\(keyCode)")
        return
    }

    commandDown.flags = commandFlags
    keyDown.flags = commandFlags
    keyUp.flags = commandFlags
    commandUp.flags = []

    for event in [commandDown, keyDown, keyUp, commandUp] {
        mark(event)
        post(event)
    }
}

private func remoteMouseButton(_ button: String) -> CGMouseButton {
    switch button.lowercased() {
    case "right":
        return .right
    case "middle", "center":
        return .center
    case "x1":
        return CGMouseButton(rawValue: 3) ?? .center
    case "x2":
        return CGMouseButton(rawValue: 4) ?? .center
    default:
        return .left
    }
}

private func remoteMouseEventType(button: String, isDown: Bool) -> CGEventType {
    switch button.lowercased() {
    case "right":
        return isDown ? .rightMouseDown : .rightMouseUp
    case "middle", "center", "x1", "x2":
        return isDown ? .otherMouseDown : .otherMouseUp
    default:
        return isDown ? .leftMouseDown : .leftMouseUp
    }
}

private func remoteMouseMoveEventType(dragButton: CGMouseButton?) -> CGEventType {
    guard let dragButton else {
        return .mouseMoved
    }

    if dragButton.rawValue == CGMouseButton.right.rawValue {
        return .rightMouseDragged
    }

    if dragButton.rawValue == CGMouseButton.left.rawValue {
        return .leftMouseDragged
    }

    return .otherMouseDragged
}

private func macShortcutKeyCode(forAction action: String) -> CGKeyCode? {
    switch action.lowercased() {
    case "selectall":
        return 0 // A
    case "copy":
        return 8 // C
    case "paste":
        return 9 // V
    case "cut":
        return 7 // X
    default:
        return nil
    }
}

private func macKeyCode(forWindowsVirtualKey virtualKey: Int) -> CGKeyCode? {
    switch virtualKey {
    case 0x08:
        return 51 // Delete/backspace
    case 0x09:
        return 48 // Tab
    case 0x0D:
        return 36 // Return
    case 0x10, 0xA0: return 56 // Shift / left Shift
    case 0xA1: return 60 // Right Shift
    case 0x11, 0xA2: return 59 // Control / left Control
    case 0xA3: return 62 // Right Control
    case 0x12, 0xA4: return 58 // Alt / left Option
    case 0xA5: return 61 // Right Option
    case 0x5B: return 55 // Left Windows / Command
    case 0x5C: return 54 // Right Windows / Command
    case 0x1B: return 53 // Escape
    case 0x20: return 49 // Space
    case 0x21: return 116 // Page Up
    case 0x22: return 121 // Page Down
    case 0x23: return 119 // End
    case 0x24: return 115 // Home
    case 0x2E: return 117 // Forward Delete
    case 0x70: return 122 // F1
    case 0x71: return 120 // F2
    case 0x72: return 99 // F3
    case 0x73: return 118 // F4
    case 0x74: return 96 // F5
    case 0x75: return 97 // F6
    case 0x76: return 98 // F7
    case 0x77: return 100 // F8
    case 0x78: return 101 // F9
    case 0x79: return 109 // F10
    case 0x7A: return 103 // F11
    case 0x7B: return 111 // F12
    case 0x25:
        return 123 // Left arrow
    case 0x26:
        return 126 // Up arrow
    case 0x27:
        return 124 // Right arrow
    case 0x28:
        return 125 // Down arrow
    default:
        return (CGKeyCode(0)...CGKeyCode(126)).first { MacInputSource.windowsVirtualKey($0) == virtualKey }
    }
}

private func escapeForLog(_ text: String) -> String {
    text
        .replacingOccurrences(of: "\r", with: "\\r")
        .replacingOccurrences(of: "\n", with: "\\n")
        .replacingOccurrences(of: "\t", with: "\\t")
}

private func isMouseEvent(_ type: CGEventType) -> Bool {
    switch type {
    case .mouseMoved,
         .leftMouseDragged,
         .rightMouseDragged,
         .otherMouseDragged,
         .leftMouseDown,
         .leftMouseUp,
         .rightMouseDown,
         .rightMouseUp,
         .otherMouseDown,
         .otherMouseUp,
         .scrollWheel:
        return true
    default:
        return false
    }
}

private func isMouseMovementEvent(_ type: CGEventType) -> Bool {
    switch type {
    case .mouseMoved, .leftMouseDragged, .rightMouseDragged, .otherMouseDragged:
        return true
    default:
        return false
    }
}

private func isKeyboardEvent(_ type: CGEventType) -> Bool {
    switch type {
    case .keyDown, .keyUp, .flagsChanged:
        return true
    default:
        return false
    }
}

private func isStopKey(event: CGEvent, type: CGEventType) -> Bool {
    guard type == .keyDown else {
        return false
    }

    let keyCode = event.getIntegerValueField(.keyboardEventKeycode)
    return keyCode == f12KeyCode || keyCode == escapeKeyCode
}

private func button(for type: CGEventType, event: CGEvent) -> CGMouseButton {
    switch type {
    case .rightMouseDown, .rightMouseUp, .rightMouseDragged:
        return .right
    case .otherMouseDown, .otherMouseUp, .otherMouseDragged:
        let rawButton = UInt32(event.getIntegerValueField(.mouseEventButtonNumber))
        return CGMouseButton(rawValue: rawButton) ?? .center
    default:
        return .left
    }
}

private func keyboardActionName(for type: CGEventType) -> String {
    switch type {
    case .keyDown:
        return "DOWN "
    case .keyUp:
        return "UP   "
    case .flagsChanged:
        return "FLAGS"
    default:
        return "KEY  "
    }
}

private func maybeRequestPermissions() {
    guard CommandLine.arguments.contains("--request-permission") else {
        return
    }

    if #available(macOS 10.15, *) {
        emit("requesting listen event access; approve the macOS prompt if it appears")
        let listenGranted = CGRequestListenEventAccess()
        emit("listen event permission requested: \(listenGranted ? "granted" : "not granted yet")")
    }

    let accessibilityOptions = [
        "AXTrustedCheckOptionPrompt": true
    ] as CFDictionary
    let accessibilityGranted = AXIsProcessTrustedWithOptions(accessibilityOptions)
    emit("accessibility permission requested: \(accessibilityGranted ? "granted" : "not granted yet")")
}

private func printPermissionStatus() {
    if #available(macOS 10.15, *) {
        let listenGranted = CGPreflightListenEventAccess()
        emit("listen event permission preflight: \(listenGranted ? "granted" : "not granted")")
    }

    emit("accessibility permission preflight: \(AXIsProcessTrusted() ? "granted" : "not granted")")
}

private func printHelp() {
    emit("BatCave macOS Agent")
    emit()
    emit("Local commands:")
    emit("  A <host> [port]  Connect and arm automatic 60-second Windows handoff test receiver.")
    emit("  M  Start mouse suppression + relay mode for 8 seconds.")
    emit("  K  Start keyboard suppression + relay mode for 8 seconds.")
    emit("  B  Start combined mouse + keyboard bridge mode for 30 seconds.")
    emit("  T  Run timed staged mouse, keyboard, then combined bridge test.")
    emit("  S  Stop active mode.")
    emit("  R  Reset virtual cursor to current macOS cursor.")
    emit("  P  Print current state.")
    emit("  L [port]  Network listeners are disabled; use the approved TLS connector.")
    emit("  C <host> [port]  Connect to explicit loopback development peer. Default port is 47870.")
    emit("  N  Print LAN state.")
    emit("  Q  Quit.")
    emit()
    emit("Safety:")
    emit("  Escape or F12 stops the active mode.")
    emit("  Each mode auto-stops.")
    emit("  Legacy networking requires --insecure-loopback-development and a literal loopback host.")
    emit("  Text clipboard sharing is disabled until you explicitly enable it.")
    emit()
    emit("Launch options:")
    emit("  --auto-receive-test <windows-host> [--port 47870]")
    emit("  --app-mode [--config <path>]")
    emit("  --setup  Open guided secure pairing and input setup.")
    emit("  --insecure-loopback-development  Allow plaintext localhost development only.")
    emit("  --share-clipboard  Opt in to text clipboard sharing in loopback development.")
    emit()
}

private func commandLoop(agent: BatCaveMacOSAgent, lan: LanBridgePeer) {
    while true {
        emitPrompt("Command [A/M/K/B/T/S/R/P/L/C/N/Q]: ")
        guard let line = readLine() else {
            DispatchQueue.main.async {
                agent.stop(reason: "stdin closed")
                exit(0)
            }
            return
        }

        let parts = line.split(whereSeparator: { $0.isWhitespace }).map(String.init)
        guard let command = parts.first?.uppercased() else {
            continue
        }

        switch command {
        case "A":
            guard parts.count >= 2 else {
                emit("Usage: A <host> [port]")
                break
            }

            do {
                lan.connect(host: parts[1], port: try parsePort(parts, index: 2))
                emit("Automatic handoff receiver armed. The Windows side should run command A; no local macOS mode is required.")
            } catch {
                emit("Command failed: \(error.localizedDescription)")
            }
        case "M":
            DispatchQueue.main.async { agent.startMouseTest(duration: 8) }
        case "K":
            DispatchQueue.main.async { agent.startKeyboardTest(duration: 8) }
        case "B":
            DispatchQueue.main.async { agent.startBridge(duration: 30) }
        case "T":
            DispatchQueue.main.async { agent.startStagedTest() }
        case "S":
            DispatchQueue.main.async { agent.stop(reason: "command") }
        case "R":
            DispatchQueue.main.async { agent.resetVirtualCursor() }
        case "P":
            DispatchQueue.main.async { agent.printState() }
        case "L":
            do {
                lan.startListening(port: try parsePort(parts, index: 1))
            } catch {
                emit("Command failed: \(error.localizedDescription)")
            }
        case "C":
            guard parts.count >= 2 else {
                emit("Usage: C <host> [port]")
                break
            }

            do {
                lan.connect(host: parts[1], port: try parsePort(parts, index: 2))
            } catch {
                emit("Command failed: \(error.localizedDescription)")
            }
        case "N":
            lan.printState()
        case "Q":
            DispatchQueue.main.async {
                agent.stop(reason: "quit")
                lan.dispose()
                exit(0)
            }
            return
        default:
            emit("Unknown command.")
        }
    }
}

private func parsePort(_ parts: [String], index: Int) throws -> UInt16 {
    guard parts.count > index else {
        return LanBridgePeer.defaultPort
    }

    guard let port = UInt16(parts[index]), port > 0 else {
        throw ArgumentError("Invalid TCP port: \(parts[index])")
    }

    return port
}

private struct LaunchOptions {
    var autoReceiveHost: String?
    var port: UInt16 = LanBridgePeer.defaultPort
    var appMode = false
    var configPath: String?
}

private var appModeMenuController: AppModeMenuController?

private final class AppLifecycle: NSObject, NSApplicationDelegate, @unchecked Sendable {
    let agent: BatCaveMacOSAgent
    let lan: LanBridgePeer
    private var sleepObserver: NSObjectProtocol?
    private var activity: NSObjectProtocol?
    private var signals: [DispatchSourceSignal] = []

    init(agent: BatCaveMacOSAgent, lan: LanBridgePeer) {
        self.agent = agent
        self.lan = lan
        super.init()
        activity = ProcessInfo.processInfo.beginActivity(options: .userInitiatedAllowingIdleSystemSleep, reason: "BatCave input sharing")
        sleepObserver = NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.willSleepNotification, object: nil, queue: .main) { [weak self] _ in
            self?.agent.stop(reason: "Mac sleeping")
            self?.lan.disconnect(reason: "Mac sleeping")
        }
        for number in [SIGINT, SIGTERM] {
            signal(number, SIG_IGN)
            let source = DispatchSource.makeSignalSource(signal: number, queue: .main)
            source.setEventHandler { [weak self] in
                self?.agent.stop(reason: "termination signal")
                Task { @MainActor in NSApp.terminate(nil) }
            }
            source.resume()
            signals.append(source)
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        agent.stop(reason: "app terminating")
        lan.dispose()
        if let activity { ProcessInfo.processInfo.endActivity(activity) }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }
}

private func parseLaunchOptions(_ args: [String]) throws -> LaunchOptions {
    var options = LaunchOptions()
    var index = 0

    while index < args.count {
        switch args[index] {
        case "--auto-receive-test":
            index += 1
            guard index < args.count else {
                throw ArgumentError("--auto-receive-test requires a Windows host value")
            }
            options.autoReceiveHost = args[index]
        case "--port":
            index += 1
            guard index < args.count else {
                throw ArgumentError("--port requires a value")
            }
            options.port = try parsePortValue(args[index])
        case "--app-mode":
            options.appMode = true
        case "--config":
            index += 1
            guard index < args.count else {
                throw ArgumentError("--config requires a path value")
            }
            options.configPath = args[index]
        default:
            break
        }

        index += 1
    }

    return options
}

private struct AppConfig {
    var autoConnect = false
    var windowsHost: String?
    var port: UInt16?
}

private func isLaunchedFromAppBundle() -> Bool {
    Bundle.main.bundlePath.hasSuffix(".app")
}

private func defaultAppConfigPath() -> String {
    let directory = FileManager.default
        .homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Application Support/BatCave/SecurePreview", isDirectory: true)
    return directory.appendingPathComponent("MacOSAgent.conf").path
}

private func resolveLaunchOptions(_ options: LaunchOptions) -> LaunchOptions {
    var resolved = options
    if isLaunchedFromAppBundle() {
        resolved.appMode = true
    }

    guard resolved.appMode else {
        return resolved
    }

    let configPath = resolved.configPath ?? defaultAppConfigPath()
    resolved.configPath = configPath
    ensureDefaultAppConfigExists(at: configPath)
    let config = readAppConfig(at: configPath)

    if resolved.autoReceiveHost == nil, config.autoConnect {
        resolved.autoReceiveHost = config.windowsHost
    }
    if let port = config.port, resolved.port == LanBridgePeer.defaultPort {
        resolved.port = port
    }

    return resolved
}

private func ensureDefaultAppConfigExists(at path: String) {
    guard !FileManager.default.fileExists(atPath: path) else {
        return
    }

    let bundledHost = Bundle.main.object(forInfoDictionaryKey: "BatCaveDefaultWindowsHost") as? String
    let defaultHost: String
    if let bundledHost, !bundledHost.isEmpty {
        defaultHost = bundledHost
    } else {
        defaultHost = "127.0.0.1"
    }
    let defaultPort = Bundle.main.object(forInfoDictionaryKey: "BatCaveDefaultPort") as? Int ?? Int(LanBridgePeer.defaultPort)

    var lines = [
        "# BatCave macOS Agent",
        "# Edit this file if the Windows host or port changes.",
        "AUTOCONNECT=0"
    ]
    lines.append("WINDOWS_HOST=\(defaultHost)")
    lines.append("PORT=\(defaultPort)")
    lines.append("")

    do {
        let url = URL(fileURLWithPath: path)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try lines.joined(separator: "\n").write(to: url, atomically: true, encoding: .utf8)
        emit("App config created at \(path)")
    } catch {
        emitError("failed to create app config at \(path): \(error.localizedDescription)")
    }
}

private func readAppConfig(at path: String) -> AppConfig {
    guard let text = try? String(contentsOfFile: path, encoding: .utf8) else {
        return AppConfig()
    }

    var config = AppConfig()
    for rawLine in text.components(separatedBy: .newlines) {
        let line = rawLine.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !line.isEmpty, !line.hasPrefix("#") else {
            continue
        }

        let parts = line.split(separator: "=", maxSplits: 1).map { String($0).trimmingCharacters(in: .whitespacesAndNewlines) }
        guard parts.count == 2 else {
            continue
        }

        switch parts[0].uppercased() {
        case "AUTOCONNECT":
            config.autoConnect = !["0", "false", "no", "off"].contains(parts[1].lowercased())
        case "WINDOWS_HOST":
            config.windowsHost = parts[1].isEmpty ? nil : parts[1]
        case "PORT":
            config.port = UInt16(parts[1])
        default:
            break
        }
    }

    return config
}

private final class AppModeMenuController: NSObject, NSMenuDelegate, @unchecked Sendable {
    private var statusItem: NSStatusItem?
    private let agent: BatCaveMacOSAgent
    private let lan: LanBridgePeer
    private let host: String?
    private let port: UInt16
    private let configPath: String
    private let inputReady: Bool

    init(agent: BatCaveMacOSAgent, lan: LanBridgePeer, host: String?, port: UInt16, configPath: String, inputReady: Bool) {
        self.agent = agent
        self.lan = lan
        self.host = host
        self.port = port
        self.configPath = configPath
        self.inputReady = inputReady
    }

    func install() {
        Task { @MainActor in
            self.installOnMain()
        }
    }

    @MainActor
    private func installOnMain() {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        item.button?.title = "BatCave"
        item.button?.toolTip = "BatCave macOS Agent"
        item.menu = buildMenu()
        statusItem = item
    }

    @MainActor
    private func buildMenu() -> NSMenu {
        let menu = NSMenu()
        menu.delegate = self
        let status = NSMenuItem(title: connectionDescription, action: nil, keyEquivalent: "")
        status.isEnabled = false
        menu.addItem(status)
        menu.addItem(.separator())
        menu.addItem(menuItem(title: "Show Status", action: #selector(showStatus)))
        menu.addItem(menuItem(title: "Reveal Config", action: #selector(revealConfig)))
        menu.addItem(.separator())
        menu.addItem(menuItem(title: "Quit BatCave", action: #selector(quit), keyEquivalent: "q"))
        return menu
    }

    private var connectionDescription: String {
        if !inputReady {
            return "Permissions needed"
        }
        if host != nil {
            return lan.statusDescription
        }
        return "No Windows host configured"
    }

    @MainActor func menuWillOpen(_ menu: NSMenu) {
        menu.items.first?.title = connectionDescription
    }

    private func menuItem(title: String, action: Selector, keyEquivalent: String = "") -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: keyEquivalent)
        item.target = self
        return item
    }

    @objc @MainActor private func showStatus() {
        agent.showAppModeStatus(host: host, port: port, inputReady: inputReady)
    }

    @objc @MainActor private func revealConfig() {
        NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: configPath)])
    }

    @objc @MainActor private func quit() {
        NSApp.terminate(nil)
    }
}

private func parsePortValue(_ value: String) throws -> UInt16 {
    guard let port = UInt16(value), port > 0 else {
        throw ArgumentError("Invalid TCP port: \(value)")
    }

    return port
}

private struct ArgumentError: LocalizedError {
    let message: String

    init(_ message: String) {
        self.message = message
    }

    var errorDescription: String? {
        message
    }
}

if CommandLine.arguments.contains("--help") {
    printHelp()
    exit(0)
}

if setupPreviewRequested() {
    runSetupPreview()
    exit(0)
}

// Finder drops stdout. Preserve diagnostics beside the isolated development
// bundle so native launch, permissions, handoffs and disconnects are observable.
if isLaunchedFromAppBundle(), !CommandLine.arguments.contains("--permission-status") {
    let directory = Bundle.main.bundleURL.deletingLastPathComponent()
    if directory.lastPathComponent == "Development" {
        let logURL = directory.appendingPathComponent("BatCaveMacOSAgent.log")
        if !FileManager.default.fileExists(atPath: logURL.path) {
            FileManager.default.createFile(atPath: logURL.path, contents: nil)
        }
        if let log = try? FileHandle(forWritingTo: logURL) {
            _ = try? log.seekToEnd()
            dup2(log.fileDescriptor, STDOUT_FILENO)
            dup2(log.fileDescriptor, STDERR_FILENO)
            emit("SESSION START \(ISO8601DateFormatter().string(from: Date())) pid=\(getpid())")
        }
    }
}

private let launchOptions: LaunchOptions
do {
    launchOptions = resolveLaunchOptions(try parseLaunchOptions(Array(CommandLine.arguments.dropFirst())))
} catch {
    emitError("launch option error: \(error.localizedDescription)")
    exit(2)
}

if CommandLine.arguments.contains("--permission-status") {
    printPermissionStatus()
    exit(0)
}

maybeRequestPermissions()
printPermissionStatus()
NSApplication.shared.setActivationPolicy(launchOptions.appMode ? .regular : .accessory)

private let lan = LanBridgePeer(allowInsecureLoopback: CommandLine.arguments.contains("--insecure-loopback-development"),
    allowLegacyClipboard: CommandLine.arguments.contains("--share-clipboard"))
private let agent = BatCaveMacOSAgent(lan: lan)
private let lifecycle = AppLifecycle(agent: agent, lan: lan)
NSApplication.shared.delegate = lifecycle
let inputPermissionsReady = AXIsProcessTrusted() && CGPreflightListenEventAccess()
let eventTapInstalled = inputPermissionsReady && agent.install(exitOnFailure: !launchOptions.appMode)
if !eventTapInstalled, !launchOptions.appMode { exit(1) }
if CommandLine.arguments.contains("--share-clipboard") { agent.startClipboardSync() }
printHelp()
agent.printState(reason: "startup")
lan.printState()

if launchOptions.appMode, !eventTapInstalled {
    emit("APP MODE waiting for permissions; input bridge is not active until BatCave is restarted after approval.")
}

if let host = launchOptions.autoReceiveHost, eventTapInstalled {
    emit()
    emit("AUTO HANDOFF RECEIVER armed.")
    emit("Connecting to Windows at \(host):\(launchOptions.port).")
    emit("Wait for the Windows countdown; no local macOS command is required.")
    lan.connect(host: host, port: launchOptions.port, automaticallyReconnect: launchOptions.appMode)
    emit()
} else if launchOptions.autoReceiveHost != nil {
    emit("AUTO HANDOFF RECEIVER waiting for permissions before connecting.")
}

if launchOptions.appMode {
    emit("APP MODE active: stdin command loop disabled.")
    agent.showAppModeStatus(host: launchOptions.autoReceiveHost, port: launchOptions.port, inputReady: eventTapInstalled)
    appModeMenuController = AppModeMenuController(
        agent: agent,
        lan: lan,
        host: launchOptions.autoReceiveHost,
        port: launchOptions.port,
        configPath: launchOptions.configPath ?? defaultAppConfigPath(),
        inputReady: eventTapInstalled
    )
    appModeMenuController?.install()
} else {
    let commandThread = Thread {
        commandLoop(agent: agent, lan: lan)
    }
    commandThread.name = "CommandReader"
    commandThread.start()
}

if launchOptions.appMode {
    NSApplication.shared.run()
    agent.stop(reason: "app exited")
    lan.dispose()
} else {
    CFRunLoopRun()
}
