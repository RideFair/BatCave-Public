// Optional Windows/real-Linux handshake test. Never controls a desktop.
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BatCave.WindowsAgent;

if (args.Length is < 2 or > 3 || !IPAddress.TryParse(args[1], out var address))
    throw new ArgumentException("Supply a disposable exchange directory and a local WSL host address.");
var exchange = Path.GetFullPath(args[0]);
if (!Directory.Exists(exchange) || Directory.EnumerateFileSystemEntries(exchange).Any())
    throw new InvalidOperationException("The exchange directory must be empty and disposable.");
var identityDirectory = Path.Combine(exchange, "windows-identity");
using var identity = DeviceIdentityStore.OpenOrCreate(identityDirectory);
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var listener = new TcpListener(address, 0);
var outgoing = args.Length == 3 && args[2] == "connect";
try
{
    File.WriteAllText(Path.Combine(exchange, "windows-card.json"),
        DeviceIdentityStore.SerializePublicCard(identity.ExportPublicCard("Windows interoperability fixture")));
    if (!outgoing)
    {
        listener.Start(1);
        File.WriteAllText(Path.Combine(exchange, "ready.json.partial"),
            JsonSerializer.Serialize(new { port = ((IPEndPoint)listener.LocalEndpoint).Port }, jsonOptions));
        File.Move(Path.Combine(exchange, "ready.json.partial"), Path.Combine(exchange, "ready.json"));
    }
    var clientCard = Path.Combine(exchange, "linux-card.json");
    while (!File.Exists(clientCard)) await Task.Delay(20, timeout.Token);
    var peer = DeviceIdentityStore.ReadPublicCard(File.ReadAllText(clientCard));
    using var socket = outgoing ? new TcpClient(address.AddressFamily) : await listener.AcceptTcpClientAsync(timeout.Token);
    if (outgoing)
    {
        var ready = Path.Combine(exchange, "linux-ready.json");
        while (!File.Exists(ready)) await Task.Delay(20, timeout.Token);
        using var settings = JsonDocument.Parse(File.ReadAllText(ready));
        await socket.ConnectAsync(address, settings.RootElement.GetProperty("port").GetInt32(), timeout.Token);
    }
    using var stream = outgoing
        ? await SecurePeerTransport.AuthenticateClientAsync(socket, identity.Certificate, peer, timeout.Token)
        : await SecurePeerTransport.AuthenticateServerAsync(socket, identity.Certificate, peer, timeout.Token);
    var frame = await new BoundedFrameReader(stream).ReadAsync(timeout.Token) ?? throw new InvalidDataException("Missing hello");
    using var message = JsonDocument.Parse(frame);
    var value = message.RootElement;
    if (value.GetProperty("protocol").GetString() != "batcave.input.v0"
        || value.GetProperty("type").GetString() != "device.hello"
        || value.GetProperty("sourceDeviceId").GetString() != peer.DeviceId
        || value.GetProperty("payload").GetProperty("deviceId").GetString() != peer.DeviceId)
        throw new InvalidDataException("Linux hello is not bound to its approved identity.");
    var hello = new { protocol = "batcave.input.v0", type = "device.hello", messageId = Guid.NewGuid().ToString("N"),
        sourceDeviceId = identity.DeviceId, sentAtUtc = DateTimeOffset.UtcNow,
        payload = new { deviceId = identity.DeviceId, deviceName = "Windows interoperability fixture", operatingSystem = "Windows",
            agentName = "BatCave.WindowsAgent", virtualDesktopBounds = new { left = 0, top = 0, width = 1920, height = 1080, right = 1919, bottom = 1079 } } };
    await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(hello, jsonOptions) + "\n"), timeout.Token);
    await stream.FlushAsync(timeout.Token);
    await Task.Delay(500, timeout.Token);
    Console.WriteLine("PASS: Windows CNG/SChannel and Linux/OpenSSL verified pinned mutual TLS 1.3 and identity-bound hello in " + (outgoing ? "Windows-connect" : "Windows-listen") + " direction.");
}
finally
{
    listener.Stop();
    identity.DeleteIdentity(identityDirectory);
}
