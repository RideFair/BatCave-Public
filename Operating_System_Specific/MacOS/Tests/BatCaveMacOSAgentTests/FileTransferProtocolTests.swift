import XCTest
import ImageIO
import UniformTypeIdentifiers
@testable import BatCaveMacOSAgent

final class FileTransferProtocolTests: XCTestCase {
    private let transfer = "22222222-2222-4222-8222-222222222222"
    private let file = "33333333-3333-4333-8333-333333333333"
    private let other = "44444444-4444-4444-8444-444444444444"
    private let channel = "11111111-1111-4111-8111-111111111111"
    private let source = "approved-device-id"
    private let emptyDigest = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"

    func testAllMessageShapesRoundTripWithNormalV0Metadata() throws {
        let entry = FileTransferEntry(fileId: file, name: "example.txt", sizeBytes: 0, sha256: emptyDigest, iconPreview: nil)
        let ticket = FileTransferTicket(encoded: String(repeating: "A", count: 43))
        let messages: [FileTransferMessage] = [
            .channelRequest, .channelReady(channelId: channel, ticket: ticket),
            .channelOpen(channelId: channel, ticket: ticket, deviceId: source), .channelOpened(channelId: channel),
            .offer(FileTransferOffer(transferId: transfer, entries: [entry])),
            .accept(FileTransferAcceptance(transferId: transfer, acceptedFileIds: [file])),
            .reject(transferId: transfer, reason: .userDeclined),
            .chunk(FileTransferChunk(transferId: transfer, fileId: file, offset: 0, data: Data("hello".utf8))),
            .chunkAck(FileTransferAcknowledgement(transferId: transfer, fileId: file, nextOffset: 5)),
            .finish(transferId: transfer, fileId: file), .complete(transferId: transfer, fileId: file),
            .cancel(transferId: transfer, reason: .sessionClosed),
            .icon(FileTransferIcon(transferId: transfer, fileId: file, png: Data([1, 2, 3])))
        ]
        for message in messages {
            let encoded = try FileTransferCodec.encode(message, sourceDeviceId: source)
            XCTAssertEqual(encoded.last, 10)
            XCTAssertEqual(encoded.filter { $0 == 10 }.count, 1)
            let parsed = try FileTransferCodec.decodeFrame(encoded, expectedSourceDeviceId: source)
            XCTAssertEqual(parsed.message, message)
            XCTAssertEqual(parsed.messageId?.utf8.count, 32)
            XCTAssertFalse(try XCTUnwrap(parsed.sentAtUtc).isEmpty)
        }
        XCTAssertFalse(String(describing: messages[1]).contains(ticket.encoded))
    }

    func testCanonicalUUIDv4AndLowercaseHashRequirements() throws {
        for id in ["ABCDEFAB-CDEF-4ABC-8ABC-ABCDEFABCDEF", file.replacingOccurrences(of: "-", with: ""),
                   "33333333-3333-1333-8333-333333333333", "33333333-3333-4333-7333-333333333333",
                   "33333333-3333-4333-c333-333333333333", "../../file", ""] {
            var entry = entryObject(); entry["fileId"] = id
            XCTAssertThrowsError(try decode("file.offer", ["transferId": transfer, "entries": [entry]]), id)
        }
        for value in [emptyDigest.uppercased(), String(repeating: "g", count: 64), String(repeating: "0", count: 63)] {
            var entry = entryObject(); entry["sha256"] = value
            XCTAssertThrowsError(try decode("file.offer", ["transferId": transfer, "entries": [entry]]))
        }
        for variant in ["8", "9", "a", "b"] {
            XCTAssertTrue(FileTransferCodec.isUUIDv4("12345678-1234-4abc-" + variant + "def-123456789abc"))
        }
    }

    func testExactIntegerTokensRejectBooleansFloatsStringsNegativesAndOverflow() throws {
        for token in ["true", "false", "1.0", "1e0", "\"1\"", "-1", "-0", "18446744073709551616", "null"] {
            let payload = "{\"transferId\":\"\(transfer)\",\"entries\":[{\"fileId\":\"\(file)\",\"name\":\"file.txt\",\"sizeBytes\":\(token),\"sha256\":\"\(emptyDigest)\"}]}"
            XCTAssertThrowsError(try decodeRaw("file.offer", payload), token)
            let chunk = "{\"transferId\":\"\(transfer)\",\"fileId\":\"\(file)\",\"offset\":\(token),\"data\":\"AA==\"}"
            XCTAssertThrowsError(try decodeRaw("file.chunk", chunk), token)
            let ack = "{\"transferId\":\"\(transfer)\",\"fileId\":\"\(file)\",\"nextOffset\":\(token)}"
            XCTAssertThrowsError(try decodeRaw("file.chunkAck", ack), token)
        }
        for malformed in ["01", "1.", "1e+", "+1"] {
            XCTAssertThrowsError(try decodeRaw("file.chunkAck", "{\"transferId\":\"\(transfer)\",\"fileId\":\"\(file)\",\"nextOffset\":\(malformed)}"))
        }
    }

