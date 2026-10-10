using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using BatCave.Audio;
using BatCave.AudioPreview;
using BatCave.WindowsAgent;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Production peers exchange bidirectional TLS control and audio", ProductionPeers),
    ("Unapproved certificate cannot register", UnapprovedCertificate),
    ("Mutual TLS and bound source identity", BoundSource),
    ("Packet session spoof is rejected", BoundSession),
    ("Hello identity spoof is rejected", BoundHello),
    ("Current approval is rechecked after TLS", RevokedApproval),
    ("Reused hello session revokes consent before replacement inventory", ReusedSession),
    ("Concurrent replacements suppress stale callbacks and cleanup", ConcurrentReplacements),
    ("Incoming admission remains bounded through hello and recovers", IncomingAdmission),
    ("Outgoing admission is bounded and dispose cancels every worker", OutgoingAdmission),
    ("Registered connection limit still permits replacement", RegisteredLimit),
    ("Oversized header is rejected without waiting for payload", OversizedFrame),
    ("Idle peer remains connected but incomplete frame expires", PartialFrame),
    ("Incomplete payload expires after a valid header", PartialPayload),
    ("A nonreading TLS recipient is disconnected by writer deadline", StalledWriter),
    ("Dispose interrupts incomplete hello and drains workers", DisposeDuringHello),
    ("Stop retires queued PCM and requires a fresh offered stream", RetiredStream),
    ("Three TLS coordinators mix selected outputs and revoke playback", CoordinatorTransportIntegration.Run),
};
foreach (var test in tests)
{
    await test.Run().WaitAsync(TimeSpan.FromSeconds(20));
    Console.WriteLine($"PASS {test.Name}");
}
Console.WriteLine($"All {tests.Length} audio transport tests passed. No audio hardware was opened.");

