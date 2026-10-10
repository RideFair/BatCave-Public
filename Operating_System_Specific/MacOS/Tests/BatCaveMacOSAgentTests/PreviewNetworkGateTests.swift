import XCTest
@testable import BatCaveMacOSAgent

final class PreviewNetworkGateTests: XCTestCase {
    func testDefaultLegacyTransportRefusesEvenLoopback() {
        let peer = LanBridgePeer()
        defer { peer.dispose() }
        peer.connect(host: "127.0.0.1", port: 9, automaticallyReconnect: true)
        XCTAssertEqual(peer.statusDescription, "Disconnected")
    }

    func testExplicitDevelopmentAccessStillRefusesLANAndHostnames() {
        let peer = LanBridgePeer(allowInsecureLoopback: true)
        defer { peer.dispose() }
        for host in ["192.0.2.1", "example.test", "localhost.example.test", "127.0.0.1.example.test", "[::1]"] {
            peer.connect(host: host, port: 9, automaticallyReconnect: true)
            XCTAssertEqual(peer.statusDescription, "Disconnected", host)
        }
    }
}
