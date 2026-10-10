import Foundation

enum FileTransferProtocolError: Error, Equatable {
    case frameTooLarge, tooManyFrames, invalidJSON, invalidEnvelope, unknownFields, unsupportedMessage
    case invalidField(String), invalidOffer, duplicateIdentifier, invalidAcceptance, invalidOffset
}

enum FileTransferReason: String, CaseIterable, Sendable {
    case userDeclined = "user_declined", unsupported, invalidOffer = "invalid_offer"
    case invalidName = "invalid_name", tooLarge = "too_large", busy, timeout, cancelled
    case sourceChanged = "source_changed", destinationUnavailable = "destination_unavailable"
    case nameCollision = "name_collision", integrityFailure = "integrity_failure"
    case protocolError = "protocol_error", sessionClosed = "session_closed", ioError = "io_error"
}

struct FileTransferIconDescriptor: Equatable, Sendable {
    let width: Int
    let height: Int
    let sizeBytes: Int
    let sha256: String

    fileprivate var isValid: Bool {
        (1...128).contains(width) && (1...128).contains(height)
            && (1...32 * 1024).contains(sizeBytes) && FileTransferCodec.isSHA256(sha256)
    }
}

struct FileTransferEntry: Equatable, Sendable {
    let fileId: String
    let name: String
    let sizeBytes: UInt64
    let sha256: String
    let iconPreview: FileTransferIconDescriptor?
}

struct FileTransferOffer: Equatable, Sendable {
    let transferId: String
    let entries: [FileTransferEntry]

    func validatedTotalBytes() throws -> UInt64 {
        guard FileTransferCodec.isUUIDv4(transferId), (1...32).contains(entries.count) else {
            throw FileTransferProtocolError.invalidOffer
        }
        var ids = Set<String>(), total: UInt64 = 0
        for entry in entries {
            guard FileTransferCodec.isUUIDv4(entry.fileId), IncomingFileStore.isSafeFilename(entry.name),
                  entry.sizeBytes <= FileTransferCodec.maximumFileBytes, FileTransferCodec.isSHA256(entry.sha256) else {
                throw FileTransferProtocolError.invalidOffer
            }
            guard ids.insert(entry.fileId).inserted else { throw FileTransferProtocolError.duplicateIdentifier }
            let sum = total.addingReportingOverflow(entry.sizeBytes)
            guard !sum.overflow, sum.partialValue <= FileTransferCodec.maximumBatchBytes else {
                throw FileTransferProtocolError.invalidOffer
            }
            total = sum.partialValue
        }
        return total
    }
}

struct FileTransferAcceptance: Equatable, Sendable {
    let transferId: String
    let acceptedFileIds: [String]

    /// The session must additionally reject acceptance after cancellation or
    /// replacement. This pure helper proves a unique, nonempty offered subset.
    func validate(against offer: FileTransferOffer) throws {
        _ = try offer.validatedTotalBytes()
        let chosen = Set(acceptedFileIds), offered = Set(offer.entries.map(\.fileId))
        guard transferId == offer.transferId, !chosen.isEmpty, chosen.count == acceptedFileIds.count,
              chosen.isSubset(of: offered) else { throw FileTransferProtocolError.invalidAcceptance }
    }
}

struct FileTransferChunk: Equatable, Sendable {
    let transferId: String
    let fileId: String
    let offset: UInt64
    let data: Data

    func endOffset() throws -> UInt64 {
        let sum = offset.addingReportingOverflow(UInt64(data.count))
        guard !data.isEmpty, data.count <= FileTransferCodec.maximumChunkBytes,
              !sum.overflow, sum.partialValue <= FileTransferCodec.maximumFileBytes else {
            throw FileTransferProtocolError.invalidOffset
        }
        return sum.partialValue
    }

    func validate(for entry: FileTransferEntry, expectedOffset: UInt64) throws {
        guard fileId == entry.fileId, offset == expectedOffset, try endOffset() <= entry.sizeBytes else {
            throw FileTransferProtocolError.invalidOffset
        }
    }
}

