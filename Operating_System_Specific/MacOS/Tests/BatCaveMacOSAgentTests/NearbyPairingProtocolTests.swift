import XCTest
import Security
@testable import BatCaveMacOSAgent

final class NearbyPairingProtocolTests: XCTestCase {
    private struct Vector: Decodable {
        struct Device: Decodable { let card: PairingCard; let inputPort: Int }
        let session: String
        let initiator: Device
        let responder: Device
        let initiatorNonce: String
        let responderNonce: String
        let initiatorCommitment: String
        let responderCommitment: String
        let transcript: String
        let code: String
        let signatures: [String: String]
    }
    private func vector() throws -> Vector {
        var root = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { root.deleteLastPathComponent() }
        return try JSONDecoder().decode(Vector.self, from: Data(contentsOf: root.appendingPathComponent("Universal/Protocol/pairing-vectors.json")))
    }
    private func descriptor(_ device: Vector.Device) -> NearbyPairingProtocol.Descriptor {
        .init(card: device.card, inputPort: device.inputPort)
    }
    // Every generated key is transient. No test calls openOrCreate or SecItemAdd.
    private func identity(bits: Int = 256) throws -> (SecKey, NearbyPairingProtocol.Descriptor) {
        let key = try XCTUnwrap(SecKeyCreateRandomKey([kSecAttrKeyType: kSecAttrKeyTypeECSECPrimeRandom,
            kSecAttrKeySizeInBits: bits, kSecAttrIsPermanent: false] as CFDictionary, nil))
        let der = try LocalCertificate.create(key: key, commonName: "Synthetic nearby identity")
        let card = PairingCard(deviceId: "macos:synthetic-nearby", displayName: "Synthetic computer",
            certificateDerBase64: der.base64EncodedString(), certificateSha256: PairingCard.fingerprint(der))
        return (key, .init(card: card, inputPort: 47870))
    }
    func testSharedCommitmentsTranscriptAndComparisonCode() throws {
        let v = try vector()
        let i = try NearbyPairingProtocol.identityBytes(session: v.session, role: .initiator, descriptor: descriptor(v.initiator),
            nonce: NearbyPairingProtocol.hex(v.initiatorNonce, bytes: 32))
        let r = try NearbyPairingProtocol.identityBytes(session: v.session, role: .responder, descriptor: descriptor(v.responder),
            nonce: NearbyPairingProtocol.hex(v.responderNonce, bytes: 32))
        XCTAssertEqual(NearbyPairingProtocol.hexString(NearbyPairingProtocol.commitment(i)), v.initiatorCommitment)
        XCTAssertEqual(NearbyPairingProtocol.hexString(NearbyPairingProtocol.commitment(r)), v.responderCommitment)
        let transcript = NearbyPairingProtocol.transcript(initiator: i, responder: r)
        XCTAssertEqual(NearbyPairingProtocol.hexString(transcript), v.transcript)
        XCTAssertEqual(try NearbyPairingProtocol.code(transcript), v.code)
        XCTAssertNotEqual(NearbyPairingProtocol.transcript(initiator: r, responder: i), transcript)
        XCTAssertThrowsError(try NearbyPairingProtocol.identityBytes(session: v.session.uppercased(), role: .initiator,
            descriptor: descriptor(v.initiator), nonce: Data(repeating: 0, count: 32)))
        XCTAssertThrowsError(try NearbyPairingProtocol.identityBytes(session: v.session, role: .initiator,
            descriptor: descriptor(v.initiator), nonce: Data(repeating: 0, count: 31)))
    }
    func testSharedRSAAndECDSAProofAndApprovalSignatures() throws {
        let v = try vector(); let transcript = try NearbyPairingProtocol.hex(v.transcript, bytes: 32)
        for role in [NearbyPairingProtocol.Role.initiator, .responder] {
            let peer = descriptor(role == .initiator ? v.initiator : v.responder)
            for stage in [NearbyPairingProtocol.Stage.proof, .approve] {
                let signature = try NearbyPairingProtocol.strictBase64(XCTUnwrap(v.signatures[role.rawValue + "-" + stage.rawValue]))
                let message = try NearbyPairingProtocol.signatureMessage(transcript: transcript, role: role, stage: stage)
                XCTAssertTrue(try NearbyPairingProtocol.verify(descriptor: peer, message: message, signature: signature))
                let otherStage: NearbyPairingProtocol.Stage = stage == .proof ? .approve : .proof
                XCTAssertFalse(try NearbyPairingProtocol.verify(descriptor: peer,
                    message: NearbyPairingProtocol.signatureMessage(transcript: transcript, role: role, stage: otherStage), signature: signature))
                XCTAssertFalse(try NearbyPairingProtocol.verify(descriptor: peer,
                    message: NearbyPairingProtocol.signatureMessage(transcript: transcript,
                        role: role == .initiator ? .responder : .initiator, stage: stage), signature: signature))
                var replay = transcript; replay[0] ^= 1
                XCTAssertFalse(try NearbyPairingProtocol.verify(descriptor: peer,
                    message: NearbyPairingProtocol.signatureMessage(transcript: replay, role: role, stage: stage), signature: signature))
            }
        }
    }
    func testTransientP256AndP384SigningAndDERValidation() throws {
        let message = Data("synthetic signed message".utf8)
        for bits in [256, 384] {
            let (key, peer) = try identity(bits: bits)
            let signature = try NearbyPairingProtocol.sign(key: key, message: message)
            XCTAssertTrue(try NearbyPairingProtocol.verify(descriptor: peer, message: message, signature: signature))
            XCTAssertThrowsError(try NearbyPairingProtocol.verify(descriptor: peer, message: message, signature: signature + Data([0])))
            XCTAssertThrowsError(try NearbyPairingProtocol.verify(descriptor: peer, message: message, signature: Data([0x30, 0])))
            XCTAssertThrowsError(try NearbyPairingProtocol.verify(descriptor: peer, message: message,
                signature: Data([0x30,6,2,1,0,2,1,1]))) // zero r
            XCTAssertThrowsError(try NearbyPairingProtocol.verify(descriptor: peer, message: message,
                signature: Data([0x30,7,2,2,0,1,2,1,1]))) // redundant integer padding
        }
    }
    func testTransientRSAAndUnsupportedKeys() throws {
        let key = try XCTUnwrap(SecKeyCreateRandomKey([kSecAttrKeyType: kSecAttrKeyTypeRSA,
            kSecAttrKeySizeInBits: 3072, kSecAttrIsPermanent: false] as CFDictionary, nil))
        let message = Data("synthetic RSA message".utf8)
        let signature = try NearbyPairingProtocol.sign(key: key, message: message)
        XCTAssertTrue(SecKeyVerifySignature(try XCTUnwrap(SecKeyCopyPublicKey(key)), .rsaSignatureMessagePKCS1v15SHA256,
            message as CFData, signature as CFData, nil))
        let weakRSA = try XCTUnwrap(SecKeyCreateRandomKey([kSecAttrKeyType: kSecAttrKeyTypeRSA,
            kSecAttrKeySizeInBits: 2048, kSecAttrIsPermanent: false] as CFDictionary, nil))
        XCTAssertThrowsError(try NearbyPairingProtocol.sign(key: weakRSA, message: message))
        let unsupportedEC = try XCTUnwrap(SecKeyCreateRandomKey([kSecAttrKeyType: kSecAttrKeyTypeECSECPrimeRandom,
            kSecAttrKeySizeInBits: 521, kSecAttrIsPermanent: false] as CFDictionary, nil))
        XCTAssertThrowsError(try NearbyPairingProtocol.sign(key: unsupportedEC, message: message))
    }
    func testFrameRoundTripsAndExactFieldTypes() throws {
        let v = try vector(); let session = v.session
        let frames: [NearbyPairingProtocol.Frame] = [
            .commit(session: session, commitment: try NearbyPairingProtocol.hex(v.initiatorCommitment, bytes: 32)),
            .reveal(session: session, nonce: try NearbyPairingProtocol.hex(v.responderNonce, bytes: 32), descriptor: descriptor(v.responder)),
            .proof(session: session, signature: try NearbyPairingProtocol.strictBase64(XCTUnwrap(v.signatures["r-proof"]))),
            .approve(session: session, signature: try NearbyPairingProtocol.strictBase64(XCTUnwrap(v.signatures["r-approve"])))]
        for frame in frames { XCTAssertEqual(try NearbyPairingProtocol.parseFrame(NearbyPairingProtocol.encodeFrame(frame)), frame) }
        let text = String(decoding: try NearbyPairingProtocol.encodeFrame(frames[0]), as: UTF8.self)
        XCTAssertThrowsError(try NearbyPairingProtocol.parseFrame(Data(text.utf8) + Data([10])))
        for mutation in [text.replacingOccurrences(of: "\"version\":1", with: "\"version\":true"),
            text.replacingOccurrences(of: "\"version\":1", with: "\"version\":1.0"),
            text.replacingOccurrences(of: "\"version\":1", with: "\"version\":1e0"),
            text.replacingOccurrences(of: "\"version\":1", with: "\"version\":\"1\""),
            text.replacingOccurrences(of: "\"version\":1", with: "\"version\":2"),
            text.replacingOccurrences(of: "pair.commit", with: "device.hello"),
            text.replacingOccurrences(of: "\"version\":1", with: "\"extra\":null,\"version\":1"),
            text.replacingOccurrences(of: "\"version\":1", with: "\"version\":1,\"version\":1"),
            text.replacingOccurrences(of: "\"version\":1", with: "\"version\":1,\"vers\\u0069on\":1")] {
            XCTAssertThrowsError(try NearbyPairingProtocol.parseFrame(Data(mutation.utf8)), mutation)
        }
    }
    func testNestedCardDuplicatesUnknownFieldsAndPortTypes() throws {
        let v = try vector()
        let frame = NearbyPairingProtocol.Frame.reveal(session: v.session,
            nonce: try NearbyPairingProtocol.hex(v.responderNonce, bytes: 32), descriptor: descriptor(v.responder))
        let text = String(decoding: try NearbyPairingProtocol.encodeFrame(frame), as: UTF8.self)
        for mutation in [text.replacingOccurrences(of: "\"schema\":", with: "\"extra\":false,\"schema\":"),
            text.replacingOccurrences(of: "\"schema\":", with: "\"schema\":\"batcave.pairing-card.v1\",\"schema\":"),
            text.replacingOccurrences(of: "\"inputPort\":47870", with: "\"inputPort\":47870.0"),
            text.replacingOccurrences(of: "\"inputPort\":47870", with: "\"inputPort\":true"),
            text.replacingOccurrences(of: "\"inputPort\":47870", with: "\"inputPort\":0"),
            text.replacingOccurrences(of: "\"inputPort\":47870", with: "\"inputPort\":65536")] {
            XCTAssertThrowsError(try NearbyPairingProtocol.parseFrame(Data(mutation.utf8)), mutation)
        }
    }
    func testStrictJSONRejectsDuplicateEscapesArraysMalformedUnicodeAndDepth() throws {
        for text in ["{\"a\":1,\"a\":2}", "{\"a\":1,\"\\u0061\":2}", "{\"a\":[]}", "[]", "{}{}",
            "{\"a\":\"\\uD800\"}", "{\"a\":\"\\uDC00\"}", "{\"a\":\"\\uD800\\u0041\"}",
            "{\"a\":01}", "{\"a\":+1}", "{\"a\":1.}", "{\"a\":1e}", "{\"a\":NaN}",
            "{\"a\":\"literal\nnewline\"}", "{\"a\":1,}", "{\"a\":true}\r"] {
            XCTAssertThrowsError(try NearbyPairingProtocol.parseObject(Data(text.utf8)), text)
        }
        XCTAssertEqual(try NearbyPairingProtocol.parseObject(Data("{\"emoji\":\"\\uD83D\\uDE00\"}".utf8))["emoji"]?.string, "😀")
        let nested8 = String(repeating: "{\"a\":", count: 7) + "{}" + String(repeating: "}", count: 7)
        _ = try NearbyPairingProtocol.parseObject(Data(nested8.utf8))
        XCTAssertThrowsError(try NearbyPairingProtocol.parseObject(Data(("{\"a\":" + nested8 + "}").utf8)))
        XCTAssertThrowsError(try NearbyPairingProtocol.parseObject(Data([123,34,97,34,58,34,0xC0,0xAF,34,125])))
        XCTAssertThrowsError(try NearbyPairingProtocol.parseObject(Data([0xEF,0xBB,0xBF,123,125])))
    }
    func testFrameBoundAndStrictEncodings() throws {
        _ = try NearbyPairingProtocol.parseObject(Data("{}".utf8) + Data(repeating: 32, count: 16_382))
        XCTAssertThrowsError(try NearbyPairingProtocol.parseObject(Data("{}".utf8) + Data(repeating: 32, count: 16_383)))
        XCTAssertThrowsError(try NearbyPairingProtocol.parseObject(Data(repeating: 32, count: 1025), maximumBytes: 1024))
        for text in ["AA==\n", " AA==", "AB==", "AA", "_w==", "AA==="] {
            XCTAssertThrowsError(try NearbyPairingProtocol.strictBase64(text), text)
        }
        XCTAssertEqual(try NearbyPairingProtocol.strictBase64("AA=="), Data([0]))
        XCTAssertThrowsError(try NearbyPairingProtocol.hex(String(repeating: "a", count: 64), bytes: 32))
        XCTAssertThrowsError(try NearbyPairingProtocol.hex(String(repeating: "A", count: 32), bytes: 16, lowercase: true))
        XCTAssertFalse(NearbyPairingProtocol.constantTimeEqual(Data([1]), Data([1, 0])))
        XCTAssertFalse(NearbyPairingProtocol.constantTimeEqual(Data([1]), Data([2])))
        XCTAssertTrue(NearbyPairingProtocol.constantTimeEqual(Data([1]), Data([1])))
    }
    func testUnicodeScalarLabelsAndNoNormalization() throws {
        for text in ["", "  ", "bad\u{200D}name", "bad\u{2028}name", "bad\u{2029}name", "bad\u{0000}name",
            String(repeating: "a", count: 129), String(repeating: "e\u{301}", count: 65)] {
            XCTAssertThrowsError(try NearbyPairingProtocol.validateLabel(text))
        }
        try NearbyPairingProtocol.validateLabel(String(repeating: "😀", count: 128))
        try NearbyPairingProtocol.validateLabel(" e\u{301} ")
        let (_, peer) = try identity()
        let first = PairingCard(deviceId: peer.card.deviceId, displayName: " e\u{301} ",
            certificateDerBase64: peer.card.certificateDerBase64, certificateSha256: peer.card.certificateSha256)
        let second = PairingCard(deviceId: peer.card.deviceId, displayName: " é ",
            certificateDerBase64: peer.card.certificateDerBase64, certificateSha256: peer.card.certificateSha256)
        let session = String(repeating: "0", count: 32); let nonce = Data(repeating: 0, count: 32)
        XCTAssertNotEqual(try NearbyPairingProtocol.identityBytes(session: session, role: .initiator, descriptor: .init(card: first, inputPort: 1), nonce: nonce),
            try NearbyPairingProtocol.identityBytes(session: session, role: .initiator, descriptor: .init(card: second, inputPort: 1), nonce: nonce))
    }
    func testCertificateExpiryFingerprintTrailingDERAndImportedCardCompatibility() throws {
        let (_, peer) = try identity()
        XCTAssertThrowsError(try NearbyPairingProtocol.validateDescriptor(peer, now: Date.distantFuture))
        let der = try NearbyPairingProtocol.strictBase64(peer.card.certificateDerBase64) + Data([0])
        let trailing = PairingCard(deviceId: peer.card.deviceId, displayName: peer.card.displayName,
            certificateDerBase64: der.base64EncodedString(), certificateSha256: PairingCard.fingerprint(der))
        XCTAssertThrowsError(try NearbyPairingProtocol.validateDescriptor(.init(card: trailing, inputPort: 1)))
        let lowercase = PairingCard(deviceId: peer.card.deviceId, displayName: peer.card.displayName,
            certificateDerBase64: peer.card.certificateDerBase64, certificateSha256: peer.card.certificateSha256.lowercased())
        XCTAssertThrowsError(try NearbyPairingProtocol.validateDescriptor(.init(card: lowercase, inputPort: 1)))
        XCTAssertEqual(try PairingCard.parse(lowercase.encoded()), lowercase) // saved identity IDs/pins remain usable
        let text = String(decoding: try peer.card.encoded(), as: UTF8.self)
        XCTAssertEqual(try PairingCard.parse(Data(text.replacingOccurrences(of: "\n", with: "\r\n").utf8)), peer.card)
        XCTAssertThrowsError(try PairingCard.parse(Data(text.replacingOccurrences(of: "\"schema\" :", with: "\"extra\" : false, \"schema\" :").utf8)))
        var noMutualTLS = try NearbyPairingProtocol.strictBase64(peer.card.certificateDerBase64)
        let clientPurpose = Data([0x2B,6,1,5,5,7,3,2])
        let position = try XCTUnwrap(noMutualTLS.range(of: clientPurpose))
        noMutualTLS[position.upperBound - 1] = 3
        let missingUsage = PairingCard(deviceId: peer.card.deviceId, displayName: peer.card.displayName,
            certificateDerBase64: noMutualTLS.base64EncodedString(), certificateSha256: PairingCard.fingerprint(noMutualTLS))
        XCTAssertThrowsError(try NearbyPairingProtocol.validateDescriptor(.init(card: missingUsage, inputPort: 1)))
    }
    func testFreshRandomMaterial() throws {
        let sessions = try (0..<8).map { _ in try NearbyPairingProtocol.randomSession() }
        let nonces = try (0..<8).map { _ in try NearbyPairingProtocol.randomNonce() }
        XCTAssertEqual(Set(sessions).count, 8); XCTAssertEqual(Set(nonces).count, 8)
        for session in sessions { XCTAssertEqual(try NearbyPairingProtocol.hex(session, bytes: 16, lowercase: true).count, 16) }
        XCTAssertTrue(nonces.allSatisfy { $0.count == 32 })
    }
}
