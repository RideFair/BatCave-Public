using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text;

namespace BatCave.WindowsAgent;

internal sealed class DeviceIdentityStore : IDisposable
{
    private sealed record IdentityReference(string DeviceId, string CertificateThumbprint, string CngKeyName);
    public const string PairingCardSchema = "batcave.pairing-card.v1";
    public sealed record PairingCard(string Schema, string DeviceId, string DisplayName,
        string CertificateDerBase64, string CertificateSha256);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly IdentityReference _reference;
    public X509Certificate2 Certificate { get; }
    public string DeviceId => _reference.DeviceId;
    public string Fingerprint => SecurePeerTransport.Fingerprint(Certificate);

    private DeviceIdentityStore(IdentityReference reference, X509Certificate2 certificate)
    {
        _reference = reference;
        Certificate = certificate;
    }

    public static DeviceIdentityStore OpenOrCreate(string directory)
    {
        if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException("Windows CNG is required."); }
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "identity.json");
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 4096) { throw new InvalidDataException("Invalid identity reference size."); }
            var reference = JsonSerializer.Deserialize<IdentityReference>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("Missing identity reference.");
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            var certificates = store.Certificates.Find(X509FindType.FindByThumbprint, reference.CertificateThumbprint, false);
            var certificate = certificates.Cast<X509Certificate2>().FirstOrDefault(cert => cert.HasPrivateKey)
                ?? throw new InvalidOperationException("The stored device key is unavailable. Restore it or explicitly reset and re-pair; identity was not replaced.");
            return new DeviceIdentityStore(reference, certificate);
        }

        var deviceId = "windows:" + Guid.NewGuid().ToString("N");
        var keyName = "BatCave-" + Guid.NewGuid().ToString("N");
        var parameters = new CngKeyCreationParameters
        {
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
            ExportPolicy = CngExportPolicies.None, KeyUsage = CngKeyUsages.Signing
        };
        parameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(3072), CngPropertyOptions.None));
        using var key = CngKey.Create(CngAlgorithm.Rsa, keyName, parameters);
        using var rsa = new RSACng(key);
        var request = new CertificateRequest("CN=BatCave " + deviceId, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
        {
            new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2")
        }, false));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(3));
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            store.Add(generated);
            var reference = new IdentityReference(deviceId, generated.Thumbprint, keyName);
            // This file contains only a reference; the private key remains non-exportable
            // in the current user's Windows CNG key provider.
            File.WriteAllText(path + ".new", JsonSerializer.Serialize(reference, JsonOptions));
            File.Move(path + ".new", path, overwrite: false);
            var certificates = store.Certificates.Find(X509FindType.FindByThumbprint, generated.Thumbprint, false);
            return new DeviceIdentityStore(reference, certificates[0]);
        }
        catch
        {
            key.Delete();
            throw;
        }
    }

    public PairingCard ExportPublicCard(string displayName) => new(PairingCardSchema,
        DeviceId, displayName, Convert.ToBase64String(Certificate.RawData), Fingerprint);

    public static ApprovedPeer ReadPublicCard(string json, bool requireCurrentCertificate = true)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 16384) { throw new InvalidDataException("Pairing card is too large."); }
        var card = JsonSerializer.Deserialize<PairingCard>(json, JsonOptions) ?? throw new InvalidDataException("Pairing card is empty.");
        if (card.Schema != PairingCardSchema || string.IsNullOrWhiteSpace(card.DeviceId)
            || card.DeviceId.EnumerateRunes().Count() > 128 || card.DeviceId.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(card.DisplayName) || card.DisplayName.EnumerateRunes().Count() > 128 || card.DisplayName.Any(char.IsControl)
            || string.IsNullOrEmpty(card.CertificateDerBase64) || card.CertificateSha256?.Length != 64)
            throw new InvalidDataException("Unsupported or invalid pairing card.");
        using var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(card.CertificateDerBase64));
        var peer = new ApprovedPeer(card.DeviceId, card.CertificateSha256);
        var actualFingerprint = SecurePeerTransport.Fingerprint(certificate);
        if (!string.Equals(actualFingerprint, card.CertificateSha256, StringComparison.OrdinalIgnoreCase)
            || (requireCurrentCertificate && !SecurePeerTransport.MatchesApprovedPeer(certificate, peer)))
            throw new InvalidDataException("Pairing certificate is expired or does not match its fingerprint.");
        return peer;
    }

    public static string SerializePublicCard(PairingCard card) => JsonSerializer.Serialize(card, JsonOptions);

    // For test cleanup / explicit identity reset only. Never called by startup,
    // reconnect, upgrades, or missing-key handling.
    public void DeleteIdentity(string directory)
    {
        if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException(); }
        var path = Path.Combine(directory, "identity.json");
        var current = JsonSerializer.Deserialize<IdentityReference>(File.ReadAllText(path), JsonOptions);
        if (current != _reference) { throw new InvalidOperationException("Identity reference changed; refusing reset."); }
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Remove(Certificate);
        using var key = CngKey.Open(_reference.CngKeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider);
        key.Delete();
        File.Delete(path);
    }

    public void Dispose() => Certificate.Dispose();
}
