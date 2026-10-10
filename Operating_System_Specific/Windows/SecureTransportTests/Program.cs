using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using BatCave.WindowsAgent;

if (args.Length == 2 && args[0] == "--write-pairing-vectors") { NearbyPairingChecks.WriteVectors(args[1]); return; }
await NearbyPairingChecks.Run();
if (args.Length == 1 && args[0] == "--nearby-only") return;

await TestLegacyLoopbackOnly();
using var serverIdentity = CreateIdentity("Windows test");
using var clientIdentity = CreateIdentity("Mac test");
var serverPin = new ApprovedPeer("windows:test", SecurePeerTransport.Fingerprint(serverIdentity));
var clientPin = new ApprovedPeer("macos:test", SecurePeerTransport.Fingerprint(clientIdentity));

Require(SecurePeerTransport.MatchesApprovedPeer(clientIdentity, clientPin), "approved leaf pin");
Require(!SecurePeerTransport.MatchesApprovedPeer(serverIdentity, clientPin), "different certificate rejected");
Require(!SecurePeerTransport.MatchesApprovedPeer(null, clientPin), "missing certificate rejected");
Require(!SecurePeerTransport.MatchesApprovedPeer(clientIdentity, clientPin with { CertificateSha256 = "invalid" }), "invalid pin rejected");
using var expired = CreateIdentity("expired", DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-1));
Require(!SecurePeerTransport.MatchesApprovedPeer(expired,
    new ApprovedPeer("expired", SecurePeerTransport.Fingerprint(expired))), "expired identity rejected even with exact pin");

await TestSession(serverPin, clientPin, shouldSucceed: true);
await TestSession(serverPin with { CertificateSha256 = clientPin.CertificateSha256 }, clientPin, shouldSucceed: false);
await TestSession(serverPin, clientPin with { CertificateSha256 = serverPin.CertificateSha256 }, shouldSucceed: false);

if (OperatingSystem.IsWindows())
{
    await WindowsPeerRoleChecks.Run();
    var directory = Path.Combine(Path.GetTempPath(), "BatCave-identity-test-" + Guid.NewGuid().ToString("N"));
    using var identity = DeviceIdentityStore.OpenOrCreate(directory);
    try
    {
        using var reopened = DeviceIdentityStore.OpenOrCreate(directory);
        Require(reopened.DeviceId == identity.DeviceId && reopened.Fingerprint == identity.Fingerprint, "identity survives relaunch");
        using var rsa = (RSACng)identity.Certificate.GetRSAPrivateKey()!;
        Require(rsa.Key.ExportPolicy == CngExportPolicies.None, "private identity key is non-exportable");
        Require(identity.Certificate.HasPrivateKey, "certificate has protected key");
        var card = DeviceIdentityStore.SerializePublicCard(identity.ExportPublicCard("Test Windows PC"));
        Require(DeviceIdentityStore.ReadPublicCard(card) == new ApprovedPeer(identity.DeviceId, identity.Fingerprint), "public pairing card roundtrip");
        try { DeviceIdentityStore.ReadPublicCard(card.Replace(identity.Fingerprint, serverPin.CertificateSha256)); throw new Exception("altered card accepted"); }
        catch (InvalidDataException) { }
        try { DeviceIdentityStore.ReadPublicCard(new string(' ', 16385)); throw new Exception("oversize card accepted"); }
        catch (InvalidDataException) { }
        await TestProtectedServerIdentity(identity.Certificate);
        await TestAuthenticatedListener(identity);
        await TestFrameLimits();
        TestApprovalStore(identity, directory);
    }
    finally
    {
        identity.DeleteIdentity(directory);
        foreach (var name in new[] { "approved-peers.json", "topology.secure.json" })
            File.Delete(Path.Combine(directory, name));
        Directory.Delete(directory); // removes only the isolated empty test directory
    }
}