    func testOfferLimitsDuplicateIDsAndSafeNames() throws {
        let oneGiB = FileTransferCodec.maximumFileBytes
        let maxEntries: [[String: Any]] = (0..<32).map { index in
            entryObject(id: id(index), size: index < 4 ? oneGiB : 0)
        }
        guard case .offer(let valid) = try decode("file.offer", ["transferId": transfer, "entries": maxEntries]).message else {
            return XCTFail("Expected offer")
        }
        XCTAssertEqual(try valid.validatedTotalBytes(), 4 * oneGiB)
        for entries in [[], maxEntries + [entryObject(id: id(33))], [entryObject(), entryObject()],
                        [entryObject(size: oneGiB + 1)], (0..<5).map { entryObject(id: id($0), size: oneGiB) }] {
            XCTAssertThrowsError(try decode("file.offer", ["transferId": transfer, "entries": entries]))
        }
        for name in ["../outside", "a/b", "a\\b", "C:file", "CON.txt", "LPT1", "bad\0name", "trailing.", "trailing ", String(repeating: "x", count: 241)] {
            var entry = entryObject(); entry["name"] = name
            XCTAssertThrowsError(try decode("file.offer", ["transferId": transfer, "entries": [entry]]), name)
        }
        var unicode = entryObject(); unicode["name"] = String(repeating: "🦇", count: 59) + ".txt"
        XCTAssertNoThrow(try decode("file.offer", ["transferId": transfer, "entries": [unicode]]))
    }

    func testCanonicalPaddedBase64AndChunkBounds() throws {
        for data in [Data([0]), Data([0, 1]), Data([0, 1, 2]), Data(repeating: 255, count: 32 * 1024)] {
            let parsed = try decode("file.chunk", ["transferId": transfer, "fileId": file, "offset": 0, "data": data.base64EncodedString()])
            guard case .chunk(let chunk) = parsed.message else { return XCTFail("Expected chunk") }
            XCTAssertEqual(chunk.data, data)
        }
        for text in ["", "YQ", "AA=", "AB==", "_w==", "AA==\n", " AA==", Data(repeating: 0, count: 32 * 1024 + 1).base64EncodedString()] {
            XCTAssertThrowsError(try decode("file.chunk", ["transferId": transfer, "fileId": file, "offset": 0, "data": text]), text.prefix(8).description)
        }
        XCTAssertThrowsError(try decode("file.chunk", ["transferId": transfer, "fileId": file,
            "offset": FileTransferCodec.maximumFileBytes, "data": "AA=="]))
    }

    func testAcceptanceAndAcknowledgementContextValidation() throws {
        let entry = FileTransferEntry(fileId: file, name: "five.txt", sizeBytes: 5, sha256: emptyDigest, iconPreview: nil)
        let offer = FileTransferOffer(transferId: transfer, entries: [entry])
        try FileTransferAcceptance(transferId: transfer, acceptedFileIds: [file]).validate(against: offer)
        for acceptance in [FileTransferAcceptance(transferId: other, acceptedFileIds: [file]),
                           FileTransferAcceptance(transferId: transfer, acceptedFileIds: []),
                           FileTransferAcceptance(transferId: transfer, acceptedFileIds: [other]),
                           FileTransferAcceptance(transferId: transfer, acceptedFileIds: [file, file])] {
            XCTAssertThrowsError(try acceptance.validate(against: offer))
        }
        XCTAssertThrowsError(try decode("file.accept", ["transferId": transfer, "acceptedFileIds": [file, file]]))
        XCTAssertThrowsError(try decode("file.accept", ["transferId": transfer, "acceptedFileIds": []]))
        let chunk = FileTransferChunk(transferId: transfer, fileId: file, offset: 0, data: Data("hello".utf8))
        try chunk.validate(for: entry, expectedOffset: 0)
        try FileTransferAcknowledgement(transferId: transfer, fileId: file, nextOffset: 5).validate(for: chunk)
        for ack in [FileTransferAcknowledgement(transferId: transfer, fileId: file, nextOffset: 4),
                    FileTransferAcknowledgement(transferId: other, fileId: file, nextOffset: 5),
                    FileTransferAcknowledgement(transferId: transfer, fileId: other, nextOffset: 5)] {
            XCTAssertThrowsError(try ack.validate(for: chunk))
        }
        XCTAssertThrowsError(try chunk.validate(for: entry, expectedOffset: 1))
        XCTAssertThrowsError(try chunk.validate(for: FileTransferEntry(fileId: file, name: "small", sizeBytes: 4, sha256: emptyDigest, iconPreview: nil), expectedOffset: 0))
        XCTAssertThrowsError(try FileTransferChunk(transferId: transfer, fileId: file, offset: UInt64.max, data: Data([0])).endOffset())
    }

