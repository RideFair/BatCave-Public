import Foundation
import Darwin

enum FreshSetupBackupError: Error, Equatable, LocalizedError {
    case unsafePath
    case missingProfile
    case notDirectory
    case notPrivate
    case pathChanged
    case profileChanged
    case destinationExists
    case filesystem(operation: String, code: Int32)

    var errorDescription: String? {
        switch self {
        case .unsafePath: return "The saved setup must be a local SecurePreview folder without symbolic links in its path."
        case .missingProfile: return "There is no saved setup folder to back up."
        case .notDirectory: return "The saved setup path is not a folder."
        case .notPrivate: return "The saved setup folder must be private to this user. Its parent folder must belong to this user and prevent writes by other users."
        case .pathChanged: return "The setup folder's location changed. Cancel and try Start fresh again."
        case .profileChanged: return "The saved setup folder changed. Cancel and try Start fresh again."
        case .destinationExists: return "The backup destination is already in use. Nothing was replaced."
        case .filesystem(let operation, let code): return "Could not \(operation) (filesystem error \(code))."
        }
    }
}

/// Moves the complete profile, rather than copying or selectively removing it.
/// No Keychain, permission, login-item, app or alias operation belongs here.
/// Preparation is read-only. The caller shows the plan's exact destination and
/// executes only after explicit consent, with profile writers and other app
/// instances stopped. The complete latest folder is backed up, including changes
/// that finished while confirmation was displayed.
enum FreshSetupBackup {
    struct Plan: Sendable {
        let sourceURL: URL
        let backupURL: URL
        fileprivate let ancestors: [DirectoryIdentity]
        fileprivate let profile: DirectoryIdentity
    }

    fileprivate struct DirectoryIdentity: Equatable, Sendable {
        let device: dev_t
        let inode: ino_t
        let owner: uid_t
        let mode: mode_t

        init(_ value: stat) {
            device = value.st_dev; inode = value.st_ino
            owner = value.st_uid; mode = value.st_mode
        }
    }

    private final class ParentDirectory {
        let fd: Int32
        let identities: [DirectoryIdentity]

        init(url: URL, expected: [DirectoryIdentity]? = nil) throws {
            var current = Darwin.open("/", O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
            guard current >= 0 else { throw FreshSetupBackup.failure("open the setup location") }
            var found: [DirectoryIdentity] = []
            do {
                found.append(try FreshSetupBackup.identity(of: current))
                for component in url.path.split(separator: "/") {
                    var entry = stat()
                    guard fstatat(current, String(component), &entry, AT_SYMLINK_NOFOLLOW) == 0 else {
                        throw FreshSetupBackup.failure("inspect the setup location")
                    }
                    guard entry.st_mode & S_IFMT == S_IFDIR else { throw FreshSetupBackupError.unsafePath }
                    let next = openat(current, String(component), O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
                    guard next >= 0 else { throw FreshSetupBackup.failure("open the setup location") }
                    Darwin.close(current); current = next
                    let opened = try FreshSetupBackup.identity(of: current)
                    guard opened == DirectoryIdentity(entry) else { throw FreshSetupBackupError.pathChanged }
                    found.append(opened)
                }
                if let expected, expected != found { throw FreshSetupBackupError.pathChanged }
                try FreshSetupBackup.requirePrivate(found.last!)
                try FreshSetupBackup.requirePath(of: current, equals: url)
                fd = current; identities = found
            } catch {
                Darwin.close(current)
                throw error
            }
        }

        deinit { Darwin.close(fd) }
    }

    /// Read-only preparation. Empty folders can be backed up; missing folders
    /// are refused so the UI never claims to have backed up nonexistent state.
    static func prepare(at source: URL, now: Date = Date(), identifier: UUID = UUID()) throws -> Plan {
        try validateSource(source)
        let parentURL = source.deletingLastPathComponent()
        let parent = try ParentDirectory(url: parentURL)
        let profile = try checkedProfile(in: parent)
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        formatter.dateFormat = "yyyyMMdd-HHmmss"
        let name = "SecurePreview-backup-" + formatter.string(from: now) + "-" + identifier.uuidString.lowercased()
        try requireAbsent(name, in: parent.fd)
        return Plan(sourceURL: source, backupURL: parentURL.appendingPathComponent(name, isDirectory: true),
                    ancestors: parent.identities, profile: profile)
    }

    /// The only mutation is a same-parent, exclusive atomic rename. All failure
    /// paths before/at rename leave the profile untouched. Descriptor-relative
    /// access and identity revalidation reject path swaps since preparation.
    /// The parent must remain under the caller's control during execution; this
    /// does not defend against a malicious process running as the same OS user.
    @discardableResult
    static func execute(_ plan: Plan) throws -> URL {
        try validateSource(plan.sourceURL)
        let parentURL = plan.sourceURL.deletingLastPathComponent()
        let parent = try ParentDirectory(url: parentURL, expected: plan.ancestors)
        guard try checkedProfile(in: parent) == plan.profile else { throw FreshSetupBackupError.profileChanged }
        try requireAbsent(plan.backupURL.lastPathComponent, in: parent.fd)
        // Repeat the anchored parent/source checks immediately before mutation.
        try requirePath(of: parent.fd, equals: parentURL)
        guard try checkedProfile(in: parent) == plan.profile else { throw FreshSetupBackupError.profileChanged }
        guard renameatx_np(parent.fd, "SecurePreview", parent.fd, plan.backupURL.lastPathComponent, UInt32(RENAME_EXCL)) == 0 else {
            if errno == EEXIST { throw FreshSetupBackupError.destinationExists }
            throw failure("back up the saved setup")
        }
        return plan.backupURL
    }

    private static func validateSource(_ source: URL) throws {
        let path = source.path
        let parts = path.split(separator: "/", omittingEmptySubsequences: false)
        guard source.isFileURL, source.host == nil || source.host == "" || source.host == "localhost",
              source.query == nil, source.fragment == nil, source.user == nil, source.password == nil,
              path.hasPrefix("/"), path.utf8.count < Int(MAXPATHLEN), parts.count > 2,
              source.lastPathComponent == "SecurePreview",
              !path.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }),
              !parts.dropFirst().dropLast().contains(where: { $0.isEmpty || $0 == "." || $0 == ".." }),
              parts.last == "SecurePreview" || (parts.last == "" && parts.dropLast().last == "SecurePreview") else {
            throw FreshSetupBackupError.unsafePath
        }
    }

