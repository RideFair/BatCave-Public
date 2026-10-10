import Foundation
import CryptoKit
import Security

struct PairingCard: Codable, Equatable, Sendable {
    var schema = "batcave.pairing-card.v1"
    let deviceId: String
    var displayName: String
    let certificateDerBase64: String
    let certificateSha256: String

    static func fingerprint(_ der: Data) -> String {
        SHA256.hash(data: der).map { String(format: "%02X", $0) }.joined()
    }

    static func parse(_ data: Data, now: Date = Date()) throws -> PairingCard {
        guard data.count <= 16 * 1024, String(data: data, encoding: .utf8) != nil else {
            throw PairingError("The pairing card must be UTF-8 text smaller than 16 KB.")
        }
        let card = try JSONDecoder().decode(Self.self, from: data)
        func validLabel(_ text: String) -> Bool {
            !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && text.count <= 128
                && !text.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) })
        }
        guard card.schema == "batcave.pairing-card.v1", validLabel(card.deviceId), validLabel(card.displayName),
              card.certificateSha256.count == 64,
              let der = Data(base64Encoded: card.certificateDerBase64),
              let certificate = SecCertificateCreateWithData(nil, der as CFData),
              fingerprint(der).caseInsensitiveCompare(card.certificateSha256) == .orderedSame,
              certificateIsCurrent(certificate, now: now) else {
            throw PairingError("This card is invalid, expired, or has a different fingerprint.")
        }
        return card
    }

    func encoded() throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        return try encoder.encode(self)
    }

    func matches(_ certificate: SecCertificate, now: Date = Date()) -> Bool {
        let der = SecCertificateCopyData(certificate) as Data
        return Self.fingerprint(der).caseInsensitiveCompare(certificateSha256) == .orderedSame
            && certificateIsCurrent(certificate, now: now)
    }
}

func certificateIsCurrent(_ certificate: SecCertificate, now: Date = Date()) -> Bool {
    let keys = [kSecOIDX509V1ValidityNotBefore, kSecOIDX509V1ValidityNotAfter] as CFArray
    guard let values = SecCertificateCopyValues(certificate, keys, nil) as? [String: Any],
          let before = values[kSecOIDX509V1ValidityNotBefore as String] as? [String: Any],
          let after = values[kSecOIDX509V1ValidityNotAfter as String] as? [String: Any],
          let start = before[kSecPropertyKeyValue as String] as? NSNumber,
          let end = after[kSecPropertyKeyValue as String] as? NSNumber else { return false }
    let time = now.timeIntervalSinceReferenceDate
    return start.doubleValue <= time && time <= end.doubleValue
}

struct PairingError: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}

// The reference file contains public data only. The private signing key never
// leaves the macOS Keychain. A missing key is an error, not an automatic re-pair.
final class PairingIdentity: @unchecked Sendable {
    let card: PairingCard
    let identity: SecIdentity
    let keyTag: Data

    private init(card: PairingCard, identity: SecIdentity, keyTag: Data) {
        self.card = card; self.identity = identity; self.keyTag = keyTag
    }

    static func openOrCreate(at directory: URL, displayName: String) throws -> PairingIdentity {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true,
            attributes: [.posixPermissions: 0o700])
        let reference = directory.appendingPathComponent("identity.json")
        if FileManager.default.fileExists(atPath: reference.path) {
            let card = try PairingCard.parse(Data(contentsOf: reference))
            guard let certificate = SecCertificateCreateWithData(nil, Data(base64Encoded: card.certificateDerBase64)! as CFData) else {
                throw PairingError("The saved identity certificate is invalid.")
            }
            var identity: SecIdentity?
            guard SecIdentityCreateWithCertificate(nil, certificate, &identity) == errSecSuccess, let identity else {
                throw PairingError("This Mac's Keychain identity is unavailable. Unlock Keychain or restore the key. Your identity was not replaced.")
            }
            return PairingIdentity(card: card, identity: identity, keyTag: Data(card.deviceId.utf8))
        }
        let deviceId = "macos:" + UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
        let tag = Data(deviceId.utf8)
        // Legacy macOS Keychain supports this unsigned local preview without
        // fabricated access-group entitlements. Distribution signing is a gate.
        let attributes: [String: Any] = [
            kSecAttrKeyType as String: kSecAttrKeyTypeECSECPrimeRandom,
            kSecAttrKeySizeInBits as String: 256,
            kSecPrivateKeyAttrs as String: [kSecAttrIsPermanent as String: true,
                kSecAttrApplicationTag as String: tag, kSecAttrLabel as String: "BatCave Preview device identity",
                kSecAttrIsExtractable as String: false]
        ]
        var error: Unmanaged<CFError>?
        guard let key = SecKeyCreateRandomKey(attributes as CFDictionary, &error) else {
            throw error?.takeRetainedValue() as Error? ?? PairingError("Could not create a Keychain identity.")
        }
        var certificate: SecCertificate?
        do {
            let der = try LocalCertificate.create(key: key, commonName: "BatCave " + deviceId)
            guard let cert = SecCertificateCreateWithData(nil, der as CFData) else { throw PairingError("Could not create the device certificate.") }
            certificate = cert
            let add = SecItemAdd([kSecClass: kSecClassCertificate, kSecValueRef: cert,
                kSecAttrLabel: "BatCave Preview " + deviceId] as CFDictionary, nil)
            guard add == errSecSuccess else { throw PairingError("Could not save the device certificate (\(add)).") }
            var identity: SecIdentity?
            let result = SecIdentityCreateWithCertificate(nil, cert, &identity)
            guard result == errSecSuccess, let identity else { throw PairingError("Could not load the Keychain identity (\(result)).") }
            let card = PairingCard(deviceId: deviceId, displayName: displayName,
                certificateDerBase64: der.base64EncodedString(), certificateSha256: PairingCard.fingerprint(der))
            _ = try PairingCard.parse(card.encoded())
            try card.encoded().write(to: reference, options: .atomic)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: reference.path)
            return PairingIdentity(card: card, identity: identity, keyTag: tag)
        } catch {
            SecItemDelete([kSecClass: kSecClassKey, kSecAttrApplicationTag: tag] as CFDictionary)
            if let certificate { SecItemDelete([kSecClass: kSecClassCertificate, kSecValueRef: certificate] as CFDictionary) }
            throw error
        }
    }
}