    func testChannelSchemaPortCanonicalTicketAndIdentityMatch() throws {
        let ticket = String(repeating: "A", count: 43)
        let ready: [String: Any] = ["channelSchema": FileTransferCodec.channelSchema, "channelId": channel, "port": 47872, "ticket": ticket]
        XCTAssertNoThrow(try decode("file.channelReady", ready))
        for (key, value) in [("channelSchema", "wrong" as Any), ("port", 47871 as Any), ("port", true as Any),
                             ("ticket", ticket + "=" as Any), ("ticket", String(repeating: "A", count: 42) + "B" as Any),
                             ("ticket", String(repeating: "+", count: 43) as Any)] {
            var wrong = ready; wrong[key] = value
            XCTAssertThrowsError(try decode("file.channelReady", wrong))
        }
        XCTAssertThrowsError(try decode("file.channelOpen", ["channelSchema": FileTransferCodec.channelSchema,
            "channelId": channel, "ticket": ticket, "deviceId": "impostor"]))
    }

    func testKnownReasonCodesOnlyAndUnknownCoreFieldsRejected() throws {
        for reason in FileTransferReason.allCases {
            XCTAssertNoThrow(try decode("file.cancel", ["transferId": transfer, "reason": reason.rawValue]))
        }
        XCTAssertThrowsError(try decode("file.reject", ["transferId": transfer, "reason": "/private/source/path"]))
        XCTAssertThrowsError(try decode("file.finish", ["transferId": transfer, "fileId": file, "destination": "/tmp/peer-path"]))
        var entry = entryObject(); entry["path"] = "../outside"
        XCTAssertThrowsError(try decode("file.offer", ["transferId": transfer, "entries": [entry]]))
        var root = envelopeObject("file.channelRequest", ["channelSchema": FileTransferCodec.channelSchema])
        root["unknown"] = true
        XCTAssertThrowsError(try FileTransferCodec.decodeFrame(JSONSerialization.data(withJSONObject: root), expectedSourceDeviceId: source))
        root = envelopeObject("file.channelRequest", ["channelSchema": FileTransferCodec.channelSchema])
        root["sourceDeviceId"] = "impostor"
        XCTAssertThrowsError(try FileTransferCodec.decodeFrame(JSONSerialization.data(withJSONObject: root), expectedSourceDeviceId: source))
    }

    func testDuplicateAndEscapedEquivalentJSONKeysAreRejected() throws {
        for key in ["transferId", "\\u0074ransferId"] {
            let payload = "{\"transferId\":\"\(transfer)\",\"\(key)\":\"\(other)\",\"fileId\":\"\(file)\"}"
            XCTAssertThrowsError(try decodeRaw("file.finish", payload)) { XCTAssertEqual($0 as? FileTransferProtocolError, .invalidJSON) }
        }
        let raw = "{\"protocol\":\"batcave.input.v0\",\"type\":\"file.channelRequest\",\"sourceDeviceId\":\"\(source)\",\"sourceDeviceId\":\"impostor\",\"payload\":{\"channelSchema\":\"\(FileTransferCodec.channelSchema)\"}}"
        XCTAssertThrowsError(try FileTransferCodec.decodeFrame(Data(raw.utf8), expectedSourceDeviceId: source))
    }

