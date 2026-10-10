@preconcurrency import ApplicationServices
@preconcurrency import AppKit
import SwiftUI
import ServiceManagement

func setupPreviewRequested() -> Bool {
    CommandLine.arguments.contains("--setup") || CommandLine.arguments.contains("--setup-preview") || Bundle.main.object(forInfoDictionaryKey: "BatCaveSetupPreview") as? Bool == true
}

private struct PreviewProfile: Codable {
    var peers: [PairingCard] = []
    var selectedPeerId: String?
    var hosts: [String: String] = [:]
    var macOnRight = true
    var completed = false
}

@MainActor
final class SetupModel: ObservableObject {
    let files = FilesShelfModel()
    @Published var showFiles = false
    @Published var step = 0
    @Published var accessibility = false
    @Published var inputMonitoring = false
    @Published var status = "Not connected"
    @Published var message: String?
    @Published var deviceName = Host.current().localizedName ?? "My Mac"
    @Published var peers: [PairingCard] = []
    @Published var selectedPeerId = ""
    @Published var host = ""
    @Published var macOnRight = true
    @Published var connected = false
    @Published var receiving = false
    @Published var clipboard = false
    @Published var loginEnabled = false
    @Published var identityReady = false
    private var profile = PreviewProfile()
    private var identity: PairingIdentity?
    private var lan: LanBridgePeer?
    private var agent: BatCaveMacOSAgent?
    private var installed = false
    private var timer: Timer?
    private var sleepObserver: NSObjectProtocol?
    private let directory: URL
    private var profileURL: URL { directory.appendingPathComponent("setup.json") }
    var peer: PairingCard? { peers.first { $0.deviceId == selectedPeerId } }
    var fingerprint: String { identity?.card.certificateSha256 ?? "" }
    var permissionsReady: Bool { accessibility && inputMonitoring }

