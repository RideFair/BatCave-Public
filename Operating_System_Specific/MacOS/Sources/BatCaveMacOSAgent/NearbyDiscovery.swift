import Foundation
import Darwin

struct NearbyComputer: Equatable, Sendable {
    let deviceID: String
    let displayName: String
    let platform: String
    let fingerprint: String
    let address: String
    let inputPort: UInt16
    let pairingPort: UInt16
    let pairing: Bool
    let seen: Date
}

struct NearbyDiscoverySnapshot: Sendable {
    let computers: [NearbyComputer]
    let error: String?
    let running: Bool
}

struct NearbyComputerCache {
    let localID: String
    private var entries = [Data: NearbyComputer]()
    init(localID: String) { self.localID = localID }
    mutating func remember(_ computer: NearbyComputer, now: Date) {
        expire(now: now)
        let key = Data(computer.deviceID.utf8)
        guard key != Data(localID.utf8), entries.count < 64 || entries[key] != nil else { return }
        entries[key] = computer
    }
    mutating func snapshot(now: Date) -> [NearbyComputer] {
        expire(now: now)
        return entries.values.sorted { $0.displayName.localizedCompare($1.displayName) == .orderedAscending }
    }
    private mutating func expire(now: Date) {
        entries = entries.filter { now.timeIntervalSince($0.value.seen) <= 12 }
    }
}

// Discovery carries public metadata only. This cache never grants approval or
// changes a saved certificate pin. Socket and cache state stay on one queue.
final class NearbyDiscovery: @unchecked Sendable {
    static let port: UInt16 = 47872
    static let group = "239.255.47.87"
    struct LANInterface: Hashable, Sendable {
        let address: UInt32
        let mask: UInt32
    }
    private let queue = DispatchQueue(label: "BatCave.NearbyDiscovery")
    private let localID: String
    private let announcement: @Sendable () throws -> Data
    private let onUpdate: @MainActor @Sendable (NearbyDiscoverySnapshot) -> Void
    private let socketFactory: @Sendable (UInt16) throws -> Int32
    private let interfaceProvider: @Sendable () -> [LANInterface]
    private var socket: Int32 = -1
    private var receiveSource: DispatchSourceRead?
    private var timer: DispatchSourceTimer?
    private var joined = Set<LANInterface>()
    private var networks = [LANInterface]()
    private var cache: NearbyComputerCache
    private var error: String?
    private var generation: UInt64 = 0
    private var revision: UInt64 = 0
    private var rateStart = Date.distantPast
    private var rateCount = 0
    private var receiveSuspended = false
    private var destinationPort: UInt16 = NearbyDiscovery.port
    private var requestedPort: UInt16 = NearbyDiscovery.port

    init(localID: String, announcement: @escaping @Sendable () throws -> Data,
         socketFactory: @escaping @Sendable (UInt16) throws -> Int32 = { try NearbyDiscovery.makeSocket(port: $0) },
         interfaceProvider: @escaping @Sendable () -> [LANInterface] = { NearbyDiscovery.interfaces() },
         onUpdate: @escaping @MainActor @Sendable (NearbyDiscoverySnapshot) -> Void) {
        self.localID = localID; self.announcement = announcement; self.onUpdate = onUpdate
        self.socketFactory = socketFactory; self.interfaceProvider = interfaceProvider
        cache = NearbyComputerCache(localID: localID)
    }

    func start(port: UInt16 = NearbyDiscovery.port) {
        queue.async { [weak self] in self?.startOnQueue(port: port) }
    }
    func refresh() {
        queue.async { [weak self] in
            guard let self else { return }
            if self.socket < 0 { self.startOnQueue(port: self.requestedPort) }
            else { self.announceOnQueue() }
        }
    }
    func stop() { queue.sync { stopOnQueue() } }
    func isLocalAddress(_ address: String) -> Bool {
        queue.sync { Self.isOnLAN(address, interfaces: networks) }
    }