    func testOptionalIconErrorsFallBackWithoutRejectingValidOffer() throws {
        let invalid: [Any] = [NSNull(), true, "wrong", ["format": "image/png", "width": true],
            ["format": "image/png", "width": 129, "height": 1, "sizeBytes": 1, "sha256": emptyDigest],
            ["format": "image/png", "width": 1, "height": 1, "sizeBytes": 0, "sha256": emptyDigest],
            ["format": "image/png", "width": 1, "height": 1, "sizeBytes": 1, "sha256": emptyDigest.uppercased()],
            ["format": "image/svg+xml", "width": 1, "height": 1, "sizeBytes": 1, "sha256": emptyDigest]]
        for preview in invalid {
            var entry = entryObject(); entry["iconPreview"] = preview
            guard case .offer(let offer) = try decode("file.offer", ["transferId": transfer, "entries": [entry]]).message else { return XCTFail("Expected offer") }
            XCTAssertNil(offer.entries[0].iconPreview)
        }
        for preview in invalid + [["format": "image/png", "data": "not base64"],
                                  ["format": "image/png", "data": "AA==", "unknown": true]] {
            guard case .icon(let icon) = try decode("file.icon", ["transferId": transfer, "fileId": file, "iconPreview": preview]).message else { return XCTFail("Expected icon") }
            XCTAssertNil(icon.png)
        }
        guard case .icon(let absent) = try decode("file.icon", ["transferId": transfer, "fileId": file]).message else { return XCTFail("Expected icon") }
        XCTAssertNil(absent.png)
        XCTAssertThrowsError(try decode("file.icon", ["transferId": "bad-id", "fileId": file, "iconPreview": NSNull()]))
    }

    func testIconExactOriginalDescriptorAndPNGValidation() throws {
        let png = try makePNG(), digest = PortableFileIcon.digest(png)
        let descriptor = FileTransferIconDescriptor(width: 32, height: 24, sizeBytes: png.count, sha256: digest)
        let raw = try decode("file.icon", ["transferId": transfer, "fileId": file,
            "iconPreview": ["format": "image/png", "data": png.base64EncodedString()]])
        guard case .icon(let icon) = raw.message else { return XCTFail("Expected icon") }
        let resolved = try XCTUnwrap(icon.resolve(declared: descriptor))
        XCTAssertEqual(resolved.width, 32); XCTAssertEqual(resolved.height, 24)
        XCTAssertNil(icon.resolve(declared: nil))
        for bad in [FileTransferIconDescriptor(width: 31, height: 24, sizeBytes: png.count, sha256: digest),
                    FileTransferIconDescriptor(width: 32, height: 24, sizeBytes: png.count + 1, sha256: digest),
                    FileTransferIconDescriptor(width: 32, height: 24, sizeBytes: png.count, sha256: emptyDigest)] {
            XCTAssertNil(icon.resolve(declared: bad))
        }
        var corrupt = png; corrupt[corrupt.count - 1] ^= 1
        let badIcon = FileTransferIcon(transferId: transfer, fileId: file, png: corrupt)
        XCTAssertNil(badIcon.resolve(declared: FileTransferIconDescriptor(width: 32, height: 24,
            sizeBytes: corrupt.count, sha256: PortableFileIcon.digest(corrupt))))
    }

    func testFragmentedAndCoalescedNDJSONAndExactFrameBoundaries() throws {
        let frame = try FileTransferCodec.encode(.channelRequest, sourceDeviceId: source)
        var reader = FileTransferFrameReader()
        XCTAssertEqual(try reader.append(frame.prefix(7)), [])
        let frames = try reader.append(Data(frame.dropFirst(7)) + frame)
        XCTAssertEqual(frames.count, 2); XCTAssertEqual(reader.bufferedBytes, 0)
        for data in frames { XCTAssertEqual(try FileTransferCodec.decodeFrame(data, expectedSourceDeviceId: source).message, .channelRequest) }
        var exact = Data(frame.dropLast())
        exact.append(Data(repeating: 32, count: FileTransferCodec.maximumFrameBytes - exact.count))
        XCTAssertNoThrow(try FileTransferCodec.decodeFrame(exact + Data([10]), expectedSourceDeviceId: source))
        XCTAssertEqual(try reader.append(exact).count, 0)
        XCTAssertEqual(reader.bufferedBytes, FileTransferCodec.maximumFrameBytes)
        XCTAssertEqual(try reader.append(Data([10])).count, 1)
        XCTAssertThrowsError(try FileTransferCodec.decodeFrame(exact + Data([32, 10]), expectedSourceDeviceId: source)) {
            XCTAssertEqual($0 as? FileTransferProtocolError, .frameTooLarge)
        }
        XCTAssertThrowsError(try reader.append(Data(repeating: 65, count: FileTransferCodec.maximumFrameBytes + 1)))
        XCTAssertEqual(reader.bufferedBytes, 0)
        XCTAssertThrowsError(try reader.append(Data([10])))
        XCTAssertThrowsError(try FileTransferCodec.decodeFrame(frame + frame, expectedSourceDeviceId: source))
        XCTAssertThrowsError(try FileTransferCodec.decodeFrame(Data([255]), expectedSourceDeviceId: source))
    }

