import XCTest
@testable import BatCaveMacOSAgent

final class NearbyDiscoveryTests: XCTestCase {
    private func bytes(id: String = "windows:fixture", name: String = "Fixture computer") throws -> Data {
        try JSONSerialization.data(withJSONObject: ["type": "batcave.nearby", "version": 1,
            "deviceId": id, "displayName": name, "platform": "windows", "fingerprint": String(repeating: "A", count: 64),
            "inputPort": 47871, "pairingPort": 47873, "pairing": true])
    }
    func testStrictAnnouncementUsesObservedAddressAndActualPorts() throws {
        let data = try bytes(name: "Café computer")
        let entry = try NearbyDiscovery.readAnnouncement(data, source: "192.0.2.10")
        XCTAssertEqual(entry.address, "192.0.2.10")
        XCTAssertEqual(entry.displayName, "Café computer")
        XCTAssertEqual(entry.inputPort, 47871); XCTAssertEqual(entry.pairingPort, 47873)
        XCTAssertTrue(entry.pairing)
        let text = try XCTUnwrap(String(data: data, encoding: .utf8))
        for invalid in [text.dropLast() + ",\"address\":\"198.51.100.2\"}",
                        text.dropLast() + ",\"type\":\"batcave.nearby\"}",
                        text.replacingOccurrences(of: "47871", with: "47873"),
                        text.replacingOccurrences(of: "47871", with: "47871.0"),
                        text.replacingOccurrences(of: String(repeating: "A", count: 64), with: String(repeating: "a", count: 64)),
                        text + "\r"] {
            XCTAssertThrowsError(try NearbyDiscovery.readAnnouncement(Data(invalid.utf8), source: "192.0.2.10"))
        }
        for address in ["127.0.0.1", "0.0.0.0", "239.255.47.87", "255.255.255.255", "::1", "example.invalid"] {
            XCTAssertThrowsError(try NearbyDiscovery.readAnnouncement(data, source: address))
        }
        XCTAssertThrowsError(try NearbyDiscovery.readAnnouncement(Data(repeating: 65, count: 1025), source: "192.0.2.10"))
        XCTAssertThrowsError(try NearbyDiscovery.readAnnouncement(try bytes(name: "Invisible\u{200b} name"), source: "192.0.2.10"))
    }
    func testLANMembershipRejectsOffNetworkBroadcastAndNetworkAddresses() throws {
        let lan = NearbyDiscovery.LANInterface(address: try XCTUnwrap(NearbyDiscovery.ipv4("192.0.2.10")),
            mask: try XCTUnwrap(NearbyDiscovery.ipv4("255.255.255.0")))
        XCTAssertTrue(NearbyDiscovery.isOnLAN("192.0.2.20", interfaces: [lan]))
        for address in ["198.51.100.20", "192.0.2.255", "192.0.2.0", "127.0.0.1"] {
            XCTAssertFalse(NearbyDiscovery.isOnLAN(address, interfaces: [lan]))
        }
        XCTAssertFalse(NearbyDiscovery.isOnLAN("192.0.2.20", interfaces: []))
    }
    func testCacheBoundsOwnIdentityExpiryAndAddressRefresh() throws {
        let now = Date()
        var cache = NearbyComputerCache(localID: "windows:fixture")
        cache.remember(try NearbyDiscovery.readAnnouncement(bytes(), source: "192.0.2.10", now: now), now: now)
        XCTAssertTrue(cache.snapshot(now: now).isEmpty)
        for number in 0..<80 {
            cache.remember(try NearbyDiscovery.readAnnouncement(bytes(id: "linux:fixture\(number)"), source: "192.0.2.10", now: now), now: now)
        }
        XCTAssertEqual(cache.snapshot(now: now).count, 64)
        let later = now.addingTimeInterval(2)
        cache.remember(try NearbyDiscovery.readAnnouncement(bytes(id: "linux:fixture0"), source: "192.0.2.11", now: later), now: later)
        XCTAssertEqual(cache.snapshot(now: later).first { $0.deviceID == "linux:fixture0" }?.address, "192.0.2.11")
        XCTAssertEqual(cache.snapshot(now: now.addingTimeInterval(13)).count, 1)
        XCTAssertTrue(cache.snapshot(now: now.addingTimeInterval(15)).isEmpty)
    }
    func testDiscoveryCachePreservesExactUTF8Identity() throws {
        let now = Date(); var cache = NearbyComputerCache(localID: "macos:fixture")
        for id in ["linux:caf\u{e9}", "linux:cafe\u{301}"] {
            cache.remember(try NearbyDiscovery.readAnnouncement(bytes(id: id), source: "192.0.2.10", now: now), now: now)
        }
        XCTAssertEqual(cache.snapshot(now: now).count, 2)
    }

