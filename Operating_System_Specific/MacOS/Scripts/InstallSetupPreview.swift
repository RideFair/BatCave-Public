import Foundation
import CryptoKit
import Security
import Darwin

private let previewIdentifier = "dev.batcave.macos-agent.secure-preview"
private let latestName = "BatCave — Latest"

private struct InstallError: Error, CustomStringConvertible {
    let description: String
    init(_ text: String) { description = text }
}

private struct FileIdentity: Equatable {
    let device: dev_t
    let inode: ino_t
    init(_ info: stat) { device = info.st_dev; inode = info.st_ino }
}

private struct PreviewIdentity: Equatable {
    let version: String
    let fingerprint: Data
}

private final class Directory {
    let fd: Int32
    init(_ url: URL, requireOwnership: Bool = false) throws {
        try checkPath(url, directories: true)
        fd = open(url.path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard fd >= 0 else { throw InstallError("Cannot open the selected folder.") }
        if requireOwnership {
            var info = stat()
            guard fstat(fd, &info) == 0, info.st_uid == getuid(), (info.st_mode & 0o022) == 0 else {
                close(fd); throw InstallError("The installation folder must belong to this user and prevent writes by other users.")
            }
        }
    }
    deinit { close(fd) }
    func url() throws -> URL {
        var bytes = [CChar](repeating: 0, count: Int(MAXPATHLEN))
        guard fcntl(fd, F_GETPATH, &bytes) == 0 else { throw InstallError("The installation folder moved or became unavailable.") }
        let path = String(decoding: bytes.prefix(while: { $0 != 0 }).map { UInt8(bitPattern: $0) }, as: UTF8.self)
        return URL(fileURLWithPath: path, isDirectory: true)
    }
    func info(_ name: String) throws -> stat? {
        var value = stat()
        if fstatat(fd, name, &value, AT_SYMLINK_NOFOLLOW) == 0 { return value }
        if errno == ENOENT { return nil }
        throw InstallError("Cannot inspect an installation destination.")
    }
}

private func absoluteURL(_ text: String, canonicalizeDefault: Bool = false) throws -> URL {
    guard text.hasPrefix("/"), text.utf8.count <= 4096,
          !text.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) else {
        throw InstallError("Paths must be bounded absolute local paths.")
    }
    let trimmed = text.hasSuffix("/") && text != "/" ? String(text.dropLast()) : text
    if canonicalizeDefault {
        // Only the script-derived default may contain its fixed Scripts/../Build
        // component. User-supplied output paths are never rewritten silently.
        return URL(fileURLWithPath: trimmed).standardizedFileURL
    }
    guard !trimmed.contains("//"), !trimmed.split(separator: "/").contains(where: { $0 == "." || $0 == ".." }) else {
        throw InstallError("Paths must not contain repeated separators, dot components or parent traversal.")
    }
    return URL(fileURLWithPath: trimmed)
}

private func checkPath(_ url: URL, directories: Bool, allowMissing: Bool = false) throws {
    let parts = url.path.split(separator: "/")
    var path = ""
    for (index, part) in parts.enumerated() {
        path += "/" + part
        var info = stat()
        if lstat(path, &info) != 0 {
            if allowMissing && errno == ENOENT { continue }
            throw InstallError("A required installation path is unavailable.")
        }
        guard (info.st_mode & S_IFMT) != S_IFLNK else { throw InstallError("Symbolic links are not allowed in installation paths.") }
        if directories || index < parts.count - 1 {
            guard (info.st_mode & S_IFMT) == S_IFDIR else { throw InstallError("An installation path is not a folder.") }
        }
    }
}