    static func announcement(card: PairingCard, inputPort: UInt16, pairingPort: UInt16, pairing: Bool) throws -> Data {
        try NearbyPairingProtocol.validateLabel(card.deviceId)
        try NearbyPairingProtocol.validateLabel(card.displayName)
        _ = try NearbyPairingProtocol.hex(card.certificateSha256, bytes: 32)
        guard inputPort > 0, pairingPort > 0, inputPort != pairingPort else { throw PairingError("Invalid discovery ports.") }
        let data = try JSONSerialization.data(withJSONObject: ["type": "batcave.nearby", "version": 1,
            "deviceId": card.deviceId, "displayName": card.displayName, "platform": "macos",
            "fingerprint": card.certificateSha256, "inputPort": Int(inputPort), "pairingPort": Int(pairingPort), "pairing": pairing])
        guard data.count <= 1024 else { throw PairingError("This computer's discovery name is too long.") }
        return data
    }

    static func readAnnouncement(_ data: Data, source: String, now: Date = Date()) throws -> NearbyComputer {
        guard isEligibleAddress(source) else { throw PairingError("Invalid discovery address.") }
        let fields = try NearbyPairingProtocol.parseObject(data, maximumBytes: 1024)
        try NearbyPairingProtocol.requireFields(fields, ["type", "version", "deviceId", "displayName", "platform", "fingerprint", "inputPort", "pairingPort", "pairing"])
        guard fields["type"]?.string == "batcave.nearby", fields["version"]?.integer == 1,
              let id = fields["deviceId"]?.string, let name = fields["displayName"]?.string,
              let platform = fields["platform"]?.string, ["windows", "macos", "linux"].contains(platform),
              let fingerprint = fields["fingerprint"]?.string, let input = fields["inputPort"]?.integer,
              let pairingPort = fields["pairingPort"]?.integer, let pairing = fields["pairing"]?.boolean,
              (1...65535).contains(input), (1...65535).contains(pairingPort), input != pairingPort else {
            throw PairingError("Invalid discovery announcement.")
        }
        try NearbyPairingProtocol.validateLabel(id); try NearbyPairingProtocol.validateLabel(name)
        _ = try NearbyPairingProtocol.hex(fingerprint, bytes: 32)
        return NearbyComputer(deviceID: id, displayName: name, platform: platform, fingerprint: fingerprint,
            address: source, inputPort: UInt16(input), pairingPort: UInt16(pairingPort), pairing: pairing, seen: now)
    }