    init() {
        directory = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("BatCave/SecurePreview")
        if let data = try? Data(contentsOf: profileURL), data.count <= 256 * 1024,
           let saved = try? JSONDecoder().decode(PreviewProfile.self, from: data) {
            profile = saved
            peers = saved.peers.filter { (try? PairingCard.parse($0.encoded())) != nil }
            selectedPeerId = saved.selectedPeerId ?? ""
            host = saved.hosts[selectedPeerId] ?? ""
            macOnRight = saved.macOnRight
        }
        refresh()
        loginEnabled = SMAppService.mainApp.status == .enabled
        timer = Timer.scheduledTimer(withTimeInterval: 0.6, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.refresh() }
        }
        sleepObserver = NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.willSleepNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.agent?.stop(reason: "Mac sleeping")
                self?.lan?.disconnect(reason: "Mac sleeping")
                self?.receiving = false
            }
        }
        if profile.completed, peer != nil, !host.isEmpty {
            step = 3
            connect() // Saved approval reconnects securely; input remains paused.
        }
    }

    func refresh() {
        accessibility = AXIsProcessTrusted()
        inputMonitoring = CGPreflightListenEventAccess()
        if let lan {
            connected = lan.isAuthenticated
            status = lan.statusDescription
            receiving = lan.isSecureInputEnabled
        }
    }

    func permission(_ input: Bool) {
        if input { _ = CGRequestListenEventAccess() }
        else { _ = AXIsProcessTrustedWithOptions([kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary) }
        let pane = input ? "Privacy_ListenEvent" : "Privacy_Accessibility"
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?" + pane) {
            NSWorkspace.shared.open(url)
        }
        message = "Enable BatCave in System Settings, then return here. If macOS asks you to quit, reopen BatCave."
    }

    func next() {
        message = nil
        if step == 0 {
            guard permissionsReady else { message = "Enable both permissions to continue."; return }
            guard prepareIdentity() else { return }
        }
        if step == 1 && peer == nil { message = "Import and approve the Windows computer's pairing card."; return }
        step = min(3, step + 1)
    }

    @discardableResult func prepareIdentity() -> Bool {
        if identity != nil { return true }
        do {
            guard !deviceName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, deviceName.count <= 128 else { throw PairingError("Enter a short name for this Mac.") }
            let value = try PairingIdentity.openOrCreate(at: directory, displayName: deviceName)
            let peer = LanBridgePeer(secureIdentity: value)
            let agent = BatCaveMacOSAgent(lan: peer, showHUD: false)
            self.identity = value; self.lan = peer; self.agent = agent
            agent.startClipboardSync()
            if let selected = self.peer { peer.approve(selected) }
            identityReady = true
            return true
        } catch { message = error.localizedDescription; return false }
    }

    func exportCard() {
        guard prepareIdentity(), let identity else { return }
        let panel = NSSavePanel()
        panel.nameFieldStringValue = "BatCave-Mac-pairing.json"
        panel.allowedContentTypes = [.json]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do { try identity.card.encoded().write(to: url, options: .atomic); message = "Saved your public card. Import it in BatCave on Windows." }
        catch { message = error.localizedDescription }
    }

    func copyCard() {
        guard prepareIdentity(), let identity else { return }
        do {
            let text = String(decoding: try identity.card.encoded(), as: UTF8.self)
            NSPasteboard.general.clearContents(); NSPasteboard.general.setString(text, forType: .string)
            message = "Public pairing card copied. Paste it on your Windows computer."
        } catch { message = error.localizedDescription }
    }

    func importCard(paste: Bool) {
        do {
            let data: Data
            if paste {
                guard let text = NSPasteboard.general.string(forType: .string) else { throw PairingError("Copy a Windows pairing card first.") }
                data = Data(text.utf8)
            } else {
                let panel = NSOpenPanel(); panel.canChooseDirectories = false; panel.allowsMultipleSelection = false
                panel.allowedContentTypes = [.json]
                guard panel.runModal() == .OK, let url = panel.url else { return }
                let size = try url.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0
                guard size <= 16 * 1024 else { throw PairingError("Pairing cards must be smaller than 16 KB.") }
                data = try Data(contentsOf: url)
            }
            let card = try PairingCard.parse(data)
            guard card.deviceId.hasPrefix("windows:"), card.deviceId != identity?.card.deviceId else { throw PairingError("Import the Windows computer's card here.") }
            let alert = NSAlert()
            alert.messageText = "Pair with \(card.displayName)?"
            alert.informativeText = "Compare this full fingerprint with the one shown on that computer. Approve only if it matches.\n\n" + card.certificateSha256
            alert.addButton(withTitle: "Approve this computer"); alert.addButton(withTitle: "Cancel")
            guard alert.runModal() == .alertFirstButtonReturn else { return }
            peers.removeAll { $0.deviceId == card.deviceId }; peers.append(card)
            selectedPeerId = card.deviceId
            selectPeer()
            message = "\(card.displayName) approved. Import this Mac's card on Windows too."
        } catch { message = error.localizedDescription }
    }

    func selectPeer() {
        agent?.stop(reason: "Selected computer changed")
        receiving = false; connected = false
        lan?.approve(peer)
        host = profile.hosts[selectedPeerId] ?? ""
        save()
    }

    func unpair() {
        agent?.stop(reason: "Computer unpaired"); lan?.approve(nil)
        peers.removeAll { $0.deviceId == selectedPeerId }
        profile.hosts.removeValue(forKey: selectedPeerId)
        selectedPeerId = peers.first?.deviceId ?? ""
        selectPeer(); save()
    }

    func save() {
        profile.peers = peers; profile.selectedPeerId = selectedPeerId.isEmpty ? nil : selectedPeerId
        if !selectedPeerId.isEmpty { profile.hosts[selectedPeerId] = host.trimmingCharacters(in: .whitespacesAndNewlines) }
        profile.macOnRight = macOnRight
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            try JSONEncoder().encode(profile).write(to: profileURL, options: .atomic)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: profileURL.path)
        } catch { message = "Could not save setup: " + error.localizedDescription }
    }

    func connect() {
        guard prepareIdentity(), let peer else { return }
        let address = host.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !address.isEmpty, address.utf8.count <= 253, !address.contains(where: { $0.isWhitespace }), !address.contains("://") else {
            message = "Enter the Windows computer's local address or hostname, without a URL or port."; return
        }
        lan?.approve(peer); save()
        lan?.connect(host: address, port: 47871, automaticallyReconnect: true)
        message = "Open BatCave on Windows, select this Mac, and keep its secure listener running."
    }

    func enableInput() {
        guard permissionsReady, connected else { message = "Grant both permissions and connect securely first."; return }
        let others = NSWorkspace.shared.runningApplications.filter {
            $0.processIdentifier != ProcessInfo.processInfo.processIdentifier && ["dev.batcave.macos-agent", "dev.batcave.setup-preview", "dev.batcave.macos-agent.secure-preview"].contains($0.bundleIdentifier ?? "")
        }
        guard others.isEmpty else { message = "Quit the other BatCave app before enabling input here. Your existing setup remains available."; return }
        if !installed {
            installed = agent?.install(exitOnFailure: false) == true
            guard installed else { message = "macOS has not enabled input access yet. Reopen BatCave after granting both permissions."; return }
        }
        lan?.enableSecureInput(true, clipboard: clipboard)
        receiving = true
        message = "Now choose Start sharing on Windows. Match its left/right setting to the diagram here."
    }

    func pause() {
        agent?.stop(reason: "Sharing paused")
        lan?.enableSecureInput(false)
        // FIN also restores the Windows source's local input. Keeping an idle
        // socket while discarding its events would strand the source in control.
        lan?.stopConnecting()
        receiving = false
    }

    func changeClipboard() { if receiving { lan?.enableSecureInput(true, clipboard: clipboard) } }

    func login(_ enabled: Bool) {
        do {
            if enabled { try SMAppService.mainApp.register() }
            else { try SMAppService.mainApp.unregister() }
            loginEnabled = SMAppService.mainApp.status == .enabled
            if !loginEnabled && enabled { message = "Approve BatCave under Login Items in System Settings." }
        } catch { message = "Could not change Open at login: " + error.localizedDescription }
    }

    func finish() {
        profile.completed = true; save()
        message = "Setup saved. BatCave stays available in the menu bar when you close this window."
    }

    func shutdown() {
        agent?.stop(reason: "App quit"); lan?.dispose(); timer?.invalidate()
        if let sleepObserver { NSWorkspace.shared.notificationCenter.removeObserver(sleepObserver) }
    }
}