private func validatePreview(_ url: URL) throws -> PreviewIdentity {
    guard url.pathExtension == "app" else { throw InstallError("The source must be a setup preview .app bundle.") }
    try checkPath(url, directories: true)
    let plistURL = url.appendingPathComponent("Contents/Info.plist")
    try checkPath(plistURL, directories: false)
    let plistBytes = try readBoundedRegularFile(plistURL, maximumBytes: 64 * 1024)
    guard plistBytes.count <= 64 * 1024,
          let plist = try PropertyListSerialization.propertyList(from: plistBytes, format: nil) as? [String: Any],
          plist["CFBundleIdentifier"] as? String == previewIdentifier,
          plist["CFBundleExecutable"] as? String == "BatCaveMacOSAgent",
          plist["CFBundleName"] as? String == "BatCave Preview",
          plist["CFBundlePackageType"] as? String == "APPL",
          let flag = plist["BatCaveSetupPreview"] as? NSNumber, CFGetTypeID(flag) == CFBooleanGetTypeID(), flag.boolValue,
          let version = plist["BatCavePreviewVersion"] as? String,
          version.utf8.count <= 64,
          version.range(of: #"^[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}-preview\.[1-9][0-9]{0,5}$"#, options: .regularExpression) != nil,
          let shortVersion = plist["CFBundleShortVersionString"] as? String,
          version.hasPrefix(shortVersion + "-preview."),
          let build = plist["CFBundleVersion"] as? String,
          build.range(of: #"^[1-9][0-9]{0,8}$"#, options: .regularExpression) != nil else {
        throw InstallError("The app identity, guided-preview flag or preview version is invalid.")
    }
    let executable = url.appendingPathComponent("Contents/MacOS/BatCaveMacOSAgent")
    try checkPath(executable, directories: false)
    var executableInfo = stat()
    guard lstat(executable.path, &executableInfo) == 0, (executableInfo.st_mode & S_IFMT) == S_IFREG,
          (executableInfo.st_mode & 0o111) != 0 else { throw InstallError("The preview executable is unavailable.") }

    var code: SecStaticCode?
    guard SecStaticCodeCreateWithPath(url as CFURL, SecCSFlags(rawValue: 0), &code) == errSecSuccess, let code,
          SecStaticCodeCheckValidity(code, SecCSFlags(rawValue: kSecCSStrictValidate | kSecCSCheckAllArchitectures), nil) == errSecSuccess else {
        throw InstallError("The preview signature is missing or invalid.")
    }
    var signing: CFDictionary?
    guard SecCodeCopySigningInformation(code, SecCSFlags(rawValue: kSecCSSigningInformation), &signing) == errSecSuccess,
          let values = signing as? [String: Any], values[kSecCodeInfoIdentifier as String] as? String == previewIdentifier else {
        throw InstallError("The code signature uses a different app identity.")
    }
    return PreviewIdentity(version: version, fingerprint: try bundleFingerprint(url))
}

private func readBoundedRegularFile(_ url: URL, maximumBytes: Int) throws -> Data {
    let fd = open(url.path, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)
    guard fd >= 0 else { throw InstallError("Cannot read preview metadata.") }
    defer { close(fd) }
    var info = stat()
    guard fstat(fd, &info) == 0, (info.st_mode & S_IFMT) == S_IFREG,
          info.st_size > 0, info.st_size <= maximumBytes else { throw InstallError("Preview metadata must be a bounded regular file.") }
    var data = Data(), buffer = [UInt8](repeating: 0, count: min(maximumBytes, 4096))
    while true {
        let count = buffer.withUnsafeMutableBytes { read(fd, $0.baseAddress!, $0.count) }
        if count < 0 && errno == EINTR { continue }
        guard count >= 0 else { throw InstallError("Cannot read preview metadata.") }
        if count == 0 { break }
        guard data.count <= maximumBytes - count else { throw InstallError("Preview metadata exceeds its size limit.") }
        data.append(contentsOf: buffer.prefix(count))
    }
    return data
}

private func bundleFingerprint(_ bundle: URL) throws -> Data {
    let manager = FileManager.default
    guard let walker = manager.enumerator(at: bundle, includingPropertiesForKeys: nil) else { throw InstallError("Cannot inspect the app bundle.") }
    var files: [(String, URL, stat)] = []
    var entries = 0, total: Int64 = 0
    for case let url as URL in walker {
        entries += 1
        guard entries <= 4096 else { throw InstallError("The preview bundle contains too many entries.") }
        var info = stat()
        guard lstat(url.path, &info) == 0 else { throw InstallError("The source bundle changed while being checked.") }
        let kind = info.st_mode & S_IFMT
        guard kind == S_IFDIR || kind == S_IFREG else { throw InstallError("Links and special files are not allowed in this setup preview bundle.") }
        if kind == S_IFREG {
            guard info.st_size >= 0, info.st_size <= 512 * 1024 * 1024,
                  total <= 512 * 1024 * 1024 - info.st_size else { throw InstallError("The preview bundle exceeds the installation size limit.") }
            total += info.st_size
            files.append((String(url.path.dropFirst(bundle.path.count + 1)), url, info))
        }
    }
    var digest = SHA256(), buffer = [UInt8](repeating: 0, count: 64 * 1024)
    for (relative, url, originalInfo) in files.sorted(by: { $0.0 < $1.0 }) {
        digest.update(data: Data("\(relative.utf8.count):\(relative):\(originalInfo.st_size):\(originalInfo.st_mode & 0o777):".utf8))
        let fd = open(url.path, O_RDONLY | O_NOFOLLOW | O_CLOEXEC)
        guard fd >= 0 else { throw InstallError("Cannot read a preview bundle file.") }
        defer { close(fd) }
        var current = stat()
        guard fstat(fd, &current) == 0, FileIdentity(current) == FileIdentity(originalInfo), current.st_size == originalInfo.st_size else {
            throw InstallError("The source bundle changed while being checked.")
        }
        var bytesRead: Int64 = 0
        while true {
            let count = buffer.withUnsafeMutableBytes { read(fd, $0.baseAddress!, $0.count) }
            if count < 0 && errno == EINTR { continue }
            guard count >= 0 else { throw InstallError("Cannot hash a preview bundle file.") }
            if count == 0 { break }
            bytesRead += Int64(count)
            guard bytesRead <= originalInfo.st_size else { throw InstallError("The source bundle changed while being checked.") }
            buffer.withUnsafeBytes { digest.update(bufferPointer: UnsafeRawBufferPointer(rebasing: $0[..<count])) }
        }
        guard bytesRead == originalInfo.st_size else { throw InstallError("The source bundle changed while being checked.") }
    }
    return Data(digest.finalize())
}

private struct ExistingAlias {
    let identity: FileIdentity
    let target: URL
}

private func validateAlias(in desktop: Directory) throws -> ExistingAlias? {
    guard let info = try desktop.info(latestName) else { return nil }
    guard (info.st_mode & S_IFMT) == S_IFREG, info.st_size > 0, info.st_size <= 1024 * 1024 else { throw InstallError("Desktop/BatCave — Latest exists and is not a native preview alias; it was preserved.") }
    let url = try desktop.url().appendingPathComponent(latestName)
    let properties = try url.resourceValues(forKeys: [.isAliasFileKey])
    guard properties.isAliasFile == true else { throw InstallError("Desktop/BatCave — Latest is an unrelated file; it was preserved.") }
    let bookmark = try URL.bookmarkData(withContentsOf: url)
    guard bookmark.count <= 1024 * 1024 else { throw InstallError("The existing desktop alias exceeds its size limit; it was preserved.") }
    var stale = false
    let target = try URL(resolvingBookmarkData: bookmark, options: [.withoutUI, .withoutMounting], relativeTo: nil, bookmarkDataIsStale: &stale)
    _ = try validatePreview(target)
    guard let checked = try desktop.info(latestName), FileIdentity(checked) == FileIdentity(info) else { throw InstallError("The desktop alias changed while being checked.") }
    return ExistingAlias(identity: FileIdentity(info), target: target)
}

private func ownedSubdirectory(_ name: String, in parent: Directory) throws -> Directory {
    if mkdirat(parent.fd, name, 0o755) != 0 && errno != EEXIST { throw InstallError("Cannot create an installation folder.") }
    return try Directory(try parent.url().appendingPathComponent(name, isDirectory: true), requireOwnership: true)
}

private final class Stage {
    let parent: Directory
    let name: String
    let directory: Directory
    var mayRemove = true
    init(in parent: Directory) throws {
        self.parent = parent
        name = ".batcave-preview-install-" + UUID().uuidString.lowercased()
        guard mkdirat(parent.fd, name, 0o700) == 0 else { throw InstallError("Cannot create private installation staging.") }
        directory = try Directory(try parent.url().appendingPathComponent(name, isDirectory: true), requireOwnership: true)
    }
    deinit {
        guard mayRemove, let actual = try? parent.info(name) else { return }
        var held = stat()
        guard fstat(directory.fd, &held) == 0, FileIdentity(held) == FileIdentity(actual), let url = try? directory.url() else { return }
        try? FileManager.default.removeItem(at: url)
    }
}

private func installAlias(target: URL, desktop: Directory, previous: ExistingAlias?) throws {
    let stage = try Stage(in: desktop)
    let stagedName = "Latest"
    let stagedURL = try stage.directory.url().appendingPathComponent(stagedName)
    let bookmark = try target.bookmarkData(options: .suitableForBookmarkFile, includingResourceValuesForKeys: nil, relativeTo: nil)
    try URL.writeBookmarkData(bookmark, to: stagedURL)
    guard let made = try stage.directory.info(stagedName),
          try stagedURL.resourceValues(forKeys: [.isAliasFileKey]).isAliasFile == true else { throw InstallError("Could not create a native Finder alias.") }
    let newIdentity = FileIdentity(made)
    if let previous {
        guard let actual = try desktop.info(latestName), FileIdentity(actual) == previous.identity else { throw InstallError("The desktop item changed; no replacement was made.") }
        guard renameatx_np(stage.directory.fd, stagedName, desktop.fd, latestName, UInt32(RENAME_SWAP)) == 0 else { throw InstallError("Could not atomically update the desktop alias.") }
        // Until the exchanged item is identified, cleanup must preserve it.
        stage.mayRemove = false
        guard let exchanged = try stage.directory.info(stagedName), FileIdentity(exchanged) == previous.identity else {
            // A raced unrelated item must survive. Restore it if our own new
            // alias still occupies the destination; otherwise retain the item
            // in staging rather than deleting something we did not create.
            if let now = try desktop.info(latestName), FileIdentity(now) == newIdentity,
               renameatx_np(stage.directory.fd, stagedName, desktop.fd, latestName, UInt32(RENAME_SWAP)) == 0 {
                // A second replacement can occur between the check and the
                // rollback swap. Clean up only if our created alias came back.
                if let returned = try stage.directory.info(stagedName), FileIdentity(returned) == newIdentity {
                    stage.mayRemove = true
                    throw InstallError("The desktop item changed; its original item was restored.")
                }
            }
            stage.mayRemove = false
            throw InstallError("The desktop item changed concurrently. An exchanged item was retained at \(stagedURL.path).")
        }
        stage.mayRemove = true
    } else {
        guard renameatx_np(stage.directory.fd, stagedName, desktop.fd, latestName, UInt32(RENAME_EXCL)) == 0 else {
            throw InstallError("A desktop item appeared before alias creation; it was preserved.")
        }
    }
}

private func run() throws {
    var source: URL?, defaultSource: URL?, userHome = FileManager.default.homeDirectoryForCurrentUser
    var validateOnly = false
    var args = Array(CommandLine.arguments.dropFirst())
    while !args.isEmpty {
        let flag = args.removeFirst()
        switch flag {
        case "--validate-only": validateOnly = true
        case "--source", "--user-home", "--default-source":
            guard !args.isEmpty else { throw InstallError("A path is required after \(flag).") }
            let url = try absoluteURL(args.removeFirst(), canonicalizeDefault: flag == "--default-source")
            if flag == "--source" { source = url }
            else if flag == "--user-home" { userHome = url }
            else { defaultSource = url }
        default: throw InstallError("Unknown installer option. Use --help.")
        }
    }
    guard userHome.path != "/", let source = source ?? defaultSource else { throw InstallError("A valid user home and source app are required.") }
    let home = try Directory(userHome, requireOwnership: true)
    let preview = try validatePreview(source)
    let appName = "BatCave Preview-" + preview.version + ".app"
    let applicationsURL = try home.url().appendingPathComponent("Applications", isDirectory: true)
    let desktopURL = try home.url().appendingPathComponent("Desktop", isDirectory: true)
    try checkPath(applicationsURL, directories: true, allowMissing: true)
    try checkPath(desktopURL, directories: true, allowMissing: true)
    var applications: Directory?, desktop: Directory?
    if FileManager.default.fileExists(atPath: applicationsURL.path) { applications = try Directory(applicationsURL, requireOwnership: true) }
    if FileManager.default.fileExists(atPath: desktopURL.path) { desktop = try Directory(desktopURL, requireOwnership: true) }
    let prior = try desktop.map { try validateAlias(in: $0) } ?? nil
    var alreadyInstalled = false
    if let applications, try applications.info(appName) != nil {
        guard try validatePreview(try applications.url().appendingPathComponent(appName)) == preview else {
            throw InstallError("A different app already uses this preview version. It was preserved; build a new preview version.")
        }
        alreadyInstalled = true
    }
    if validateOnly {
        print("Validated secure setup preview \(preview.version). No installation files were written.")
        print("App: \(applicationsURL.appendingPathComponent(appName).path)")
        print("Finder alias: \(desktopURL.appendingPathComponent(latestName).path)")
        return
    }
    if applications == nil { applications = try ownedSubdirectory("Applications", in: home) }
    if desktop == nil { desktop = try ownedSubdirectory("Desktop", in: home) }
    let apps = applications!, desk = desktop!
    let destination = try apps.url().appendingPathComponent(appName)
    if !alreadyInstalled {
        let stage = try Stage(in: apps)
        let temporary = try stage.directory.url().appendingPathComponent(appName)
        try FileManager.default.copyItem(at: source, to: temporary)
        guard try validatePreview(temporary) == preview else { throw InstallError("The app changed while copying; no app was installed.") }
        guard renameatx_np(stage.directory.fd, appName, apps.fd, appName, UInt32(RENAME_EXCL)) == 0 else {
            throw InstallError("A same-version app appeared during installation; it was preserved.")
        }
    }
    if prior?.target.path != destination.path { try installAlias(target: destination, desktop: desk, previous: prior) }
    guard try validateAlias(in: desk)?.target.path == destination.path else {
        throw InstallError("The desktop alias changed after installation. The installed app was preserved.")
    }
    print("\(alreadyInstalled ? "Verified existing" : "Installed") secure setup preview \(preview.version): \(destination.path)")
    print("Native Finder alias ready: \(try desk.url().appendingPathComponent(latestName).path)")
    print("The app was not opened. Existing preview versions and the working app were preserved.")
}

do { try run() }
catch {
    fputs("BatCave preview install: \(error)\n", stderr)
    exit(2)
}
