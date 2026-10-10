import XCTest
import CryptoKit
import Darwin
@testable import BatCaveMacOSAgent

final class FileTransferStoreTests: XCTestCase {
    func testTraversalAndCrossPlatformUnsafeNamesAreRejectedWithoutCreatingFiles() throws {
        let fixture = try ReceivingFixture()
        defer { fixture.cleanup() }
        let store = try IncomingFileStore(destination: fixture.directory)
        for filename in ["", ".", "..", "../escape.txt", "folder/file.txt", "folder\\file.txt", "/tmp/escape",
                         "C:\\escape.txt", "name:stream", "a\0b", "line\nbreak", "trailing.", "trailing ",
                         "CON", "con.txt", "CON .txt", "AUX.txt", "COM1", "LPT9.csv", "COM¹.txt", "<file>", "a?b",
                         "fake\u{202E}txt.exe", String(repeating: "x", count: 241)] {
            XCTAssertThrowsError(try store.begin(filename: filename, expectedSize: 0, sha256: checksum(Data()))) {
                XCTAssertEqual($0 as? IncomingFileStoreError, .unsafeFilename, filename)
            }
        }
        XCTAssertEqual(store.activeTransferCount, 0)
        XCTAssertEqual(try fixture.contents(), [])
        XCTAssertTrue(IncomingFileStore.isSafeFilename("Résumé 🦇.txt"))
        XCTAssertTrue(IncomingFileStore.isSafeFilename(".hidden"))
        XCTAssertTrue(IncomingFileStore.isSafeFilename("comparison.txt"))
    }

    func testContiguousChunksProduceExactHashCheckedFileWithPrivatePermissions() throws {
        let fixture = try ReceivingFixture()
        defer { fixture.cleanup() }
        let store = try IncomingFileStore(destination: fixture.directory,
            limits: .init(maximumFileBytes: 32, maximumChunkBytes: 4, maximumConcurrentTransfers: 2))
        let body = Data("abcdefghi".utf8)
        let id = try store.begin(filename: "received.txt", expectedSize: UInt64(body.count), sha256: checksum(body).uppercased())
        let staging = try XCTUnwrap(fixture.contents().first)
        let attributes = try FileManager.default.attributesOfItem(atPath: fixture.directory.appendingPathComponent(staging).path)
        XCTAssertEqual((attributes[.posixPermissions] as? NSNumber)?.intValue, 0o700)
        // Completion must enforce data-only permissions even if the staging
        // mode was unexpectedly changed by another local process.
        try FileManager.default.setAttributes([.posixPermissions: 0o777], ofItemAtPath:
            fixture.directory.appendingPathComponent(staging).appendingPathComponent("contents.partial").path)
        XCTAssertFalse(FileManager.default.fileExists(atPath: fixture.directory.appendingPathComponent("received.txt").path))
        try store.append(to: id, offset: 0, data: body.subdata(in: 0..<4))
        try store.append(to: id, offset: 4, data: body.subdata(in: 4..<8))
        try store.append(to: id, offset: 8, data: body.subdata(in: 8..<9))
        let result = try store.complete(id)
        XCTAssertEqual(result.lastPathComponent, "received.txt")
        XCTAssertEqual(try Data(contentsOf: result), body)
        let savedAttributes = try FileManager.default.attributesOfItem(atPath: result.path)
        XCTAssertEqual((savedAttributes[.posixPermissions] as? NSNumber)?.intValue, 0o600)
        XCTAssertEqual(try fixture.contents(), ["received.txt"])
        XCTAssertEqual(store.activeTransferCount, 0)
    }

