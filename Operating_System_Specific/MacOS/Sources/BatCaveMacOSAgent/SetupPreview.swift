@preconcurrency import ApplicationServices
@preconcurrency import AppKit
import SwiftUI
import ServiceManagement

func setupPreviewRequested() -> Bool {
    CommandLine.arguments.contains("--setup") || CommandLine.arguments.contains("--setup-preview") || Bundle.main.object(forInfoDictionaryKey: "BatCaveSetupPreview") as? Bool == true
}

enum SetupPage: String, CaseIterable { case nearby, arrangement, permissions }

private func sameDeviceID(_ a: String, _ b: String) -> Bool { a.utf8.elementsEqual(b.utf8) }
private func profileKey(_ id: String) -> String { "id:" + Data(id.utf8).base64EncodedString() }

struct PreviewProfile: Codable {
    var peers: [PairingCard] = []
    var selectedPeerId: String?
    var hosts: [String: String] = [:]
    var inputPorts: [String: UInt16] = [:]
    var roles: [String: LanInputRole] = [:]
    var macOnRight = true
    var completed = false
    init() {}
    func host(for id: String) -> String? { hosts[profileKey(id)] ?? hosts.first { sameDeviceID($0.key, id) }?.value }
    func port(for id: String) -> UInt16 { inputPorts[profileKey(id)] ?? inputPorts.first { sameDeviceID($0.key, id) }?.value ?? 47871 }
    func role(for id: String) -> LanInputRole { roles[profileKey(id)] ?? roles.first { sameDeviceID($0.key, id) }?.value ?? .receiver }
    mutating func forget(_ id: String) {
        for key in Array(hosts.keys) where key == profileKey(id) || sameDeviceID(key, id) { hosts.removeValue(forKey: key) }
        for key in Array(inputPorts.keys) where key == profileKey(id) || sameDeviceID(key, id) { inputPorts.removeValue(forKey: key) }
        for key in Array(roles.keys) where key == profileKey(id) || sameDeviceID(key, id) { roles.removeValue(forKey: key) }
    }
    private enum CodingKeys: String, CodingKey { case peers, selectedPeerId, hosts, inputPorts, roles, macOnRight, completed }
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        peers = try c.decodeIfPresent([PairingCard].self, forKey: .peers) ?? []
        selectedPeerId = try c.decodeIfPresent(String.self, forKey: .selectedPeerId)
        hosts = try c.decodeIfPresent([String: String].self, forKey: .hosts) ?? [:]
        inputPorts = try c.decodeIfPresent([String: UInt16].self, forKey: .inputPorts) ?? [:]
        roles = try c.decodeIfPresent([String: LanInputRole].self, forKey: .roles) ?? [:]
        macOnRight = try c.decodeIfPresent(Bool.self, forKey: .macOnRight) ?? true
        completed = try c.decodeIfPresent(Bool.self, forKey: .completed) ?? false
    }
}

private final class NearbyPublicState: @unchecked Sendable {
    private let lock = NSLock()
    private var cards: [PairingCard] = []
    private var port: UInt16 = 47871
    private var pairingPort: UInt16 = 47873
    private var pairing = false
    func update(cards: [PairingCard], port: UInt16, pairingPort: UInt16, pairing: Bool) {
        lock.withLock { self.cards = cards; self.port = port; self.pairingPort = pairingPort; self.pairing = pairing }
    }
    func accepts(_ card: PairingCard) -> Bool {
        lock.withLock {
            (cards.count < 64 || cards.contains { sameDeviceID($0.deviceId, card.deviceId) }) && cards.allSatisfy {
                sameDeviceID($0.deviceId, card.deviceId)
                    ? $0.certificateSha256.caseInsensitiveCompare(card.certificateSha256) == .orderedSame
                    : $0.certificateSha256.caseInsensitiveCompare(card.certificateSha256) != .orderedSame
            }
        }
    }
    func announcement(_ card: PairingCard) throws -> Data {
        try lock.withLock { try NearbyDiscovery.announcement(card: card, inputPort: port, pairingPort: pairingPort, pairing: pairing) }
    }
}

@MainActor
final class SetupModel: ObservableObject {
    let files = FilesShelfModel()
    @Published var showFiles = false
    @Published var page: SetupPage = .nearby
    @Published var accessibility = false
    @Published var inputMonitoring = false
    @Published var status = "Looking for computers…"
    @Published var message: String?
    @Published var peers: [PairingCard] = []
    @Published var selectedPeerId = ""
    @Published var macOnRight = true
    @Published var role: LanInputRole = .receiver
    @Published var connected = false
    @Published var sharing = false
    @Published var clipboard = false
    @Published var loginEnabled = false
    @Published var nearby: [NearbyComputer] = []
    @Published var selectedNearbyId = ""
    @Published var confirmation: NearbyPairingConfirmation?
    @Published var waitingForApproval = false
    @Published var pairingOpen = false
    @Published var pairingBusy = false
    @Published var discoveryError: String?
    @Published var addingComputer = false
    @Published var showDetails = false
    private var profile = PreviewProfile()
    private var identity: PairingIdentity?
    private var lan: LanBridgePeer?
    private var agent: BatCaveMacOSAgent?
    private var discovery: NearbyDiscovery?
    private var pairing: NearbyPairingService?
    private let publicState = NearbyPublicState()
    private var initialSharing = InitialSharingGate()
    private var locallyApprovedSession: UUID?
    private var installed = false
    private var foreground = false
    private var openRequested = false
    private var candidates: [Data: (address: String, port: UInt16)] = [:]
    private var dialedCandidate: String?
    private var nextDial = Date.distantPast
    private var timer: Timer?
    private var observers: [NSObjectProtocol] = []
    private var lockObserver: NSObjectProtocol?
    private var shuttingDown = false
    private let inspectionOnly: Bool
    private let directory: URL
    private var profileURL: URL { directory.appendingPathComponent("setup.json") }
    var peer: PairingCard? { peers.first { sameDeviceID($0.deviceId, selectedPeerId) } }
    var fingerprint: String { identity?.card.certificateSha256 ?? "" }
    var permissionsReady: Bool { accessibility && inputMonitoring }
    var browsing: Bool { peer == nil || addingComputer }
    var selectedNearby: NearbyComputer? { nearby.first { sameDeviceID($0.deviceID, selectedNearbyId) } }
    var keyboardOwner: String { role == .source ? "This Mac" : (peer?.displayName ?? "The other computer") }

