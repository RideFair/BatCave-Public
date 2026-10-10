import XCTest
@preconcurrency import AppKit
import SwiftUI
import Security
@testable import BatCaveMacOSAgent

final class NearbySetupTests: XCTestCase, @unchecked Sendable {
    private let now = Date(timeIntervalSince1970: 1_800_000_000)
    private let pin = String(repeating: "AB", count: 32)

    func testInitialApprovalConsumedOnceBeforeActivation() {
        var gate = InitialSharingGate()
        gate.grant(InitialSharingConsent(pairingSession: UUID(), peerID: "windows:example",
                                        fingerprint: pin, role: .receiver, now: now))
        XCTAssertTrue(gate.consume(peerID: "windows:example", fingerprint: pin.lowercased(),
                                   role: .receiver, inputSession: "first", now: now))
        XCTAssertNil(gate.consent)
        XCTAssertFalse(gate.consume(peerID: "windows:example", fingerprint: pin,
                                    role: .receiver, inputSession: "reconnect", now: now))
    }

    func testInitialApprovalRejectsChangedPinRolePeerAndExistingSession() {
        let value = InitialSharingConsent(pairingSession: UUID(), peerID: "macos:example",
            fingerprint: pin, role: .source, now: now, previousInputSession: "old")
        let attempts: [(String, String, LanInputRole, String)] = [
            ("linux:other", pin, .source, "new"),
            ("macos:example", String(repeating: "CD", count: 32), .source, "new"),
            ("macos:example", pin, .receiver, "new"),
            ("macos:example", pin, .source, "old")
        ]
        for attempt in attempts {
            var gate = InitialSharingGate(); gate.grant(value)
            XCTAssertFalse(gate.consume(peerID: attempt.0, fingerprint: attempt.1,
                                        role: attempt.2, inputSession: attempt.3, now: now))
            XCTAssertNil(gate.consent)
            XCTAssertFalse(gate.consume(peerID: "macos:example", fingerprint: pin,
                                        role: .source, inputSession: "new", now: now))
        }
    }

    func testApprovalExpiresAndLifecycleRevocationCannotRearm() {
        let value = InitialSharingConsent(pairingSession: UUID(), peerID: "linux:example", fingerprint: pin, role: .receiver, now: now)
        XCTAssertTrue(value.permits(peerID: value.peerID, fingerprint: pin, role: .receiver,
                                   inputSession: "new", now: now.addingTimeInterval(29.999)))
        XCTAssertFalse(value.permits(peerID: value.peerID, fingerprint: pin, role: .receiver,
                                    inputSession: "new", now: now.addingTimeInterval(30)))
        for _ in ["Pause", "navigation", "role change", "lock", "sleep", "disconnect", "replacement", "quit"] {
            var gate = InitialSharingGate(); gate.grant(value); gate.revoke()
            XCTAssertFalse(gate.consume(peerID: value.peerID, fingerprint: pin, role: .receiver, inputSession: "new", now: now))
        }
    }

    func testCanonicalUnicodeIdentityIsNotEquivalentApproval() {
        let value = InitialSharingConsent(pairingSession: UUID(), peerID: "macos:caf\u{e9}", fingerprint: pin, role: .source, now: now)
        XCTAssertFalse(value.permits(peerID: "macos:cafe\u{301}", fingerprint: pin, role: .source, inputSession: "new", now: now))
    }

    func testClockRollbackCannotExtendOneUseApproval() {
        let value = InitialSharingConsent(pairingSession: UUID(), peerID: "linux:example",
            fingerprint: pin, role: .receiver, now: now, uptime: 100)
        XCTAssertFalse(value.permits(peerID: value.peerID, fingerprint: pin, role: .receiver,
            inputSession: "new", now: now.addingTimeInterval(-300), uptime: 130))
    }