static async Task ProductionPeers()
{
    await using var f = new Fixture();
    using var client = new AudioPeerNetwork(f.Peer.Id, f.Peer.Certificate, () => new[] { f.Server.Approval }, () => Inventory(f.Peer.Id));
    var serverPackets = 0; var clientPackets = 0; var controls = 0;
    f.Network.PacketReceived += (_, _) => Interlocked.Increment(ref serverPackets);
    client.PacketReceived += (_, _) => Interlocked.Increment(ref clientPackets);
    client.ControlReceived += (_, _, _) => Interlocked.Increment(ref controls);
    try
    {
        await client.Connect("127.0.0.1", f.Server.Approval, f.Network.LocalPort);
        Require(client.Session(f.Server.Id) is not null && f.Network.Session(f.Peer.Id) is not null, "Production TLS peers did not register.");
        Require(client.SendAudio(f.Server.Id, Guid.NewGuid(), 0, new float[AudioLimits.SamplesPerPacket]), "Client audio enqueue failed.");
        var serverStream = Guid.NewGuid();
        Require(f.Network.SendControl(f.Peer.Id, "offer", new { streamId = serverStream }), "Control enqueue failed.");
        Require(f.Network.SendAudio(f.Peer.Id, serverStream, 0, new float[AudioLimits.SamplesPerPacket]), "Server audio enqueue failed.");
        await Until(() => serverPackets == 1 && clientPackets == 1 && controls == 1);
    }
    finally { client.Dispose(); await client.WhenStoppedAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
}
static async Task UnapprovedCertificate()
{
    await using var f = new Fixture(); using var unknown = new Identity("unapproved-peer");
    await MustFail(() => f.Open(unknown)); await Until(() => f.Network.PendingCount == 0);
    Require(f.Network.ConnectedIds.Length == 0, "Unapproved certificate registered.");
}
static async Task BoundSource()
{
    await using var f = new Fixture();
    var packets = 0; f.Network.PacketReceived += (_, _) => Interlocked.Increment(ref packets);
    await using var peer = await f.Open(); await peer.Hello(); await Until(() => f.Network.Session(peer.Identity.Id) is not null);
    await peer.Packet(peer.Identity.Id, peer.Session); await Until(() => Volatile.Read(ref packets) == 1);
    await peer.Packet("forged-source", peer.Session); await Until(() => f.Network.Session(peer.Identity.Id) is null);
    Require(packets == 1, "Spoofed source reached packet callback.");
}
static async Task BoundSession()
{
    await using var f = new Fixture(); var packets = 0;
    f.Network.PacketReceived += (_, _) => Interlocked.Increment(ref packets);
    await using var peer = await f.Open(); await peer.Hello(); await Until(() => f.Network.Session(peer.Identity.Id) is not null);
    await peer.Packet(peer.Identity.Id, Guid.NewGuid()); await Until(() => f.Network.Session(peer.Identity.Id) is null);
    Require(packets == 0, "Packet from another session was delivered.");
}
static async Task BoundHello()
{
    await using var f = new Fixture(); var inventories = 0;
    f.Network.InventoryReceived += (_, _) => Interlocked.Increment(ref inventories);
    await using var peer = await f.Open(); await peer.Hello("forged-hello");
    await Until(() => f.Network.PendingCount == 0);
    Require(inventories == 0 && f.Network.ConnectedIds.Length == 0, "Spoofed hello registered.");
}
static async Task RevokedApproval()
{
    await using var f = new Fixture(); var inventories = 0;
    f.Network.InventoryReceived += (_, _) => Interlocked.Increment(ref inventories);
    await using var peer = await f.Open(); // TLS succeeded, hello has not been sent.
    f.SetApprovals(Array.Empty<ApprovedPeer>());
    await peer.Hello(); await Until(() => f.Network.PendingCount == 0);
    Require(inventories == 0 && f.Network.ConnectedIds.Length == 0, "Revoked approval registered after TLS.");
}
static async Task ReusedSession()
{
    await using var f = new Fixture(); using var routing = new AudioRoutingSession();
    var events = new ConcurrentQueue<string>();
    f.Network.InventoryReceived += (session, inventory) => { routing.SetAuthenticatedInventory(session, inventory); events.Enqueue("inventory"); };
    f.Network.PeerDisconnected += id => { routing.RemoveDevice(id); events.Enqueue("disconnect"); };
    var sessionId = Guid.NewGuid();
    await using var first = await f.Open(session: sessionId); await first.Hello(); await Until(() => events.Count == 1);
    routing.SetConsent(true); Require(routing.IsConsented, "Test consent was not enabled.");
    await using var second = await f.Open(session: sessionId); await second.Hello(); await Until(() => events.Count >= 3);
    Require(events.ToArray().SequenceEqual(new[] { "inventory", "disconnect", "inventory" }), "Replacement callbacks were reordered.");
    Require(!routing.IsConsented && f.Network.Session(first.Identity.Id)?.SessionId == sessionId, "Reused session retained consent.");
    await Task.Delay(100);
    Require(events.Count == 3 && f.Network.ConnectedIds.Length == 1, "Old cleanup disconnected the replacement.");
}
static async Task ConcurrentReplacements()
{
    await using var f = new Fixture(); var inventories = 0; var controls = 0; var disconnects = 0; Guid lastSession = Guid.Empty;
    f.Network.InventoryReceived += (session, _) => { lastSession = session.SessionId; Interlocked.Increment(ref inventories); };
    f.Network.PeerDisconnected += _ => Interlocked.Increment(ref disconnects);
    f.Network.ControlReceived += (_, _, _) => Interlocked.Increment(ref controls);
    var peers = new List<ManualPeer>();
    try
    {
        for (var wave = 0; wave < 4; wave++)
        {
            var batch = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => f.Open())); peers.AddRange(batch);
            await Task.WhenAll(batch.Select(peer => peer.Hello()));
            await Until(() => Volatile.Read(ref inventories) == (wave + 1) * 4);
        }
        var active = peers.Single(peer => peer.Session == lastSession);
        foreach (var obsolete in peers.Where(peer => peer != active))
            try { await obsolete.Control(); } catch (IOException) { } catch (ObjectDisposedException) { }
        await active.Control(); await Until(() => Volatile.Read(ref controls) == 1); await Task.Delay(100);
        Require(disconnects == inventories - 1 && controls == 1 && f.Network.ConnectedIds.Length == 1
            && f.Network.Session(active.Identity.Id)?.SessionId == active.Session, "Concurrent replacement delivered stale callbacks or removed the winner.");
    }
    finally { foreach (var peer in peers) await peer.DisposeAsync(); }
}
static async Task IncomingAdmission()
{
    await using var f = new Fixture(); var sockets = new List<TcpClient>();
    try
    {
        for (var i = 0; i < 4; i++) { var socket = new TcpClient(); await socket.ConnectAsync(IPAddress.Loopback, f.Network.LocalPort); sockets.Add(socket); }
        await Until(() => f.Network.PendingCount == 4);
        using var overflow = new TcpClient(); await overflow.ConnectAsync(IPAddress.Loopback, f.Network.LocalPort);
        var closed = new byte[1];
        try { Require(await overflow.GetStream().ReadAsync(closed).AsTask().WaitAsync(TimeSpan.FromSeconds(2)) == 0, "Fifth admission remained open."); }
        catch (IOException) { }
        f.Network.Disconnect("not-yet-identified"); await Until(() => f.Network.PendingCount == 0);
        await using var peer = await f.Open(); Require(f.Network.PendingCount == 1, "TLS released its hello admission early.");
        await peer.Hello(); await Until(() => f.Network.PendingCount == 0 && f.Network.ConnectedIds.Length == 1);
    }
    finally { foreach (var socket in sockets) socket.Dispose(); }
}
static async Task OutgoingAdmission()
{
    using var identity = new Identity("outbound-peer"); using var target = new Identity("outbound-target");
    using var network = new AudioPeerNetwork(identity.Id, identity.Certificate, () => new[] { target.Approval }, () => Inventory(identity.Id));
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port; var sockets = new List<TcpClient>();
    var attempts = new List<Task>();
    try
    {
        for (var i = 0; i < 4; i++) { attempts.Add(network.Connect("127.0.0.1", target.Approval, port)); sockets.Add(await listener.AcceptTcpClientAsync()); }
        Require(network.PendingCount == 4, "Outgoing handshakes exceeded admission limit.");
        await MustFail(() => network.Connect("127.0.0.1", target.Approval, port));
        network.Dispose(); foreach (var attempt in attempts) await MustFail(() => attempt);
        await network.WhenStoppedAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Require(network.PendingCount == 0, "Outgoing cancellation leaked pending admissions.");
    }
    finally { listener.Stop(); foreach (var socket in sockets) socket.Dispose(); }
}
static async Task RegisteredLimit()
{
    await using var f = new Fixture(); var identities = Enumerable.Range(0, 9).Select(i => new Identity($"limit-peer-{i}")).ToArray();
    var peers = new List<ManualPeer>();
    try
    {
        f.SetApprovals(identities.Select(identity => identity.Approval).ToArray());
        for (var i = 0; i < 8; i++) { var peer = await f.Open(identities[i]); peers.Add(peer); await peer.Hello(); await Until(() => f.Network.ConnectedIds.Length == i + 1); }
        await using var overflow = await f.Open(identities[8]); await overflow.Hello(); await Until(() => f.Network.PendingCount == 0);
        Require(f.Network.ConnectedIds.Length == 8 && f.Network.Session(identities[8].Id) is null, "Ninth registered device bypassed limit.");
        await using var replacement = await f.Open(identities[0]); await replacement.Hello();
        await Until(() => f.Network.Session(identities[0].Id)?.SessionId == replacement.Session);
        Require(f.Network.ConnectedIds.Length == 8 && f.Network.PendingCount == 0, "Full registration table prevented replacement or leaked admission.");
    }
    finally { foreach (var peer in peers) await peer.DisposeAsync(); foreach (var identity in identities) identity.Dispose(); }
}
static async Task OversizedFrame()
{
    await using var f = new Fixture(); await using var peer = await f.Open(); await peer.Hello(); await Until(() => f.Network.ConnectedIds.Length == 1);
    var header = new byte[5]; header[0] = 1; BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), 16385);
    await peer.Stream.WriteAsync(header); await Until(() => f.Network.ConnectedIds.Length == 0, 1000);
}
static async Task PartialFrame()
{
    await using var f = new Fixture(); await using var peer = await f.Open(); await peer.Hello(); await Until(() => f.Network.ConnectedIds.Length == 1);
    await Task.Delay(2200); Require(f.Network.ConnectedIds.Length == 1, "An idle authenticated peer was incorrectly timed out.");
    await peer.Stream.WriteAsync(new byte[] { 2 }); await Until(() => f.Network.ConnectedIds.Length == 0, 3500);
}
static async Task DisposeDuringHello()
{
    await using var f = new Fixture(); await using var peer = await f.Open();
    Require(f.Network.PendingCount == 1, "Delayed hello was not tracked.");
    f.Network.Dispose(); await f.Network.WhenStoppedAsync().WaitAsync(TimeSpan.FromSeconds(3));
    Require(f.Network.PendingCount == 0 && f.Network.ConnectedIds.Length == 0, "Dispose left a TLS hello worker running.");
}
static async Task PartialPayload()
{
    await using var f = new Fixture(); await using var peer = await f.Open(); await peer.Hello(); await Until(() => f.Network.ConnectedIds.Length == 1);
    var frame = new byte[6]; frame[0] = 2; BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), 4000);
    await peer.Stream.WriteAsync(frame); await Until(() => f.Network.ConnectedIds.Length == 0, 3500);
}
static async Task StalledWriter()
{
    await using var f = new Fixture(); await using var peer = await f.Open(); await peer.Hello(); await Until(() => f.Network.ConnectedIds.Length == 1);
    // The peer leaves TLS connected but never consumes server PCM. Its small TCP
    // receive buffer forces real socket backpressure; production Writer must abort.
    var stream = Guid.NewGuid(); var samples = new float[AudioLimits.SamplesPerPacket]; ulong sequence = 0;
    var started = Stopwatch.GetTimestamp();
    while (f.Network.ConnectedIds.Length > 0 && Stopwatch.GetElapsedTime(started).TotalSeconds < 6)
    {
        for (var i = 0; i < 32; i++) f.Network.SendAudio(peer.Identity.Id, stream, sequence++, samples);
        await Task.Delay(2);
    }
    Require(f.Network.ConnectedIds.Length == 0, "Nonreading recipient retained a stalled audio writer.");
}
static async Task RetiredStream()
{
    await using var f = new Fixture(); await using var peer = await f.Open(); await peer.Hello(); await Until(() => f.Network.ConnectedIds.Length == 1);
    var previous = Guid.NewGuid(); var fresh = Guid.NewGuid(); var samples = new float[AudioLimits.SamplesPerPacket];
    for (ulong sequence = 0; sequence < 64; sequence++) f.Network.SendAudio(peer.Identity.Id, previous, sequence, samples);
    Require(f.Network.SendControl(peer.Identity.Id, "stop", new { streamId = Guid.Empty }), "Stop control failed.");
    Require(!f.Network.SendAudio(peer.Identity.Id, previous, 65, samples), "Retired capture callback could enqueue PCM.");
    Require(!f.Network.SendAudio(peer.Identity.Id, fresh, 0, samples), "A new unoffered stream bypassed paused sending.");
    Require(!f.Network.SendControl(peer.Identity.Id, "offer", new { streamId = previous }), "Retired stream could be offered again.");
    Require(f.Network.SendControl(peer.Identity.Id, "offer", new { streamId = fresh }), "Fresh offered stream was not permitted.");
    Require(f.Network.SendAudio(peer.Identity.Id, fresh, 0, samples), "Fresh PCM was not permitted.");
    Require(!f.Network.SendAudio(peer.Identity.Id, previous, 66, samples), "Fresh offer revived retired PCM.");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)); var stopped = false; var receivedFresh = false;
    for (var i = 0; i < 128 && !receivedFresh; i++)
    {
        var frame = await AudioPeerNetwork.ReadFrame(peer.Stream, timeout.Token);
        if (frame.Kind == 1)
        { using var doc = JsonDocument.Parse(frame.Data); if (doc.RootElement.GetProperty("type").GetString() == "stop") stopped = true; }
        else
        {
            Require(AudioPacketCodec.TryDecode(frame.Data, out var packet) && packet is not null, "Invalid transmitted PCM.");
            if (stopped) Require(packet!.StreamId == fresh, "Queued retired PCM was transmitted after stop.");
            receivedFresh = packet!.StreamId == fresh;
        }
    }
    Require(stopped && receivedFresh, "Stop/fresh stream framing did not complete.");
}
static AudioDeviceInventory Inventory(string id) => new(id, "Test audio device", false, 1,
    new[] { new AudioOutputDescriptor("test-speakers", "Test speakers", AudioOutputKind.Speakers, true, true, true) }, true, true);