    func testSizeHashAndConcurrentLimitsRejectBeforeAllocatingMoreStorage() throws {
        let fixture = try ReceivingFixture()
        defer { fixture.cleanup() }
        let store = try IncomingFileStore(destination: fixture.directory,
            limits: .init(maximumFileBytes: 8, maximumChunkBytes: 4, maximumConcurrentTransfers: 1))
        XCTAssertThrowsError(try IncomingFileStore(destination: fixture.directory,
            limits: .init(maximumFileBytes: UInt64.max))) {
            XCTAssertEqual($0 as? IncomingFileStoreError, .invalidLimits)
        }
        XCTAssertThrowsError(try store.begin(filename: "large.txt", expectedSize: 9, sha256: checksum(Data()))) {
            XCTAssertEqual($0 as? IncomingFileStoreError, .fileTooLarge)
        }
        for invalidHash in ["", String(repeating: "g", count: 64), String(repeating: "0", count: 63)] {
            XCTAssertThrowsError(try store.begin(filename: "invalid.txt", expectedSize: 0, sha256: invalidHash)) {
                XCTAssertEqual($0 as? IncomingFileStoreError, .invalidHash)
            }
        }
        XCTAssertEqual(try fixture.contents(), [])
        let id = try store.begin(filename: "one.txt", expectedSize: 0, sha256: checksum(Data()))
        XCTAssertThrowsError(try store.begin(filename: "two.txt", expectedSize: 0, sha256: checksum(Data()))) {
            XCTAssertEqual($0 as? IncomingFileStoreError, .tooManyTransfers)
        }
        XCTAssertEqual(try fixture.contents().count, 1)
        store.cancel(id)
        XCTAssertEqual(try fixture.contents(), [])
    }

    func testWrongOffsetsOversizeAndEmptyChunksCancelAndCleanUp() throws {
        let fixture = try ReceivingFixture()
        defer { fixture.cleanup() }
        let store = try IncomingFileStore(destination: fixture.directory,
            limits: .init(maximumFileBytes: 10, maximumChunkBytes: 4, maximumConcurrentTransfers: 1))
        let body = Data("abcd".utf8)
        let id = try store.begin(filename: "bad-offset.txt", expectedSize: 4, sha256: checksum(body))
        try store.append(to: id, offset: 0, data: Data("ab".utf8))
        XCTAssertThrowsError(try store.append(to: id, offset: 0, data: Data("cd".utf8))) {
            XCTAssertEqual($0 as? IncomingFileStoreError, .offsetMismatch(expected: 2, actual: 0))
        }
        XCTAssertEqual(try fixture.contents(), [])
        XCTAssertThrowsError(try store.complete(id)) { XCTAssertEqual($0 as? IncomingFileStoreError, .unknownTransfer) }
        let future = try store.begin(filename: "gap.txt", expectedSize: 4, sha256: checksum(body))
        XCTAssertThrowsError(try store.append(to: future, offset: UInt64.max, data: Data([1]))) {
            XCTAssertEqual($0 as? IncomingFileStoreError, .offsetMismatch(expected: 0, actual: UInt64.max))
        }
        for data in [Data(), Data(repeating: 0, count: 5)] {
            let rejected = try store.begin(filename: "bad-chunk.txt", expectedSize: 4, sha256: checksum(body))
            XCTAssertThrowsError(try store.append(to: rejected, offset: 0, data: data)) {
                XCTAssertEqual($0 as? IncomingFileStoreError, .invalidChunk)
            }
            XCTAssertEqual(try fixture.contents(), [])
        }
        let tooLong = try store.begin(filename: "overflow.txt", expectedSize: 1, sha256: checksum(Data([0])))
        XCTAssertThrowsError(try store.append(to: tooLong, offset: 0, data: Data([0, 0]))) {
            XCTAssertEqual($0 as? IncomingFileStoreError, .sizeMismatch)
        }
        XCTAssertEqual(try fixture.contents(), [])
    }