    func testOlderProfilePreservesApprovedIDsAndDefaultsToPausedReceiver() throws {
        let old = Data(#"{"peers":[],"selectedPeerId":"windows:existing","hosts":{"windows:existing":"192.0.2.1"},"macOnRight":false,"completed":true}"#.utf8)
        let profile = try JSONDecoder().decode(PreviewProfile.self, from: old)
        XCTAssertEqual(profile.selectedPeerId, "windows:existing")
        XCTAssertEqual(profile.hosts["windows:existing"], "192.0.2.1")
        XCTAssertFalse(profile.macOnRight)
        XCTAssertTrue(profile.roles.isEmpty)
        XCTAssertTrue(profile.inputPorts.isEmpty)
        let encoded = try JSONEncoder().encode(profile)
        XCTAssertFalse(String(decoding: encoded, as: UTF8.self).contains("consent"))
        XCTAssertFalse(String(decoding: encoded, as: UTF8.self).contains("sharing"))
    }

    func testFreshSetupOpensNearbyWithoutPermissionOrFileWizard() async {
        await MainActor.run {
            let model = SetupModel(inspectionOnly: true)
            XCTAssertEqual(model.page, .nearby)
            XCTAssertTrue(model.peers.isEmpty)
            XCTAssertTrue(model.browsing)
            XCTAssertFalse(model.sharing)
            XCTAssertFalse(model.permissionsReady)
            model.navigate(.arrangement)
            XCTAssertEqual(model.page, .arrangement)
            model.navigate(.permissions)
            XCTAssertEqual(model.page, .permissions)
            model.navigate(.nearby)
            XCTAssertTrue(model.browsing)
            XCTAssertFalse(model.sharing)
        }
    }

    /// Render only our synthetic SwiftUI view into a bitmap. No window, app
    /// activation, native input, system screenshot or identity/profile access.
    func testSyntheticSetupRendersAtDefaultAndMinimumSizes() async throws {
        guard let folder = ProcessInfo.processInfo.environment["BATCAVE_RENDER_OUTPUT"] else { return }
        let key = try XCTUnwrap(SecKeyCreateRandomKey([kSecAttrKeyType: kSecAttrKeyTypeECSECPrimeRandom,
            kSecAttrKeySizeInBits: 256] as CFDictionary, nil))
        let der = try LocalCertificate.create(key: key, commonName: "Synthetic display fixture")
        let card = PairingCard(deviceId: "windows:synthetic", displayName: "Office computer",
            certificateDerBase64: der.base64EncodedString(), certificateSha256: PairingCard.fingerprint(der))
        try await MainActor.run {
            try FileManager.default.createDirectory(atPath: folder, withIntermediateDirectories: true)
            for size in [NSSize(width: 920, height: 670), NSSize(width: 800, height: 580)] {
                for state in ["empty", "nearby", "comparison", "waiting", "linked", "arrangement", "permissions"] {
                    let model = SetupModel(inspectionOnly: true)
                    model.status = "Input paused"
                    if state == "nearby" {
                        model.nearby = [NearbyComputer(deviceID: card.deviceId, displayName: card.displayName,
                            platform: "windows", fingerprint: card.certificateSha256, address: "192.0.2.1",
                            inputPort: 47871, pairingPort: 47873, pairing: true, seen: Date())]
                        model.selectedNearbyId = card.deviceId; model.pairingOpen = true
                    }
                    if state == "comparison" || state == "waiting" {
                        model.confirmation = NearbyPairingConfirmation(sessionID: UUID(),
                            peer: NearbyPairingProtocol.Descriptor(card: card, inputPort: UInt16(47871)), code: "1234 5678", initiator: false)
                        model.waitingForApproval = state == "waiting"
                    }
                    if ["linked", "arrangement"].contains(state) {
                        model.peers = [card]; model.selectedPeerId = card.deviceId; model.connected = true
                        model.accessibility = true; model.inputMonitoring = true
                    }
                    if state == "arrangement" { model.page = .arrangement }
                    if state == "permissions" { model.page = .permissions }
                    let view = makeSetupHostingView(model: model)
                    XCTAssertEqual(view.effectiveAppearance.bestMatch(from: [.aqua, .darkAqua]), .aqua)
                    view.frame = NSRect(origin: .zero, size: size)
                    var rendered: Result<Data, Error>?
                    view.effectiveAppearance.performAsCurrentDrawingAppearance {
                        rendered = Result {
                            view.layoutSubtreeIfNeeded()
                            let bitmap = try XCTUnwrap(view.bitmapImageRepForCachingDisplay(in: view.bounds))
                            view.cacheDisplay(in: view.bounds, to: bitmap)
                            return try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
                        }
                    }
                    let png = try XCTUnwrap(rendered).get()
                    XCTAssertGreaterThan(png.count, 5000)
                    try png.write(to: URL(fileURLWithPath: folder).appendingPathComponent("Mac-\(state)-\(Int(size.width))x\(Int(size.height)).png"))
                }
            }
        }
    }
}