// Minimal X.509 DER construction. Signing and key generation use Security's
// P-256/SHA-256 implementation, not application cryptographic primitives.
enum LocalCertificate {
    private static func der(_ tag: UInt8, _ content: Data) -> Data {
        let n = content.count
        var size = n
        var bytes = [UInt8]()
        if n < 128 { bytes = [UInt8(n)] } else {
            while size > 0 { bytes.insert(UInt8(size & 255), at: 0); size >>= 8 }
            bytes.insert(0x80 | UInt8(bytes.count), at: 0)
        }
        return Data([tag] + bytes) + content
    }
    private static func seq(_ parts: Data...) -> Data { der(0x30, parts.reduce(Data(), +)) }
    private static func oid(_ bytes: [UInt8]) -> Data { der(6, Data(bytes)) }
    private static func date(_ value: Date) -> Data {
        let formatter = DateFormatter(); formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(secondsFromGMT: 0); formatter.dateFormat = "yyyyMMddHHmmss'Z'"
        return der(0x18, Data(formatter.string(from: value).utf8))
    }
    static func create(key: SecKey, commonName: String, now: Date = Date()) throws -> Data {
        var error: Unmanaged<CFError>?
        guard let publicKey = SecKeyCopyPublicKey(key),
              let publicData = SecKeyCopyExternalRepresentation(publicKey, &error) as Data? else {
            throw PairingError("Could not read the public identity key.")
        }
        let signatureAlgorithm = seq(oid([0x2A,0x86,0x48,0xCE,0x3D,0x04,0x03,0x02]))
        let name = seq(der(0x31, seq(oid([0x55,0x04,0x03]), der(0x0C, Data(commonName.utf8)))))
        var serial = [UInt8](repeating: 0, count: 16)
        guard SecRandomCopyBytes(kSecRandomDefault, serial.count, &serial) == errSecSuccess else { throw PairingError("Random identity generation failed.") }
        serial[0] &= 0x7f; serial[0] |= 1
        let publicInfo = seq(seq(oid([0x2A,0x86,0x48,0xCE,0x3D,0x02,0x01]),
            oid([0x2A,0x86,0x48,0xCE,0x3D,0x03,0x01,0x07])), der(3, Data([0]) + publicData))
        let basic = seq(oid([0x55,0x1D,0x13]), der(1, Data([0xFF])), der(4, seq()))
        let usage = seq(oid([0x55,0x1D,0x0F]), der(1, Data([0xFF])), der(4, der(3, Data([7,0x80]))))
        let extended = seq(oid([0x55,0x1D,0x25]), der(4, seq(
            oid([0x2B,6,1,5,5,7,3,1]), oid([0x2B,6,1,5,5,7,3,2]))))
        let tbs = seq(der(0xA0, der(2, Data([2]))), der(2, Data(serial)), signatureAlgorithm,
            name, seq(date(now.addingTimeInterval(-300)), date(now.addingTimeInterval(3 * 365 * 86400))),
            name, publicInfo, der(0xA3, seq(basic, usage, extended)))
        guard let signature = SecKeyCreateSignature(key, .ecdsaSignatureMessageX962SHA256, tbs as CFData, &error) as Data? else {
            throw error?.takeRetainedValue() as Error? ?? PairingError("Could not sign the device certificate.")
        }
        return seq(tbs, signatureAlgorithm, der(3, Data([0]) + signature))
    }
}