    func testSearchAgainRetriesFailedSocketStartupAndReportsContinuingFailure() {
        let firstFailure = expectation(description: "Initial socket failure visible")
        let retryFailure = expectation(description: "Repeated socket failure visible")
        let recovered = expectation(description: "Manual retry opens discovery socket")
        let factory = RetryDiscoverySocketFactory()
        let recorder = RetryDiscoveryRecorder(first: firstFailure, second: retryFailure, recovered: recovered)
        let discovery = NearbyDiscovery(localID: "macos:fixture", announcement: { Data("{}".utf8) },
            socketFactory: { try factory.open(port: $0) }, interfaceProvider: { [] },
            onUpdate: { recorder.record($0) })
        defer { discovery.stop() }

        // An ephemeral socket and zero interfaces prevent multicast joins or
        // announcements onto the user's LAN. The first two attempts fail before
        // creating any socket; the third uses the real nonblocking socket setup.
        discovery.start(port: 0)
        wait(for: [firstFailure], timeout: 3)
        XCTAssertFalse(recorder.latest?.running ?? true)
        XCTAssertTrue(recorder.latest?.error?.contains("search again") ?? false)

        discovery.refresh()
        wait(for: [retryFailure], timeout: 3)
        XCTAssertFalse(recorder.latest?.running ?? true)
        XCTAssertNotNil(recorder.latest?.error)

        discovery.refresh()
        wait(for: [recovered], timeout: 3)
        XCTAssertTrue(recorder.latest?.running ?? false)
        XCTAssertEqual(factory.ports, [0, 0, 0], "Retry must retain the originally requested port")
        XCTAssertTrue(recorder.latest?.computers.isEmpty ?? false)
    }
}

private final class RetryDiscoverySocketFactory: @unchecked Sendable {
    private let lock = NSLock()
    private var requested = [UInt16]()
    var ports: [UInt16] { lock.withLock { requested } }
    func open(port: UInt16) throws -> Int32 {
        let attempt = lock.withLock { requested.append(port); return requested.count }
        if attempt <= 2 { throw PairingError("Injected startup failure") }
        return try NearbyDiscovery.makeSocket(port: port)
    }
}

private final class RetryDiscoveryRecorder: @unchecked Sendable {
    private let lock = NSLock()
    private var snapshot: NearbyDiscoverySnapshot?
    private var failures = 0
    private var didRecover = false
    private let first: XCTestExpectation
    private let second: XCTestExpectation
    private let recovered: XCTestExpectation
    var latest: NearbyDiscoverySnapshot? { lock.withLock { snapshot } }
    init(first: XCTestExpectation, second: XCTestExpectation, recovered: XCTestExpectation) {
        self.first = first; self.second = second; self.recovered = recovered
    }
    func record(_ value: NearbyDiscoverySnapshot) {
        lock.withLock { snapshot = value }
        if !value.running && value.error != nil {
            failures += 1
            if failures == 1 { first.fulfill() }
            if failures == 2 { second.fulfill() }
        }
        if value.running && !didRecover { didRecover = true; recovered.fulfill() }
    }
}