private struct SetupView: View {
    @ObservedObject var model: SetupModel
    private let steps = ["Permissions", "Pair computers", "Arrange screens", "Link devices"]
    var body: some View {
        HStack(spacing: 0) {
            VStack(alignment: .leading, spacing: 30) {
                VStack(alignment: .leading, spacing: 6) {
                    Image(systemName: "display.2").font(.system(size: 32)).foregroundStyle(.blue)
                    Text("BatCave").font(.system(size: 26, weight: .bold))
                    Text("DEVICE SETUP").font(.system(size: 10, weight: .semibold)).tracking(1.6).foregroundStyle(.secondary)
                }.padding(.bottom, 8)
                ForEach(0..<4) { index in
                    HStack(spacing: 12) {
                        ZStack {
                            Circle().fill(index == model.step ? Color.blue : Color.secondary.opacity(0.12)).frame(width: 26, height: 26)
                            Text(index < model.step ? "✓" : "\(index + 1)").font(.system(size: 12, weight: .semibold)).foregroundStyle(index == model.step ? .white : .primary)
                        }
                        Text(steps[index]).font(.system(size: 13, weight: index == model.step ? .semibold : .regular))
                    }
                }
                Spacer()
                Button { model.showFiles = true } label: { Label("Files", systemImage: "doc.on.doc") }
                    .font(.system(size: 12))
                Label("Encrypted device linking", systemImage: "lock.shield").font(.system(size: 12)).foregroundStyle(.secondary)
            }.padding(28).frame(width: 215).background(Color(nsColor: .windowBackgroundColor))
            Divider()
            VStack(alignment: .leading, spacing: 18) {
                HStack {
                    Text("STEP \(model.step + 1) OF 4").font(.system(size: 10, weight: .bold)).tracking(1.3).foregroundStyle(.secondary)
                    Spacer()
                    Label(model.status, systemImage: model.connected ? "lock.shield.fill" : "circle").font(.system(size: 11)).foregroundStyle(model.connected ? .green : .secondary)
                }
                ScrollView {
                    VStack(alignment: .leading, spacing: 20) {
                        switch model.step {
                        case 0: permissions
                        case 1: pairing
                        case 2: layout
                        default: linking
                        }
                        if let message = model.message {
                            Label(message, systemImage: "info.circle").font(.system(size: 12)).foregroundStyle(.secondary)
                                .padding(12).frame(maxWidth: .infinity, alignment: .leading).background(Color.blue.opacity(0.06)).cornerRadius(8)
                        }
                    }.frame(maxWidth: .infinity, alignment: .leading).padding(.trailing, 4)
                }
                Divider()
                HStack {
                    if model.step > 0 { Button("Back") { model.step -= 1; model.message = nil } }
                    Spacer()
                    if model.step < 3 {
                        Button(model.step == 2 ? "Link devices" : "Continue") { model.next() }
                            .buttonStyle(.borderedProminent)
                            .disabled(model.step == 0 ? !model.permissionsReady : model.step == 1 ? model.peer == nil : !model.connected)
                    } else {
                        Button("Save setup") { model.finish() }.buttonStyle(.borderedProminent)
                    }
                }
            }.padding(30).frame(maxWidth: .infinity, maxHeight: .infinity)
        }.frame(minWidth: 880, minHeight: 640).background(Color(nsColor: .controlBackgroundColor))
            .sheet(isPresented: $model.showFiles) {
                VStack(spacing: 0) {
                    FilesShelfView(model: model.files)
                    HStack { Spacer(); Button("Done") { model.showFiles = false }.keyboardShortcut(.cancelAction) }.padding([.horizontal, .bottom], 22)
                }
            }
    }
    private func heading(_ title: String, _ subtitle: String) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title).font(.system(size: 25, weight: .semibold))
            Text(subtitle).font(.system(size: 13)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        }
    }
    private var permissions: some View {
        VStack(alignment: .leading, spacing: 18) {
            heading("One keyboard. Both computers.", "Move your Windows mouse onto your Mac and keep typing. First, give BatCave the two permissions macOS requires.")
            permissionRow("Accessibility", "Lets BatCave move the pointer and type on your Mac.", ready: model.accessibility, input: false)
            permissionRow("Input Monitoring", "Lets BatCave detect input and safely release held keys.", ready: model.inputMonitoring, input: true)
            Button("Check permissions again") { model.refresh() }
            Text("If another BatCave app is open, quit it before enabling input here.")
                .font(.system(size: 12)).foregroundStyle(.secondary)
        }
    }
    private func permissionRow(_ name: String, _ description: String, ready: Bool, input: Bool) -> some View {
        HStack(spacing: 14) {
            Image(systemName: ready ? "checkmark.circle.fill" : "lock.circle").font(.system(size: 24)).foregroundStyle(ready ? .green : .secondary)
            VStack(alignment: .leading, spacing: 5) { Text(name).fontWeight(.semibold); Text(description).font(.system(size: 12)).foregroundStyle(.secondary) }
            Spacer()
            Button(ready ? "Enabled" : "Open settings") { model.permission(input) }.disabled(ready)
        }.padding(18).background(Color(nsColor: .windowBackgroundColor)).cornerRadius(10)
    }
    private var pairing: some View {
        VStack(alignment: .leading, spacing: 18) {
            heading("Pair with your Windows computer", "Exchange a public pairing card in both directions. Approve only the computer whose fingerprint you’ve checked.")
            HStack { Button("Copy Mac card") { model.copyCard() }; Button("Paste Windows card") { model.importCard(paste: true) } }
            DisclosureGroup("Use a card file") {
                HStack { Button("Save Mac card…") { model.exportCard() }; Button("Import Windows card…") { model.importCard(paste: false) } }
            }
            if !model.peers.isEmpty {
                Picker("Paired computer", selection: $model.selectedPeerId) {
                    ForEach(model.peers, id: \.deviceId) { Text($0.displayName).tag($0.deviceId) }
                }.onChange(of: model.selectedPeerId) { _ in model.selectPeer() }
                if model.peer != nil {
                    Button("Unpair this computer", role: .destructive) { model.unpair() }
                }
            }
            Text("Cards contain public identity information. Your private key stays in this Mac’s Keychain.")
                .font(.system(size: 12)).foregroundStyle(.secondary)
            DisclosureGroup("Connection details") {
                if model.identityReady {
                    Text("This Mac’s fingerprint").font(.system(size: 11, weight: .medium))
                    Text(model.fingerprint).font(.system(size: 10, design: .monospaced)).textSelection(.enabled)
                }
                if let peer = model.peer {
                    Text("Paired computer’s fingerprint").font(.system(size: 11, weight: .medium))
                    Text(peer.certificateSha256).font(.system(size: 10, design: .monospaced)).textSelection(.enabled)
                }
            }
        }
    }
    private var layout: some View {
        VStack(alignment: .leading, spacing: 18) {
            heading("Arrange your computers", "Match this diagram to your desk. Set the same Mac placement in BatCave on Windows, which controls the crossing boundary.")
            ScreenArrangement(macOnRight: model.macOnRight, peerName: model.peer?.displayName ?? "Windows")
            Picker("The Mac sits", selection: $model.macOnRight) { Text("Left of Windows").tag(false); Text("Right of Windows").tag(true) }
                .pickerStyle(.segmented).onChange(of: model.macOnRight) { _ in model.save() }
            Text("This Mac has \(NSScreen.screens.count) display\(NSScreen.screens.count == 1 ? "" : "s"). Each computer’s displays are shown as one desktop.")
                .font(.system(size: 12)).foregroundStyle(.secondary)
            HStack { TextField("Windows local address or hostname", text: $model.host); Button(model.connected ? "Reconnect" : "Connect securely") { model.connect() }.disabled(model.host.isEmpty) }
            Text("Find the address in BatCave on Windows. Both computers must be on the same local network.").font(.system(size: 12)).foregroundStyle(.secondary)
            if model.connected { Label("Encrypted connection verified", systemImage: "lock.shield.fill").foregroundStyle(.green) }
        }
    }
    private var linking: some View {
        VStack(alignment: .leading, spacing: 16) {
            heading("Link devices", "Enable Mac input here, then choose Start sharing on Windows. Move your mouse through the shared edge to use your Mac.")
            HStack {
                if !model.connected { Button("Reconnect") { model.connect() }.disabled(model.peer == nil || model.host.isEmpty) }
                Button(model.receiving ? "Pause Mac input" : "Enable Mac input") { if model.receiving { model.pause() } else { model.enableInput() } }
                    .buttonStyle(.borderedProminent).disabled(!model.connected || !model.permissionsReady)
                Text(model.receiving ? "Ready for Windows input" : "Input is paused").font(.system(size: 12)).foregroundStyle(.secondary)
            }
            Toggle("Share text clipboard while controlling the Mac", isOn: $model.clipboard).onChange(of: model.clipboard) { _ in model.changeClipboard() }
            Toggle("Open BatCave at login", isOn: Binding(get: { model.loginEnabled }, set: { model.login($0) }))
            Text("Normal crossings stay quiet. Escape or F12 stops control. After a lost connection, input stays paused until you enable it again.")
                .font(.system(size: 12)).foregroundStyle(.secondary)
        }
    }
}

