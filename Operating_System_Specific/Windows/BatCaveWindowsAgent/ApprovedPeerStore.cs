using System.Text.Json;

namespace BatCave.WindowsAgent;

internal sealed class ApprovedPeerStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private DeviceIdentityStore.PairingCard[] _cards;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public ApprovedPeerStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "approved-peers.json");
        if (File.Exists(_path) && new FileInfo(_path).Length > 1024 * 1024)
            throw new InvalidDataException("Approval store exceeds its limit.");
        _cards = File.Exists(_path)
            ? JsonSerializer.Deserialize<DeviceIdentityStore.PairingCard[]>(File.ReadAllText(_path), Options)
                ?? throw new InvalidDataException("Invalid approval store.")
            : [];
        Validate(_cards, requireCurrentCertificate: false);
    }

    public DeviceIdentityStore.PairingCard[] Cards { get { lock (_gate) { return _cards.ToArray(); } } }
    public ApprovedPeer[] Peers => Cards.Select(card => new ApprovedPeer(card.DeviceId, card.CertificateSha256)).ToArray();

    // Call only after the user explicitly confirms the full public identity.
    public void Approve(DeviceIdentityStore.PairingCard card)
    {
        lock (_gate)
        {
            var replacement = _cards.Where(existing => existing.DeviceId != card.DeviceId).Append(card).ToArray();
            Validate(replacement);
            Save(replacement);
        }
    }

    public void Remove(string deviceId)
    {
        lock (_gate) { Save(_cards.Where(card => card.DeviceId != deviceId).ToArray()); }
    }

    private static void Validate(DeviceIdentityStore.PairingCard[] cards, bool requireCurrentCertificate = true)
    {
        if (cards.Length > 64 || cards.Select(card => card.DeviceId).Distinct(StringComparer.Ordinal).Count() != cards.Length
            || cards.Select(card => card.CertificateSha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() != cards.Length)
            throw new InvalidDataException("Duplicate or excessive approved identities.");
        foreach (var card in cards) { DeviceIdentityStore.ReadPublicCard(DeviceIdentityStore.SerializePublicCard(card), requireCurrentCertificate); }
    }

    private void Save(DeviceIdentityStore.PairingCard[] replacement)
    {
        File.WriteAllText(_path + ".new", JsonSerializer.Serialize(replacement, Options));
        File.Move(_path + ".new", _path, overwrite: true);
        _cards = replacement;
    }
}
