import Foundation
import CryptoKit
import Security

// Public-identity bootstrap only. These messages cannot carry input or clipboard.
// The native service owns message order, deadlines and explicit user approval.
enum NearbyPairingProtocol {
    struct Descriptor: Equatable, Sendable {
        let card: PairingCard
        let inputPort: Int
        init(card: PairingCard, inputPort: Int) { self.card = card; self.inputPort = inputPort }
        init(card: PairingCard, inputPort: UInt16) { self.init(card: card, inputPort: Int(inputPort)) }
    }
    enum Role: String, Sendable { case initiator = "i", responder = "r" }
    enum Stage: String, Sendable { case proof, approve }
    enum Frame: Equatable, Sendable {
        case commit(session: String, commitment: Data)
        case reveal(session: String, nonce: Data, descriptor: Descriptor)
        case proof(session: String, signature: Data)
        case approve(session: String, signature: Data)
        var session: String {
            switch self {
            case .commit(let session, _), .reveal(let session, _, _),
                 .proof(let session, _), .approve(let session, _): return session
            }
        }
        var type: String {
            switch self {
            case .commit: return "pair.commit"
            case .reveal: return "pair.reveal"
            case .proof: return "pair.proof"
            case .approve: return "pair.approve"
            }
        }
    }

    static func pack(_ parts: Data...) -> Data {
        var result = Data()
        for part in parts {
            precondition(part.count <= Int(UInt32.max))
            let count = UInt32(part.count)
            result.append(contentsOf: [UInt8(count >> 24), UInt8((count >> 16) & 255),
                UInt8((count >> 8) & 255), UInt8(count & 255)])
            result.append(part)
        }
        return result
    }
    static func identityBytes(session: String, role: Role, descriptor: Descriptor, nonce: Data) throws -> Data {
        guard nonce.count == 32 else { throw invalid("Invalid pairing nonce.") }
        try validateDescriptor(descriptor)
        return pack(Data("batcave.pair.v1.identity".utf8), try hex(session, bytes: 16, lowercase: true),
            Data(role.rawValue.utf8), Data(descriptor.card.deviceId.utf8), Data(descriptor.card.displayName.utf8),
            try strictBase64(descriptor.card.certificateDerBase64), Data(String(descriptor.inputPort).utf8), nonce)
    }
    static func commitment(_ identity: Data) -> Data { Data(SHA256.hash(data: identity)) }
    static func transcript(initiator: Data, responder: Data) -> Data {
        Data(SHA256.hash(data: pack(Data("batcave.pair.v1.transcript".utf8), initiator, responder)))
    }
    static func code(_ transcript: Data) throws -> String {
        guard transcript.count == 32 else { throw invalid("Invalid pairing transcript.") }
        let digest = Array(SHA256.hash(data: pack(Data("batcave.pair.v1.code".utf8), transcript)))
        let value = digest.prefix(4).reduce(UInt32(0)) { ($0 << 8) | UInt32($1) } % 100_000_000
        let digits = String(format: "%08u", value)
        return String(digits.prefix(4)) + " " + String(digits.suffix(4))
    }
    static func signatureMessage(transcript: Data, role: Role, stage: Stage) throws -> Data {
        guard transcript.count == 32 else { throw invalid("Invalid pairing transcript.") }
        return pack(Data(("batcave.pair.v1." + stage.rawValue).utf8), transcript, Data(role.rawValue.utf8))
    }
    static func sign(key: SecKey, message: Data) throws -> Data {
        let algorithm = try signatureAlgorithm(key)
        guard SecKeyIsAlgorithmSupported(key, .sign, algorithm) else { throw invalid("The device signing key is unavailable.") }
        var error: Unmanaged<CFError>?
        guard let signature = SecKeyCreateSignature(key, algorithm, message as CFData, &error) as Data? else {
            throw error?.takeRetainedValue() as Error? ?? invalid("Could not sign the pairing approval.")
        }
        return signature
    }
    static func verify(descriptor: Descriptor, message: Data, signature: Data, now: Date = Date()) throws -> Bool {
        let certificate = try validatedCertificate(descriptor.card, now: now)
        guard (1...65535).contains(descriptor.inputPort), let key = SecCertificateCopyKey(certificate) else {
            throw invalid("The device identity key is unavailable.")
        }
        let algorithm = try signatureAlgorithm(key)
        if algorithm == .ecdsaSignatureMessageX962SHA256 { try validateECDSASignature(signature) }
        guard !signature.isEmpty, signature.count <= 1024,
              SecKeyIsAlgorithmSupported(key, .verify, algorithm) else { return false }
        return SecKeyVerifySignature(key, algorithm, message as CFData, signature as CFData, nil)
    }
    static func validateDescriptor(_ descriptor: Descriptor, now: Date = Date()) throws {
        guard (1...65535).contains(descriptor.inputPort) else { throw invalid("Invalid input port.") }
        _ = try validatedCertificate(descriptor.card, now: now)
    }
    // Validation is shared with imported cards; legacy cards may retain lowercase
    // pins, but newly exchanged nearby cards require the exact uppercase encoding.
    static func validatedCertificate(_ card: PairingCard, now: Date = Date(), exactFingerprint: Bool = true) throws -> SecCertificate {
        try validateLabel(card.deviceId); try validateLabel(card.displayName)
        guard card.schema == "batcave.pairing-card.v1" else { throw invalid("Unsupported pairing card.") }
        let der = try strictBase64(card.certificateDerBase64)
        guard der.count <= 12_000, let certificate = SecCertificateCreateWithData(nil, der as CFData),
              certificateIsCurrent(certificate, now: now),
              PairingCard.fingerprint(der).caseInsensitiveCompare(card.certificateSha256) == .orderedSame,
              let key = SecCertificateCopyKey(certificate) else { throw invalid("This card is invalid or expired.") }
        _ = try hex(exactFingerprint ? card.certificateSha256 : card.certificateSha256.uppercased(), bytes: 32)
        try validateCertificateDER(der)
        _ = try signatureAlgorithm(key)
        return certificate
    }
    static func validateLabel(_ text: String) throws {
        guard (1...128).contains(text.unicodeScalars.count), text.utf8.count <= 512,
              !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { throw invalid("Invalid device name.") }
        for scalar in text.unicodeScalars {
            switch scalar.properties.generalCategory {
            case .control, .format, .surrogate, .lineSeparator, .paragraphSeparator:
                throw invalid("Invalid character in device name.")
            default: break
            }
        }
    }
    static func hex(_ text: String, bytes: Int, lowercase: Bool = false) throws -> Data {
        let allowed = lowercase ? "0123456789abcdef" : "0123456789ABCDEF"
        guard text.utf8.count == bytes * 2, text.utf8.allSatisfy({ allowed.utf8.contains($0) }) else {
            throw invalid("Invalid pairing hex encoding.")
        }
        let digits = Array(text.utf8)
        func nibble(_ byte: UInt8) -> UInt8 { byte <= 57 ? byte - 48 : byte - (lowercase ? 87 : 55) }
        return Data(stride(from: 0, to: digits.count, by: 2).map { nibble(digits[$0]) << 4 | nibble(digits[$0 + 1]) })
    }
    static func hexString(_ data: Data, lowercase: Bool = false) -> String {
        data.map { String(format: lowercase ? "%02x" : "%02X", $0) }.joined()
    }
    static func randomSession() throws -> String { hexString(try randomBytes(count: 16), lowercase: true) }
    static func randomNonce() throws -> Data { try randomBytes(count: 32) }
    static func randomBytes(count: Int) throws -> Data {
        var bytes = [UInt8](repeating: 0, count: count)
        guard SecRandomCopyBytes(kSecRandomDefault, count, &bytes) == errSecSuccess else { throw invalid("Secure random generation failed.") }
        return Data(bytes)
    }
    static func constantTimeEqual(_ lhs: Data, _ rhs: Data) -> Bool {
        guard lhs.count == rhs.count else { return false }
        return zip(lhs, rhs).reduce(UInt8(0)) { $0 | ($1.0 ^ $1.1) } == 0
    }
    static func strictBase64(_ text: String, maximumBytes: Int = 12_000) throws -> Data {
        guard text.utf8.count <= ((maximumBytes + 2) / 3) * 4,
              let value = Data(base64Encoded: text), value.count <= maximumBytes,
              value.base64EncodedString() == text else { throw invalid("Invalid pairing base64 encoding.") }
        return value
    }
    static func parseObject(_ data: Data, maximumBytes: Int = 16_384, allowCarriageReturn: Bool = false) throws -> [String: NearbyPairingJSONValue] {
        guard data.count <= maximumBytes, (allowCarriageReturn || !data.contains(13)), String(data: data, encoding: .utf8) != nil else {
            throw invalid("Invalid pairing frame.")
        }
        var parser = NearbyPairingJSONParser(bytes: Array(data))
        return try parser.parse()
    }
    static func requireFields(_ object: [String: NearbyPairingJSONValue], _ fields: Set<String>) throws {
        guard Set(object.keys) == fields else { throw invalid("Unexpected pairing fields.") }
    }
    static func parseFrame(_ data: Data, now: Date = Date()) throws -> Frame {
        guard !data.contains(10) else { throw invalid("A pairing frame must precede its newline delimiter.") }
        let object = try parseObject(data)
        guard let type = object["type"]?.string, object["version"]?.integer == 1,
              let session = object["session"]?.string else { throw invalid("Unsupported pairing frame.") }
        _ = try hex(session, bytes: 16, lowercase: true)
        switch type {
        case "pair.commit":
            try requireFields(object, ["type", "version", "session", "commitment"])
            guard let text = object["commitment"]?.string else { throw invalid("Missing pairing commitment.") }
            return .commit(session: session, commitment: try hex(text, bytes: 32))
        case "pair.reveal":
            try requireFields(object, ["type", "version", "session", "nonce", "card", "inputPort"])
            guard let nonce = object["nonce"]?.string, let port = object["inputPort"]?.integer,
                  case .object(let cardObject) = object["card"] else { throw invalid("Missing pairing identity.") }
            let descriptor = Descriptor(card: try card(from: cardObject), inputPort: port)
            try validateDescriptor(descriptor, now: now)
            return .reveal(session: session, nonce: try hex(nonce, bytes: 32), descriptor: descriptor)
        case "pair.proof", "pair.approve":
            try requireFields(object, ["type", "version", "session", "signature"])
            guard let signature = object["signature"]?.string, signature.utf8.count <= 1400 else { throw invalid("Invalid pairing signature.") }
            let bytes = try strictBase64(signature, maximumBytes: 1024)
            guard !bytes.isEmpty else { throw invalid("Missing pairing signature.") }
            return type == "pair.proof" ? .proof(session: session, signature: bytes) : .approve(session: session, signature: bytes)
        default: throw invalid("Unknown pairing message.")
        }
    }
    static func parse(_ data: Data, now: Date = Date()) throws -> Frame { try parseFrame(data, now: now) }
    static func card(from object: [String: NearbyPairingJSONValue]) throws -> PairingCard {
        try requireFields(object, ["schema", "deviceId", "displayName", "certificateDerBase64", "certificateSha256"])
        guard let schema = object["schema"]?.string, let id = object["deviceId"]?.string,
              let name = object["displayName"]?.string, let der = object["certificateDerBase64"]?.string,
              let pin = object["certificateSha256"]?.string else { throw invalid("Invalid pairing card fields.") }
        return PairingCard(schema: schema, deviceId: id, displayName: name, certificateDerBase64: der, certificateSha256: pin)
    }
    static func encodeFrame(_ frame: Frame) throws -> Data {
        var object: [String: Any] = ["type": frame.type, "version": 1, "session": frame.session]
        switch frame {
        case .commit(_, let commitment): object["commitment"] = hexString(commitment)
        case .reveal(_, let nonce, let descriptor):
            object["nonce"] = hexString(nonce); object["inputPort"] = descriptor.inputPort
            object["card"] = ["schema": descriptor.card.schema, "deviceId": descriptor.card.deviceId,
                "displayName": descriptor.card.displayName, "certificateDerBase64": descriptor.card.certificateDerBase64,
                "certificateSha256": descriptor.card.certificateSha256]
        case .proof(_, let signature), .approve(_, let signature): object["signature"] = signature.base64EncodedString()
        }
        let data = try JSONSerialization.data(withJSONObject: object, options: [.sortedKeys, .withoutEscapingSlashes])
        _ = try parseFrame(data)
        return data
    }
    static func encoded(_ frame: Frame) throws -> Data { try encodeFrame(frame) }
    private static func signatureAlgorithm(_ key: SecKey) throws -> SecKeyAlgorithm {
        guard let attributes = SecKeyCopyAttributes(key) as? [String: Any],
              let type = attributes[kSecAttrKeyType as String] as? String,
              let bits = attributes[kSecAttrKeySizeInBits as String] as? Int else { throw invalid("Unsupported device identity key.") }
        if type == kSecAttrKeyTypeRSA as String, (3072...8192).contains(bits) { return .rsaSignatureMessagePKCS1v15SHA256 }
        if type == kSecAttrKeyTypeECSECPrimeRandom as String, bits == 256 || bits == 384 { return .ecdsaSignatureMessageX962SHA256 }
        throw invalid("Unsupported device identity key.")
    }
    private static func validateECDSASignature(_ data: Data) throws {
        let sequence = try NearbyPairingDER.parse(data)
        guard sequence.tag == 0x30 else { throw invalid("Invalid ECDSA signature.") }
        let integers = try sequence.children()
        guard integers.count == 2 else { throw invalid("Invalid ECDSA signature.") }
        for integer in integers {
            let bytes = Array(integer.content)
            guard integer.tag == 2, !bytes.isEmpty, bytes.count <= 49, bytes[0] & 128 == 0,
                  !(bytes.count > 1 && bytes[0] == 0 && bytes[1] & 128 == 0),
                  bytes.contains(where: { $0 != 0 }) else { throw invalid("Invalid ECDSA signature integer.") }
        }
    }
    private static func validateCertificateDER(_ data: Data) throws {
        let certificate = try NearbyPairingDER.parse(data)
        guard certificate.tag == 0x30 else { throw invalid("Invalid device certificate DER.") }
        let certificateFields = try certificate.children()
        guard certificateFields.count == 3, certificateFields[0].tag == 0x30 else { throw invalid("Invalid device certificate DER.") }
        let tbs = try certificateFields[0].children()
        let offset = tbs.first?.tag == 0xA0 ? 1 : 0
        guard tbs.count > offset + 5 else { throw invalid("Invalid device certificate fields.") }
        let spki = try tbs[offset + 5].children()
        guard spki.count == 2 else { throw invalid("Invalid certificate public key.") }
        let algorithm = try spki[0].children()
        let ecOID = Data([0x2A,0x86,0x48,0xCE,0x3D,0x02,0x01])
        if algorithm.first?.content == ecOID {
            guard algorithm.count == 2, algorithm[1].tag == 6,
                  [Data([0x2A,0x86,0x48,0xCE,0x3D,0x03,0x01,0x07]), Data([0x2B,0x81,0x04,0x00,0x22])].contains(algorithm[1].content) else {
                throw invalid("Unsupported identity curve.")
            }
        } else {
            guard algorithm.first?.tag == 6, algorithm.first?.content == Data([0x2A,0x86,0x48,0x86,0xF7,0x0D,0x01,0x01,0x01]) else {
                throw invalid("Unsupported certificate key algorithm.")
            }
        }
        let extensions = tbs.filter { $0.tag == 0xA3 }
        guard extensions.count == 1 else { throw invalid("The device certificate must support mutual TLS.") }
        let extensionSequence = try extensions[0].children()
        guard extensionSequence.count == 1, extensionSequence[0].tag == 0x30 else { throw invalid("Invalid certificate extensions.") }
        var purposes: Set<Data> = []; var seen: Set<Data> = []
        for item in try extensionSequence[0].children() {
            let fields = try item.children()
            guard item.tag == 0x30, fields.count == 2 || fields.count == 3, fields[0].tag == 6,
                  seen.insert(fields[0].content).inserted, fields.last?.tag == 4 else { throw invalid("Invalid certificate extension.") }
            if fields[0].content == Data([0x55,0x1D,0x25]) {
                let eku = try NearbyPairingDER.parse(fields.last!.content)
                guard eku.tag == 0x30 else { throw invalid("Invalid certificate usage.") }
                for purpose in try eku.children() {
                    guard purpose.tag == 6 else { throw invalid("Invalid certificate usage.") }
                    purposes.insert(purpose.content)
                }
            }
        }
        guard purposes.contains(Data([0x2B,6,1,5,5,7,3,1])), purposes.contains(Data([0x2B,6,1,5,5,7,3,2])) else {
            throw invalid("The device certificate must support mutual TLS.")
        }
    }
    fileprivate static func invalid(_ message: String) -> PairingError { PairingError(message) }
}

