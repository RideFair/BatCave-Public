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
}
