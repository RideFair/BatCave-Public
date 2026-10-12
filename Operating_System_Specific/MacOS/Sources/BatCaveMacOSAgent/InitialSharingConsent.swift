import Foundation

/// An approval authorizes only the first new authenticated input session.
/// This value is never encoded in the saved profile.
struct InitialSharingConsent: Equatable, Sendable {
    let pairingSession: UUID
    let peerID: String
    let fingerprint: String
    let role: LanInputRole
    let expires: Date
    let monotonicExpires: TimeInterval
    let previousInputSession: String?

    init(pairingSession: UUID, peerID: String, fingerprint: String, role: LanInputRole,
         now: Date = Date(), previousInputSession: String? = nil,
         uptime: TimeInterval = ProcessInfo.processInfo.systemUptime) {
        self.pairingSession = pairingSession
        self.peerID = peerID
        self.fingerprint = fingerprint.uppercased()
        self.role = role
        expires = now.addingTimeInterval(30)
        monotonicExpires = uptime + 30
        self.previousInputSession = previousInputSession
    }

    func permits(peerID: String, fingerprint: String, role: LanInputRole,
                 inputSession: String, now: Date = Date(),
                 uptime: TimeInterval = ProcessInfo.processInfo.systemUptime) -> Bool {
        now < expires && uptime < monotonicExpires
            && self.peerID.utf8.elementsEqual(peerID.utf8) && self.fingerprint == fingerprint.uppercased()
            && self.role == role && inputSession != previousInputSession
    }
}

struct InitialSharingGate: Sendable {
    private(set) var consent: InitialSharingConsent?
    mutating func grant(_ value: InitialSharingConsent) { consent = value }
    mutating func revoke() { consent = nil }

    mutating func consume(peerID: String, fingerprint: String, role: LanInputRole,
                          inputSession: String, now: Date = Date(),
                          uptime: TimeInterval = ProcessInfo.processInfo.systemUptime) -> Bool {
        // Clear before any permission check or native activation can fail.
        let value = consent
        consent = nil
        return value?.permits(peerID: peerID, fingerprint: fingerprint, role: role,
                              inputSession: inputSession, now: now, uptime: uptime) == true
    }
}