Console.WriteLine("PASS: native TLS 1.3 mutual authentication and encrypted JSON round trip; wrong server/client pin, missing/invalid/expired certificate rejection.");
Console.WriteLine("PASS: persistent non-exportable Windows CNG identity, public pairing-card validation, protected identity TLS handshake.");
Console.WriteLine("PASS: integrated secure listener, certificate-bound hello, plaintext rejection, unpair cleanup, frame/clipboard limits, persistent approval storage and secure topology.");
Console.WriteLine("PASS: legacy plaintext listener binds loopback only and rejects remote addresses before connecting.");

async Task TestLegacyLoopbackOnly()
{
    using var development = new LanBridgePeer();
    await development.StartListeningAsync(0);
    var field = typeof(LanBridgePeer).GetField("_listener", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    var endpoint = (IPEndPoint)((TcpListener)field.GetValue(development)!).LocalEndpoint;
    Require(endpoint.Address.Equals(IPAddress.Loopback), "plaintext listener is bound only to IPv4 loopback");
    using var local = new TcpClient();
    await local.ConnectAsync(IPAddress.Loopback, endpoint.Port).WaitAsync(TimeSpan.FromSeconds(5));
    foreach (var host in new[] { "203.0.113.1", "example.invalid", "0.0.0.0" })
    {
        using var outgoing = new LanBridgePeer();
        try { await outgoing.ConnectAsync(host, endpoint.Port); throw new Exception("remote plaintext destination accepted"); }
        catch (InvalidOperationException) { }
    }
}

async Task TestAuthenticatedListener(DeviceIdentityStore identity)
{
    var reservation = new TcpListener(IPAddress.Loopback, 0);
    reservation.Start();
    var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
    reservation.Stop();
    var approved = new[] { clientPin };
    using var lan = new LanBridgePeer(identity, () => approved);
    lan.UpdateLocalBounds(new VirtualDesktopBounds(0, 0, 1920, 1080));
    var connected = 0;
    var clipboard = 0;
    lan.PeerConnected += _ => Interlocked.Increment(ref connected);
    lan.ClipboardTextReceived += (_, _) => Interlocked.Increment(ref clipboard);
    await lan.StartListeningAsync(port);
    var expectedServer = new ApprovedPeer(identity.DeviceId, identity.Fingerprint);
    var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);

    // A trusted certificate still cannot claim another approved device's ID.
    using (var client = new TcpClient())
    {
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var tls = await SecurePeerTransport.AuthenticateClientAsync(client, clientIdentity, expectedServer);
        using var reader = new StreamReader(tls, Encoding.UTF8, false, leaveOpen: true);
        using var writer = new StreamWriter(tls, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await reader.ReadLineAsync();
        await writer.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(BatCaveEnvelope.Create("impostor", "device.hello",
            new DeviceHelloPayload("impostor", "Mac", "macOS", "test", new ScreenBounds(0, 0, 1920, 1080, 1919, 1079))), options));
        Require(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) is null, "certificate cannot claim another device ID");
        Require(!lan.HasPeer && connected == 0, "mismatched hello never arms control");
    }
    foreach (var invalid in new object[]
    {
        new { protocol = "wrong-protocol", type = "device.hello", sourceDeviceId = clientPin.DeviceId, payload = new { deviceId = clientPin.DeviceId } },
        new { protocol = BatCaveProtocol.Name, type = "device.hello", sourceDeviceId = clientPin.DeviceId },
        BatCaveEnvelope.Create(clientPin.DeviceId, "device.hello", new DeviceHelloPayload(clientPin.DeviceId,
            "Mac", "macOS", "test", new ScreenBounds(int.MaxValue, 0, 1920, 1080, 1919, 1079))),
        BatCaveEnvelope.Create(clientPin.DeviceId, "device.hello", new DeviceHelloPayload(clientPin.DeviceId,
            "Mac", "macOS", "test", new ScreenBounds(0, 0, 1920, 1080, 9000, 1079)))
    })
    {
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, port);
        using var tls = await SecurePeerTransport.AuthenticateClientAsync(client, clientIdentity, expectedServer);
        using var reader = new StreamReader(tls, Encoding.UTF8, false, leaveOpen: true);
        using var writer = new StreamWriter(tls, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await reader.ReadLineAsync();
        await writer.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(invalid, options));
        Require(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) is null && !lan.HasPeer,
            "invalid authenticated envelope or screen geometry ends the connection");
    }
    // Existing Mac versions report the exclusive screen maximum.
    using (var client = new TcpClient())
    {
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var tls = await SecurePeerTransport.AuthenticateClientAsync(client, clientIdentity, expectedServer);
        using var reader = new StreamReader(tls, Encoding.UTF8, false, leaveOpen: true);
        using var writer = new StreamWriter(tls, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await reader.ReadLineAsync();
        await writer.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(BatCaveEnvelope.Create(clientPin.DeviceId,
            "device.hello", new DeviceHelloPayload(clientPin.DeviceId, "Mac", "macOS", "test", new ScreenBounds(0, 0, 1920, 1080, 1920, 1080))), options));
        await Until(() => lan.HasPeer);
        lan.Disconnect();
        Require(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) is null, "exclusive Mac bounds remain compatible");
    }
    using (var client = new TcpClient())
    {
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var tls = await SecurePeerTransport.AuthenticateClientAsync(client, clientIdentity, expectedServer);
        using var reader = new StreamReader(tls, Encoding.UTF8, false, leaveOpen: true);
        using var writer = new StreamWriter(tls, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await reader.ReadLineAsync();
        await SendHello(writer);
        await Until(() => lan.HasPeer);
        // A plaintext connection cannot replace an authenticated active session.
        using (var plaintext = new TcpClient())
        {
            await plaintext.ConnectAsync(IPAddress.Loopback, port);
            await plaintext.GetStream().WriteAsync(Encoding.UTF8.GetBytes("{\"type\":\"device.hello\"}\n"));
        }
        await writer.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(BatCaveEnvelope.Create(clientPin.DeviceId,
            "clipboard.text", new ClipboardTextPayload("text/plain", new string('x', 65537))), options));
        Require(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) is null, "oversize decoded clipboard closes session");
        Require(clipboard == 0, "oversize clipboard never reaches OS");
    }
    using (var client = new TcpClient())
    {
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var tls = await SecurePeerTransport.AuthenticateClientAsync(client, clientIdentity, expectedServer);
        using var reader = new StreamReader(tls, Encoding.UTF8, false, leaveOpen: true);
        using var writer = new StreamWriter(tls, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await reader.ReadLineAsync();
        await SendHello(writer);
        await Until(() => lan.HasPeer);
        approved = [];
        lan.DisconnectUnapprovedPeer();
        Require(!lan.HasPeer, "unpair immediately clears peer readiness");
        Require(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) is null, "unpair closes active TLS session");
    }

    Task SendHello(StreamWriter writer) => writer.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(
        BatCaveEnvelope.Create(clientPin.DeviceId, "device.hello", new DeviceHelloPayload(clientPin.DeviceId,
            "Mac", "macOS", "test", new ScreenBounds(0, 0, 1920, 1080, 1919, 1079))), options));
}

