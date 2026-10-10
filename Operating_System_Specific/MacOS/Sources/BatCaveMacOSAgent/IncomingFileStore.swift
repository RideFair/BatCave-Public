import Foundation
import CryptoKit
import Darwin

enum IncomingFileStoreError: Error, Equatable, LocalizedError {
    case invalidLimits
    case unsafeFilename
    case invalidHash
    case fileTooLarge
    case tooManyTransfers
    case unknownTransfer
    case invalidChunk
    case offsetMismatch(expected: UInt64, actual: UInt64)
    case sizeMismatch
    case hashMismatch
    case stagingFileChanged
    case destinationExists
    case noAvailableFilename
    case filesystem(operation: String, code: Int32)

    var errorDescription: String? {
        switch self {
        case .invalidLimits: return "The file transfer limits are invalid."
        case .unsafeFilename: return "The filename is not a safe filename on all supported computers."
        case .invalidHash: return "The file's SHA-256 checksum is invalid."
        case .fileTooLarge: return "The file exceeds the receiving size limit."
        case .tooManyTransfers: return "Too many files are already being received."
        case .unknownTransfer: return "This file transfer is no longer active."
        case .invalidChunk: return "The file chunk is empty or exceeds the receiving limit."
        case .offsetMismatch: return "The file chunks arrived out of order."
        case .sizeMismatch: return "The received file length does not match its announced length."
        case .hashMismatch: return "The received file does not match its SHA-256 checksum."
        case .stagingFileChanged: return "The private receiving file was changed or replaced."
        case .destinationExists: return "The destination filename is already in use. Choose or approve a new name before receiving the file."
        case .noAvailableFilename: return "A unique destination filename could not be selected."
        case .filesystem(let operation, let code): return "Could not \(operation) (filesystem error \(code))."
        }
    }
}

/// A local receiving store, independent of any transport or wire contract.
/// The caller obtains explicit user consent and a user-chosen directory before
/// calling begin. Peer metadata can supply only one filename, never a path.
/// Methods are synchronized. Rejected chunks cancel their transfer; disconnect
/// must call cancelAll. Completed files are data-only, mode 0600, never opened.
final class IncomingFileStore: @unchecked Sendable {
    struct Limits: Sendable {
        var maximumFileBytes: UInt64 = 1024 * 1024 * 1024
        var maximumChunkBytes: Int = 32 * 1024
        var maximumConcurrentTransfers: Int = 4
    }

    private struct Transfer {
        let filename: String
        let expectedSize: UInt64
        let expectedHash: Data
        let directoryName: String
        let directoryFD: Int32
        let fileFD: Int32
        let fileDevice: dev_t
        let fileInode: ino_t
        var bytesReceived: UInt64 = 0
        var hasher = SHA256()
    }

    private let lock = NSLock()
    private let destinationFD: Int32
    private let destinationURL: URL
    private let limits: Limits
    private var transfers: [UUID: Transfer] = [:]
    private static let partialName = "contents.partial"

