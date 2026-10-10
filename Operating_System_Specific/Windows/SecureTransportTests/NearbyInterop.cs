using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using BatCave.WindowsAgent;

internal static class NearbyInterop
{
    // Isolated cross-language test endpoint; never opens an installed profile,
    // dispatches input or writes approvals. The harness compares reported codes.
    internal static async Task Run(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 4 || args[0] is not ("client" or "server"))
            throw new ArgumentException("--nearby-interop client|server numeric-address port result-path");
        var address = IPAddress.Parse(args[1]); var port = int.Parse(args[2]);
        if (address.AddressFamily != AddressFamily.InterNetwork || port is < 1 or > 65535)
            throw new ArgumentException("IPv4 and a valid test port are required.");
        var directory = Path.Combine(Path.GetTempPath(), "BatCave-Nearby-Interop-" + Guid.NewGuid().ToString("N"));
        using var identity = DeviceIdentityStore.OpenOrCreate(directory);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var client = new TcpClient(AddressFamily.InterNetwork);
            TcpClient connection = client;
            using var listener = new TcpListener(address, port);
            if (args[0] == "server")
            {
                listener.Start(1);
                Console.WriteLine("READY: isolated Windows pairing listener");
                connection = await listener.AcceptTcpClientAsync(timeout.Token);
            }
            else await client.ConnectAsync(address, port, timeout.Token);
            using (connection)
            {
                string? code = null;
                var peer = await NearbyPairingProtocol.RunAsync(connection.GetStream(), identity.Certificate,
                    new(identity.ExportPublicCard("Synthetic Windows computer"), 47871), args[0] == "client",
                    (comparison, _) => { code = comparison.Code; return Task.FromResult(true); }, _ => true, timeout.Token);
                File.WriteAllText(args[3], JsonSerializer.Serialize(new { code, localId = identity.DeviceId,
                    remoteId = peer.Card.DeviceId, remotePort = peer.InputPort, verified = true }));
                Console.WriteLine("PASS: Windows bootstrap verified both identity proofs and approvals; compare the result code with the Linux fixture.");
            }
        }
        finally { identity.DeleteIdentity(directory); Directory.Delete(directory); }
    }
}
