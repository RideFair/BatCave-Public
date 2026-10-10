using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace BatCave.WindowsAgent;

// A public-identity bootstrap only. This stream can never dispatch input.
// See Universal/Protocol/Nearby_Pairing.md for exact cross-platform encodings.
internal static class NearbyPairingProtocol
{
    internal sealed record Descriptor(DeviceIdentityStore.PairingCard Card, int InputPort);
    internal sealed record Confirmation(Descriptor Peer, string Code, bool Initiator);
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static byte[] Pack(params byte[][] parts)
    {
        using var output = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var part in parts)
        {
            BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)part.Length));
            output.Write(length); output.Write(part);
        }
        return output.ToArray();
    }

    internal static byte[] IdentityBytes(string session, string role, Descriptor descriptor, byte[] nonce)
    {
        if (role is not ("i" or "r") || nonce.Length != 32) throw new InvalidDataException("Invalid pairing contribution.");
        ValidateDescriptor(descriptor);
        return Pack(Utf8.GetBytes("batcave.pair.v1.identity"), Hex(session, 16, lower: true), Utf8.GetBytes(role),
            Utf8.GetBytes(descriptor.Card.DeviceId), Utf8.GetBytes(descriptor.Card.DisplayName),
            Convert.FromBase64String(descriptor.Card.CertificateDerBase64),
            Utf8.GetBytes(descriptor.InputPort.ToString(CultureInfo.InvariantCulture)), nonce);
    }

    internal static byte[] Transcript(byte[] initiator, byte[] responder) =>
        SHA256.HashData(Pack(Utf8.GetBytes("batcave.pair.v1.transcript"), initiator, responder));

    internal static string Code(byte[] transcript)
    {
        var digest = SHA256.HashData(Pack(Utf8.GetBytes("batcave.pair.v1.code"), transcript));
        var digits = (BinaryPrimitives.ReadUInt32BigEndian(digest) % 100000000).ToString("D8", CultureInfo.InvariantCulture);
        return digits[..4] + " " + digits[4..];
    }

    internal static byte[] SignatureMessage(byte[] transcript, string role, string stage)
    {
        if (transcript.Length != 32 || role is not ("i" or "r") || stage is not ("proof" or "approve"))
            throw new InvalidDataException("Invalid pairing signature context.");
        return Pack(Utf8.GetBytes("batcave.pair.v1." + stage), transcript, Utf8.GetBytes(role));
    }

    internal static byte[] Sign(X509Certificate2 certificate, byte[] message)
    {
        using var rsa = certificate.GetRSAPrivateKey();
        if (rsa is not null) return rsa.SignData(message, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ec = certificate.GetECDsaPrivateKey();
        if (ec is not null) return ec.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        throw new CryptographicException("Device signing key is unavailable.");
    }

    internal static bool Verify(Descriptor descriptor, byte[] message, byte[] signature)
    {
        ValidateDescriptor(descriptor);
        using var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(descriptor.Card.CertificateDerBase64));
        using var rsa = certificate.GetRSAPublicKey();
        if (rsa is not null) return rsa.VerifyData(message, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ec = certificate.GetECDsaPublicKey();
        return ec is not null && ec.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    internal static void ValidateLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Utf8.GetByteCount(value) > 512) throw new InvalidDataException("Invalid device name.");
        var count = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            count++;
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                throw new InvalidDataException("Invalid character in device name.");
        }
        if (count > 128) throw new InvalidDataException("Device name is too long.");
    }

    internal static void ValidateDescriptor(Descriptor descriptor)
    {
        ValidateLabel(descriptor.Card.DeviceId); ValidateLabel(descriptor.Card.DisplayName);
        if (descriptor.InputPort is < 1 or > 65535) throw new InvalidDataException("Invalid input port.");
        DeviceIdentityStore.ReadPublicCard(DeviceIdentityStore.SerializePublicCard(descriptor.Card));
        var der = Convert.FromBase64String(descriptor.Card.CertificateDerBase64);
        using var certificate = X509CertificateLoader.LoadCertificate(der);
        if (Convert.ToBase64String(der) != descriptor.Card.CertificateDerBase64 || !der.SequenceEqual(certificate.RawData))
            throw new InvalidDataException("Invalid certificate encoding.");
        var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault();
        if (eku is null || !eku.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == "1.3.6.1.5.5.7.3.1")
            || !eku.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == "1.3.6.1.5.5.7.3.2"))
            throw new InvalidDataException("Identity does not support mutual TLS.");
        using var rsa = certificate.GetRSAPublicKey();
        using var ec = certificate.GetECDsaPublicKey();
        if (rsa is not null && rsa.KeySize is >= 3072 and <= 8192) return;
        if (ec is not null && ec.ExportParameters(false).Curve.Oid.Value is "1.2.840.10045.3.1.7" or "1.3.132.0.34") return;
        throw new InvalidDataException("Unsupported device identity key.");
    }

    internal static byte[] Hex(string value, int size, bool lower = false)
    {
        if (value.Length != size * 2 || value.Any(c => !char.IsAsciiHexDigit(c))
            || (lower ? value != value.ToLowerInvariant() : value != value.ToUpperInvariant()))
            throw new InvalidDataException("Invalid pairing encoding.");
        return Convert.FromHexString(value);
    }

    internal static JsonDocument Parse(string data)
    {
        if (Utf8.GetByteCount(data) > 16384 || data.Contains('\r')) throw new InvalidDataException("Invalid pairing frame.");
        var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 8 });
        try { CheckObject(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }

    private static void CheckObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected pairing object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate pairing field.");
            if (property.Value.ValueKind == JsonValueKind.Object) CheckObject(property.Value);
            if (property.Value.ValueKind == JsonValueKind.Array) throw new InvalidDataException("Unexpected pairing array.");
        }
    }

    internal static void Fields(JsonElement frame, params string[] allowed)
    {
        if (frame.ValueKind != JsonValueKind.Object || frame.EnumerateObject().Count() != allowed.Length
            || frame.EnumerateObject().Any(p => !allowed.Contains(p.Name, StringComparer.Ordinal)))
            throw new InvalidDataException("Unexpected pairing fields.");
    }

    // The callback must represent this session's explicit local comparison. It
    // is never invoked until possession of the committed identity is proven.
    internal static async Task<Descriptor> RunAsync(Stream stream, X509Certificate2 identity, Descriptor local,
        bool initiator, Func<Confirmation, CancellationToken, Task<bool>> confirm,
        Func<Descriptor, bool> acceptPeer, CancellationToken cancellationToken,
        ApprovedPeer? expected = null)
    {
        ValidateDescriptor(local);
        if (SecurePeerTransport.Fingerprint(identity) != local.Card.CertificateSha256.ToUpperInvariant())
            throw new InvalidDataException("Local pairing identity mismatch.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(TimeSpan.FromSeconds(120));
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        handshake.CancelAfter(TimeSpan.FromSeconds(10));
        var token = handshake.Token;
        var reader = new BoundedFrameReader(stream, 16384, allowCarriageReturn: false);
        var session = initiator ? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant() : "";
        var role = initiator ? "i" : "r"; var otherRole = initiator ? "r" : "i";
        byte[] remoteCommitment;
        if (!initiator)
        {
            using var first = Parse(await reader.ReadAsync(token).ConfigureAwait(false) ?? throw new EndOfStreamException());
            var frame = first.RootElement;
            Fields(frame, "type", "version", "session", "commitment");
            if (frame.GetProperty("type").GetString() != "pair.commit" || frame.GetProperty("version").GetInt32() != 1)
                throw new InvalidDataException("Unsupported pairing request.");
            session = frame.GetProperty("session").GetString()!; Hex(session, 16, lower: true);
            remoteCommitment = Hex(frame.GetProperty("commitment").GetString()!, 32);
        }
        else remoteCommitment = [];
        var nonce = RandomNumberGenerator.GetBytes(32);
        var localBytes = IdentityBytes(session, role, local, nonce);
        await WriteAsync(new { type = "pair.commit", version = 1, session, commitment = Convert.ToHexString(SHA256.HashData(localBytes)) }, token);
        if (initiator)
        {
            using var commit = await ReadAsync("pair.commit", token, "commitment");
            remoteCommitment = Hex(commit.RootElement.GetProperty("commitment").GetString()!, 32);
            await RevealAsync();
        }
        using var reveal = await ReadAsync("pair.reveal", token, "nonce", "card", "inputPort");
        var peerElement = reveal.RootElement;
        var cardElement = peerElement.GetProperty("card");
        Fields(cardElement, "schema", "deviceId", "displayName", "certificateDerBase64", "certificateSha256");
        var peer = new Descriptor(cardElement.Deserialize<DeviceIdentityStore.PairingCard>(Json)
            ?? throw new InvalidDataException("Missing identity."), peerElement.GetProperty("inputPort").GetInt32());
        var peerBytes = IdentityBytes(session, otherRole, peer, Hex(peerElement.GetProperty("nonce").GetString()!, 32));
        if (!CryptographicOperations.FixedTimeEquals(remoteCommitment, SHA256.HashData(peerBytes)))
            throw new InvalidDataException("Pairing commitment changed.");
        if (peer.Card.DeviceId == local.Card.DeviceId || peer.Card.CertificateSha256.Equals(local.Card.CertificateSha256, StringComparison.OrdinalIgnoreCase)
            || (expected is not null && (peer.Card.DeviceId != expected.DeviceId
                || !peer.Card.CertificateSha256.Equals(expected.CertificateSha256, StringComparison.OrdinalIgnoreCase))) || !acceptPeer(peer))
            throw new InvalidDataException("The computer's identity changed. Cancel and choose it again.");
        if (!initiator) await RevealAsync();
        var transcript = initiator ? Transcript(localBytes, peerBytes) : Transcript(peerBytes, localBytes);
        await SendSignature("proof", token);
        await ReadSignature("proof", token);
        // A fresh token keeps the UI deadline independent of the 10-second handshake.
        token = lifetime.Token;
        var remoteApproval = ReadSignature("approve", token);
        using var prompt = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            var localApproval = confirm(new(peer, Code(transcript), initiator), prompt.Token);
            var first = await Task.WhenAny(localApproval, remoteApproval).ConfigureAwait(false);
            if (first == remoteApproval) await remoteApproval.ConfigureAwait(false); // EOF/errors cancel the UI immediately.
            if (!await localApproval.WaitAsync(token).ConfigureAwait(false)) throw new OperationCanceledException("Pairing declined.");
            token.ThrowIfCancellationRequested();
            if (!acceptPeer(peer)) throw new InvalidDataException("Approval changed during pairing.");
            await SendSignature("approve", token);
            await remoteApproval.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return peer;
        }
        finally
        {
            prompt.Cancel(); lifetime.Cancel();
            try { await remoteApproval.ConfigureAwait(false); } catch (Exception) { /* Observe a cancelled or failed read. */ }
        }

        async Task RevealAsync() => await WriteAsync(new { type = "pair.reveal", version = 1, session,
            nonce = Convert.ToHexString(nonce), card = local.Card, inputPort = local.InputPort }, token);
        async Task SendSignature(string stage, CancellationToken ct) => await WriteAsync(new { type = "pair." + stage,
            version = 1, session, signature = Convert.ToBase64String(Sign(identity, SignatureMessage(transcript, role, stage))) }, ct);
        async Task ReadSignature(string stage, CancellationToken ct)
        {
            using var proof = await ReadAsync("pair." + stage, ct, "signature");
            var value = proof.RootElement.GetProperty("signature").GetString()!;
            if (value.Length > 1400) throw new InvalidDataException("Signature exceeds its limit.");
            var signature = Convert.FromBase64String(value);
            if (Convert.ToBase64String(signature) != value || !Verify(peer, SignatureMessage(transcript, otherRole, stage), signature))
                throw new InvalidDataException("Invalid pairing signature.");
        }
        async Task WriteAsync(object value, CancellationToken ct)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
            if (bytes.Length > 16384) throw new InvalidDataException("Pairing frame exceeds its limit.");
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.WriteAsync(new byte[] { 10 }, ct).ConfigureAwait(false);
        }
        async Task<JsonDocument> ReadAsync(string type, CancellationToken ct, params string[] extra)
        {
            var document = Parse(await reader.ReadAsync(ct).ConfigureAwait(false) ?? throw new EndOfStreamException());
            try
            {
                var frame = document.RootElement;
                Fields(frame, new[] { "type", "version", "session" }.Concat(extra).ToArray());
                if (frame.GetProperty("type").GetString() != type || frame.GetProperty("version").GetInt32() != 1
                    || frame.GetProperty("session").GetString() != session) throw new InvalidDataException("Unexpected pairing message.");
                return document;
            }
            catch { document.Dispose(); throw; }
        }
    }
}