    func testDefaultDraftLimitsAcceptOneGiBOfferAndRejectLargerFilesOrChunks() throws {
        let fixture = try ReceivingFixture()
        defer { fixture.cleanup() }
        let store = try IncomingFileStore(destination: fixture.directory)
        let oneGiB: UInt64 = 1024 * 1024 * 1024
        let largest = try store.begin(filename: "largest.bin", expectedSize: oneGiB, sha256: checksum(Data()))
        store.cancel(largest) // An offer allocates no file contents.
        XCTAssertThrowsError(try store.begin(filename: "too-large.bin", expectedSize: oneGiB + 1, sha256: checksum(Data()))) {
            XCTAssertEqual($0 as? IncomingFileStoreError, .fileTooLarge)
        }
        let chunk = Data(repeating: 0, count: 32 * 1024 + 1)
        let id = try store.begin(filename: "large-chunk.bin", expectedSize: UInt64(chunk.count), sha256: checksum(chunk))
        XCTAssertThrowsError(try store.append(to: id, offset: 0, data: chunk)) {
            XCTAssertEqual($0 as? IncomingFileStoreError, .invalidChunk)
        }
        XCTAssertEqual(store.activeTransferCount, 0)
        XCTAssertEqual(try fixture.contents(), [])
    }

    func testIncompleteOrHashMismatchNeverPublishesFile() throws {
        let fixture = try ReceivingFixture()
        defer { fixture.cleanup() }
        let store = try IncomingFileStore(destination: fixture.directory)
        let incomplete = try store.begin(filename: "incomplete.txt", expectedSize: 2, sha256: checksum(Data([1, 2])))
        try store.append(to: incomplete, offset: 0, data: Data([1]))
        XCTAssertThrowsError(try store.complete(incomplete)) { XCTAssertEqual($0 as? IncomingFileStoreError, .sizeMismatch) }
        XCTAssertEqual(try fixture.contents(), [])
        let corrupt = try store.begin(filename: "corrupt.txt", expectedSize: 2, sha256: checksum(Data([1, 2])))
        try store.append(to: corrupt, offset: 0, data: Data([2, 1]))
        XCTAssertThrowsError(try store.complete(corrupt)) { XCTAssertEqual($0 as? IncomingFileStoreError, .hashMismatch) }
        XCTAssertEqual(try fixture.contents(), [])
        XCTAssertEqual(store.activeTransferCount, 0)
        let empty = try store.begin(filename: "empty.txt", expectedSize: 0, sha256: checksum(Data()))
        XCTAssertEqual(try Data(contentsOf: store.complete(empty)), Data())
    }

    func testCollisionNamesPreserveExistingFilesFoldersAndSymlinkTargets() throws {
        let fixture = try ReceivingFixture()
        defer { fixture.cleanup() }
        let original = Data("keep original".utf8)
        let target = fixture.root.appendingPathComponent("outside.txt")
        try original.write(to: target)
        try original.write(to: fixture.directory.appendingPathComponent("report.txt"))
        try FileManager.default.createDirectory(at: fixture.directory.appendingPathComponent("report (1).txt"), withIntermediateDirectories: false)
        try FileManager.default.createSymbolicLink(at: fixture.directory.appendingPathComponent("report (2).txt"), withDestinationURL: target)
        let store = try IncomingFileStore(destination: fixture.directory)
        let body = Data("new file".utf8)
        for suffix in [3, 4] {
            let id = try store.begin(filename: "report.txt", expectedSize: UInt64(body.count), sha256: checksum(body))
            try store.append(to: id, offset: 0, data: body)
            let result = try store.complete(id, allowCollisionRename: true)
            XCTAssertEqual(result.lastPathComponent, "report (\(suffix)).txt")
            XCTAssertEqual(try Data(contentsOf: result), body)
        }
        XCTAssertEqual(try Data(contentsOf: target), original)
        XCTAssertEqual(try Data(contentsOf: fixture.directory.appendingPathComponent("report.txt")), original)
        XCTAssertEqual(try FileManager.default.destinationOfSymbolicLink(atPath: fixture.directory.appendingPathComponent("report (2).txt").path), target.path)
        XCTAssertFalse(try fixture.contents().contains(where: { $0.hasPrefix(".batcave-incoming-") }))
    }

