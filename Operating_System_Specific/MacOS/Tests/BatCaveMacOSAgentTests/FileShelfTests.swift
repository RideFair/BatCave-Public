import XCTest
@testable import BatCaveMacOSAgent

final class FileShelfTests: XCTestCase {
    func testRegularFilesDeduplicateAndKeepZeroByteFiles() throws {
        let directory = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let empty = directory.appendingPathComponent("empty.txt")
        let file = directory.appendingPathComponent("example.txt")
        try Data().write(to: empty)
        try Data("hello".utf8).write(to: file)
        let selection = try FileShelfSelection.validate([empty, file, file])
        XCTAssertEqual(selection.map(\.url), [empty, file])
        XCTAssertEqual(selection.map(\.bytes), [0, 5])
    }

    func testRejectsFoldersAndLinksIncludingLinkedParentDirectories() throws {
        let directory = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let file = directory.appendingPathComponent("example.txt")
        try Data("hello".utf8).write(to: file)
        let link = directory.appendingPathComponent("example-link.txt")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: file)
        XCTAssertThrowsError(try FileShelfSelection.validate([directory]))
        XCTAssertThrowsError(try FileShelfSelection.validate([link]))
        let parentLink = directory.appendingPathComponent("linked-folder")
        try FileManager.default.createSymbolicLink(at: parentLink, withDestinationURL: directory)
        XCTAssertThrowsError(try FileShelfSelection.validate([parentLink.appendingPathComponent(file.lastPathComponent)]))
        XCTAssertThrowsError(try FileShelfSelection.validate([URL(string: "https://example.com/file.txt")!]))
    }

    func testSelectionAndSparseFileSizeBounds() throws {
        let directory = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let oversized = directory.appendingPathComponent("too-large.bin")
        try Data().write(to: oversized)
        let handle = try FileHandle(forWritingTo: oversized)
        try handle.truncate(atOffset: UInt64(FileShelfSelection.maximumFileBytes + 1))
        try handle.close()
        XCTAssertThrowsError(try FileShelfSelection.validate([oversized]))
        XCTAssertThrowsError(try FileShelfSelection.validate([]))
        XCTAssertThrowsError(try FileShelfSelection.validate(Array(repeating: oversized, count: FileShelfSelection.maximumFiles + 1)))
    }

    func testRevalidationSeesGrowthAndReplacedSymbolicLinkUsingTheSameURL() throws {
        let directory = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let originalURL = directory.appendingPathComponent("changing.txt")
        try Data("hello".utf8).write(to: originalURL)
        // Explicitly populate Foundation's URL resource cache before mutation.
        let cached = try originalURL.resourceValues(forKeys: [.fileSizeKey, .isRegularFileKey, .isSymbolicLinkKey])
        XCTAssertEqual(cached.fileSize, 5)
        XCTAssertEqual(try FileShelfSelection.validate([originalURL]).first?.bytes, 5)
        try Data(repeating: 65, count: 200).write(to: originalURL)
        XCTAssertEqual(try FileShelfSelection.validate([originalURL]).first?.bytes, 200)
        let target = directory.appendingPathComponent("target.txt")
        try Data("target".utf8).write(to: target)
        try FileManager.default.removeItem(atPath: originalURL.path)
        try FileManager.default.createSymbolicLink(atPath: originalURL.path, withDestinationPath: target.path)
        XCTAssertThrowsError(try FileShelfSelection.validate([originalURL]))
    }

    private func temporaryDirectory() throws -> URL {
        // Foundation's resolvingSymlinksInPath still returns /var for the
        // system temp directory on macOS. Use the canonical private path here.
        let directory = URL(fileURLWithPath: "/private/tmp", isDirectory: true)
            .appendingPathComponent("BatCaveFileShelfTests-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true,
            attributes: [.posixPermissions: 0o700])
        return directory
    }
}