async Task TestFrameLimits()
{
    using var valid = new MemoryStream(Encoding.UTF8.GetBytes("{\"text\":\"é\"}\n{}\r\n"));
    var reader = new BoundedFrameReader(valid);
    Require(await reader.ReadAsync() == "{\"text\":\"é\"}" && await reader.ReadAsync() == "{}" && await reader.ReadAsync() is null,
        "UTF-8 and coalesced frames decode correctly");
    foreach (var bytes in new[] { Encoding.UTF8.GetBytes(new string('x', 262145) + "\n"), new byte[] { 0xFF, 0x0A }, Encoding.UTF8.GetBytes("truncated") })
    {
        using var invalid = new MemoryStream(bytes);
        try { await new BoundedFrameReader(invalid).ReadAsync(); throw new Exception("invalid frame accepted"); }
        catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException) { }
    }
}

void TestApprovalStore(DeviceIdentityStore identity, string directory)
{
    var store = new ApprovedPeerStore(directory);
    Require(store.Peers.Length == 0, "fresh profile trusts no device");
    var card = identity.ExportPublicCard("Public identity test");
    store.Approve(card);
    Require(new ApprovedPeerStore(directory).Peers.Single().DeviceId == identity.DeviceId, "explicit approval survives reload");
    try { store.Approve(card with { DeviceId = "another-device" }); throw new Exception("same certificate rebound to another ID"); }
    catch (InvalidDataException) { }
    store.Remove(identity.DeviceId);
    Require(new ApprovedPeerStore(directory).Peers.Length == 0, "unpair survives reload");
    var path = Path.Combine(directory, "topology.secure.json");
    SecureTopology.Write(path, identity.DeviceId, new VirtualDesktopBounds(-1920, 0, 3840, 1080),
        new DeviceHelloPayload("macos:approved", "Mac", "macOS", "test", new ScreenBounds(0, 0, 2240, 1260, 2239, 1259)), true);
    Require(BatCave.Universal.Topology.TopologyMap.TryLoad(path, identity.DeviceId, out var topology, out _)
        && topology!.TryMapCrossing(new BatCave.Universal.Topology.Point2D(1918, 540),
            new BatCave.Universal.Topology.Point2D(1922, 540), identity.DeviceId, out var handoff)
        && handoff.TargetDeviceId == "macos:approved", "secure topology routes the approved ID");
}