indirect enum NearbyPairingJSONValue: Equatable, Sendable {
    case object([String: NearbyPairingJSONValue]), string(String), number(String), boolean(Bool), null
    var string: String? { if case .string(let value) = self { return value }; return nil }
    var integer: Int? {
        guard case .number(let token) = self, !token.contains("."), !token.contains("e"), !token.contains("E") else { return nil }
        return Int(token)
    }
    var boolean: Bool? { if case .boolean(let value) = self { return value }; return nil }
}

// Foundation decoders discard duplicate fields. Scan before interpreting any
// identity or signature, including duplicate names written with JSON escapes.
private struct NearbyPairingJSONParser {
    let bytes: [UInt8]
    var offset = 0
    mutating func parse() throws -> [String: NearbyPairingJSONValue] {
        whitespace()
        guard case .object(let result) = try value(depth: 0) else { throw failure() }
        whitespace(); guard offset == bytes.count else { throw failure() }
        return result
    }
    mutating func whitespace() { while offset < bytes.count && [9,10,13,32].contains(bytes[offset]) { offset += 1 } }
    mutating func consume(_ byte: UInt8) -> Bool {
        guard offset < bytes.count && bytes[offset] == byte else { return false }; offset += 1; return true
    }
    mutating func value(depth: Int) throws -> NearbyPairingJSONValue {
        whitespace(); guard offset < bytes.count else { throw failure() }
        switch bytes[offset] {
        case 123:
            guard depth < 8 else { throw failure() }
            offset += 1; whitespace(); var result: [String: NearbyPairingJSONValue] = [:]
            if consume(125) { return .object(result) }
            while true {
                whitespace(); let key = try string(); whitespace()
                guard result[key] == nil, consume(58) else { throw failure() }
                result[key] = try value(depth: depth + 1); whitespace()
                if consume(125) { return .object(result) }
                guard consume(44) else { throw failure() }
            }
        case 34: return .string(try string())
        case 116: try literal("true"); return .boolean(true)
        case 102: try literal("false"); return .boolean(false)
        case 110: try literal("null"); return .null
        case 45, 48...57: return .number(try number())
        default: throw failure()
        }
    }
    mutating func literal(_ text: String) throws {
        let expected = Array(text.utf8)
        guard offset + expected.count <= bytes.count, Array(bytes[offset..<(offset + expected.count)]) == expected else { throw failure() }
        offset += expected.count
    }
    mutating func string() throws -> String {
        guard consume(34) else { throw failure() }; var output = [UInt8]()
        while offset < bytes.count {
            let byte = bytes[offset]; offset += 1
            if byte == 34 { guard let text = String(bytes: output, encoding: .utf8) else { throw failure() }; return text }
            guard byte >= 32 else { throw failure() }
            if byte != 92 { output.append(byte); continue }
            guard offset < bytes.count else { throw failure() }; let escape = bytes[offset]; offset += 1
            switch escape {
            case 34, 47, 92: output.append(escape)
            case 98: output.append(8)
            case 102: output.append(12)
            case 110: output.append(10)
            case 114: output.append(13)
            case 116: output.append(9)
            case 117:
                let first = try unicodeUnit(); var scalarValue = UInt32(first)
                if (0xD800...0xDBFF).contains(first) {
                    guard consume(92), consume(117) else { throw failure() }; let second = try unicodeUnit()
                    guard (0xDC00...0xDFFF).contains(second) else { throw failure() }
                    scalarValue = 0x10000 + (UInt32(first) - 0xD800) * 1024 + UInt32(second) - 0xDC00
                } else if (0xDC00...0xDFFF).contains(first) { throw failure() }
                guard let scalar = Unicode.Scalar(scalarValue) else { throw failure() }
                output.append(contentsOf: String(scalar).utf8)
            default: throw failure()
            }
        }
        throw failure()
    }
    mutating func unicodeUnit() throws -> UInt16 {
        guard offset + 4 <= bytes.count else { throw failure() }; var result: UInt16 = 0
        for _ in 0..<4 {
            let byte = bytes[offset]; offset += 1; let value: UInt16
            switch byte { case 48...57: value = UInt16(byte - 48); case 65...70: value = UInt16(byte - 55)
            case 97...102: value = UInt16(byte - 87); default: throw failure() }
            result = result * 16 + value
        }
        return result
    }
    mutating func number() throws -> String {
        let start = offset; _ = consume(45)
        if !consume(48) { guard digit(nonzero: true) else { throw failure() }; while digit() {} }
        if consume(46) { guard digit() else { throw failure() }; while digit() {} }
        if consume(101) || consume(69) { if !consume(43) { _ = consume(45) }; guard digit() else { throw failure() }; while digit() {} }
        return String(bytes: bytes[start..<offset], encoding: .utf8)!
    }
    mutating func digit(nonzero: Bool = false) -> Bool {
        guard offset < bytes.count, (nonzero ? 49...57 : 48...57).contains(bytes[offset]) else { return false }; offset += 1; return true
    }
    func failure() -> PairingError { PairingError("Invalid strict pairing JSON.") }
}

