import XCTest
import Foundation
import Darwin
@testable import BatCaveMacOSAgent

final class FreshSetupBackupTests: XCTestCase, @unchecked Sendable {
    private let now = Date(timeIntervalSince1970: 1_800_000_000)
    private let identifier = UUID(uuidString: "00000000-0000-0000-0000-000000000001")!

    private func fixture(_ body: (URL, URL) throws -> Void) throws {
        let root = URL(fileURLWithPath: "/private/tmp", isDirectory: true)
            .appendingPathComponent("BatCave-fresh-setup-tests-" + UUID().uuidString, isDirectory: true)
        let parent = root.appendingPathComponent("BatCave", isDirectory: true)
        let source = parent.appendingPathComponent("SecurePreview", isDirectory: true)
        try FileManager.default.createDirectory(at: source, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        defer { try? FileManager.default.removeItem(at: root) }
        try body(parent, source)
    }

    private func plan(_ source: URL) throws -> FreshSetupBackup.Plan {
        try FreshSetupBackup.prepare(at: source, now: now, identifier: identifier)
    }

    private func info(_ url: URL) throws -> stat {
        var value = stat()
        guard lstat(url.path, &value) == 0 else { throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno)) }
        return value
    }

    private func exists(_ url: URL) -> Bool {
        var value = stat()
        return lstat(url.path, &value) == 0
    }

    func testPreparationDoesNotChangeOriginalAndAtomicBackupPreservesCompleteTree() throws {
        try fixture { parent, source in
            let nested = source.appendingPathComponent("nested", isDirectory: true)
            try FileManager.default.createDirectory(at: nested, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o750])
            let file = nested.appendingPathComponent("state.json")
            let contents = Data("synthetic setup including paired state".utf8)
            try contents.write(to: file)
            try FileManager.default.setAttributes([.posixPermissions: 0o640], ofItemAtPath: file.path)
            try Data("synthetic public identity reference".utf8).write(to: source.appendingPathComponent("identity.json"))
            try Data([0, 1, 255]).write(to: source.appendingPathComponent(".hidden"))
            let sourceBefore = try info(source), fileBefore = try info(file), nestedBefore = try info(nested)
            let outside = parent.appendingPathComponent("outside.txt")
            try Data("outside remains unchanged".utf8).write(to: outside)
            try FileManager.default.createSymbolicLink(at: source.appendingPathComponent("preserved-link"), withDestinationURL: outside)
            let prepared = try plan(source)
            XCTAssertTrue(exists(source)); XCTAssertFalse(exists(prepared.backupURL))
            XCTAssertEqual(try Data(contentsOf: file), contents)
            XCTAssertEqual(try info(source).st_ino, sourceBefore.st_ino)
            // A callback that finishes while confirmation is displayed must
            // remain in the complete folder, without invalidating its identity.
            try Data("latest saved state".utf8).write(to: source.appendingPathComponent("saved-during-confirmation"))

            let backup = try FreshSetupBackup.execute(prepared)
            XCTAssertEqual(backup, prepared.backupURL); XCTAssertFalse(exists(source))
            let sourceAfter = try info(backup)
            XCTAssertEqual(sourceAfter.st_dev, sourceBefore.st_dev); XCTAssertEqual(sourceAfter.st_ino, sourceBefore.st_ino)
            XCTAssertEqual(sourceAfter.st_mode, sourceBefore.st_mode)
            let backedFile = backup.appendingPathComponent("nested/state.json")
            XCTAssertEqual(try Data(contentsOf: backedFile), contents)
            XCTAssertEqual(try info(backedFile).st_ino, fileBefore.st_ino)
            XCTAssertEqual(try info(backedFile).st_mode, fileBefore.st_mode)
            XCTAssertEqual(try info(backup.appendingPathComponent("nested")).st_mode, nestedBefore.st_mode)
            XCTAssertEqual(try Data(contentsOf: backup.appendingPathComponent(".hidden")), Data([0, 1, 255]))
            XCTAssertTrue(exists(backup.appendingPathComponent("identity.json")))
            XCTAssertEqual(try String(contentsOf: backup.appendingPathComponent("saved-during-confirmation"), encoding: .utf8), "latest saved state")
            XCTAssertEqual(try info(backup.appendingPathComponent("preserved-link")).st_mode & S_IFMT, S_IFLNK)
            XCTAssertEqual(try String(contentsOf: outside, encoding: .utf8), "outside remains unchanged")
        }
    }

    func testEmptyFolderCanBeBackedUpAndMissingFolderIsRefused() throws {
        try fixture { _, source in
            let backup = try FreshSetupBackup.execute(plan(source))
            XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: backup.path), [])
            XCTAssertThrowsError(try plan(source)) { XCTAssertEqual($0 as? FreshSetupBackupError, .missingProfile) }
            XCTAssertTrue(exists(backup)); XCTAssertFalse(exists(source))
        }
    }

    func testUniqueDefaultNamesAndDeterministicDisplayedDestination() throws {
        try fixture { parent, source in
            let first = try FreshSetupBackup.prepare(at: source)
            let second = try FreshSetupBackup.prepare(at: source)
            XCTAssertNotEqual(first.backupURL, second.backupURL)
            let prepared = try plan(source)
            XCTAssertEqual(prepared.backupURL.deletingLastPathComponent(), parent)
            XCTAssertTrue(prepared.backupURL.lastPathComponent.hasSuffix(identifier.uuidString.lowercased()))
            XCTAssertEqual(try plan(source).backupURL, prepared.backupURL)
            XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: parent.path), ["SecurePreview"])
        }
    }

    func testUnsafeNamesRemoteURLsAndTraversalAreRefused() throws {
        try fixture { parent, source in
            let candidates = [
                parent, parent.appendingPathComponent("OtherProfile"),
                URL(string: "https://example.com/SecurePreview")!,
                URL(string: "file://example.com/private/tmp/SecurePreview")!,
                URL(string: "file:///SecurePreview")!,
                URL(string: parent.absoluteString + "../BatCave/SecurePreview")!,
                URL(string: parent.absoluteString + "./SecurePreview")!,
                URL(string: parent.absoluteString + "/SecurePreview")!,
                URL(string: source.absoluteString + "?changed=1")!
            ]
            for candidate in candidates { XCTAssertThrowsError(try plan(candidate)) }
            XCTAssertTrue(exists(source))
            XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: parent.path), ["SecurePreview"])
        }
    }

    func testSourceSymlinkAndRegularFileArePreservedAndRefused() throws {
        try fixture { parent, source in
            try FileManager.default.removeItem(at: source)
            let actual = parent.appendingPathComponent("actual", isDirectory: true)
            try FileManager.default.createDirectory(at: actual, withIntermediateDirectories: false)
            try FileManager.default.createSymbolicLink(at: source, withDestinationURL: actual)
            XCTAssertThrowsError(try plan(source)) { XCTAssertEqual($0 as? FreshSetupBackupError, .unsafePath) }
            XCTAssertEqual(try info(source).st_mode & S_IFMT, S_IFLNK); XCTAssertTrue(exists(actual))
            try FileManager.default.removeItem(at: source)
            let contents = Data("not a directory".utf8)
            try contents.write(to: source)
            XCTAssertThrowsError(try plan(source)) { XCTAssertEqual($0 as? FreshSetupBackupError, .notDirectory) }
            XCTAssertEqual(try Data(contentsOf: source), contents)
        }
    }

    func testSymlinkedAncestorIsRefusedWithoutTouchingTarget() throws {
        try fixture { parent, source in
            let alias = parent.deletingLastPathComponent().appendingPathComponent("alias", isDirectory: true)
            try FileManager.default.createSymbolicLink(at: alias, withDestinationURL: parent)
            XCTAssertThrowsError(try plan(alias.appendingPathComponent("SecurePreview", isDirectory: true)))
            XCTAssertTrue(exists(source)); XCTAssertEqual(try info(alias).st_mode & S_IFMT, S_IFLNK)
        }
    }

    func testSourceSwapSinceConfirmationPreservesBothDirectories() throws {
        try fixture { parent, source in
            let original = Data("original".utf8), replacement = Data("replacement".utf8)
            try original.write(to: source.appendingPathComponent("state"))
            let prepared = try plan(source)
            let detached = parent.appendingPathComponent("saved-original", isDirectory: true)
            try FileManager.default.moveItem(at: source, to: detached)
            try FileManager.default.createDirectory(at: source, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
            try replacement.write(to: source.appendingPathComponent("state"))
            XCTAssertThrowsError(try FreshSetupBackup.execute(prepared)) { XCTAssertEqual($0 as? FreshSetupBackupError, .profileChanged) }
            XCTAssertEqual(try Data(contentsOf: source.appendingPathComponent("state")), replacement)
            XCTAssertEqual(try Data(contentsOf: detached.appendingPathComponent("state")), original)
            XCTAssertFalse(exists(prepared.backupURL))
        }
    }

    func testAncestorSwapSinceConfirmationPreservesBothTrees() throws {
        try fixture { parent, source in
            try Data("original".utf8).write(to: source.appendingPathComponent("state"))
            let prepared = try plan(source)
            let detached = parent.deletingLastPathComponent().appendingPathComponent("saved-parent", isDirectory: true)
            try FileManager.default.moveItem(at: parent, to: detached)
            try FileManager.default.createDirectory(at: source, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            try Data("replacement".utf8).write(to: source.appendingPathComponent("state"))
            XCTAssertThrowsError(try FreshSetupBackup.execute(prepared)) { XCTAssertEqual($0 as? FreshSetupBackupError, .pathChanged) }
            XCTAssertEqual(try String(contentsOf: source.appendingPathComponent("state"), encoding: .utf8), "replacement")
            XCTAssertEqual(try String(contentsOf: detached.appendingPathComponent("SecurePreview/state"), encoding: .utf8), "original")
            XCTAssertFalse(exists(prepared.backupURL))
        }
    }

    func testAncestorBecomingSymlinkAfterPreparationIsRefused() throws {
        try fixture { parent, source in
            try Data("original".utf8).write(to: source.appendingPathComponent("state"))
            let prepared = try plan(source)
            let detached = parent.deletingLastPathComponent().appendingPathComponent("saved-parent", isDirectory: true)
            try FileManager.default.moveItem(at: parent, to: detached)
            try FileManager.default.createSymbolicLink(at: parent, withDestinationURL: detached)
            XCTAssertThrowsError(try FreshSetupBackup.execute(prepared))
            XCTAssertEqual(try info(parent).st_mode & S_IFMT, S_IFLNK)
            XCTAssertEqual(try String(contentsOf: detached.appendingPathComponent("SecurePreview/state"), encoding: .utf8), "original")
            XCTAssertFalse(exists(detached.appendingPathComponent(prepared.backupURL.lastPathComponent)))
        }
    }

    func testSourceBecomingSymlinkOrMissingAfterPreparationIsRefused() throws {
        try fixture { parent, source in
            let prepared = try plan(source)
            let detached = parent.appendingPathComponent("saved-original", isDirectory: true)
            try FileManager.default.moveItem(at: source, to: detached)
            XCTAssertThrowsError(try FreshSetupBackup.execute(prepared)) { XCTAssertEqual($0 as? FreshSetupBackupError, .missingProfile) }
            try FileManager.default.createSymbolicLink(at: source, withDestinationURL: detached)
            XCTAssertThrowsError(try FreshSetupBackup.execute(prepared)) { XCTAssertEqual($0 as? FreshSetupBackupError, .unsafePath) }
            XCTAssertEqual(try info(source).st_mode & S_IFMT, S_IFLNK)
            XCTAssertTrue(exists(detached)); XCTAssertFalse(exists(prepared.backupURL))
        }
    }

    func testExistingAndLateBackupCollisionsAreNeverOverwritten() throws {
        try fixture { parent, source in
            let prepared = try plan(source)
            let originalInode = try info(source).st_ino
            let kinds = ["file", "directory", "symlink"]
            for kind in kinds {
                switch kind {
                case "file": try Data("existing backup".utf8).write(to: prepared.backupURL)
                case "directory": try FileManager.default.createDirectory(at: prepared.backupURL, withIntermediateDirectories: false)
                default: try FileManager.default.createSymbolicLink(at: prepared.backupURL, withDestinationURL: parent)
                }
                let collision = try info(prepared.backupURL)
                XCTAssertThrowsError(try plan(source)) { XCTAssertEqual($0 as? FreshSetupBackupError, .destinationExists) }
                XCTAssertThrowsError(try FreshSetupBackup.execute(prepared)) { XCTAssertEqual($0 as? FreshSetupBackupError, .destinationExists) }
                XCTAssertEqual(try info(source).st_ino, originalInode)
                XCTAssertEqual(try info(prepared.backupURL).st_ino, collision.st_ino)
                try FileManager.default.removeItem(at: prepared.backupURL)
            }
        }
    }

    func testWritableByOtherUsersProfileAndParentAreRefused() throws {
        try fixture { parent, source in
            try FileManager.default.setAttributes([.posixPermissions: 0o770], ofItemAtPath: source.path)
            XCTAssertThrowsError(try plan(source)) { XCTAssertEqual($0 as? FreshSetupBackupError, .notPrivate) }
            try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: source.path)
            try FileManager.default.setAttributes([.posixPermissions: 0o770], ofItemAtPath: parent.path)
            XCTAssertThrowsError(try plan(source)) { XCTAssertEqual($0 as? FreshSetupBackupError, .notPrivate) }
            XCTAssertTrue(exists(source))
        }
    }

    func testProfileReadableByOtherUsersIsRefusedWithoutChangingMode() throws {
        try fixture { parent, source in
            try Data("synthetic private state".utf8).write(to: source.appendingPathComponent("state"))
            let prepared = try plan(source)
            try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: source.path)
            let original = try info(source)
            XCTAssertThrowsError(try plan(source)) { XCTAssertEqual($0 as? FreshSetupBackupError, .notPrivate) }
            XCTAssertThrowsError(try FreshSetupBackup.execute(prepared)) { XCTAssertEqual($0 as? FreshSetupBackupError, .notPrivate) }
            XCTAssertEqual(try info(source).st_mode, original.st_mode)
            XCTAssertEqual(try info(source).st_ino, original.st_ino)
            XCTAssertEqual(try String(contentsOf: source.appendingPathComponent("state"), encoding: .utf8), "synthetic private state")
            XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: parent.path), ["SecurePreview"])
        }
    }

    func testRenamePermissionErrorPreservesOriginalExactly() throws {
        if geteuid() == 0 { throw XCTSkip("A root process bypasses directory write permissions.") }
        try fixture { parent, source in
            try Data("original".utf8).write(to: source.appendingPathComponent("state"))
            let original = try info(source)
            try FileManager.default.setAttributes([.posixPermissions: 0o500], ofItemAtPath: parent.path)
            defer { try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: parent.path) }
            let prepared = try plan(source)
            XCTAssertThrowsError(try FreshSetupBackup.execute(prepared)) { error in
                guard case .filesystem = error as? FreshSetupBackupError else { return XCTFail("Expected a filesystem rename failure: \(error)") }
            }
            XCTAssertEqual(try info(source).st_ino, original.st_ino)
            XCTAssertEqual(try info(source).st_mode, original.st_mode)
            XCTAssertEqual(try String(contentsOf: source.appendingPathComponent("state"), encoding: .utf8), "original")
            XCTAssertFalse(exists(prepared.backupURL))
        }
    }

    func testReusingPlanCannotOverwriteCompletedBackup() throws {
        try fixture { _, source in
            let prepared = try plan(source)
            let backup = try FreshSetupBackup.execute(prepared)
            let original = try info(backup)
            XCTAssertThrowsError(try FreshSetupBackup.execute(prepared))
            XCTAssertEqual(try info(backup).st_ino, original.st_ino)
            XCTAssertFalse(exists(source))
        }
    }

    func testConcurrentExecutionsPublishExactlyOneCompleteBackup() throws {
        final class Results: @unchecked Sendable {
            let lock = NSLock()
            private var completed = 0
            func recordSuccess() { lock.lock(); completed += 1; lock.unlock() }
            var successes: Int { lock.lock(); defer { lock.unlock() }; return completed }
        }
        try fixture { _, source in
            try Data("original".utf8).write(to: source.appendingPathComponent("state"))
            let original = try info(source)
            let prepared = try plan(source), results = Results(), group = DispatchGroup()
            for _ in 0..<2 {
                group.enter()
                DispatchQueue.global().async {
                    defer { group.leave() }
                    if (try? FreshSetupBackup.execute(prepared)) != nil { results.recordSuccess() }
                }
            }
            XCTAssertEqual(group.wait(timeout: .now() + 5), .success)
            XCTAssertEqual(results.successes, 1)
            XCTAssertFalse(exists(source))
            XCTAssertEqual(try info(prepared.backupURL).st_ino, original.st_ino)
            XCTAssertEqual(try String(contentsOf: prepared.backupURL.appendingPathComponent("state"), encoding: .utf8), "original")
        }
    }
}