private struct ScreenArrangement: View {
    let macOnRight: Bool
    let peerName: String
    var body: some View {
        HStack(spacing: 18) {
            monitor(macOnRight ? peerName : "This Mac", mac: !macOnRight)
            Image(systemName: "arrow.left.arrow.right").foregroundStyle(.blue).font(.system(size: 20))
            monitor(macOnRight ? "This Mac" : peerName, mac: macOnRight)
        }.frame(maxWidth: .infinity).padding(24).background(Color.blue.opacity(0.04)).cornerRadius(12)
    }
    private func monitor(_ title: String, mac: Bool) -> some View {
        VStack(spacing: 5) {
            VStack(spacing: 8) {
                Image(systemName: mac ? "desktopcomputer" : "display").font(.system(size: 27))
                Text(title).font(.system(size: 12, weight: .medium)).lineLimit(1)
                Text(mac ? "Receives input" : "Mouse + keyboard").font(.system(size: 10)).foregroundStyle(.secondary)
            }.frame(width: 165, height: 100).background(Color(nsColor: .windowBackgroundColor)).cornerRadius(8)
                .overlay(RoundedRectangle(cornerRadius: 8).stroke(mac ? Color.blue : Color.secondary.opacity(0.3), lineWidth: 2))
            Rectangle().fill(Color.secondary.opacity(0.3)).frame(width: 4, height: 9)
            Capsule().fill(Color.secondary.opacity(0.3)).frame(width: 38, height: 3)
        }
    }
}

