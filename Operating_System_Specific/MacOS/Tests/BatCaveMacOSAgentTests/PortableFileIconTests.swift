import XCTest
@preconcurrency import AppKit
import ImageIO
import UniformTypeIdentifiers
import Darwin
@testable import BatCaveMacOSAgent

final class PortableFileIconTests: XCTestCase, @unchecked Sendable {
    func testStaticPNGRoundTripAndMetadataRemoved() throws {
        let png = try makePNG(width: 32, height: 24)
        let icon = try PortableFileIcon.validate(png: png, sha256: PortableFileIcon.digest(png).uppercased())
        XCTAssertEqual(icon.width, 32); XCTAssertEqual(icon.height, 24)
        XCTAssertLessThanOrEqual(icon.png.count, PortableFileIcon.maximumBytes)
        let roundTrip = try PortableFileIcon.validate(png: icon.png, sha256: icon.sha256)
        XCTAssertEqual(roundTrip.width, icon.width); XCTAssertEqual(roundTrip.height, icon.height)
        let annotated = insertChunk("tEXt", payload: Data("Comment\0Private source path".utf8), into: png)
        let cleaned = try PortableFileIcon.validate(png: annotated, sha256: PortableFileIcon.digest(annotated))
        XCTAssertNil(cleaned.png.range(of: Data("Private source path".utf8)))
        XCTAssertEqual(cleaned.png, icon.png)
    }

    func testRejectsUnboundedWrongHashCorruptAnimatedAndNonPNG() throws {
        let png = try makePNG(width: 32, height: 32)
        XCTAssertThrowsError(try PortableFileIcon.validate(png: png, sha256: String(repeating: "0", count: 64)))
        let large = Data(repeating: 0, count: PortableFileIcon.maximumBytes + 1)
        XCTAssertThrowsError(try PortableFileIcon.validate(png: large, sha256: PortableFileIcon.digest(large)))
        let huge = try makePNG(width: 129, height: 1)
        XCTAssertThrowsError(try PortableFileIcon.validate(png: huge, sha256: PortableFileIcon.digest(huge)))
        var corrupt = png; corrupt[corrupt.count - 1] ^= 1
        XCTAssertThrowsError(try PortableFileIcon.validate(png: corrupt, sha256: PortableFileIcon.digest(corrupt)))
        let animated = insertChunk("acTL", payload: Data(repeating: 0, count: 8), into: png)
        XCTAssertThrowsError(try PortableFileIcon.validate(png: animated, sha256: PortableFileIcon.digest(animated)))
        let svg = Data("<svg xmlns='http://www.w3.org/2000/svg'/>".utf8)
        XCTAssertThrowsError(try PortableFileIcon.validate(png: svg, sha256: PortableFileIcon.digest(svg)))
        let trailing = png + Data([0])
        XCTAssertThrowsError(try PortableFileIcon.validate(png: trailing, sha256: PortableFileIcon.digest(trailing)))
    }

    func testCacheEvictionAndRechecksBytesOnCacheHit() throws {
        let cache = PortableFileIconCache(maximumEntries: 2, maximumBytes: 100_000)
        let first = try makePNG(width: 16, height: 16), second = try makePNG(width: 17, height: 16), third = try makePNG(width: 18, height: 16)
        for png in [first, second, third] { _ = try cache.resolve(png: png, sha256: PortableFileIcon.digest(png)) }
        XCTAssertEqual(cache.count, 2); XCTAssertLessThanOrEqual(cache.storedBytes, 100_000)
        XCTAssertThrowsError(try cache.resolve(png: first, sha256: PortableFileIcon.digest(third)))
        let tiny = PortableFileIconCache(maximumEntries: 256, maximumBytes: 1)
        _ = try tiny.resolve(png: first, sha256: PortableFileIcon.digest(first))
        XCTAssertEqual(tiny.count, 0)
    }

    @MainActor func testSourceIconIsQuickBoundedRasterAndFinderCustomIconPersists() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("BatCave-Icon-Test-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let file = directory.appendingPathComponent("sample.txt")
        try Data("Icon test".utf8).write(to: file)
        let start = Date()
        let icon = try PortableFileIcon.capture(fileURL: file)
        let milliseconds = Date().timeIntervalSince(start) * 1000
        print("BatCave source file icon conversion: \(String(format: "%.1f", milliseconds)) ms, \(icon.png.count) bytes")
        let warmStart = Date()
        for _ in 0..<5 { _ = try PortableFileIcon.capture(fileURL: file) }
        print("BatCave warm file icon conversion average: \(String(format: "%.1f", Date().timeIntervalSince(warmStart) * 200)) ms")
        XCTAssertEqual(icon.width, 128); XCTAssertEqual(icon.height, 128)
        let verified = try PortableFileIconCache.shared.resolve(png: icon.png, sha256: icon.sha256)
        XCTAssertTrue(NSWorkspace.shared.setIcon(verified.image, forFile: file.path, options: []))
        // Finder custom icon data belongs to the received file, not global associations.
        var finderInfo = [UInt8](repeating: 0, count: 32)
        let length = getxattr(file.path, "com.apple.FinderInfo", &finderInfo, finderInfo.count, 0, XATTR_NOFOLLOW)
        XCTAssertEqual(length, 32)
        XCTAssertNotEqual(finderInfo[8] & 0x04, 0) // Finder's kHasCustomIcon flag.
    }

    private func makePNG(width: Int, height: Int) throws -> Data {
        let context = try XCTUnwrap(CGContext(data: nil, width: width, height: height, bitsPerComponent: 8,
            bytesPerRow: width * 4, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        context.setFillColor(CGColor(red: 0.2, green: 0.4, blue: 0.9, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        let image = try XCTUnwrap(context.makeImage()), data = NSMutableData()
        let destination = try XCTUnwrap(CGImageDestinationCreateWithData(data, UTType.png.identifier as CFString, 1, nil))
        CGImageDestinationAddImage(destination, image, nil); XCTAssertTrue(CGImageDestinationFinalize(destination))
        return data as Data
    }

    private func insertChunk(_ name: String, payload: Data, into png: Data) -> Data {
        var chunk = Data()
        var length = UInt32(payload.count).bigEndian
        withUnsafeBytes(of: &length) { chunk.append(contentsOf: $0) }
        let body = Data(name.utf8) + payload
        chunk.append(body)
        var crc = UInt32.max
        for byte in body {
            crc ^= UInt32(byte)
            for _ in 0..<8 { crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320 : 0) }
        }
        var checksum = (crc ^ UInt32.max).bigEndian
        withUnsafeBytes(of: &checksum) { chunk.append(contentsOf: $0) }
        // Place after IHDR, before pixel data.
        return png.prefix(33) + chunk + png.dropFirst(33)
    }
}