static async Task Until(Func<bool> predicate)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (!predicate()) { await Task.Delay(10, timeout.Token); }
}

async Task TestProtectedServerIdentity(X509Certificate2 protectedIdentity)
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    try
    {
        using var client = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = await accept;
        var serverAuth = SecurePeerTransport.AuthenticateServerAsync(server, protectedIdentity, clientPin);
        var clientAuth = SecurePeerTransport.AuthenticateClientAsync(client, clientIdentity,
            new ApprovedPeer("protected", SecurePeerTransport.Fingerprint(protectedIdentity)));
        using var serverTls = await serverAuth;
        using var clientTls = await clientAuth;
        Require(serverTls.IsMutuallyAuthenticated && clientTls.IsMutuallyAuthenticated, "protected-key identity authenticates TLS");
    }
    finally { listener.Stop(); }
}

async Task TestSession(ApprovedPeer expectedServer, ApprovedPeer expectedClient, bool shouldSucceed)
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
    try
    {
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepting = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        using var server = await accepting;
        var serverTask = RunServer();
        var clientTask = RunClient();
        var outcomes = await Task.WhenAll(serverTask, clientTask);
        Require(shouldSucceed ? outcomes.All(success => success) : outcomes.Any(success => !success),
            shouldSucceed ? "TLS 1.3 loopback succeeds" : "unapproved peer cannot exchange application data");

        async Task<bool> RunServer()
        {
            try
            {
                using var tls = await SecurePeerTransport.AuthenticateServerAsync(server, serverIdentity, expectedClient, timeout.Token);
                using var reader = new StreamReader(tls, Encoding.UTF8, false, leaveOpen: true);
                using var writer = new StreamWriter(tls, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                Require(await reader.ReadLineAsync(timeout.Token) == "{\"type\":\"device.hello\"}", "encrypted frame received");
                await writer.WriteLineAsync("approved");
                return true;
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException) { return false; }
        }

        async Task<bool> RunClient()
        {
            try
            {
                using var tls = await SecurePeerTransport.AuthenticateClientAsync(client, clientIdentity, expectedServer, timeout.Token);
                using var reader = new StreamReader(tls, Encoding.UTF8, false, leaveOpen: true);
                using var writer = new StreamWriter(tls, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync("{\"type\":\"device.hello\"}");
                return await reader.ReadLineAsync(timeout.Token) == "approved";
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException) { return false; }
        }
    }
    finally { listener.Stop(); }
}

static X509Certificate2 CreateIdentity(string name, DateTimeOffset? start = null, DateTimeOffset? end = null)
{
    using var rsa = RSA.Create(2048);
    var request = new CertificateRequest("CN=" + name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
    {
        new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2")
    }, false));
    using var generated = request.CreateSelfSigned(start ?? DateTimeOffset.UtcNow.AddMinutes(-1), end ?? DateTimeOffset.UtcNow.AddDays(1));
    // Isolated ephemeral test identities only; production keys must use CNG.
    return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
}

static void Require(bool condition, string reason)
{
    if (!condition) { throw new Exception(reason); }
}