@MainActor
private final class SetupApplication: NSObject, NSApplicationDelegate {
    let model = SetupModel()
    var window: NSWindow?
    var statusItem: NSStatusItem?
    private var signals: [DispatchSourceSignal] = []
    func applicationDidFinishLaunching(_ notification: Notification) {
        for number in [SIGINT, SIGTERM] {
            signal(number, SIG_IGN)
            let source = DispatchSource.makeSignalSource(signal: number, queue: .main)
            source.setEventHandler { Task { @MainActor in NSApp.terminate(nil) } }
            source.resume(); signals.append(source)
        }
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 920, height: 670), styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        window.title = "BatCave — Device setup"
        window.contentView = NSHostingView(rootView: SetupView(model: model))
        window.isReleasedWhenClosed = false; window.center(); window.makeKeyAndOrderFront(nil)
        self.window = window
        let menu = NSMenu()
        for (title, action) in [("Open setup", #selector(show)), ("Files", #selector(files)), ("Pause input", #selector(pause)), ("Quit BatCave", #selector(quit))] {
            let item = NSMenuItem(title: title, action: action, keyEquivalent: ""); item.target = self; menu.addItem(item)
        }
        let status = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        status.button?.title = "BatCave"; status.menu = menu; statusItem = status
        NSApp.activate(ignoringOtherApps: true)
    }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool { show(); return true }
    func applicationWillTerminate(_ notification: Notification) { model.shutdown() }
    @objc func show() { window?.makeKeyAndOrderFront(nil); NSApp.activate(ignoringOtherApps: true) }
    @objc func files() { show(); model.showFiles = true }
    @objc func pause() { model.pause() }
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
    appMenu.addItem(withTitle: "Quit BatCave", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
    item.submenu = appMenu; menu.addItem(item); app.mainMenu = menu
    app.run()
    withExtendedLifetime(delegate) {}
}