static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
static async Task Until(Func<bool> predicate, int milliseconds = 3000)
{
    using var deadline = new CancellationTokenSource(milliseconds);
    while (!predicate()) await Task.Delay(10, deadline.Token);
}
static async Task MustFail(Func<Task> operation)
{
    try { await operation().WaitAsync(TimeSpan.FromSeconds(4)); }
    catch (Exception ex) when (ex is not TimeoutException) { return; }
    throw new InvalidOperationException("Expected operation to fail promptly.");
}

sealed class Identity : IDisposable
{
    private readonly RSA _key = RSA.Create(2048);
    public string Id { get; }
    public X509Certificate2 Certificate { get; }
    public ApprovedPeer Approval => new(Id, SecurePeerTransport.Fingerprint(Certificate));
    public Identity(string id)
    {
        Id = id;
        var request = new CertificateRequest("CN=BatCave Audio Transport Test", _key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        // Schannel on Windows needs a temporary OS-managed key container. Do not
        // persist it, add certificates to a store, or touch a product identity profile.
        var pfx = generated.Export(X509ContentType.Pfx);
        try { Certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }
    public void Dispose() { Certificate.Dispose(); _key.Dispose(); }
}
sealed class Fixture : IAsyncDisposable
{
    private ApprovedPeer[] _approvals;
    public readonly Identity Server = new("test-server");
    public readonly Identity Peer = new("test-peer");
    public AudioPeerNetwork Network { get; }
    public Fixture()
    {
        _approvals = new[] { Peer.Approval };
        Network = new(Server.Id, Server.Certificate, () => Volatile.Read(ref _approvals), () => TestInventory(Server.Id));
        Network.Listen(0, IPAddress.Loopback);
    }
    public void SetApprovals(ApprovedPeer[] approvals) => Volatile.Write(ref _approvals, approvals);
    public Task<ManualPeer> Open(Identity? identity = null, Guid? session = null) => ManualPeer.Open(this, identity ?? Peer, session ?? Guid.NewGuid());
    public async ValueTask DisposeAsync()
    { Network.Dispose(); await Network.WhenStoppedAsync().WaitAsync(TimeSpan.FromSeconds(4)); Server.Dispose(); Peer.Dispose(); }
    public static AudioDeviceInventory TestInventory(string id) => new(id, "Test audio device", false, 1,
        new[] { new AudioOutputDescriptor("test-speakers", "Test speakers", AudioOutputKind.Speakers, true, true, true) }, true, true);
}
sealed class ManualPeer : IAsyncDisposable
{
    private readonly TcpClient _socket;
    public SslStream Stream { get; }
    public Identity Identity { get; }
    public Guid Session { get; }
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private ManualPeer(TcpClient socket, SslStream stream, Identity identity, Guid session)
    { _socket = socket; Stream = stream; Identity = identity; Session = session; }
    public static async Task<ManualPeer> Open(Fixture fixture, Identity identity, Guid session)
    {
        var socket = new TcpClient { ReceiveBufferSize = 4096 }; SslStream? stream = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.ConnectAsync(IPAddress.Loopback, fixture.Network.LocalPort, timeout.Token);
            stream = await SecurePeerTransport.AuthenticateClientAsync(socket, identity.Certificate, fixture.Server.Approval, timeout.Token);
            var hello = await AudioPeerNetwork.ReadFrame(stream, timeout.Token);
            if (hello.Kind != 1) throw new InvalidDataException("Server did not send hello.");
            return new(socket, stream, identity, session);
        }
        catch { stream?.Dispose(); socket.Dispose(); throw; }
    }
    public Task Hello(string? claim = null) => AudioPeerNetwork.WriteFrame(Stream, 1, JsonSerializer.SerializeToUtf8Bytes(new
    { protocol = AudioPeerNetwork.Protocol, type = "hello", deviceId = claim ?? Identity.Id, sessionId = Session, inventory = Fixture.TestInventory(claim ?? Identity.Id) }, _json), CancellationToken.None);
    public Task Packet(string origin, Guid session) => AudioPeerNetwork.WriteFrame(Stream, 2,
        AudioPacketCodec.Encode(new(origin, session, Guid.NewGuid(), 0, new[] { origin }, new float[AudioLimits.SamplesPerPacket])), CancellationToken.None);
    public Task Control() => AudioPeerNetwork.WriteFrame(Stream, 1,
        JsonSerializer.SerializeToUtf8Bytes(new { type = "offer", payload = new { streamId = Guid.NewGuid() } }, _json), CancellationToken.None);
    public ValueTask DisposeAsync() { _socket.Dispose(); Stream.Dispose(); return ValueTask.CompletedTask; }
}