    private static func checkedProfile(in parent: ParentDirectory) throws -> DirectoryIdentity {
        var entry = stat()
        guard fstatat(parent.fd, "SecurePreview", &entry, AT_SYMLINK_NOFOLLOW) == 0 else {
            if errno == ENOENT { throw FreshSetupBackupError.missingProfile }
            throw failure("inspect the saved setup")
        }
        if entry.st_mode & S_IFMT == S_IFLNK { throw FreshSetupBackupError.unsafePath }
        guard entry.st_mode & S_IFMT == S_IFDIR else { throw FreshSetupBackupError.notDirectory }
        let fd = openat(parent.fd, "SecurePreview", O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard fd >= 0 else { throw failure("open the saved setup") }
        defer { Darwin.close(fd) }
        let opened = try identity(of: fd)
        guard opened == DirectoryIdentity(entry) else { throw FreshSetupBackupError.profileChanged }
        try requirePrivate(opened)
        // Existing logs or pairing data must not become a readable backup for
        // other users. Refuse weak modes rather than modifying original state.
        guard opened.mode & 0o077 == 0 else { throw FreshSetupBackupError.notPrivate }
        return opened
    }

    private static func identity(of fd: Int32) throws -> DirectoryIdentity {
        var value = stat()
        guard fstat(fd, &value) == 0 else { throw failure("inspect the setup folder") }
        return DirectoryIdentity(value)
    }

    private static func requirePrivate(_ value: DirectoryIdentity) throws {
        guard value.owner == geteuid(), value.mode & 0o022 == 0 else { throw FreshSetupBackupError.notPrivate }
    }

    private static func requirePath(of fd: Int32, equals url: URL) throws {
        var bytes = [CChar](repeating: 0, count: Int(MAXPATHLEN))
        guard fcntl(fd, F_GETPATH, &bytes) == 0 else { throw failure("check the setup location") }
        let path = String(decoding: bytes.prefix(while: { $0 != 0 }).map { UInt8(bitPattern: $0) }, as: UTF8.self)
        guard path == url.path else { throw FreshSetupBackupError.pathChanged }
    }

    private static func requireAbsent(_ name: String, in parentFD: Int32) throws {
        var entry = stat()
        if fstatat(parentFD, name, &entry, AT_SYMLINK_NOFOLLOW) == 0 { throw FreshSetupBackupError.destinationExists }
        guard errno == ENOENT else { throw failure("inspect the backup destination") }
    }

    private static func failure(_ operation: String) -> FreshSetupBackupError {
        .filesystem(operation: operation, code: errno)
    }
}