    init(inspectionOnly: Bool = false) {
        self.inspectionOnly = inspectionOnly
        directory = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("BatCave/SecurePreview")
        guard !inspectionOnly else { return }
        if let data = try? Data(contentsOf: profileURL), data.count <= 256 * 1024,
           let saved = try? JSONDecoder().decode(PreviewProfile.self, from: data) {
            profile = saved
            var ids = Set<Data>(), pins = Set<String>()
            peers = saved.peers.prefix(64).filter {
                (try? PairingCard.parse($0.encoded())) != nil && ids.insert(Data($0.deviceId.utf8)).inserted && pins.insert($0.certificateSha256.uppercased()).inserted
            }
            selectedPeerId = peers.contains { card in saved.selectedPeerId.map { sameDeviceID(card.deviceId, $0) } == true } ? saved.selectedPeerId! : (peers.first?.deviceId ?? "")
            macOnRight = saved.macOnRight
            role = saved.role(for: selectedPeerId)
        }
        loginEnabled = SMAppService.mainApp.status == .enabled
        timer = Timer.scheduledTimer(withTimeInterval: 0.6, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.refresh() }
        }
        for name in [NSWorkspace.willSleepNotification, NSWorkspace.sessionDidResignActiveNotification] {
            observers.append(NSWorkspace.shared.notificationCenter.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.suspend(reason: "Mac sleeping or locked") }
            })
        }
        lockObserver = DistributedNotificationCenter.default().addObserver(forName: Notification.Name("com.apple.screenIsLocked"), object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.suspend(reason: "Mac locked") }
        }
        // Prepare only network identity. Input taps/permissions remain untouched.
        _ = prepareIdentity()
        refresh()
    }

    func refresh() {
        guard !inspectionOnly, !shuttingDown else { return }
        accessibility = AXIsProcessTrusted()
        inputMonitoring = CGPreflightListenEventAccess()
        if let lan {
            connected = lan.isAuthenticated
            status = lan.statusDescription
            sharing = role == .source ? lan.isSourceSharingEnabled : lan.isSecureInputEnabled
            if !permissionsReady && sharing { pause(reason: "Input permission changed") }
            if foreground && page == .nearby && !openRequested { openPairingWindow() }
        }
        if let value = initialSharing.consent, Date() >= value.expires || ProcessInfo.processInfo.systemUptime >= value.monotonicExpires { initialSharing.revoke() }
        reconnectKnownPeer()
    }

    @discardableResult func prepareIdentity() -> Bool {
        guard !inspectionOnly, !shuttingDown else { return false }
        if identity != nil { return true }
        do {
            let value = try PairingIdentity.openOrCreate(at: directory, displayName: Host.current().localizedName ?? "My Mac")
            let transport = LanBridgePeer(secureIdentity: value)
            let native = BatCaveMacOSAgent(lan: transport, showHUD: false)
            identity = value; lan = transport; agent = native
            transport.onAuthenticated = { [weak self] session in Task { @MainActor in self?.authenticated(session) } }
            transport.onConnectionAttemptFailed = { [weak self] _ in Task { @MainActor in self?.initialSharing.revoke() } }
            transport.onSessionEnded = { [weak self] _ in Task { @MainActor in self?.sharing = false } }
            transport.onSessionInvalidated = { [weak self] sessionID, _ in
                Task { @MainActor in
                    guard let self else { return }
                    if self.initialSharing.consent?.previousInputSession != sessionID { self.initialSharing.revoke() }
                }
            }
            transport.approve(peer)
            transport.configureRole(role)
            transport.startListening(port: 47871)
            native.startClipboardSync()
            let local = NearbyPairingProtocol.Descriptor(card: value.card, inputPort: UInt16(47871))
            pairing = NearbyPairingService(local: local, sign: { try value.sign(message: $0) },
                acceptPeer: { [publicState] in publicState.accepts($0.card) },
                onUpdate: { [weak self] in self?.pairingChanged($0) },
                onPaired: { [weak self] in self?.completedPairing($0) },
                onFailure: { [weak self] reason in
                    self?.initialSharing.revoke(); self?.locallyApprovedSession = nil
                    self?.confirmation = nil; self?.waitingForApproval = false; self?.message = reason
                })
            discovery = NearbyDiscovery(localID: value.card.deviceId,
                announcement: { [publicState] in try publicState.announcement(value.card) },
                onUpdate: { [weak self] in self?.discovered($0) })
            updatePublicState()
            discovery?.start()
            return true
        } catch { message = error.localizedDescription; return false }
    }

    private func updatePublicState() {
        publicState.update(cards: peers, port: lan?.listeningPort ?? 47871,
                           pairingPort: 47873, pairing: pairingOpen)
    }

    func setForeground(_ active: Bool) {
        guard !shuttingDown else { return }
        guard foreground != active else { return }
        foreground = active
        if active && page == .nearby { openRequested = false; refresh() }
        else { closePairingWindow() }
    }

    func navigate(_ destination: SetupPage) {
        guard destination != page else { return }
        closePairingWindow()
        page = destination; message = nil
        if destination == .nearby && foreground { openRequested = false; refresh() }
    }

    func showFileShelf() { closePairingWindow(); showFiles = true }

    func retry() {
        guard !shuttingDown else { return }
        initialSharing.revoke(); locallyApprovedSession = nil
        pairing?.cancel(); confirmation = nil; waitingForApproval = false
        message = nil; discoveryError = nil; nextDial = .distantPast; dialedCandidate = nil
        if !prepareIdentity() { return }
        if lan?.listeningPort == nil { lan?.startListening(port: 47871) }
        discovery?.refresh()
        openRequested = false; refresh()
    }

    private func openPairingWindow() {
        guard !shuttingDown, foreground, page == .nearby, !showFiles, identity != nil else { return }
        openRequested = true
        pairing?.openWindow(port: 47873)
    }

    private func closePairingWindow() {
        initialSharing.revoke(); locallyApprovedSession = nil
        confirmation = nil; waitingForApproval = false
        pairing?.closeWindow(); pairingOpen = false; openRequested = false
        updatePublicState()
    }

    private func pairingChanged(_ state: NearbyPairingState) {
        pairingOpen = state.isOpen; pairingBusy = state.busy
        confirmation = state.confirmation
        if !state.isOpen { initialSharing.revoke(); locallyApprovedSession = nil }
        if !state.busy && state.confirmation == nil { waitingForApproval = false }
        updatePublicState()
    }

    private func discovered(_ snapshot: NearbyDiscoverySnapshot) {
        nearby = snapshot.computers
        discoveryError = snapshot.error
        if !nearby.contains(where: { sameDeviceID($0.deviceID, selectedNearbyId) }) { selectedNearbyId = "" }
        for computer in nearby {
            guard let card = peers.first(where: { sameDeviceID($0.deviceId, computer.deviceID) }),
                  card.certificateSha256.caseInsensitiveCompare(computer.fingerprint) == .orderedSame else { continue }
            candidates[Data(card.deviceId.utf8)] = (computer.address, computer.inputPort)
        }
        reconnectKnownPeer()
    }

    func linkSelectedComputer() {
        guard foreground, page == .nearby, pairingOpen, !pairingBusy,
              let computer = selectedNearby, computer.pairing else { return }
        initialSharing.revoke(); locallyApprovedSession = nil
        message = nil; pairing?.link(computer)
    }

    func approveComparison() {
        guard foreground, page == .nearby, pairingOpen, !waitingForApproval,
              let confirmation else { return }
        locallyApprovedSession = confirmation.sessionID
        waitingForApproval = true
        pairing?.approve(sessionID: confirmation.sessionID)
    }

    func cancelPairing() {
        initialSharing.revoke(); locallyApprovedSession = nil
        pairing?.cancel(); confirmation = nil; waitingForApproval = false
        message = "Linking cancelled. Choose your computer to try again."
    }

    private func completedPairing(_ result: NearbyPairingResult) {
        guard foreground, page == .nearby, locallyApprovedSession == result.sessionID,
              publicState.accepts(result.peer.card), let identity,
              !sameDeviceID(result.peer.card.deviceId, identity.card.deviceId) else {
            initialSharing.revoke(); message = "Linking ended. Choose your computer to try again."; return
        }
        let card = result.peer.card
        let previousSession = lan?.authenticatedSession?.sessionId
        let previous = (peers, selectedPeerId, role, macOnRight, profile)
        // A saved pin cannot be replaced; validate again immediately before saving.
        peers.removeAll { sameDeviceID($0.deviceId, card.deviceId) }; peers.append(card)
        selectedPeerId = card.deviceId
        role = result.initiator ? .source : .receiver
        macOnRight = !result.initiator
        profile.roles[profileKey(card.deviceId)] = role
        profile.inputPorts[profileKey(card.deviceId)] = UInt16(result.peer.inputPort)
        profile.completed = true
        candidates[Data(card.deviceId.utf8)] = (result.address, UInt16(result.peer.inputPort))
        guard save() else {
            (peers, selectedPeerId, role, macOnRight, profile) = previous
            updatePublicState(); locallyApprovedSession = nil; confirmation = nil; waitingForApproval = false
            initialSharing.revoke(); lan?.stopConnecting()
            message = "The computer approved this Mac, but this Mac could not save the link. Search again to retry."; return
        }
        agent?.stop(reason: "Computer linked")
        lan?.approve(card); lan?.configureRole(role)
        confirmation = nil; waitingForApproval = false; locallyApprovedSession = nil; addingComputer = false
        initialSharing.grant(InitialSharingConsent(pairingSession: result.sessionID,
            peerID: card.deviceId, fingerprint: card.certificateSha256, role: role,
            previousInputSession: previousSession))
        nextDial = .distantPast; dialedCandidate = nil
        reconnectKnownPeer()
        message = "Computer linked. Connecting automatically…"
    }

    private func reconnectKnownPeer() {
        guard !inspectionOnly, !shuttingDown, let identity, let peer, let lan, !connected, Date() >= nextDial,
              identity.card.deviceId.utf16.lexicographicallyPrecedes(peer.deviceId.utf16) else { return }
        let candidate = candidates[Data(peer.deviceId.utf8)] ?? profile.host(for: peer.deviceId).map { ($0, profile.port(for: peer.deviceId)) }
        guard let candidate else { return }
        let key = "\(peer.deviceId)@\(candidate.0):\(candidate.1)"
        guard key != dialedCandidate else { return }
        dialedCandidate = key; nextDial = Date().addingTimeInterval(5)
        lan.connect(host: candidate.0, port: candidate.1, automaticallyReconnect: true)
    }

    private func authenticated(_ session: LanAuthenticatedSession) {
        guard !shuttingDown, let current = lan?.authenticatedSession, current.sessionId == session.sessionId, current.activationGeneration == session.activationGeneration,
              sameDeviceID(session.deviceId, selectedPeerId), session.role == role,
              peer?.certificateSha256.caseInsensitiveCompare(session.fingerprint) == .orderedSame else { return }
        connected = true
        if session.isOutgoing, let address = session.endpointHost, let port = session.endpointPort {
            // Persist the endpoint this exact pinned socket proved, never a
            // discovery candidate which may have changed during its handshake.
            profile.hosts[profileKey(session.deviceId)] = address
            profile.inputPorts[profileKey(session.deviceId)] = port
            _ = save()
        }
        if initialSharing.consume(peerID: session.deviceId, fingerprint: session.fingerprint,
                                   role: session.role, inputSession: session.sessionId) {
            activateSharing(session)
        }
        refresh()
    }

    func selectPeer(_ id: String) {
        guard peers.contains(where: { sameDeviceID($0.deviceId, id) }), !sameDeviceID(id, selectedPeerId) else { return }
        pause(reason: "Selected computer changed"); closePairingWindow()
        selectedPeerId = id; role = profile.role(for: id)
        lan?.approve(peer); lan?.configureRole(role)
        connected = false; dialedCandidate = nil; nextDial = .distantPast
        _ = save(); refresh()
    }

    func changeRole(_ value: LanInputRole) {
        guard role != value else { return }
        pause(reason: "Keyboard and mouse source changed")
        role = value; profile.roles[profileKey(selectedPeerId)] = value
        lan?.configureRole(value); _ = save()
    }

    func changeArrangement(_ onRight: Bool) {
        pause(reason: "Screen arrangement changed")
        macOnRight = onRight; _ = save()
    }

    func addComputer() { pause(reason: "Adding a computer"); addingComputer = true; retry() }
    func linkAgain() {
        pause(reason: "Comparing the code again")
        addingComputer = true; selectedNearbyId = selectedPeerId; retry()
    }

    func unpair() {
        let id = selectedPeerId
        pause(reason: "Computer unlinked"); closePairingWindow(); lan?.approve(nil)
        peers.removeAll { sameDeviceID($0.deviceId, id) }
        profile.forget(id)
        candidates.removeValue(forKey: Data(id.utf8))
        selectedPeerId = peers.first?.deviceId ?? ""; role = profile.role(for: selectedPeerId)
        lan?.approve(peer); lan?.configureRole(role)
        connected = false; addingComputer = peers.isEmpty; dialedCandidate = nil
        _ = save(); retry()
    }

    @discardableResult func save() -> Bool {
        guard !inspectionOnly, !shuttingDown else { return false }
        profile.peers = peers; profile.selectedPeerId = selectedPeerId.isEmpty ? nil : selectedPeerId
        profile.macOnRight = macOnRight
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            try JSONEncoder().encode(profile).write(to: profileURL, options: .atomic)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: profileURL.path)
            updatePublicState(); return true
        } catch { message = "Could not save setup: " + error.localizedDescription; return false }
    }

    func startSharing() {
        guard let session = lan?.authenticatedSession else { message = "Keep BatCave open on both computers while they connect."; return }
        initialSharing.revoke()
        activateSharing(session)
    }

    private func activateSharing(_ session: LanAuthenticatedSession) {
        guard !shuttingDown, let current = lan?.authenticatedSession, current.sessionId == session.sessionId,
              current.activationGeneration == session.activationGeneration, role == session.role,
              sameDeviceID(session.deviceId, selectedPeerId),
              peer?.certificateSha256.caseInsensitiveCompare(session.fingerprint) == .orderedSame else { return }
        closePairingWindow(); openRequested = true
        guard permissionsReady else {
            message = "Your computers are linked. Allow Accessibility and Input Monitoring, then choose Start sharing."
            return
        }
        guard !hasOtherRunningCopy() else { message = "Quit the other BatCave copy, then choose Start sharing here."; return }
        if !installed { installed = agent?.install(exitOnFailure: false) == true }
        guard installed else { message = "Reopen BatCave after granting both input permissions, then choose Start sharing."; return }
        if role == .source {
            guard agent?.startSourceSharing(session: session, peerOnRight: !macOnRight) == true else {
                message = "Sharing could not start. Check permissions and try Start sharing again."; return
            }
        } else { lan?.enableSecureInput(true, clipboard: clipboard, forSession: session.sessionId, forActivation: session.activationGeneration) }
        sharing = true
        message = role == .source ? "Move your pointer across the shared edge to use the other computer." : "Ready to use the other computer’s keyboard and mouse."
    }

    func pause(reason: String = "Sharing paused") {
        initialSharing.revoke(); locallyApprovedSession = nil
        closePairingWindow(); openRequested = true
        agent?.stop(reason: reason); lan?.pauseSharing(reason: reason)
        sharing = false
    }

    private func suspend(reason: String) {
        foreground = false; pause(reason: reason); closePairingWindow()
        lan?.disconnect(reason: reason)
    }

    func permission(_ input: Bool) {
        closePairingWindow()
        if input { _ = CGRequestListenEventAccess() }
        else { _ = AXIsProcessTrustedWithOptions([kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary) }
        let pane = input ? "Privacy_ListenEvent" : "Privacy_Accessibility"
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?" + pane) { NSWorkspace.shared.open(url) }
        message = "Enable BatCave in System Settings, then return here. If macOS asks you to quit, reopen BatCave. Sharing stays paused."
    }

    func changeClipboard(_ value: Bool) {
        clipboard = value
        if role == .receiver, let session = lan?.authenticatedSession {
            lan?.setClipboardEnabled(value, forSession: session.sessionId, forActivation: session.activationGeneration)
        }
    }

    func login(_ enabled: Bool) {
        do {
            if enabled { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
            loginEnabled = SMAppService.mainApp.status == .enabled
            if !loginEnabled && enabled { message = "Approve BatCave under Login Items in System Settings." }
        } catch { message = "Could not change Open at login: " + error.localizedDescription }
    }

    private func hasOtherRunningCopy() -> Bool {
        NSWorkspace.shared.runningApplications.contains {
            $0.processIdentifier != ProcessInfo.processInfo.processIdentifier && ["dev.batcave.macos-agent", "dev.batcave.setup-preview", "dev.batcave.macos-agent.secure-preview"].contains($0.bundleIdentifier ?? "")
        }
    }

    private func validateFreshSetup() throws {
        guard !inspectionOnly, !shuttingDown else { throw PairingError("Quit and reopen BatCave before starting fresh.") }
        guard !sharing, lan?.isSecureInputEnabled != true, lan?.isSourceSharingEnabled != true else {
            throw PairingError("Pause sharing before starting fresh.")
        }
        guard !hasOtherRunningCopy() else { throw PairingError("Quit the other BatCave copy before starting fresh.") }
        let loginStatus = SMAppService.mainApp.status
        guard loginStatus != .enabled, loginStatus != .requiresApproval else {
            throw PairingError("Turn off Open BatCave at login before starting fresh.")
        }
    }

    func prepareFreshSetup() throws -> FreshSetupBackup.Plan {
        try validateFreshSetup()
        pause(reason: "Preparing fresh setup")
        return try FreshSetupBackup.prepare(at: directory)
    }

    func finishFreshSetup(_ plan: FreshSetupBackup.Plan) throws -> URL {
        try validateFreshSetup()
        // Stop callbacks before moving the directory so no delayed save can
        // recreate the old profile after the atomic backup.
        shutdown()
        do { return try FreshSetupBackup.execute(plan) }
        catch { throw PairingError("Your setup could not be moved: " + error.localizedDescription + " Quit and reopen BatCave before trying again.") }
    }

    func shutdown() {
        guard !shuttingDown else { return }
        shuttingDown = true; foreground = false
        pause(reason: "App quit"); closePairingWindow(); discovery?.stop(); pairing?.dispose(); lan?.dispose(); timer?.invalidate()
        observers.forEach { NSWorkspace.shared.notificationCenter.removeObserver($0) }; observers.removeAll()
        if let lockObserver { DistributedNotificationCenter.default().removeObserver(lockObserver); self.lockObserver = nil }
    }
}

private enum SetupTheme {
    static let background = Color(red: 242 / 255, green: 244 / 255, blue: 248 / 255)
    static let sidebar = Color(red: 24 / 255, green: 29 / 255, blue: 47 / 255)
    static let accent = Color(red: 99 / 255, green: 79 / 255, blue: 225 / 255)
    static let ink = Color(red: 31 / 255, green: 38 / 255, blue: 57 / 255)
    static let muted = Color(red: 94 / 255, green: 106 / 255, blue: 129 / 255)
    static let success = Color(red: 32 / 255, green: 121 / 255, blue: 71 / 255)
}

struct SetupView: View {
    @ObservedObject var model: SetupModel
    var body: some View {
        HStack(spacing: 0) {
            VStack(alignment: .leading, spacing: 24) {
                Label("BatCave", systemImage: "display.2").font(.system(size: 25, weight: .bold)).padding(.bottom, 22)
                navigation("Nearby computers", icon: "desktopcomputer", page: .nearby)
                navigation("Arrange screens", icon: "rectangle.2.swap", page: .arrangement)
                navigation("Permissions", icon: "lock.shield", page: .permissions)
                Spacer()
                Button { model.showFileShelf() } label: { Label("Files", systemImage: "doc.on.doc") }.buttonStyle(.plain)
                Text("Encrypted input sharing").font(.system(size: 11)).foregroundStyle(.white.opacity(0.65))
            }.padding(24).frame(width: 205).foregroundStyle(.white).background(SetupTheme.sidebar)
            VStack(alignment: .leading, spacing: 20) {
                HStack {
                    Text("\(model.page == .nearby ? "NEARBY COMPUTERS" : model.page == .arrangement ? "ARRANGE SCREENS" : "PERMISSIONS")")
                        .font(.system(size: 10, weight: .bold)).tracking(1.2).foregroundStyle(SetupTheme.muted)
                    Spacer()
                    Label(model.status, systemImage: model.connected ? "lock.shield.fill" : "circle")
                        .font(.system(size: 11)).foregroundStyle(model.connected ? SetupTheme.success : SetupTheme.muted).lineLimit(2)
                }
                ScrollView {
                    VStack(alignment: .leading, spacing: 22) {
                        switch model.page {
                        case .nearby: nearbyPage
                        case .arrangement: arrangement
                        case .permissions: permissions
                        }
                        if let message = model.message {
                            Label(message, systemImage: "info.circle").font(.system(size: 12)).fixedSize(horizontal: false, vertical: true)
                                .padding(14).frame(maxWidth: .infinity, alignment: .leading).background(SetupTheme.accent.opacity(0.07)).cornerRadius(10)
                        }
                    }.padding(22).frame(maxWidth: .infinity, alignment: .leading).background(.white).cornerRadius(16)
                }
                HStack {
                    Button("Connection details") { model.showDetails.toggle() }.buttonStyle(.plain).font(.system(size: 11))
                    Spacer()
                    if model.sharing { Button("Pause sharing") { model.pause() } }
                }
                if model.showDetails {
                    Text("Approved devices use pinned mutual TLS. Nearby names never approve a computer.\nThis Mac’s full certificate fingerprint: \(model.fingerprint.isEmpty ? "Available after identity preparation" : model.fingerprint)")
                        .font(.system(size: 10, design: .monospaced)).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                }
            }.padding(28).frame(maxWidth: .infinity, maxHeight: .infinity)
        }.frame(minWidth: 800, minHeight: 580).background(SetupTheme.background).foregroundStyle(SetupTheme.ink).tint(SetupTheme.accent).environment(\.colorScheme, .light).preferredColorScheme(.light)
            .sheet(isPresented: $model.showFiles, onDismiss: { model.retry() }) {
                VStack(spacing: 0) {
                    FilesShelfView(model: model.files)
                    HStack { Spacer(); Button("Done") { model.showFiles = false }.keyboardShortcut(.cancelAction) }.padding([.horizontal, .bottom], 22)
                }
            }
    }
    private func navigation(_ title: String, icon: String, page: SetupPage) -> some View {
        Button { model.navigate(page) } label: {
            Label(title, systemImage: icon).font(.system(size: 13, weight: .medium)).frame(maxWidth: .infinity, alignment: .leading)
                .padding(12).background(model.page == page ? SetupTheme.accent : Color.clear).cornerRadius(9)
        }.buttonStyle(.plain)
    }
    private func heading(_ title: String, _ explanation: String) -> some View {
        VStack(alignment: .leading, spacing: 9) {
            Text(title).font(.system(size: 26, weight: .semibold)).fixedSize(horizontal: false, vertical: true)
            Text(explanation).font(.system(size: 13)).foregroundStyle(SetupTheme.muted).fixedSize(horizontal: false, vertical: true)
        }
    }
    @ViewBuilder private var nearbyPage: some View {
        if let comparison = model.confirmation {
            heading("Link with \(comparison.peer.card.displayName)?", "Check all eight digits on your other computer. Approve only if they match.")
            Text(comparison.code).font(.system(size: 38, weight: .semibold, design: .monospaced)).foregroundStyle(SetupTheme.accent)
                .accessibilityLabel("Comparison code \(comparison.code)").textSelection(.enabled).padding(.vertical, 10)
            Text(comparison.initiator ? "This Mac will share its keyboard and mouse." : "This Mac will use the other computer’s keyboard and mouse.")
                .font(.system(size: 13)).fixedSize(horizontal: false, vertical: true)
            HStack {
                Button("Cancel linking") { model.cancelPairing() }
                Spacer()
                Button(model.waitingForApproval ? "Waiting for the other computer…" : "Codes match · Link and share") { model.approveComparison() }
                    .buttonStyle(.borderedProminent).disabled(model.waitingForApproval)
            }
        } else if model.browsing {
            heading("Nearby computers", "Open BatCave on your other computer. Choose it below to link.")
            if model.nearby.isEmpty {
                VStack(alignment: .leading, spacing: 12) {
                    Label("Looking for your other computer…", systemImage: "wifi")
                    Text("Keep both computers on the same Wi-Fi or wired network, with BatCave open. Guest Wi-Fi may block nearby computers.")
                        .font(.system(size: 12)).foregroundStyle(SetupTheme.muted).fixedSize(horizontal: false, vertical: true)
                }.padding(18).frame(maxWidth: .infinity, alignment: .leading).background(SetupTheme.background).cornerRadius(10)
            } else {
                ForEach(Array(model.nearby.enumerated()), id: \.offset) { _, computer in
                    Button { model.selectedNearbyId = computer.deviceID } label: {
                        HStack(spacing: 16) {
                            Image(systemName: "desktopcomputer").font(.system(size: 25)).foregroundStyle(SetupTheme.accent)
                            VStack(alignment: .leading, spacing: 5) {
                                Text(computer.displayName).font(.system(size: 14, weight: .semibold)).lineLimit(2)
                                Text("\(computer.platform == "macos" ? "Mac" : computer.platform == "linux" ? "Linux" : "Windows") · \(computer.pairing ? "Ready to link" : "Open Nearby computers there")")
                                    .font(.system(size: 11)).foregroundStyle(SetupTheme.muted)
                            }
                            Spacer()
                            if sameDeviceID(model.selectedNearbyId, computer.deviceID) { Image(systemName: "checkmark.circle.fill").foregroundStyle(SetupTheme.accent) }
                        }.padding(16).frame(maxWidth: .infinity, alignment: .leading)
                            .background(sameDeviceID(model.selectedNearbyId, computer.deviceID) ? SetupTheme.accent.opacity(0.08) : SetupTheme.background).cornerRadius(10)
                    }.buttonStyle(.plain).accessibilityLabel("\(computer.displayName), \(computer.platform)")
                }
            }
            if let error = model.discoveryError { Text(error).font(.system(size: 12)).foregroundStyle(SetupTheme.muted).fixedSize(horizontal: false, vertical: true) }
            if !model.pairingOpen { Text("Nearby linking is paused. Choose Search again when both computers are ready.").font(.system(size: 12)).foregroundStyle(SetupTheme.muted) }
            HStack {
                Button("Search again") { model.retry() }.disabled(model.pairingBusy)
                Spacer()
                Button(model.pairingBusy ? "Linking…" : "Link computer") { model.linkSelectedComputer() }
                    .buttonStyle(.borderedProminent).disabled(model.pairingBusy || !model.pairingOpen || model.selectedNearby?.pairing != true)
            }
            if model.pairingBusy { Button("Cancel linking") { model.cancelPairing() } }
            if !model.peers.isEmpty { Button("Cancel adding computer") { model.addingComputer = false; model.cancelPairing() } }
        } else {
            heading(model.peer?.displayName ?? "Linked computer", model.connected ? "Connected securely. Choose whose keyboard and mouse you want to use." : "Looking for this computer. Keep BatCave open on both devices; we’ll connect automatically.")
            if model.peers.count > 1 {
                Picker("Linked computer", selection: Binding(get: { Data(model.selectedPeerId.utf8) }, set: { model.selectPeer(String(decoding: $0, as: UTF8.self)) })) {
                    ForEach(Array(model.peers.enumerated()), id: \.offset) { _, card in Text(card.displayName).tag(Data(card.deviceId.utf8)) }
                }
            }
            Picker("Keyboard and mouse", selection: Binding(get: { model.role }, set: { model.changeRole($0) })) {
                Text("This Mac").tag(LanInputRole.source)
                Text("The other computer").tag(LanInputRole.receiver)
            }.pickerStyle(.segmented)
            Text(model.sharing ? "Sharing is on. Move across the shared screen edge." : "Sharing is paused. Reconnecting keeps input off until you choose Start sharing.")
                .font(.system(size: 12)).foregroundStyle(SetupTheme.muted).fixedSize(horizontal: false, vertical: true)
            if !model.connected { Button("Link again") { model.linkAgain() } }
            if !model.permissionsReady { Button("Allow Mac input permissions") { model.navigate(.permissions) } }
            HStack {
                Button(model.sharing ? "Pause sharing" : "Start sharing") { if model.sharing { model.pause() } else { model.startSharing() } }
                    .buttonStyle(.borderedProminent).disabled(!model.connected)
                Button("Arrange screens") { model.navigate(.arrangement) }
            }
            HStack {
                Button("Add another computer") { model.addComputer() }
                Button("Unlink this computer", role: .destructive) { model.unpair() }
            }
            Toggle("Share text clipboard while receiving input", isOn: Binding(get: { model.clipboard }, set: { model.changeClipboard($0) })).toggleStyle(.checkbox).disabled(model.role == .source)
            Toggle("Open BatCave at login", isOn: Binding(get: { model.loginEnabled }, set: { model.login($0) })).toggleStyle(.checkbox)
        }
    }
    private var arrangement: some View {
        VStack(alignment: .leading, spacing: 20) {
            heading("Arrange screens", "The other screen starts to the right of the keyboard and mouse computer. Change it to match your desk.")
            if let peer = model.peer {
                ScreenArrangement(macOnRight: model.macOnRight, peerName: peer.displayName, role: model.role)
                Picker("This Mac’s position", selection: Binding(get: { model.macOnRight }, set: { model.changeArrangement($0) })) {
                    Text("Left of the other computer").tag(false)
                    Text("Right of the other computer").tag(true)
                }.pickerStyle(.segmented)
                Text("Changing the arrangement pauses sharing. Resume it on the computer providing the keyboard and mouse.")
                    .font(.system(size: 12)).foregroundStyle(SetupTheme.muted).fixedSize(horizontal: false, vertical: true)
                Button("Back to computer") { model.navigate(.nearby) }
            } else { Text("Link a computer first. BatCave will choose a usable arrangement for you.").font(.system(size: 13)) }
        }
    }
    private var permissions: some View {
        VStack(alignment: .leading, spacing: 18) {
            heading("Allow input on this Mac", "Link your computers first. macOS permissions are needed when you start using or sharing a keyboard and mouse.")
            permissionRow("Accessibility", "Lets BatCave move the pointer and type on this Mac.", ready: model.accessibility, input: false)
            permissionRow("Input Monitoring", "Lets BatCave detect the shared edge and safely release held input.", ready: model.inputMonitoring, input: true)
            Button("Check permissions again") { model.refresh() }
            Text("If macOS asks you to quit, reopen BatCave after allowing access. Your linked computers are remembered; sharing stays paused.")
                .font(.system(size: 12)).foregroundStyle(SetupTheme.muted).fixedSize(horizontal: false, vertical: true)
            Button("Back to computers") { model.navigate(.nearby) }
        }
    }
    private func permissionRow(_ name: String, _ description: String, ready: Bool, input: Bool) -> some View {
        HStack(spacing: 12) {
            Image(systemName: ready ? "checkmark.circle.fill" : "lock.circle").font(.system(size: 23)).foregroundStyle(ready ? SetupTheme.success : SetupTheme.muted)
            VStack(alignment: .leading, spacing: 5) { Text(name).fontWeight(.semibold); Text(description).font(.system(size: 12)).foregroundStyle(SetupTheme.muted).fixedSize(horizontal: false, vertical: true) }
            Spacer()
            Button(ready ? "Allowed" : "Open settings") { model.permission(input) }.disabled(ready)
        }.padding(16).background(SetupTheme.background).cornerRadius(10)
    }
}

private struct ScreenArrangement: View {
    let macOnRight: Bool
    let peerName: String
    let role: LanInputRole
    var body: some View {
        HStack(spacing: 14) {
            monitor(macOnRight ? peerName : "This Mac", source: macOnRight ? role == .receiver : role == .source)
            Image(systemName: "arrow.left.arrow.right").foregroundStyle(SetupTheme.accent).font(.system(size: 18))
            monitor(macOnRight ? "This Mac" : peerName, source: macOnRight ? role == .source : role == .receiver)
        }.frame(maxWidth: .infinity).padding(16).background(SetupTheme.background).cornerRadius(12)
    }
    private func monitor(_ title: String, source: Bool) -> some View {
        VStack(spacing: 5) {
            VStack(spacing: 8) {
                Image(systemName: "display").font(.system(size: 25))
                Text(title).font(.system(size: 12, weight: .medium)).lineLimit(2)
                Text(source ? "Mouse + keyboard" : "Shared screen").font(.system(size: 10)).foregroundStyle(SetupTheme.muted)
            }.frame(maxWidth: .infinity).frame(height: 110).background(.white).cornerRadius(8)
                .overlay(RoundedRectangle(cornerRadius: 8).stroke(source ? SetupTheme.accent : SetupTheme.muted.opacity(0.35), lineWidth: 2))
            Rectangle().fill(SetupTheme.muted.opacity(0.4)).frame(width: 4, height: 9)
            Capsule().fill(SetupTheme.muted.opacity(0.4)).frame(width: 38, height: 3)
        }
    }
}

// Native controls and SwiftUI share the fixed light palette, including when
// rendered offscreen without a window inheriting the desktop appearance.
@MainActor
func makeSetupHostingView(model: SetupModel) -> NSHostingView<SetupView> {
    let view = NSHostingView(rootView: SetupView(model: model))
    view.appearance = NSAppearance(named: .aqua)
    return view
}

@MainActor
private final class SetupApplication: NSObject, NSApplicationDelegate, NSWindowDelegate {
    let model = SetupModel()
    var window: NSWindow?
    var statusItem: NSStatusItem?
    private var signals: [DispatchSourceSignal] = []
    private var observers: [NSObjectProtocol] = []
    func applicationDidFinishLaunching(_ notification: Notification) {
        for number in [SIGINT, SIGTERM] {
            signal(number, SIG_IGN)
            let source = DispatchSource.makeSignalSource(signal: number, queue: .main)
            source.setEventHandler { Task { @MainActor in NSApp.terminate(nil) } }
            source.resume(); signals.append(source)
        }
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 920, height: 670), styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        self.window = window
        window.title = "BatCave"
        window.minSize = NSSize(width: 800, height: 580)
        window.appearance = NSAppearance(named: .aqua)
        window.contentView = makeSetupHostingView(model: model)
        window.delegate = self
        window.isReleasedWhenClosed = false; window.center()
        for name in [NSApplication.didBecomeActiveNotification, NSApplication.didResignActiveNotification] {
            observers.append(NotificationCenter.default.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.updateForeground() }
            })
        }
        let menu = NSMenu()
        for (title, action) in [("Open BatCave", #selector(show)), ("Files", #selector(files)), ("Pause sharing", #selector(pause)), ("Start fresh…", #selector(startFresh)), ("Quit BatCave", #selector(quit))] {
            let item = NSMenuItem(title: title, action: action, keyEquivalent: ""); item.target = self; menu.addItem(item)
        }
        let status = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        status.button?.title = "BatCave"; status.menu = menu; statusItem = status
        show()
    }
    private func updateForeground() { model.setForeground(NSApp.isActive && window?.isKeyWindow == true && window?.isVisible == true) }
    func windowDidBecomeKey(_ notification: Notification) { updateForeground() }
    func windowDidResignKey(_ notification: Notification) { model.setForeground(false) }
    func windowWillClose(_ notification: Notification) { model.setForeground(false) }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool { show(); return true }
    func applicationWillTerminate(_ notification: Notification) {
        model.shutdown(); observers.forEach { NotificationCenter.default.removeObserver($0) }
    }
    @objc func show() {
        window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        updateForeground()
    }
    @objc func files() { show(); model.showFileShelf() }
    @objc func pause() { model.pause() }
    @objc func startFresh() {
        do {
            let plan = try model.prepareFreshSetup()
            let confirmation = NSAlert()
            confirmation.messageText = "Start fresh?"
            confirmation.informativeText = "Your current setup will be saved here:\n\n\(plan.backupURL.path)\n\nBatCave will quit. Reopen it to link your computers again. Your existing device keys and Mac permissions will be kept."
            confirmation.addButton(withTitle: "Cancel")
            confirmation.addButton(withTitle: "Back up and start fresh")
            guard confirmation.runModal() == .alertSecondButtonReturn else { return }
            let backup = try model.finishFreshSetup(plan)
            let result = NSAlert()
            result.messageText = "Your setup is backed up"
            result.informativeText = "Saved here:\n\n\(backup.path)\n\nReopen BatCave to start with a new setup. Keep this backup private; it contains your saved computer details."
            result.addButton(withTitle: "Quit BatCave")
            result.runModal()
            NSApp.terminate(nil)
        } catch {
            let alert = NSAlert()
            alert.alertStyle = .warning
            alert.messageText = "Could not start fresh"
            alert.informativeText = error.localizedDescription
            alert.addButton(withTitle: "OK")
            alert.runModal()
        }
    }
    @objc func quit() { NSApp.terminate(nil) }
}

@MainActor
func runSetupPreview() {
    let app = NSApplication.shared
    app.setActivationPolicy(.regular)
    let delegate = SetupApplication()
    app.delegate = delegate
    let menu = NSMenu()
    let item = NSMenuItem(); let appMenu = NSMenu()
    let fresh = NSMenuItem(title: "Start fresh…", action: #selector(SetupApplication.startFresh), keyEquivalent: "")
    fresh.target = delegate; appMenu.addItem(fresh); appMenu.addItem(.separator())
    appMenu.addItem(withTitle: "Quit BatCave", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
    item.submenu = appMenu; menu.addItem(item); app.mainMenu = menu
    app.run()
    withExtendedLifetime(delegate) {}
}