    init(destination: URL, limits: Limits = Limits()) throws {
        guard destination.isFileURL, limits.maximumFileBytes > 0,
              limits.maximumFileBytes <= UInt64(Int64.max),
              limits.maximumChunkBytes > 0, limits.maximumConcurrentTransfers > 0 else {
            throw IncomingFileStoreError.invalidLimits
        }
        // Opening the selected directory once prevents later path replacements
        // from redirecting writes. A symlink at the selected directory is refused.
        let fd = Darwin.open(destination.path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard fd >= 0 else { throw Self.filesystemError("open the receiving folder") }
        destinationFD = fd
        destinationURL = destination
        self.limits = limits
    }

    deinit {
        cancelAll()
        Darwin.close(destinationFD)
    }

    var activeTransferCount: Int {
        lock.lock(); defer { lock.unlock() }
        return transfers.count
    }

    /// Returns a local identifier. The transport keeps its own peer/session IDs.
    func begin(filename: String, expectedSize: UInt64, sha256: String) throws -> UUID {
        lock.lock(); defer { lock.unlock() }
        guard Self.isSafeFilename(filename) else { throw IncomingFileStoreError.unsafeFilename }
        guard let expectedHash = Self.parseHash(sha256) else { throw IncomingFileStoreError.invalidHash }
        guard expectedSize <= limits.maximumFileBytes else { throw IncomingFileStoreError.fileTooLarge }
        guard transfers.count < limits.maximumConcurrentTransfers else { throw IncomingFileStoreError.tooManyTransfers }

        let id = UUID()
        let directoryName = ".batcave-incoming-" + id.uuidString.lowercased()
        guard mkdirat(destinationFD, directoryName, 0o700) == 0 else {
            throw Self.filesystemError("create private receiving storage")
        }
        let stagingFD = openat(destinationFD, directoryName, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard stagingFD >= 0 else {
            let failure = Self.filesystemError("open private receiving storage")
            unlinkat(destinationFD, directoryName, AT_REMOVEDIR)
            throw failure
        }
        let fileFD = openat(stagingFD, Self.partialName, O_RDWR | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
        guard fileFD >= 0 else {
            let failure = Self.filesystemError("create the receiving file")
            Darwin.close(stagingFD)
            unlinkat(destinationFD, directoryName, AT_REMOVEDIR)
            throw failure
        }
        var info = stat()
        guard fstat(fileFD, &info) == 0 else {
            let failure = Self.filesystemError("inspect the receiving file")
            Darwin.close(fileFD)
            unlinkat(stagingFD, Self.partialName, 0)
            Darwin.close(stagingFD)
            unlinkat(destinationFD, directoryName, AT_REMOVEDIR)
            throw failure
        }
        transfers[id] = Transfer(filename: filename, expectedSize: expectedSize, expectedHash: expectedHash,
            directoryName: directoryName, directoryFD: stagingFD, fileFD: fileFD,
            fileDevice: info.st_dev, fileInode: info.st_ino)
        return id
    }

    func append(to id: UUID, offset: UInt64, data: Data) throws {
        lock.lock(); defer { lock.unlock() }
        guard var transfer = transfers[id] else { throw IncomingFileStoreError.unknownTransfer }
        do {
            guard !data.isEmpty, data.count <= limits.maximumChunkBytes else {
                throw IncomingFileStoreError.invalidChunk
            }
            guard offset == transfer.bytesReceived else {
                throw IncomingFileStoreError.offsetMismatch(expected: transfer.bytesReceived, actual: offset)
            }
            guard UInt64(data.count) <= transfer.expectedSize - transfer.bytesReceived else {
                throw IncomingFileStoreError.sizeMismatch
            }
            try data.withUnsafeBytes { buffer in
                var written = 0
                while written < buffer.count {
                    let count = Darwin.write(transfer.fileFD, buffer.baseAddress!.advanced(by: written), buffer.count - written)
                    if count < 0, errno == EINTR { continue }
                    guard count > 0 else { throw Self.filesystemError("write the receiving file") }
                    written += count
                }
            }
            transfer.hasher.update(data: data)
            transfer.bytesReceived += UInt64(data.count)
            transfers[id] = transfer
        } catch {
            cancelLocked(id)
            throw error
        }
    }

    /// Publishes the fully checked file using an exclusive, atomic rename.
    /// Existing regular files, folders and symlinks are never overwritten.
    /// Collision renaming requires prior, explicit user approval by the caller;
    /// the default refuses the collision and removes this transfer's staging.
    func complete(_ id: UUID, allowCollisionRename: Bool = false) throws -> URL {
        lock.lock(); defer { lock.unlock() }
        guard let transfer = transfers[id] else { throw IncomingFileStoreError.unknownTransfer }
        do {
            guard transfer.bytesReceived == transfer.expectedSize else { throw IncomingFileStoreError.sizeMismatch }
            guard Data(transfer.hasher.finalize()) == transfer.expectedHash else { throw IncomingFileStoreError.hashMismatch }
            _ = try checkedStagingInfo(transfer)
            guard fchmod(transfer.fileFD, 0o600) == 0 else {
                throw Self.filesystemError("set private received-file permissions")
            }
            while fsync(transfer.fileFD) != 0 {
                if errno == EINTR { continue }
                throw Self.filesystemError("finish writing the receiving file")
            }
            // Checking the received stream alone cannot detect same-inode,
            // same-length modifications of the private staging file. Reread
            // the held descriptor with fixed memory before publishing it.
            try verifyStoredContents(transfer)
            for suffix in 0...10_000 {
                let name = Self.destinationName(transfer.filename, suffix: suffix)
                guard name.utf8.count <= 255 else { throw IncomingFileStoreError.unsafeFilename }
                if renameatx_np(transfer.directoryFD, Self.partialName, destinationFD, name, UInt32(RENAME_EXCL)) == 0 {
                    cancelLocked(id)
                    return currentDestinationURL().appendingPathComponent(name, isDirectory: false)
                }
                if errno == EEXIST {
                    guard allowCollisionRename else { throw IncomingFileStoreError.destinationExists }
                    continue
                }
                throw Self.filesystemError("save the received file")
            }
            throw IncomingFileStoreError.noAvailableFilename
        } catch {
            cancelLocked(id)
            throw error
        }
    }

    func cancel(_ id: UUID) {
        lock.lock(); defer { lock.unlock() }
        cancelLocked(id)
    }

    func cancelAll() {
        lock.lock(); defer { lock.unlock() }
        for id in Array(transfers.keys) { cancelLocked(id) }
    }

    private func checkedStagingInfo(_ transfer: Transfer) throws -> stat {
        var openInfo = stat(), namedInfo = stat()
        guard fstat(transfer.fileFD, &openInfo) == 0,
              fstatat(transfer.directoryFD, Self.partialName, &namedInfo, AT_SYMLINK_NOFOLLOW) == 0,
              openInfo.st_dev == transfer.fileDevice, openInfo.st_ino == transfer.fileInode,
              namedInfo.st_dev == transfer.fileDevice, namedInfo.st_ino == transfer.fileInode,
              (namedInfo.st_mode & S_IFMT) == S_IFREG, namedInfo.st_nlink == 1,
              openInfo.st_size >= 0, UInt64(openInfo.st_size) == transfer.expectedSize else {
            throw IncomingFileStoreError.stagingFileChanged
        }
        return openInfo
    }

    private func verifyStoredContents(_ transfer: Transfer) throws {
        let before = try checkedStagingInfo(transfer)
        var hasher = SHA256()
        var buffer = [UInt8](repeating: 0, count: 64 * 1024)
        var offset: UInt64 = 0
        while offset < transfer.expectedSize {
            let wanted = Int(min(UInt64(buffer.count), transfer.expectedSize - offset))
            let count = buffer.withUnsafeMutableBytes {
                pread(transfer.fileFD, $0.baseAddress!, wanted, off_t(offset))
            }
            if count < 0, errno == EINTR { continue }
            guard count >= 0 else { throw Self.filesystemError("verify the receiving file") }
            guard count > 0 else { throw IncomingFileStoreError.sizeMismatch }
            buffer.withUnsafeBytes {
                hasher.update(bufferPointer: UnsafeRawBufferPointer(rebasing: $0[..<count]))
            }
            offset += UInt64(count)
        }
        var extra: UInt8 = 0
        var tail: Int
        repeat { tail = pread(transfer.fileFD, &extra, 1, off_t(offset)) } while tail < 0 && errno == EINTR
        guard tail >= 0 else { throw Self.filesystemError("verify the receiving file length") }
        guard tail == 0 else { throw IncomingFileStoreError.sizeMismatch }
        guard Data(hasher.finalize()) == transfer.expectedHash else { throw IncomingFileStoreError.hashMismatch }
        let after = try checkedStagingInfo(transfer)
        guard before.st_mtimespec.tv_sec == after.st_mtimespec.tv_sec,
              before.st_mtimespec.tv_nsec == after.st_mtimespec.tv_nsec,
              before.st_ctimespec.tv_sec == after.st_ctimespec.tv_sec,
              before.st_ctimespec.tv_nsec == after.st_ctimespec.tv_nsec else {
            throw IncomingFileStoreError.stagingFileChanged
        }
    }

    private func cancelLocked(_ id: UUID) {
        guard let transfer = transfers.removeValue(forKey: id) else { return }
        Darwin.close(transfer.fileFD)
        // unlinkat removes a tampered symlink itself without following it.
        unlinkat(transfer.directoryFD, Self.partialName, 0)
        var openInfo = stat(), namedInfo = stat()
        let sameDirectory = fstat(transfer.directoryFD, &openInfo) == 0
            && fstatat(destinationFD, transfer.directoryName, &namedInfo, AT_SYMLINK_NOFOLLOW) == 0
            && openInfo.st_dev == namedInfo.st_dev && openInfo.st_ino == namedInfo.st_ino
            && (namedInfo.st_mode & S_IFMT) == S_IFDIR
        Darwin.close(transfer.directoryFD)
        if sameDirectory { unlinkat(destinationFD, transfer.directoryName, AT_REMOVEDIR) }
    }

    private func currentDestinationURL() -> URL {
        var bytes = [CChar](repeating: 0, count: Int(MAXPATHLEN))
        if fcntl(destinationFD, F_GETPATH, &bytes) == 0 {
            let pathBytes = bytes.prefix(while: { $0 != 0 }).map { UInt8(bitPattern: $0) }
            return URL(fileURLWithPath: String(decoding: pathBytes, as: UTF8.self), isDirectory: true)
        }
        return destinationURL
    }

    static func isSafeFilename(_ name: String) -> Bool {
        // Reserve room for a collision suffix within the 255-byte filesystem limit.
        guard !name.isEmpty, name.utf8.count <= 240, name != ".", name != "..",
              !name.hasSuffix("."), !name.hasSuffix(" "),
              !name.unicodeScalars.contains(where: {
                  CharacterSet.controlCharacters.contains($0) || "/\\:*?\"<>|".unicodeScalars.contains($0)
                      || (0x202A...0x202E).contains($0.value) || (0x2066...0x2069).contains($0.value)
              }) else { return false }
        let stem = name.split(separator: ".", omittingEmptySubsequences: false)[0]
            .trimmingCharacters(in: CharacterSet(charactersIn: " ")).uppercased()
        let reserved = ["CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$"]
        if reserved.contains(stem) { return false }
        for prefix in ["COM", "LPT"] {
            if ["1", "2", "3", "4", "5", "6", "7", "8", "9", "¹", "²", "³"].contains(where: { stem == prefix + $0 }) { return false }
        }
        return true
    }

    private static func destinationName(_ original: String, suffix: Int) -> String {
        guard suffix > 0 else { return original }
        let value = original as NSString
        let ext = value.pathExtension
        let stem = ext.isEmpty ? original : value.deletingPathExtension
        return stem + " (\(suffix))" + (ext.isEmpty ? "" : "." + ext)
    }

    private static func parseHash(_ text: String) -> Data? {
        guard text.utf8.count == 64 else { return nil }
        let bytes = Array(text.utf8)
        func digit(_ byte: UInt8) -> UInt8? {
            if (48...57).contains(byte) { return byte - 48 }
            if (65...70).contains(byte) { return byte - 65 + 10 }
            if (97...102).contains(byte) { return byte - 97 + 10 }
            return nil
        }
        var output = Data()
        for i in stride(from: 0, to: bytes.count, by: 2) {
            guard let high = digit(bytes[i]), let low = digit(bytes[i + 1]) else { return nil }
            output.append(high << 4 | low)
        }
        return output
    }

    private static func filesystemError(_ operation: String) -> IncomingFileStoreError {
        .filesystem(operation: operation, code: errno)
    }
}