struct FileTransferAcknowledgement: Equatable, Sendable {
    let transferId: String
    let fileId: String
    let nextOffset: UInt64

    /// Call only with the current outstanding chunk. A missing outstanding
    /// chunk (including a repeated ack) is rejected by the session state machine.
    func validate(for outstanding: FileTransferChunk) throws {
        guard transferId == outstanding.transferId, fileId == outstanding.fileId,
              nextOffset == (try outstanding.endOffset()) else { throw FileTransferProtocolError.invalidOffset }
    }
}

struct FileTransferIcon: Equatable, Sendable {
    let transferId: String
    let fileId: String
    /// nil represents an optional preview that was absent or malformed.
    let png: Data?

    /// Run on the bounded file worker. Validate original encoded metadata before
    /// PortableFileIcon normalizes the pixels and strips metadata. Any optional
    /// icon failure falls back; the file's content transfer remains valid.
    func resolve(declared descriptor: FileTransferIconDescriptor?) -> PortableFileIcon? {
        guard let descriptor, descriptor.isValid, let png,
              png.count == descriptor.sizeBytes, PortableFileIcon.digest(png) == descriptor.sha256,
              png.count >= 33 else { return nil }
        let bytes = [UInt8](png)
        guard Array(bytes.prefix(8)) == [137, 80, 78, 71, 13, 10, 26, 10],
              Array(bytes[8..<16]) == [0, 0, 0, 13, 73, 72, 68, 82] else { return nil }
        func dimension(_ at: Int) -> UInt32 {
            UInt32(bytes[at]) << 24 | UInt32(bytes[at + 1]) << 16 | UInt32(bytes[at + 2]) << 8 | UInt32(bytes[at + 3])
        }
        guard dimension(16) == UInt32(descriptor.width), dimension(20) == UInt32(descriptor.height),
              let icon = try? PortableFileIconCache.shared.resolve(png: png, sha256: descriptor.sha256),
              icon.width == descriptor.width, icon.height == descriptor.height else { return nil }
        return icon
    }
}

/// Tickets are deliberately redacted when DTOs are printed. The session still
/// owns cryptographic generation, monotonic expiry, atomic consumption and pins.
struct FileTransferTicket: Equatable, Sendable, CustomStringConvertible, CustomDebugStringConvertible {
    let encoded: String
    var description: String { "<redacted file channel ticket>" }
    var debugDescription: String { description }
}

enum FileTransferMessage: Equatable, Sendable {
    case channelRequest
    case channelReady(channelId: String, ticket: FileTransferTicket)
    case channelOpen(channelId: String, ticket: FileTransferTicket, deviceId: String)
    case channelOpened(channelId: String)
    case offer(FileTransferOffer)
    case icon(FileTransferIcon)
    case accept(FileTransferAcceptance)
    case reject(transferId: String, reason: FileTransferReason)
    case chunk(FileTransferChunk)
    case chunkAck(FileTransferAcknowledgement)
    case finish(transferId: String, fileId: String)
    case complete(transferId: String, fileId: String)
    case cancel(transferId: String, reason: FileTransferReason)
}

struct FileTransferEnvelope: Equatable, Sendable {
    let sourceDeviceId: String
    let messageId: String?
    let sentAtUtc: String?
    let message: FileTransferMessage
}

/// Codec only: no sockets, capabilities, consent, state, disk writes or native
/// input. The transport supplies the exact pinned source ID. Session checks must
/// enforce negotiated capabilities, ticket/session binding, active transfer IDs,
/// one batch/chunk in flight, deadlines and consent before using these DTOs.
enum FileTransferCodec {
    static let maximumFrameBytes = 256 * 1024 // excludes one terminating LF
    static let maximumChunkBytes = 32 * 1024
    static let maximumFileBytes: UInt64 = 1024 * 1024 * 1024
    static let maximumBatchBytes: UInt64 = 4 * 1024 * 1024 * 1024
    static let channelSchema = "batcave.file.channel.v1"
    static let bulkPort = 47872

