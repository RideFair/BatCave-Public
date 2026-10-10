@preconcurrency import AppKit
import CryptoKit
import ImageIO
import UniformTypeIdentifiers

enum PortableFileIconError: Error, Equatable {
    case tooLarge, invalidHash, invalidPNG, invalidDimensions, unsupportedImage, renderFailed
}

/// Optional display metadata. File contents never depend on an icon succeeding.
/// Only a bounded static PNG is accepted, then decoded and re-rendered without
/// its source metadata. Never hand peer-provided icon bytes directly to NSImage.
struct PortableFileIcon: Sendable, Equatable {
    static let maximumDimension = 128
    static let maximumBytes = 32 * 1024
    let png: Data
    let sha256: String
    let width: Int
    let height: Int

    private init(png: Data, sha256: String, width: Int, height: Int) {
        self.png = png; self.sha256 = sha256; self.width = width; self.height = height
    }

    @MainActor var image: NSImage { NSImage(data: png) ?? NSImage(size: NSSize(width: width, height: height)) }

    /// Run on the file worker, never the input/UI path. Apple permits workspace
    /// icon lookup on any thread. Copy its image so rendering has a private object.
    static func capture(fileURL: URL) throws -> Self {
        guard let icon = NSWorkspace.shared.icon(forFile: fileURL.path).copy() as? NSImage else {
            throw PortableFileIconError.renderFailed
        }
        var proposed = NSRect(x: 0, y: 0, width: maximumDimension, height: maximumDimension)
        guard let source = icon.cgImage(forProposedRect: &proposed, context: nil, hints: nil) else {
            throw PortableFileIconError.renderFailed
        }
        do { return try render(source, width: maximumDimension, height: maximumDimension) }
        catch PortableFileIconError.tooLarge {
            // A detailed icon can exceed the encoded budget. A smaller optional
            // preview preserves its appearance without blocking file contents.
            return try render(source, width: 64, height: 64)
        }
    }

    static func validate(png: Data, sha256: String) throws -> Self {
        guard png.count <= maximumBytes else { throw PortableFileIconError.tooLarge }
        guard sha256.utf8.count == 64, sha256.utf8.allSatisfy({ (48...57).contains($0) || (65...70).contains($0) || (97...102).contains($0) }),
              digest(png) == sha256.lowercased() else { throw PortableFileIconError.invalidHash }
        let cleaned = try boundedPNG(png)
        guard let source = CGImageSourceCreateWithData(cleaned as CFData, [kCGImageSourceShouldCache: false] as CFDictionary),
              CGImageSourceGetCount(source) == 1,
              CGImageSourceGetType(source) as String? == UTType.png.identifier,
              let pixels = CGImageSourceCreateImageAtIndex(source, 0, [kCGImageSourceShouldCache: false] as CFDictionary) else {
            throw PortableFileIconError.unsupportedImage
        }
        guard (1...maximumDimension).contains(pixels.width), (1...maximumDimension).contains(pixels.height) else {
            throw PortableFileIconError.invalidDimensions
        }
        return try render(pixels, width: pixels.width, height: pixels.height)
    }