    func testCollisionDefaultsToRefusalWithoutUserApprovedRename() throws {
        let fixture = try ReceivingFixture()
        defer { fixture.cleanup() }
        let store = try IncomingFileStore(destination: fixture.directory)
        let body = Data("new contents".utf8)
        let existing = Data("existing contents".utf8)
        let target = fixture.directory.appendingPathComponent("shared.txt")
        let id = try store.begin(filename: target.lastPathComponent, expectedSize: UInt64(body.count), sha256: checksum(body))
        try store.append(to: id, offset: 0, data: body)
        // The collision appears after receiving began, so checking names at
        // acceptance cannot substitute for the atomic completion check.
        try existing.write(to: target)
        XCTAssertThrowsError(try store.complete(id)) {
            XCTAssertEqual($0 as? IncomingFileStoreError, .destinationExists)
        }
        XCTAssertEqual(try Data(contentsOf: target), existing)
        XCTAssertEqual(store.activeTransferCount, 0)
        XCTAssertEqual(try fixture.contents(), ["shared.txt"])
    }

    func testDotfileAndMaximumUTF8FilenameCollisionsStayWithinFilesystemLimit() throws {
        let fixture = try ReceivingFixture()
        defer { fixture.cleanup() }
        let store = try IncomingFileStore(destination: fixture.directory)
        let unicodeStem = String(repeating: "🦇", count: 59)
        let plain = String(repeating: "x", count: 240)
        let cases = [(".hidden", ".hidden (1)"), (".prefs.json", ".prefs (1).json"),
                     (unicodeStem + ".txt", unicodeStem + " (1).txt"), (plain, plain + " (1)")]
        let original = Data("original".utf8)
        for (filename, expected) in cases {
            XCTAssertLessThanOrEqual(filename.utf8.count, 240)
            try original.write(to: fixture.directory.appendingPathComponent(filename))
            let id = try store.begin(filename: filename, expectedSize: 0, sha256: checksum(Data()))
            let saved = try store.complete(id, allowCollisionRename: true)
            XCTAssertEqual(saved.lastPathComponent, expected)
            XCTAssertLessThanOrEqual(saved.lastPathComponent.utf8.count, 255)
            XCTAssertEqual(try Data(contentsOf: saved), Data())
            XCTAssertEqual(try Data(contentsOf: fixture.directory.appendingPathComponent(filename)), original)
        }
        XCTAssertFalse(try fixture.contents().contains(where: { $0.hasPrefix(".batcave-incoming-") }))
    }

    func testStreamingDiskVerificationAcrossMultipleBuffersPreservesExactData() throws {
        let fixture = try ReceivingFixture()
        defer { fixture.cleanup() }
        let store = try IncomingFileStore(destination: fixture.directory)
        let body = Data((0..<(2 * 64 * 1024 + 17)).map { UInt8($0 % 251) })
        let id = try store.begin(filename: "multiple-buffers.bin", expectedSize: UInt64(body.count), sha256: checksum(body))
        for start in stride(from: 0, to: body.count, by: 32 * 1024) {
            let end = min(start + 32 * 1024, body.count)
            try store.append(to: id, offset: UInt64(start), data: body.subdata(in: start..<end))
        }
        XCTAssertEqual(try Data(contentsOf: store.complete(id)), body)
        XCTAssertEqual(try fixture.contents(), ["multiple-buffers.bin"])
    }

    func testSameInodeSameSizeStagingTamperFailsDiskHashAndCleansUp() throws {
        let fixture = try ReceivingFixture()
        defer { fixture.cleanup() }
        let store = try IncomingFileStore(destination: fixture.directory)
        let body = Data(repeating: 0x41, count: 2 * 64 * 1024 + 17)
        let id = try store.begin(filename: "tampered.bin", expectedSize: UInt64(body.count), sha256: checksum(body))
        for start in stride(from: 0, to: body.count, by: 32 * 1024) {
            let end = min(start + 32 * 1024, body.count)
            try store.append(to: id, offset: UInt64(start), data: body.subdata(in: start..<end))
        }
        let partial = fixture.directory.appendingPathComponent(try XCTUnwrap(fixture.contents().first))
            .appendingPathComponent("contents.partial")
        let fd = Darwin.open(partial.path, O_WRONLY | O_NOFOLLOW | O_CLOEXEC)
        XCTAssertGreaterThanOrEqual(fd, 0)
        guard fd >= 0 else { return }
        defer { Darwin.close(fd) }
        var before = stat(), after = stat()
        XCTAssertEqual(fstat(fd, &before), 0)
        var replacement: UInt8 = 0x42
        XCTAssertEqual(pwrite(fd, &replacement, 1, 64 * 1024 + 5), 1)
        XCTAssertEqual(fstat(fd, &after), 0)
        XCTAssertEqual(before.st_ino, after.st_ino)
        XCTAssertEqual(before.st_size, after.st_size)
        XCTAssertThrowsError(try store.complete(id)) { XCTAssertEqual($0 as? IncomingFileStoreError, .hashMismatch) }
        XCTAssertEqual(store.activeTransferCount, 0)
        XCTAssertEqual(try fixture.contents(), [])
    }