private struct NearbyPairingDER {
    let tag: UInt8
    let content: Data
    static func parse(_ data: Data) throws -> NearbyPairingDER {
        var reader = Reader(bytes: Array(data)); let result = try reader.next()
        guard reader.offset == reader.bytes.count else { throw PairingError("Trailing certificate or signature DER.") }
        return result
    }
    func children() throws -> [NearbyPairingDER] {
        var reader = Reader(bytes: Array(content)); var children: [NearbyPairingDER] = []
        while reader.offset < reader.bytes.count { children.append(try reader.next()) }
        return children
    }
    struct Reader {
        let bytes: [UInt8]; var offset = 0
        mutating func next() throws -> NearbyPairingDER {
            guard offset + 2 <= bytes.count else { throw PairingError("Truncated DER.") }
            let tag = bytes[offset]; let first = bytes[offset + 1]; offset += 2; var length = Int(first)
            guard tag & 31 != 31 else { throw PairingError("Unsupported DER tag.") }
            if first & 128 != 0 {
                let count = Int(first & 127)
                guard (1...4).contains(count), offset + count <= bytes.count, bytes[offset] != 0 else { throw PairingError("Invalid DER length.") }
                length = 0
                for _ in 0..<count { length = length * 256 + Int(bytes[offset]); offset += 1 }
                guard length >= 128 else { throw PairingError("Noncanonical DER length.") }
            }
            guard length <= bytes.count - offset else { throw PairingError("Truncated DER value.") }
            let content = Data(bytes[offset..<(offset + length)]); offset += length
            return NearbyPairingDER(tag: tag, content: content)
        }
    }
}
