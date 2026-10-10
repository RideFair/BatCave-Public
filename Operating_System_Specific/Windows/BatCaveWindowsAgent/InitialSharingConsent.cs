namespace BatCave.WindowsAgent;

// Memory only. Created solely after explicit "Link and share" approval on both
// computers, then removed before considering the first authenticated hello.
internal sealed record InitialSharingConsent(string DeviceId, string Fingerprint, bool ReceiveInput,
    long PreviousSession, DateTimeOffset Expires)
{
    internal bool Allows(string deviceId, string fingerprint, bool receiving, long session, DateTimeOffset now) =>
        now < Expires && now >= Expires.AddSeconds(-30) && session == PreviousSession + 1
        && DeviceId == deviceId && Fingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase) && ReceiveInput == receiving;
}