    func testCancelDisconnectAndDeinitRemovePartialFiles() throws {
        let fixture = try ReceivingFixture()
        defer { fixture.cleanup() }
        var store: IncomingFileStore? = try IncomingFileStore(destination: fixture.directory)
        let one = try XCTUnwrap(store).begin(filename: "one.txt", expectedSize: 2, sha256: checksum(Data([1, 2])))
        let two = try XCTUnwrap(store).begin(filename: "two.txt", expectedSize: 2, sha256: checksum(Data([1, 2])))
        try store?.append(to: one, offset: 0, data: Data([1]))
        try store?.append(to: two, offset: 0, data: Data([1]))
        store?.cancel(one)
        store?.cancel(one) // Idempotent, including stale callers after disconnect.
        XCTAssertEqual(try fixture.contents().count, 1)
        store?.cancelAll()
        XCTAssertEqual(try fixture.contents(), [])
        _ = try XCTUnwrap(store).begin(filename: "deinit.txt", expectedSize: 0, sha256: checksum(Data()))
        store = nil
        XCTAssertEqual(try fixture.contents(), [])
    }

    func testTwoIndependentReceiversAtomicallySelectDifferentNames() throws {
        let fixture = try ReceivingFixture()
        defer { fixture.cleanup() }
        let first = try IncomingFileStore(destination: fixture.directory)
        let second = try IncomingFileStore(destination: fixture.directory)
        let firstBody = Data("first sender".utf8), secondBody = Data("second sender".utf8)
        let firstID = try first.begin(filename: "shared.txt", expectedSize: UInt64(firstBody.count), sha256: checksum(firstBody))
        let secondID = try second.begin(filename: "shared.txt", expectedSize: UInt64(secondBody.count), sha256: checksum(secondBody))
        try first.append(to: firstID, offset: 0, data: firstBody)
        try second.append(to: secondID, offset: 0, data: secondBody)
        let results = CompletionResults()
        DispatchQueue.concurrentPerform(iterations: 2) { index in
            do { results.record(.success(try index == 0 ? first.complete(firstID, allowCollisionRename: true)
                : second.complete(secondID, allowCollisionRename: true))) }
            catch { results.record(.failure(error)) }
        }
        XCTAssertEqual(results.values.count, 2)
        let urls = try results.values.map { try $0.get() }
        XCTAssertEqual(Set(urls.map(\.lastPathComponent)), ["shared.txt", "shared (1).txt"])
        XCTAssertEqual(Set(try urls.map { try Data(contentsOf: $0) }), [firstBody, secondBody])
        XCTAssertEqual(try fixture.contents().count, 2)
    }