    static func decodeFrame(_ frame: Data, expectedSourceDeviceId: String) throws -> FileTransferEnvelope {
        var body = frame
        if body.last == 10 { body.removeLast() }
        guard body.count <= maximumFrameBytes else { throw FileTransferProtocolError.frameTooLarge }
        guard !body.isEmpty, !body.contains(10), String(data: body, encoding: .utf8) != nil else {
            throw FileTransferProtocolError.invalidJSON
        }
        let root = try WireJSONParser(body).parse().objectValue()
        try fields(root, required: ["protocol", "type", "sourceDeviceId", "payload"], optional: ["messageId", "sentAtUtc"])
        guard try string(root, "protocol") == "batcave.input.v0" else { throw FileTransferProtocolError.invalidEnvelope }
        let source = try string(root, "sourceDeviceId")
        guard validDeviceId(source), source == expectedSourceDeviceId else { throw FileTransferProtocolError.invalidEnvelope }
        var messageId: String?, sentAtUtc: String?
        if root["messageId"] != nil {
            let value = try string(root, "messageId")
            guard value.utf8.count == 32, value.utf8.allSatisfy(isLowerHex) else { throw FileTransferProtocolError.invalidField("messageId") }
            messageId = value
        }
        if root["sentAtUtc"] != nil {
            let value = try string(root, "sentAtUtc")
            guard !value.isEmpty, value.utf8.count <= 64,
                  !value.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) else {
                throw FileTransferProtocolError.invalidField("sentAtUtc")
            }
            sentAtUtc = value
        }
        let payload = try root["payload"]!.objectValue()
        let type = try string(root, "type")
        let message: FileTransferMessage
        switch type {
        case "file.channelRequest":
            try fields(payload, required: ["channelSchema"]); try schema(payload)
            message = .channelRequest
        case "file.channelReady":
            try fields(payload, required: ["channelSchema", "channelId", "port", "ticket"]); try schema(payload)
            guard try integer(payload, "port", maximum: 65535) == bulkPort else { throw FileTransferProtocolError.invalidField("port") }
            message = .channelReady(channelId: try identifier(payload, "channelId"), ticket: try ticket(payload))
        case "file.channelOpen":
            try fields(payload, required: ["channelSchema", "channelId", "ticket", "deviceId"]); try schema(payload)
            guard try string(payload, "deviceId") == source else { throw FileTransferProtocolError.invalidField("deviceId") }
            message = .channelOpen(channelId: try identifier(payload, "channelId"), ticket: try ticket(payload), deviceId: source)
        case "file.channelOpened":
            try fields(payload, required: ["channelId"])
            message = .channelOpened(channelId: try identifier(payload, "channelId"))
        case "file.offer":
            try fields(payload, required: ["transferId", "entries"])
            guard case .array(let values) = payload["entries"], (1...32).contains(values.count) else { throw FileTransferProtocolError.invalidOffer }
            let entries = try values.map { value -> FileTransferEntry in
                let entry = try value.objectValue()
                try fields(entry, required: ["fileId", "name", "sizeBytes", "sha256"], optional: ["iconPreview"])
                let name = try string(entry, "name")
                guard IncomingFileStore.isSafeFilename(name) else { throw FileTransferProtocolError.invalidField("name") }
                return FileTransferEntry(fileId: try identifier(entry, "fileId"), name: name,
                    sizeBytes: try integer(entry, "sizeBytes", maximum: maximumFileBytes), sha256: try hash(entry, "sha256"),
                    iconPreview: try? iconDescriptor(entry["iconPreview"]))
            }
            let offer = FileTransferOffer(transferId: try identifier(payload, "transferId"), entries: entries)
            _ = try offer.validatedTotalBytes(); message = .offer(offer)
        case "file.accept":
            try fields(payload, required: ["transferId", "acceptedFileIds"])
            guard case .array(let values) = payload["acceptedFileIds"], (1...32).contains(values.count) else { throw FileTransferProtocolError.invalidAcceptance }
            let ids = try values.map { value -> String in
                guard case .string(let text) = value, isUUIDv4(text) else { throw FileTransferProtocolError.invalidField("acceptedFileIds") }
                return text
            }
            guard Set(ids).count == ids.count else { throw FileTransferProtocolError.duplicateIdentifier }
            message = .accept(FileTransferAcceptance(transferId: try identifier(payload, "transferId"), acceptedFileIds: ids))
        case "file.reject", "file.cancel":
            try fields(payload, required: ["transferId", "reason"])
            guard let reason = FileTransferReason(rawValue: try string(payload, "reason")) else { throw FileTransferProtocolError.invalidField("reason") }
            let id = try identifier(payload, "transferId")
            message = type == "file.reject" ? .reject(transferId: id, reason: reason) : .cancel(transferId: id, reason: reason)
        case "file.chunk":
            try fields(payload, required: ["transferId", "fileId", "offset", "data"])
            let chunk = FileTransferChunk(transferId: try identifier(payload, "transferId"), fileId: try identifier(payload, "fileId"),
                offset: try integer(payload, "offset", maximum: maximumFileBytes), data: try base64(string(payload, "data"), maximum: maximumChunkBytes))
            _ = try chunk.endOffset(); message = .chunk(chunk)
        case "file.chunkAck":
            try fields(payload, required: ["transferId", "fileId", "nextOffset"])
            message = .chunkAck(FileTransferAcknowledgement(transferId: try identifier(payload, "transferId"), fileId: try identifier(payload, "fileId"),
                nextOffset: try integer(payload, "nextOffset", maximum: maximumFileBytes)))
        case "file.finish", "file.complete":
            try fields(payload, required: ["transferId", "fileId"])
            let transfer = try identifier(payload, "transferId"), file = try identifier(payload, "fileId")
            message = type == "file.finish" ? .finish(transferId: transfer, fileId: file) : .complete(transferId: transfer, fileId: file)
        case "file.icon":
            try fields(payload, required: ["transferId", "fileId"], optional: ["iconPreview"])
            let png = try? iconData(payload["iconPreview"])
            message = .icon(FileTransferIcon(transferId: try identifier(payload, "transferId"), fileId: try identifier(payload, "fileId"), png: png))
        default: throw FileTransferProtocolError.unsupportedMessage
        }
        return FileTransferEnvelope(sourceDeviceId: source, messageId: messageId, sentAtUtc: sentAtUtc, message: message)
    }

    /// Emits the normal v0 metadata and exactly one LF. Revalidation means even
    /// manually constructed DTOs cannot emit invalid core fields or oversize data.
    static func encode(_ message: FileTransferMessage, sourceDeviceId: String,
                       messageId: String? = nil, sentAtUtc: String? = nil) throws -> Data {
        let (type, payload) = try payload(for: message)
        let formatter = ISO8601DateFormatter(); formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let root: [String: Any] = ["protocol": "batcave.input.v0", "type": type, "sourceDeviceId": sourceDeviceId,
            "messageId": messageId ?? UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased(),
            "sentAtUtc": sentAtUtc ?? formatter.string(from: Date()), "payload": payload]
        var frame = try JSONSerialization.data(withJSONObject: root, options: [.sortedKeys])
        _ = try decodeFrame(frame, expectedSourceDeviceId: sourceDeviceId)
        frame.append(10); return frame
    }

    static func isUUIDv4(_ value: String) -> Bool {
        let bytes = Array(value.utf8)
        guard bytes.count == 36, bytes[14] == 52, [56, 57, 97, 98].contains(bytes[19]) else { return false }
        for index in bytes.indices {
            if [8, 13, 18, 23].contains(index) {
                if bytes[index] != 45 { return false }
            } else if !isLowerHex(bytes[index]) { return false }
        }
        return true
    }

    static func isSHA256(_ value: String) -> Bool { value.utf8.count == 64 && value.utf8.allSatisfy(isLowerHex) }
    private static func isLowerHex(_ byte: UInt8) -> Bool { (48...57).contains(byte) || (97...102).contains(byte) }
    private static func validDeviceId(_ value: String) -> Bool {
        !value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && value.count <= 128
            && !value.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) })
    }
    private static func fields(_ value: [String: WireJSON], required: Set<String>, optional: Set<String> = []) throws {
        let keys = Set(value.keys)
        guard required.isSubset(of: keys) else { throw FileTransferProtocolError.invalidEnvelope }
        guard keys.isSubset(of: required.union(optional)) else { throw FileTransferProtocolError.unknownFields }
    }
    private static func string(_ value: [String: WireJSON], _ key: String) throws -> String {
        guard case .string(let result) = value[key] else { throw FileTransferProtocolError.invalidField(key) }
        return result
    }
    private static func integer(_ value: [String: WireJSON], _ key: String, maximum: UInt64) throws -> UInt64 {
        guard case .number(let token) = value[key], token.utf8.allSatisfy({ (48...57).contains($0) }),
              let result = UInt64(token), result <= maximum else { throw FileTransferProtocolError.invalidField(key) }
        return result
    }
    private static func identifier(_ value: [String: WireJSON], _ key: String) throws -> String {
        let result = try string(value, key)
        guard isUUIDv4(result) else { throw FileTransferProtocolError.invalidField(key) }; return result
    }
    private static func hash(_ value: [String: WireJSON], _ key: String) throws -> String {
        let result = try string(value, key)
        guard isSHA256(result) else { throw FileTransferProtocolError.invalidField(key) }; return result
    }
    private static func schema(_ value: [String: WireJSON]) throws {
        guard try string(value, "channelSchema") == channelSchema else { throw FileTransferProtocolError.invalidField("channelSchema") }
    }
    private static func ticket(_ value: [String: WireJSON]) throws -> FileTransferTicket {
        let text = try string(value, "ticket")
        guard text.utf8.count == 43, text.utf8.allSatisfy({ (65...90).contains($0) || (97...122).contains($0) || (48...57).contains($0) || $0 == 45 || $0 == 95 }),
              let bytes = Data(base64Encoded: text.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/") + "="),
              bytes.count == 32,
              bytes.base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "") == text else {
            throw FileTransferProtocolError.invalidField("ticket")
        }
        return FileTransferTicket(encoded: text)
    }
    private static func base64(_ text: String, maximum: Int) throws -> Data {
        guard text.utf8.count <= ((maximum + 2) / 3) * 4,
              let data = Data(base64Encoded: text), !data.isEmpty, data.count <= maximum,
              data.base64EncodedString() == text else { throw FileTransferProtocolError.invalidField("data") }
        return data
    }
    private static func iconDescriptor(_ value: WireJSON?) throws -> FileTransferIconDescriptor {
        guard let value else { throw FileTransferProtocolError.invalidField("iconPreview") }
        let object = try value.objectValue()
        try fields(object, required: ["format", "width", "height", "sizeBytes", "sha256"])
        guard try string(object, "format") == "image/png" else { throw FileTransferProtocolError.invalidField("format") }
        let descriptor = FileTransferIconDescriptor(width: Int(try integer(object, "width", maximum: 128)),
            height: Int(try integer(object, "height", maximum: 128)), sizeBytes: Int(try integer(object, "sizeBytes", maximum: 32 * 1024)),
            sha256: try hash(object, "sha256"))
        guard descriptor.isValid else { throw FileTransferProtocolError.invalidField("iconPreview") }
        return descriptor
    }
    private static func iconData(_ value: WireJSON?) throws -> Data {
        guard let value else { throw FileTransferProtocolError.invalidField("iconPreview") }
        let object = try value.objectValue(); try fields(object, required: ["format", "data"])
        guard try string(object, "format") == "image/png" else { throw FileTransferProtocolError.invalidField("format") }
        return try base64(string(object, "data"), maximum: 32 * 1024)
    }

    private static func payload(for message: FileTransferMessage) throws -> (String, [String: Any]) {
        func pair(_ transfer: String, _ file: String) -> [String: Any] { ["transferId": transfer, "fileId": file] }
        switch message {
        case .channelRequest: return ("file.channelRequest", ["channelSchema": channelSchema])
        case .channelReady(let id, let ticket):
            return ("file.channelReady", ["channelSchema": channelSchema, "channelId": id, "port": bulkPort, "ticket": ticket.encoded])
        case .channelOpen(let id, let ticket, let device):
            return ("file.channelOpen", ["channelSchema": channelSchema, "channelId": id, "ticket": ticket.encoded, "deviceId": device])
        case .channelOpened(let id): return ("file.channelOpened", ["channelId": id])
        case .offer(let offer):
            _ = try offer.validatedTotalBytes()
            let entries: [[String: Any]] = offer.entries.map { entry in
                var object: [String: Any] = ["fileId": entry.fileId, "name": entry.name, "sizeBytes": entry.sizeBytes, "sha256": entry.sha256]
                if let icon = entry.iconPreview, icon.isValid {
                    object["iconPreview"] = ["format": "image/png", "width": icon.width, "height": icon.height, "sizeBytes": icon.sizeBytes, "sha256": icon.sha256]
                }
                return object
            }
            return ("file.offer", ["transferId": offer.transferId, "entries": entries])
        case .icon(let icon):
            guard let png = icon.png, !png.isEmpty, png.count <= 32 * 1024 else { throw FileTransferProtocolError.invalidField("iconPreview") }
            var object = pair(icon.transferId, icon.fileId); object["iconPreview"] = ["format": "image/png", "data": png.base64EncodedString()]
            return ("file.icon", object)
        case .accept(let acceptance): return ("file.accept", ["transferId": acceptance.transferId, "acceptedFileIds": acceptance.acceptedFileIds])
        case .reject(let id, let reason): return ("file.reject", ["transferId": id, "reason": reason.rawValue])
        case .chunk(let chunk):
            _ = try chunk.endOffset()
            var object = pair(chunk.transferId, chunk.fileId); object["offset"] = chunk.offset; object["data"] = chunk.data.base64EncodedString()
            return ("file.chunk", object)
        case .chunkAck(let ack):
            var object = pair(ack.transferId, ack.fileId); object["nextOffset"] = ack.nextOffset; return ("file.chunkAck", object)
        case .finish(let id, let file): return ("file.finish", pair(id, file))
        case .complete(let id, let file): return ("file.complete", pair(id, file))
        case .cancel(let id, let reason): return ("file.cancel", ["transferId": id, "reason": reason.rawValue])
        }
    }
}

