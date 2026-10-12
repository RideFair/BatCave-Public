// Isolated bootstrap -> pinned TLS fixture. No GUI, OS input or saved approvals.
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BatCave.WindowsAgent;

if (args.Length is < 2 or > 3 || !IPAddress.TryParse(args[1], out var address))
    throw new ArgumentException("Supply an empty disposable directory and the isolated test address.");
var exchange = Path.GetFullPath(args[0]);
if (!Directory.Exists(exchange) || Directory.EnumerateFileSystemEntries(exchange).Any())
    throw new InvalidOperationException("The exchange directory must be empty.");
var identityPath = Path.Combine(exchange, "windows-identity");
using var identity = DeviceIdentityStore.OpenOrCreate(identityPath);
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var initiator = args.Length == 3 && args[2] == "connect";
var bootstrap = new TcpListener(initiator ? IPAddress.Loopback : address, 0);
var input = new TcpListener(initiator ? IPAddress.Loopback : address, 0);
try
{
    bootstrap.Start(1); input.Start(1);
    var card = identity.ExportPublicCard("Windows pairing fixture");
    var local = new NearbyPairingProtocol.Descriptor(card, ((IPEndPoint)input.LocalEndpoint).Port);
    File.WriteAllText(Path.Combine(exchange, "windows-card.json"), DeviceIdentityStore.SerializePublicCard(card));
    Atomic("windows-ready.json", JsonSerializer.Serialize(new { pairingPort = ((IPEndPoint)bootstrap.LocalEndpoint).Port, inputPort = local.InputPort }, options));
    await WaitFile("linux-card.json");
    var expected = DeviceIdentityStore.ReadPublicCard(File.ReadAllText(Path.Combine(exchange, "linux-card.json")));
    using var client = initiator ? new TcpClient(address.AddressFamily) : await bootstrap.AcceptTcpClientAsync(timeout.Token);
    if (initiator)
    {
        await WaitFile("linux-ready.json");
        using var ready = JsonDocument.Parse(File.ReadAllText(Path.Combine(exchange, "linux-ready.json")));
        await client.ConnectAsync(address, ready.RootElement.GetProperty("pairingPort").GetInt32(), timeout.Token);
    }
    var peer = await NearbyPairingProtocol.RunAsync(client.GetStream(), identity.Certificate, local, initiator,
        async (confirmation, token) =>
        {
            Atomic("windows-code.txt", confirmation.Code);
            while (!File.Exists(Path.Combine(exchange, "linux-code.txt"))) await Task.Delay(20, token);
            // This fixture simulates two explicit clicks after exact comparison.
            return File.ReadAllText(Path.Combine(exchange, "linux-code.txt")) == confirmation.Code;
        }, descriptor => descriptor.Card.DeviceId == expected.DeviceId && descriptor.Card.CertificateSha256 == expected.CertificateSha256,
        timeout.Token, initiator ? expected : null);
    client.Close();
    using var tlsClient = initiator ? new TcpClient(address.AddressFamily) : await input.AcceptTcpClientAsync(timeout.Token);
    if (initiator) await tlsClient.ConnectAsync(address, peer.InputPort, timeout.Token);
    var pin = new ApprovedPeer(peer.Card.DeviceId, peer.Card.CertificateSha256);
    using var tls = initiator
        ? await SecurePeerTransport.AuthenticateClientAsync(tlsClient, identity.Certificate, pin, timeout.Token)
        : await SecurePeerTransport.AuthenticateServerAsync(tlsClient, identity.Certificate, pin, timeout.Token);
    await tls.WriteAsync(Encoding.ASCII.GetBytes("batcave-pairing-fixture\n"), timeout.Token);
    var reply = await new BoundedFrameReader(tls).ReadAsync(timeout.Token);
    if (reply != "batcave-pairing-fixture") throw new InvalidDataException("Missing isolated TLS reply.");
    Console.WriteLine("PASS: matching code, RSA/ECDSA proofs and approvals, then pinned TLS 1.3; Windows " + (initiator ? "initiates" : "responds") + ". No native input.");
}
finally { bootstrap.Stop(); input.Stop(); identity.DeleteIdentity(identityPath); }

void Atomic(string name, string value)
{
    var path = Path.Combine(exchange, name); File.WriteAllText(path + ".partial", value); File.Move(path + ".partial", path);
}
async Task WaitFile(string name)
{
    while (!File.Exists(Path.Combine(exchange, name))) await Task.Delay(20, timeout.Token);
}