    private static func render(_ source: CGImage, width: Int, height: Int) throws -> Self {
        guard let space = CGColorSpace(name: CGColorSpace.sRGB),
              let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8,
                  bytesPerRow: width * 4, space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            throw PortableFileIconError.renderFailed
        }
        context.interpolationQuality = .high
        context.draw(source, in: CGRect(x: 0, y: 0, width: width, height: height))
        guard let rendered = context.makeImage() else { throw PortableFileIconError.renderFailed }
        let data = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(data, UTType.png.identifier as CFString, 1, nil) else {
            throw PortableFileIconError.renderFailed
        }
        CGImageDestinationAddImage(destination, rendered, nil)
        guard CGImageDestinationFinalize(destination) else { throw PortableFileIconError.renderFailed }
        // Remove even encoder-added metadata from the portable result.
        let png = try boundedPNG(data as Data)
        guard png.count <= maximumBytes else { throw PortableFileIconError.tooLarge }
        return Self(png: png, sha256: digest(png), width: width, height: height)
    }

    static func digest(_ data: Data) -> String { SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined() }

    /// Parse length and IHDR bounds BEFORE ImageIO. Strip ancillary metadata,
    /// reject animations, invalid CRCs, trailing data and unknown critical chunks.
    private static func boundedPNG(_ data: Data) throws -> Data {
        let bytes = [UInt8](data)
        let signature: [UInt8] = [137, 80, 78, 71, 13, 10, 26, 10]
        guard bytes.count >= 45, Array(bytes.prefix(8)) == signature else { throw PortableFileIconError.invalidPNG }
        func number(_ at: Int) -> UInt32 {
            UInt32(bytes[at]) << 24 | UInt32(bytes[at + 1]) << 16 | UInt32(bytes[at + 2]) << 8 | UInt32(bytes[at + 3])
        }
        var result = Data(signature), at = 8, sawHeader = false, sawPixels = false, pixelsEnded = false, sawPalette = false, sawTransparency = false
        var colorType: UInt8 = 0
        while at <= bytes.count - 12 {
            let length = Int(number(at))
            guard length <= bytes.count - at - 12 else { throw PortableFileIconError.invalidPNG }
            let end = at + 12 + length
            let nameBytes = Array(bytes[(at + 4)..<(at + 8)])
            guard nameBytes.allSatisfy({ (65...90).contains($0) || (97...122).contains($0) }), (65...90).contains(nameBytes[2]),
                  crc(bytes, range: (at + 4)..<(at + 8 + length)) == number(at + 8 + length) else {
                throw PortableFileIconError.invalidPNG
            }
            let name = String(decoding: nameBytes, as: UTF8.self)
            guard sawHeader || name == "IHDR" else { throw PortableFileIconError.invalidPNG }
            switch name {
            case "IHDR":
                guard !sawHeader, at == 8, length == 13 else { throw PortableFileIconError.invalidPNG }
                guard (1...UInt32(maximumDimension)).contains(number(at + 8)), (1...UInt32(maximumDimension)).contains(number(at + 12)) else {
                    throw PortableFileIconError.invalidDimensions
                }
                let depth = bytes[at + 16]; colorType = bytes[at + 17]
                let validDepth = [UInt8(0): [1, 2, 4, 8, 16], 2: [8, 16], 3: [1, 2, 4, 8], 4: [8, 16], 6: [8, 16]][colorType]?.contains(Int(depth)) == true
                guard validDepth, bytes[at + 18] == 0, bytes[at + 19] == 0, bytes[at + 20] <= 1 else { throw PortableFileIconError.invalidPNG }
                sawHeader = true
            case "PLTE":
                guard !sawPalette, !sawPixels, colorType != 0, colorType != 4, length > 0, length <= 768, length % 3 == 0 else { throw PortableFileIconError.invalidPNG }
                sawPalette = true
            case "tRNS":
                guard !sawTransparency, !sawPixels,
                      (colorType == 0 && length == 2) || (colorType == 2 && length == 6) || (colorType == 3 && sawPalette && length > 0 && length <= 256) else { throw PortableFileIconError.invalidPNG }
                sawTransparency = true
            case "IDAT":
                guard !pixelsEnded, colorType != 3 || sawPalette else { throw PortableFileIconError.invalidPNG }
                sawPixels = true
            case "IEND":
                guard sawPixels, length == 0, end == bytes.count else { throw PortableFileIconError.invalidPNG }
                result.append(contentsOf: bytes[at..<end])
                return result
            case "acTL", "fcTL", "fdAT": throw PortableFileIconError.unsupportedImage
            default:
                guard (97...122).contains(nameBytes[0]) else { throw PortableFileIconError.unsupportedImage }
            }
            if name != "IDAT" && sawPixels { pixelsEnded = true }
            if ["IHDR", "PLTE", "tRNS", "IDAT"].contains(name) { result.append(contentsOf: bytes[at..<end]) }
            at = end
        }
        throw PortableFileIconError.invalidPNG
    }

    private static func crc(_ bytes: [UInt8], range: Range<Int>) -> UInt32 {
        var value = UInt32.max
        for byte in bytes[range] {
            value ^= UInt32(byte)
            for _ in 0..<8 { value = (value >> 1) ^ ((value & 1) == 1 ? 0xedb88320 : 0) }
        }
        return value ^ UInt32.max
    }
}

/// Cache uses original offer digests as keys; every lookup checks that supplied
/// bytes actually match that digest. Its decoded raster memory is also bounded.
final class PortableFileIconCache: @unchecked Sendable {
    static let shared = PortableFileIconCache()
    private let lock = NSLock()
    private let maximumEntries: Int
    private let maximumBytes: Int
    private var values: [String: PortableFileIcon] = [:]
    private var order: [String] = []
    private var byteCount = 0
    init(maximumEntries: Int = 32, maximumBytes: Int = 128 * 1024) {
        self.maximumEntries = max(0, maximumEntries); self.maximumBytes = max(0, maximumBytes)
    }
    var count: Int { lock.lock(); defer { lock.unlock() }; return values.count }
    var storedBytes: Int { lock.lock(); defer { lock.unlock() }; return byteCount }
    func resolve(png: Data, sha256: String) throws -> PortableFileIcon {
        guard png.count <= PortableFileIcon.maximumBytes else { throw PortableFileIconError.tooLarge }
        let key = sha256.lowercased()
        guard key == PortableFileIcon.digest(png) else { throw PortableFileIconError.invalidHash }
        // Serialize bounded decodes and reserve their worst-case retained cost
        // before allocating a peer image, as required by the shared contract.
        lock.lock(); defer { lock.unlock() }
        if let value = values[key] {
            order.removeAll { $0 == key }; order.append(key); return value
        }
        let reservation = PortableFileIcon.maximumBytes + PortableFileIcon.maximumDimension * PortableFileIcon.maximumDimension * 4
        if maximumEntries > 0, reservation <= maximumBytes {
            while !order.isEmpty && (values.count >= maximumEntries || byteCount > maximumBytes - reservation) {
                let oldest = order.removeFirst()
                if let removed = values.removeValue(forKey: oldest) { byteCount -= removed.png.count + removed.width * removed.height * 4 }
            }
        }
        let value = try PortableFileIcon.validate(png: png, sha256: sha256)
        let cost = value.png.count + value.width * value.height * 4
        guard maximumEntries > 0, cost <= maximumBytes else { return value }
        while !order.isEmpty && (values.count >= maximumEntries || byteCount > maximumBytes - cost) {
            let oldest = order.removeFirst()
            if let removed = values.removeValue(forKey: oldest) { byteCount -= removed.png.count + removed.width * removed.height * 4 }
        }
        values[key] = value; order.append(key); byteCount += cost
        return value
    }
}