/// One incomplete frame and each ingress chunk are bounded. It can receive
/// fragmented/coalesced lines; consumers decode returned frames immediately.
struct FileTransferFrameReader: Sendable {
    static let maximumReturnedFrames = 4096
    private var pending = Data()
    var bufferedBytes: Int { pending.count }
    mutating func append(_ bytes: Data) throws -> [Data] {
        guard bytes.count <= FileTransferCodec.maximumFrameBytes + 1 else {
            pending.removeAll(); throw FileTransferProtocolError.frameTooLarge
        }
        var frames: [Data] = []
        do {
            for byte in bytes {
                if byte == 10 {
                    guard !pending.isEmpty else { throw FileTransferProtocolError.invalidJSON }
                    guard frames.count < Self.maximumReturnedFrames else { throw FileTransferProtocolError.tooManyFrames }
                    frames.append(pending); pending.removeAll(keepingCapacity: true)
                } else {
                    guard pending.count < FileTransferCodec.maximumFrameBytes else { throw FileTransferProtocolError.frameTooLarge }
                    pending.append(byte)
                }
            }
            return frames
        } catch { pending.removeAll(); throw error }
    }
    mutating func reset() { pending.removeAll() }
}

// A small bounded JSON grammar parser preserves numeric token types and rejects
// duplicate keys, including escaped equivalents. NSNumber coercion would accept
// booleans, rounded integers or integral-looking floating point in some decoders.
private indirect enum WireJSON {
    case object([String: WireJSON]), array([WireJSON]), string(String), number(String), bool, null
    func objectValue() throws -> [String: WireJSON] {
        guard case .object(let value) = self else { throw FileTransferProtocolError.invalidEnvelope }; return value
    }
}

