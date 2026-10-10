using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BatCave.WindowsAgent;
using Descriptor = BatCave.WindowsAgent.NearbyPairingProtocol.Descriptor;

internal static class NearbyPairingChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static void Require(bool condition, string detail) { if (!condition) throw new Exception("Nearby: " + detail); }

    internal static void WriteVectors(string path)
    {
        using var a = Certificate(false); using var b = Certificate(true);
        var initiator = Describe(a, "windows:pairing-vector-a", "Office computer", 47871);
        var responder = Describe(b, "macos:pairing-vector-b", "Laptop computer", 47870);
        var session = "000102030405060708090a0b0c0d0e0f";
        var n1 = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var n2 = Enumerable.Range(32, 32).Select(i => (byte)i).ToArray();
        var d1 = NearbyPairingProtocol.IdentityBytes(session, "i", initiator, n1);
        var d2 = NearbyPairingProtocol.IdentityBytes(session, "r", responder, n2);
        var transcript = NearbyPairingProtocol.Transcript(d1, d2);
        var signatures = new Dictionary<string, string>();
        foreach (var stage in new[] { "proof", "approve" })
        {
            signatures["i-" + stage] = Convert.ToBase64String(NearbyPairingProtocol.Sign(a, NearbyPairingProtocol.SignatureMessage(transcript, "i", stage)));
            signatures["r-" + stage] = Convert.ToBase64String(NearbyPairingProtocol.Sign(b, NearbyPairingProtocol.SignatureMessage(transcript, "r", stage)));
        }
        File.WriteAllText(path, JsonSerializer.Serialize(new { description = "Synthetic public certificates only; ephemeral private keys were discarded.", session,
            initiator, responder, initiatorNonce = Convert.ToHexString(n1), responderNonce = Convert.ToHexString(n2),
            initiatorCommitment = Convert.ToHexString(SHA256.HashData(d1)), responderCommitment = Convert.ToHexString(SHA256.HashData(d2)),
            transcript = Convert.ToHexString(transcript), code = NearbyPairingProtocol.Code(transcript), signatures }, Json) + "\n");
    }

    internal static async Task Run()
    {
        CheckVectors(); CheckDiscovery();
        await Exchange();
        await Exchange(decline: true);
        await Exchange(expectedWrong: true);
        foreach (var field in new[] { "nonce", "name", "proof", "order", "duplicate", "session", "port" }) await Exchange(mutation: field);
        await DisconnectDuringConfirmation();
        Console.WriteLine("PASS: nearby pairing vectors, RSA/ECDSA TCP exchange, matching codes, mutual approval, rejection, tampering, replay context, EOF cancellation and bounded discovery.");
    }

    private static X509Certificate2 Certificate(bool elliptic)
    {
        using var rsa = elliptic ? null : RSA.Create(3072);
        using var ec = elliptic ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : null;
        var request = elliptic ? new CertificateRequest("CN=BatCave synthetic pairing", ec!, HashAlgorithmName.SHA256)
            : new CertificateRequest("CN=BatCave synthetic pairing", rsa!, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2") }, false));
        return request.CreateSelfSigned(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2040, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    private static Descriptor Describe(X509Certificate2 cert, string id, string name, int port) => new(new(DeviceIdentityStore.PairingCardSchema,
        id, name, Convert.ToBase64String(cert.RawData), SecurePeerTransport.Fingerprint(cert)), port);

    private static void CheckVectors()
    {
        using var vector = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "pairing-vectors.json")));
        var root = vector.RootElement;
        var a = root.GetProperty("initiator").Deserialize<Descriptor>(Json)!; var b = root.GetProperty("responder").Deserialize<Descriptor>(Json)!;
        var session = root.GetProperty("session").GetString()!;
        var d1 = NearbyPairingProtocol.IdentityBytes(session, "i", a, Convert.FromHexString(root.GetProperty("initiatorNonce").GetString()!));
        var d2 = NearbyPairingProtocol.IdentityBytes(session, "r", b, Convert.FromHexString(root.GetProperty("responderNonce").GetString()!));
        Require(Convert.ToHexString(SHA256.HashData(d1)) == root.GetProperty("initiatorCommitment").GetString(), "initiator commitment vector");
        Require(Convert.ToHexString(SHA256.HashData(d2)) == root.GetProperty("responderCommitment").GetString(), "responder commitment vector");
        var transcript = NearbyPairingProtocol.Transcript(d1, d2);
        Require(Convert.ToHexString(transcript) == root.GetProperty("transcript").GetString(), "transcript vector");
        Require(NearbyPairingProtocol.Code(transcript) == root.GetProperty("code").GetString(), "comparison code vector");
        foreach (var role in new[] { "i", "r" }) foreach (var stage in new[] { "proof", "approve" })
        {
            var signature = Convert.FromBase64String(root.GetProperty("signatures").GetProperty(role + "-" + stage).GetString()!);
            var descriptor = role == "i" ? a : b;
            Require(NearbyPairingProtocol.Verify(descriptor, NearbyPairingProtocol.SignatureMessage(transcript, role, stage), signature), "signature vector");
            Require(!NearbyPairingProtocol.Verify(descriptor, NearbyPairingProtocol.SignatureMessage(transcript, role, stage == "proof" ? "approve" : "proof"), signature), "proof cannot become approval");
            Require(!NearbyPairingProtocol.Verify(descriptor, NearbyPairingProtocol.SignatureMessage(SHA256.HashData(transcript), role, stage), signature), "approval cannot replay in another transcript");
        }
        try { using var bad = NearbyPairingProtocol.Parse("{\"type\":1,\"type\":2}"); throw new Exception("Duplicate property accepted."); } catch (InvalidDataException) { }
        try { NearbyPairingProtocol.ValidateLabel("Computer\u202Ename"); throw new Exception("Bidi control accepted."); } catch (InvalidDataException) { }
    }

    private static void CheckDiscovery()
    {
        using var cert = Certificate(true);
        var card = Describe(cert, "linux:synthetic", "Desk computer", 47870).Card;
        var now = DateTimeOffset.UtcNow;
        var bytes = NearbyDiscovery.Announcement(card, 47870, 47873, true);
        var peer = NearbyDiscovery.ReadAnnouncement(bytes, IPAddress.Parse("192.0.2.15"), now);
        Require(peer.InputPort == 47870 && peer.Address.ToString() == "192.0.2.15", "source address and actual input port preserved");
        using var discovery = new NearbyDiscovery("windows:self", () => bytes);
        discovery.Remember(peer with { Seen = now.AddSeconds(-13) }, now);
        Require(discovery.Computers.Length == 0, "stale discovery expires");
        discovery.Remember(peer with { DeviceId = "windows:self" }, now);
        Require(discovery.Computers.Length == 0, "own announcements ignored");
        for (var i = 0; i < 90; i++) discovery.Remember(peer with { DeviceId = "linux:synthetic-" + i }, now);
        Require(discovery.Computers.Length == 64, "discovery cache bounded");
        var networks = new[] { new NearbyDiscovery.LanInterface(IPAddress.Parse("192.0.2.10"), IPAddress.Parse("255.255.255.0")) };
        Require(NearbyDiscovery.OnLan(peer.Address, networks), "same LAN accepted");
        Require(!NearbyDiscovery.OnLan(IPAddress.Parse("198.51.100.1"), networks), "different LAN rejected");
        foreach (var address in new[] { "0.0.0.0", "127.0.0.1", "255.255.255.255", "239.255.47.87" })
            Require(!NearbyDiscovery.EligibleAddress(IPAddress.Parse(address)), "invalid discovery source rejected");
        try { NearbyDiscovery.ReadAnnouncement(new byte[1025], peer.Address, now); throw new Exception("Large discovery accepted."); } catch (InvalidDataException) { }
    }

    private static async Task Exchange(bool decline = false, bool expectedWrong = false, string? mutation = null)
    {
        using var aCert = Certificate(false); using var bCert = Certificate(true);
        var a = Describe(aCert, "windows:synthetic-a", "Office computer", 47871);
        var b = Describe(bCert, "linux:synthetic-b", "Laptop computer", 47870);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token);
        using var server = await listener.AcceptTcpClientAsync(deadline.Token);
        var aShown = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bShown = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = 0; var successes = 0;
        Stream clientStream = client.GetStream();
        if (mutation is not null) clientStream = new MutatingStream(clientStream, mutation);
        var first = Side(client, clientStream, aCert, a, true, aShown, bShown);
        var second = Side(server, server.GetStream(), bCert, b, false, bShown, aShown);
        await Task.WhenAll(first, second).WaitAsync(deadline.Token);
        Require(mutation is null && !decline && !expectedWrong ? successes == 2 : successes == 0 && failures == 2, "approval outcome " + (mutation ?? (decline ? "decline" : expectedWrong ? "wrong identity" : "success")));

        async Task Side(TcpClient socket, Stream stream, X509Certificate2 cert, Descriptor local, bool initiator,
            TaskCompletionSource<string> own, TaskCompletionSource<string> other)
        {
            try
            {
                var result = await NearbyPairingProtocol.RunAsync(stream, cert, local, initiator, async (confirmation, ct) =>
                {
                    own.TrySetResult(confirmation.Code);
                    var code = await other.Task.WaitAsync(ct);
                    Require(confirmation.Code == code, "both screens show same comparison code");
                    return !decline;
                }, _ => true, deadline.Token, initiator ? new(b.Card.DeviceId, expectedWrong ? new string('0', 64) : b.Card.CertificateSha256) : null);
                Require(result.Card.DeviceId != local.Card.DeviceId, "paired the other identity");
                Interlocked.Increment(ref successes);
            }
            catch (Exception) { Interlocked.Increment(ref failures); }
            finally { socket.Dispose(); }
        }
    }

    private static async Task DisconnectDuringConfirmation()
    {
        using var aCert = Certificate(false); using var bCert = Certificate(true);
        var a = Describe(aCert, "windows:synthetic-a", "Office computer", 47871);
        var b = Describe(bCert, "linux:synthetic-b", "Laptop computer", 47870);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = await listener.AcceptTcpClientAsync();
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = NearbyPairingProtocol.RunAsync(client.GetStream(), aCert, a, true, async (_, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult()); shown.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token); return false;
        }, _ => true, deadline.Token);
        var second = NearbyPairingProtocol.RunAsync(server.GetStream(), bCert, b, false, async (_, token) =>
        { await Task.Delay(Timeout.InfiniteTimeSpan, token); return false; }, _ => true, deadline.Token);
        await shown.Task.WaitAsync(deadline.Token); server.Dispose();
        await cancelled.Task.WaitAsync(deadline.Token);
        try { await first; throw new Exception("EOF accepted."); } catch (IOException) { }
        deadline.Cancel();
        try { await second; } catch (Exception) { }
    }

    private sealed class MutatingStream(Stream stream, string mutation) : Stream
    {
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => stream.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => stream.Write(buffer, offset, count);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
        {
            if (bytes.Span[0] == '{')
            {
                var node = JsonNode.Parse(bytes.Span)!.AsObject(); var type = node["type"]!.GetValue<string>();
                if (type == "pair.reveal")
                {
                    if (mutation == "nonce") node["nonce"] = new string('0', 64);
                    if (mutation == "name") node["card"]!["displayName"] = "Impersonated computer";
                    if (mutation == "port") node["inputPort"] = 443;
                    if (mutation == "session") node["session"] = new string('0', 32);
                    if (mutation == "duplicate")
                    {
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(node.ToJsonString().Insert(1, "\"type\":\"pair.reveal\",")), ct); return;
                    }
                }
                if (type == "pair.proof" && mutation == "proof") node["signature"] = Convert.ToBase64String(new byte[384]);
                if (type == "pair.proof" && mutation == "order") node["type"] = "pair.approve";
                bytes = Encoding.UTF8.GetBytes(node.ToJsonString());
            }
            await stream.WriteAsync(bytes, ct);
        }
        public override void Flush() => stream.Flush(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