    func testOptionalEnvelopeMetadataIsBoundedAndGeneratedDTOsRevalidate() throws {
        var root = envelopeObject("file.channelRequest", ["channelSchema": FileTransferCodec.channelSchema])
        let minimal = try FileTransferCodec.decodeFrame(JSONSerialization.data(withJSONObject: root), expectedSourceDeviceId: source)
        XCTAssertNil(minimal.messageId); XCTAssertNil(minimal.sentAtUtc)
        for (key, value) in [("messageId", true as Any), ("messageId", String(repeating: "f", count: 33) as Any),
                             ("sentAtUtc", String(repeating: "x", count: 65) as Any), ("sentAtUtc", "line\nbreak" as Any)] {
            root = envelopeObject("file.channelRequest", ["channelSchema": FileTransferCodec.channelSchema]); root[key] = value
            XCTAssertThrowsError(try FileTransferCodec.decodeFrame(JSONSerialization.data(withJSONObject: root), expectedSourceDeviceId: source))
        }
        XCTAssertThrowsError(try FileTransferCodec.encode(.finish(transferId: "wrong", fileId: file), sourceDeviceId: source))
        XCTAssertThrowsError(try FileTransferCodec.encode(.chunkAck(FileTransferAcknowledgement(transferId: transfer, fileId: file, nextOffset: UInt64.max)), sourceDeviceId: source))
    }

    func testMalformedCoalescedLinesCannotAllocateUnboundedFrameObjects() throws {
        var reader = FileTransferFrameReader()
        let lines = Data(String(repeating: "x\n", count: FileTransferFrameReader.maximumReturnedFrames).utf8)
        let frames = try reader.append(lines)
        XCTAssertEqual(frames.count, FileTransferFrameReader.maximumReturnedFrames)
        XCTAssertThrowsError(try FileTransferCodec.decodeFrame(try XCTUnwrap(frames.first), expectedSourceDeviceId: source))
        XCTAssertThrowsError(try reader.append(lines + Data("x\n".utf8))) {
            XCTAssertEqual($0 as? FileTransferProtocolError, .tooManyFrames)
        }
        XCTAssertEqual(reader.bufferedBytes, 0)
        XCTAssertEqual(try reader.append(Data("partial".utf8)), [])
        XCTAssertThrowsError(try reader.append(Data(repeating: 65, count: FileTransferCodec.maximumFrameBytes + 2)))
        XCTAssertEqual(reader.bufferedBytes, 0)
    }

    private func id(_ index: Int) -> String { String(format: "%08x-0000-4000-8000-000000000000", index) }
    private func entryObject(id: String? = nil, size: UInt64 = 0) -> [String: Any] {
        ["fileId": id ?? file, "name": "example.txt", "sizeBytes": size, "sha256": emptyDigest]
    }
    private func envelopeObject(_ type: String, _ payload: [String: Any]) -> [String: Any] {
        ["protocol": "batcave.input.v0", "type": type, "sourceDeviceId": source, "payload": payload]
    }
    private func decode(_ type: String, _ payload: [String: Any]) throws -> FileTransferEnvelope {
        try FileTransferCodec.decodeFrame(JSONSerialization.data(withJSONObject: envelopeObject(type, payload)), expectedSourceDeviceId: source)
    }
    private func decodeRaw(_ type: String, _ payload: String) throws -> FileTransferEnvelope {
        let root = "{\"protocol\":\"batcave.input.v0\",\"type\":\"\(type)\",\"sourceDeviceId\":\"\(source)\",\"payload\":\(payload)}"
        return try FileTransferCodec.decodeFrame(Data(root.utf8), expectedSourceDeviceId: source)
    }
    private func makePNG() throws -> Data {
        let context = try XCTUnwrap(CGContext(data: nil, width: 32, height: 24, bitsPerComponent: 8, bytesPerRow: 32 * 4,
            space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        context.setFillColor(CGColor(red: 0.2, green: 0.5, blue: 0.8, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: 32, height: 24))
        let data = NSMutableData(), destination = try XCTUnwrap(CGImageDestinationCreateWithData(data, UTType.png.identifier as CFString, 1, nil))
        CGImageDestinationAddImage(destination, try XCTUnwrap(context.makeImage()), nil)
        XCTAssertTrue(CGImageDestinationFinalize(destination)); return data as Data
    }
}