private final class WireJSONParser {
    private let bytes: [UInt8]
    private var at = 0
    init(_ data: Data) { bytes = Array(data) }
    func parse() throws -> WireJSON {
        let result = try value(depth: 0); whitespace()
        guard at == bytes.count else { throw FileTransferProtocolError.invalidJSON }; return result
    }
    private func whitespace() { while at < bytes.count && [9, 10, 13, 32].contains(bytes[at]) { at += 1 } }
    private func take(_ byte: UInt8) -> Bool {
        whitespace(); guard at < bytes.count, bytes[at] == byte else { return false }; at += 1; return true
    }
    private func value(depth: Int) throws -> WireJSON {
        whitespace(); guard depth <= 32, at < bytes.count else { throw FileTransferProtocolError.invalidJSON }
        switch bytes[at] {
        case 123:
            at += 1; var object: [String: WireJSON] = [:]
            if take(125) { return .object(object) }
            repeat {
                whitespace(); let key = try text()
                guard object[key] == nil, take(58) else { throw FileTransferProtocolError.invalidJSON }
                object[key] = try value(depth: depth + 1)
                if take(125) { return .object(object) }
                guard take(44) else { throw FileTransferProtocolError.invalidJSON }
            } while true
        case 91:
            at += 1; var array: [WireJSON] = []
            if take(93) { return .array(array) }
            repeat {
                array.append(try value(depth: depth + 1))
                if take(93) { return .array(array) }
                guard take(44) else { throw FileTransferProtocolError.invalidJSON }
            } while true
        case 34: return .string(try text())
        case 116: try literal("true"); return .bool
        case 102: try literal("false"); return .bool
        case 110: try literal("null"); return .null
        case 45, 48...57: return .number(try number())
        default: throw FileTransferProtocolError.invalidJSON
        }
    }
    private func literal(_ text: String) throws {
        let expected = Array(text.utf8)
        guard at + expected.count <= bytes.count, Array(bytes[at..<(at + expected.count)]) == expected else {
            throw FileTransferProtocolError.invalidJSON
        }
        at += expected.count
    }
    private func text() throws -> String {
        guard at < bytes.count, bytes[at] == 34 else { throw FileTransferProtocolError.invalidJSON }
        let start = at; at += 1
        while at < bytes.count {
            if bytes[at] == 34 {
                at += 1
                guard let text = (try? JSONSerialization.jsonObject(with: Data(bytes[start..<at]), options: [.fragmentsAllowed])) as? String else {
                    throw FileTransferProtocolError.invalidJSON
                }
                return text
            }
            guard bytes[at] >= 32 else { throw FileTransferProtocolError.invalidJSON }
            if bytes[at] == 92 {
                at += 1; guard at < bytes.count else { throw FileTransferProtocolError.invalidJSON }
                if bytes[at] == 117 {
                    guard at + 4 < bytes.count, bytes[(at + 1)...(at + 4)].allSatisfy({
                        (48...57).contains($0) || (65...70).contains($0) || (97...102).contains($0)
                    }) else { throw FileTransferProtocolError.invalidJSON }
                    at += 4
                } else if ![34, 47, 92, 98, 102, 110, 114, 116].contains(bytes[at]) { throw FileTransferProtocolError.invalidJSON }
            }
            at += 1
        }
        throw FileTransferProtocolError.invalidJSON
    }
    private func number() throws -> String {
        let start = at
        if bytes[at] == 45 { at += 1 }
        guard at < bytes.count else { throw FileTransferProtocolError.invalidJSON }
        if bytes[at] == 48 { at += 1 }
        else {
            guard (49...57).contains(bytes[at]) else { throw FileTransferProtocolError.invalidJSON }
            while at < bytes.count && (48...57).contains(bytes[at]) { at += 1 }
        }
        if at < bytes.count && bytes[at] == 46 { at += 1; try digits() }
        if at < bytes.count && [69, 101].contains(bytes[at]) {
            at += 1
            if at < bytes.count && [43, 45].contains(bytes[at]) { at += 1 }
            try digits()
        }
        return String(decoding: bytes[start..<at], as: UTF8.self)
    }
    private func digits() throws {
        let start = at
        while at < bytes.count && (48...57).contains(bytes[at]) { at += 1 }
        guard at > start else { throw FileTransferProtocolError.invalidJSON }
    }
}