    func testSymlinkSelectedDirectoryIsRejectedAndDirectoryReplacementCannotRedirectWrites() throws {
        let fixture = try ReceivingFixture()
        defer { fixture.cleanup() }
        let link = fixture.root.appendingPathComponent("linked-folder")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: fixture.directory)
        XCTAssertThrowsError(try IncomingFileStore(destination: link))
        let store = try IncomingFileStore(destination: fixture.directory)
        let id = try store.begin(filename: "bound-to-original.txt", expectedSize: 0, sha256: checksum(Data()))
        let renamed = fixture.root.appendingPathComponent("original-renamed")
        try FileManager.default.moveItem(at: fixture.directory, to: renamed)
        try FileManager.default.createDirectory(at: fixture.directory, withIntermediateDirectories: false)
        let result = try store.complete(id)
        XCTAssertEqual(result.deletingLastPathComponent().resolvingSymlinksInPath(), renamed.resolvingSymlinksInPath())
        XCTAssertTrue(FileManager.default.fileExists(atPath: result.path))
        XCTAssertEqual(try fixture.contents(), [])
    }

    func testReplacingPartialFileWithSymlinkOrHardlinkCannotPublishOrTouchOutsideFile() throws {
        let fixture = try ReceivingFixture()
        defer { fixture.cleanup() }
        let outside = fixture.root.appendingPathComponent("outside.txt")
        let body = Data("safe contents".utf8)
        try body.write(to: outside)
        let store = try IncomingFileStore(destination: fixture.directory)
        let id = try store.begin(filename: "never-saved.txt", expectedSize: UInt64(body.count), sha256: checksum(body))
        try store.append(to: id, offset: 0, data: body)
        let staging = fixture.directory.appendingPathComponent(try XCTUnwrap(fixture.contents().first))
        let partial = staging.appendingPathComponent("contents.partial")
        try FileManager.default.removeItem(at: partial)
        try FileManager.default.createSymbolicLink(at: partial, withDestinationURL: outside)
        XCTAssertThrowsError(try store.complete(id)) { XCTAssertEqual($0 as? IncomingFileStoreError, .stagingFileChanged) }
        XCTAssertEqual(try Data(contentsOf: outside), body)
        XCTAssertEqual(try fixture.contents(), [])

        let hardlinked = try store.begin(filename: "hardlink.txt", expectedSize: UInt64(body.count), sha256: checksum(body))
        try store.append(to: hardlinked, offset: 0, data: body)
        let secondPartial = fixture.directory.appendingPathComponent(try XCTUnwrap(fixture.contents().first)).appendingPathComponent("contents.partial")
        let extraLink = fixture.root.appendingPathComponent("unexpected-hardlink")
        try FileManager.default.linkItem(at: secondPartial, to: extraLink)
        XCTAssertThrowsError(try store.complete(hardlinked)) { XCTAssertEqual($0 as? IncomingFileStoreError, .stagingFileChanged) }
        XCTAssertEqual(try fixture.contents(), [])
    }

    func testRenamedStagingDirectoryReplacementIsNotRemovedDuringCancellation() throws {
        let fixture = try ReceivingFixture()
        defer { fixture.cleanup() }
        let store = try IncomingFileStore(destination: fixture.directory)
        let id = try store.begin(filename: "cancel.txt", expectedSize: 0, sha256: checksum(Data()))
        let staging = fixture.directory.appendingPathComponent(try XCTUnwrap(fixture.contents().first))
        let moved = fixture.root.appendingPathComponent("moved-staging")
        try FileManager.default.moveItem(at: staging, to: moved)
        try FileManager.default.createDirectory(at: staging, withIntermediateDirectories: false)
        store.cancel(id)
        XCTAssertTrue(FileManager.default.fileExists(atPath: staging.path))
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: moved.path), [])
    }

    private func checksum(_ data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }
}

private final class CompletionResults: @unchecked Sendable {
    private let lock = NSLock()
    private(set) var values: [Result<URL, Error>] = []
    func record(_ result: Result<URL, Error>) {
        lock.lock(); defer { lock.unlock() }
        values.append(result)
    }
}

private struct ReceivingFixture {
    let root: URL
    let directory: URL
    init() throws {
        root = FileManager.default.temporaryDirectory.resolvingSymlinksInPath()
            .appendingPathComponent("BatCave-files-test-" + UUID().uuidString, isDirectory: true)
        directory = root.appendingPathComponent("receiving", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    }
    func contents() throws -> [String] { try FileManager.default.contentsOfDirectory(atPath: directory.path).sorted() }
    func cleanup() { try? FileManager.default.removeItem(at: root) }
}