    static func ipv4(_ address: String) -> UInt32? {
        var value = in_addr()
        guard address.withCString({ inet_pton(AF_INET, $0, &value) }) == 1 else { return nil }
        return UInt32(bigEndian: value.s_addr)
    }
    static func isEligibleAddress(_ address: String) -> Bool {
        guard let value = ipv4(address) else { return false }
        let first = value >> 24
        return first > 0 && first < 224 && first != 127 && value != UInt32.max
    }
    static func isOnLAN(_ address: String, interfaces: [LANInterface]) -> Bool {
        guard isEligibleAddress(address), let value = ipv4(address) else { return false }
        return interfaces.contains { network in
            network.mask != 0 && value & network.mask == network.address & network.mask
                && value | network.mask != UInt32.max && value & ~network.mask != 0
        }
    }
    static func interfaces() -> [LANInterface] {
        var pointer: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&pointer) == 0 else { return [] }
        defer { freeifaddrs(pointer) }
        var cursor = pointer; var result = Set<LANInterface>()
        while let current = cursor {
            defer { cursor = current.pointee.ifa_next }
            let value = current.pointee
            let flags = Int32(value.ifa_flags)
            let name = String(cString: value.ifa_name)
            guard flags & IFF_UP != 0, flags & IFF_RUNNING != 0, flags & IFF_MULTICAST != 0,
                  flags & IFF_LOOPBACK == 0, name.hasPrefix("en") || name.hasPrefix("bridge"),
                  let address = value.ifa_addr, let mask = value.ifa_netmask,
                  address.pointee.sa_family == UInt8(AF_INET) else { continue }
            let a = UnsafeRawPointer(address).assumingMemoryBound(to: sockaddr_in.self).pointee.sin_addr.s_addr
            let m = UnsafeRawPointer(mask).assumingMemoryBound(to: sockaddr_in.self).pointee.sin_addr.s_addr
            result.insert(LANInterface(address: UInt32(bigEndian: a), mask: UInt32(bigEndian: m)))
        }
        return result.sorted { $0.address < $1.address }
    }

    static func makeSocket(port: UInt16) throws -> Int32 {
        let descriptor = Darwin.socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
        guard descriptor >= 0 else { throw PairingError("Discovery socket could not open.") }
        var yes: Int32 = 1; var buffer: Int32 = 65536; var ttl: UInt8 = 1; var loop: UInt8 = 0
        var bound = sockaddr_in(); bound.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        bound.sin_family = UInt8(AF_INET); bound.sin_port = port.bigEndian; bound.sin_addr.s_addr = INADDR_ANY
        let configured = setsockopt(descriptor, SOL_SOCKET, SO_REUSEADDR, &yes, socklen_t(MemoryLayout.size(ofValue: yes))) == 0
            && setsockopt(descriptor, SOL_SOCKET, SO_REUSEPORT, &yes, socklen_t(MemoryLayout.size(ofValue: yes))) == 0
            && setsockopt(descriptor, IPPROTO_IP, IP_MULTICAST_TTL, &ttl, 1) == 0
            && setsockopt(descriptor, IPPROTO_IP, IP_MULTICAST_LOOP, &loop, 1) == 0
            && withUnsafePointer(to: &bound) { ptr in
                ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { Darwin.bind(descriptor, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
            } == 0
        guard configured, fcntl(descriptor, F_SETFL, O_NONBLOCK) == 0 else {
            Darwin.close(descriptor); throw PairingError("Discovery socket could not bind.")
        }
        _ = setsockopt(descriptor, SOL_SOCKET, SO_RCVBUF, &buffer, socklen_t(MemoryLayout.size(ofValue: buffer)))
        return descriptor
    }
    private func startOnQueue(port: UInt16) {
        guard socket == -1 else { return }
        requestedPort = port; generation &+= 1
        let currentGeneration = generation
        let descriptor: Int32
        do { descriptor = try socketFactory(port) }
        catch { failedStart(); return }
        guard descriptor >= 0 else { failedStart(); return }
        socket = descriptor; destinationPort = port; error = nil; rateStart = Date(); rateCount = 0
        let source = DispatchSource.makeReadSource(fileDescriptor: descriptor, queue: queue)
        source.setEventHandler { [weak self] in
            guard let self, self.generation == currentGeneration else { return }
            self.receiveOnQueue()
        }
        // Dispatch owns this fd until cancellation completes, preventing an old
        // source from closing a newly reused descriptor after a restart.
        source.setCancelHandler { Darwin.close(descriptor) }
        receiveSource = source; source.resume()
        let ticker = DispatchSource.makeTimerSource(queue: queue)
        ticker.schedule(deadline: .now(), repeating: 3)
        ticker.setEventHandler { [weak self] in
            guard let self, self.generation == currentGeneration else { return }
            self.announceOnQueue()
        }
        timer = ticker; ticker.resume()
    }
    private func failedStart() {
        error = "Nearby discovery is blocked. Allow BatCave on your local network, then search again."
        publish()
    }
    private func refreshInterfaces() {
        let next = Set(interfaceProvider())
        for old in joined.subtracting(next) {
            var membership = membership(for: old)
            _ = setsockopt(socket, IPPROTO_IP, IP_DROP_MEMBERSHIP, &membership, socklen_t(MemoryLayout<ip_mreq>.size))
            joined.remove(old)
        }
        for network in next.subtracting(joined) {
            var membership = membership(for: network)
            if setsockopt(socket, IPPROTO_IP, IP_ADD_MEMBERSHIP, &membership, socklen_t(MemoryLayout<ip_mreq>.size)) == 0 { joined.insert(network) }
            else { error = "Nearby discovery could not join this network. Reconnect Wi-Fi and search again." }
        }
        networks = Array(next)
    }
    private func membership(for network: LANInterface) -> ip_mreq {
        var value = ip_mreq(); _ = Self.group.withCString { inet_pton(AF_INET, $0, &value.imr_multiaddr) }
        value.imr_interface.s_addr = network.address.bigEndian; return value
    }
    private func announceOnQueue() {
        guard socket >= 0 else { return }
        error = nil; refreshInterfaces()
        if networks.isEmpty { error = "Connect both computers to the same Wi-Fi or wired network." }
        do {
            let bytes = try announcement()
            guard bytes.count <= 1024 else { throw PairingError("Discovery announcement exceeds its limit.") }
            var destination = sockaddr_in(); destination.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
            destination.sin_family = UInt8(AF_INET); destination.sin_port = destinationPort.bigEndian
            _ = Self.group.withCString { inet_pton(AF_INET, $0, &destination.sin_addr) }
            for network in joined {
                var address = in_addr(s_addr: network.address.bigEndian)
                let selected = setsockopt(socket, IPPROTO_IP, IP_MULTICAST_IF, &address, socklen_t(MemoryLayout<in_addr>.size)) == 0
                let sent = bytes.withUnsafeBytes { data in
                    withUnsafePointer(to: &destination) { ptr in ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                        sendto(socket, data.baseAddress, data.count, 0, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
                    } }
                }
                if !selected || sent != bytes.count { error = "Nearby discovery is blocked. Allow BatCave on your local network, then search again." }
            }
        } catch { self.error = "Nearby discovery could not announce this computer. Check its name and search again." }
        publish()
    }
    private func receiveOnQueue() {
        guard socket >= 0 else { return }
        // Read at most 64 datagrams per dispatch. Floods are dropped before JSON
        // parsing, and the kernel's receive queue is bounded separately.
        for _ in 0..<64 {
            var source = sockaddr_in(); var size = socklen_t(MemoryLayout<sockaddr_in>.size)
            var bytes = [UInt8](repeating: 0, count: 1025)
            let count = bytes.withUnsafeMutableBytes { data in
                withUnsafeMutablePointer(to: &source) { ptr in ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    recvfrom(socket, data.baseAddress, data.count, 0, $0, &size)
                } }
            }
            if count < 0 { return }
            let now = Date()
            if now.timeIntervalSince(rateStart) >= 1 { rateStart = now; rateCount = 0 }
            rateCount += 1
            if rateCount > 64 {
                if let source = receiveSource, !receiveSuspended {
                    receiveSuspended = true; source.suspend(); let currentGeneration = generation
                    queue.asyncAfter(deadline: .now() + 0.1) { [weak self] in
                        guard let self, self.generation == currentGeneration, self.receiveSuspended else { return }
                        self.receiveSuspended = false; self.receiveSource?.resume()
                    }
                }
                return
            }
            guard count <= 1024, source.sin_family == UInt8(AF_INET) else { continue }
            var host = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
            guard inet_ntop(AF_INET, &source.sin_addr, &host, socklen_t(host.count)) != nil else { continue }
            let address = String(decoding: host.prefix { $0 != 0 }.map { UInt8(bitPattern: $0) }, as: UTF8.self)
            guard Self.isOnLAN(address, interfaces: networks),
                  let computer = try? Self.readAnnouncement(Data(bytes.prefix(count)), source: address, now: now) else { continue }
            cache.remember(computer, now: now); publish()
        }
    }
    private func publish() {
        revision &+= 1; let version = revision
        let value = NearbyDiscoverySnapshot(computers: cache.snapshot(now: Date()),
            error: error, running: socket >= 0)
        DispatchQueue.main.async { [weak self] in
            guard let self, self.queue.sync(execute: { self.revision == version }) else { return }
            self.onUpdate(value)
        }
    }
    private func stopOnQueue() {
        generation &+= 1; timer?.cancel(); timer = nil
        if receiveSuspended { receiveSource?.resume(); receiveSuspended = false }
        receiveSource?.cancel(); receiveSource = nil; socket = -1
        joined.removeAll(); networks.removeAll(); cache = NearbyComputerCache(localID: localID); error = nil; publish()
    }
    deinit { timer?.cancel(); if receiveSuspended { receiveSource?.resume() }; receiveSource?.cancel() }
}
