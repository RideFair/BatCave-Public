using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace BatCave.WindowsAgent;

internal sealed record NearbyComputer(string DeviceId, string DisplayName, string Platform, string Fingerprint,
    IPAddress Address, int InputPort, int PairingPort, bool Pairing, DateTimeOffset Seen)
{
    public override string ToString() => DisplayName + "  ·  " + (Platform == "macos" ? "Mac" : Platform == "linux" ? "Linux" : "Windows");
}

internal sealed class NearbyDiscovery : IDisposable
{
    internal const int DiscoveryPort = 47872;
    internal static readonly IPAddress Group = IPAddress.Parse("239.255.47.87");
    // This JSON is UDP protocol data, never HTML. Avoid inflating ordinary
    // Unicode computer names into six-byte escapes under the shared byte limit.
    private static readonly JsonSerializerOptions WireJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal sealed record LanInterface(IPAddress Address, IPAddress Mask);
    private readonly object _gate = new();
    private readonly Dictionary<string, NearbyComputer> _computers = new(StringComparer.Ordinal);
    private readonly Func<byte[]> _announcement;
    private readonly string _localId;
    private readonly CancellationTokenSource _stop = new();
    private UdpClient? _socket;
    private IPAddress _group = Group;
    private int _port = DiscoveryPort;
    private LanInterface[] _interfaces = [];
    private readonly HashSet<LanInterface> _joined = [];
    private Task? _receive, _announce;
    internal string? Error { get; private set; }
    internal long ReceivedPackets, OffLanPackets, InvalidPackets;

    internal NearbyDiscovery(string localId, Func<byte[]> announcement) { _localId = localId; _announcement = announcement; }

    internal NearbyComputer[] Computers
    {
        get
        {
            lock (_gate)
            {
                Expire(DateTimeOffset.UtcNow);
                return _computers.Values.OrderBy(p => p.DisplayName, StringComparer.CurrentCulture).ToArray();
            }
        }
    }

    internal static byte[] Announcement(DeviceIdentityStore.PairingCard card, int inputPort, int pairingPort, bool pairing) =>
        JsonSerializer.SerializeToUtf8Bytes(new { type = "batcave.nearby", version = 1, deviceId = card.DeviceId,
            displayName = card.DisplayName, platform = "windows", fingerprint = card.CertificateSha256.ToUpperInvariant(), inputPort, pairingPort, pairing }, WireJson);

    internal static NearbyComputer ReadAnnouncement(ReadOnlySpan<byte> bytes, IPAddress source, DateTimeOffset now)
    {
        if (bytes.Length > 1024 || !EligibleAddress(source)) throw new InvalidDataException("Invalid discovery announcement.");
        using var document = NearbyPairingProtocol.Parse(new UTF8Encoding(false, true).GetString(bytes));
        var value = document.RootElement;
        NearbyPairingProtocol.Fields(value, "type", "version", "deviceId", "displayName", "platform", "fingerprint", "inputPort", "pairingPort", "pairing");
        if (value.GetProperty("type").GetString() != "batcave.nearby" || value.GetProperty("version").GetInt32() != 1)
            throw new InvalidDataException("Unsupported discovery version.");
        var id = value.GetProperty("deviceId").GetString()!; var name = value.GetProperty("displayName").GetString()!;
        NearbyPairingProtocol.ValidateLabel(id); NearbyPairingProtocol.ValidateLabel(name);
        var platform = value.GetProperty("platform").GetString()!;
        if (platform is not ("windows" or "macos" or "linux")) throw new InvalidDataException("Unknown computer platform.");
        var fingerprint = value.GetProperty("fingerprint").GetString()!; NearbyPairingProtocol.Hex(fingerprint, 32);
        var input = value.GetProperty("inputPort").GetInt32(); var pairing = value.GetProperty("pairingPort").GetInt32();
        if (input is < 1 or > 65535 || pairing is < 1 or > 65535 || input == pairing) throw new InvalidDataException("Invalid advertised ports.");
        return new(id, name, platform, fingerprint, source, input, pairing, value.GetProperty("pairing").GetBoolean(), now);
    }

    internal static bool EligibleAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] is > 0 and < 224 && !address.Equals(IPAddress.Broadcast);
    }

    internal static bool OnLan(IPAddress address, IEnumerable<LanInterface> interfaces)
    {
        if (!EligibleAddress(address)) return false;
        var bytes = address.GetAddressBytes();
        return interfaces.Any(network =>
        {
            var local = network.Address.GetAddressBytes(); var mask = network.Mask.GetAddressBytes();
            return mask.Any(b => b != 0) && Enumerable.Range(0, 4).All(i => (bytes[i] & mask[i]) == (local[i] & mask[i]))
                && Enumerable.Range(0, 4).Any(i => (bytes[i] | mask[i]) != 255);
        });
    }

    internal static LanInterface[] Interfaces() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.SupportsMulticast
            && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => EligibleAddress(a.Address)).Select(a => new LanInterface(a.Address, a.IPv4Mask)).Distinct().ToArray();

    internal bool IsLocalAddress(IPAddress address) => OnLan(address, _interfaces);

    internal void Start(int port = DiscoveryPort, IPAddress? multicastGroup = null, bool loopback = false)
    {
        if (_socket is not null) return;
        _group = multicastGroup ?? Group;
        var socket = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            socket.Client.ExclusiveAddressUse = false;
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            _port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
            socket.MulticastLoopback = loopback;
            socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
            _socket = socket;
            RefreshInterfaces();
            _receive = ReceiveAsync(); _announce = AnnounceAsync();
        }
        catch { socket.Dispose(); _socket = null; throw; }
    }

    private void RefreshInterfaces()
    {
        var next = Interfaces();
        foreach (var old in _joined.Except(next).ToArray())
        {
            try { _socket!.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership, new MulticastOption(_group, old.Address)); }
            catch (SocketException) { }
            _joined.Remove(old);
        }
        foreach (var network in next.Except(_joined))
            try { _socket!.JoinMulticastGroup(_group, network.Address); _joined.Add(network); }
            catch (SocketException) { Error = "Network discovery could not join this network. Reconnect Wi-Fi and search again."; }
        _interfaces = next;
    }

    private async Task AnnounceAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                Error = null; RefreshInterfaces();
                var data = _announcement();
                if (data.Length > 1024) throw new InvalidDataException("Discovery announcement exceeds its limit.");
                if (_interfaces.Length == 0) Error = "Connect to a Wi-Fi or wired network to find your other computer.";
                foreach (var network in _interfaces)
                {
                    try
                    {
                        _socket!.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, network.Address.GetAddressBytes());
                        await _socket.SendAsync(data, new IPEndPoint(_group, _port), _stop.Token).ConfigureAwait(false);
                    }
                    catch (SocketException) { Error = "Nearby discovery is blocked. Allow BatCave on your private network, then search again."; }
                }
                await Task.Delay(TimeSpan.FromSeconds(3), _stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
        catch (Exception) { Error = "Nearby discovery stopped. Reopen BatCave to restart it."; }
    }

    private async Task ReceiveAsync()
    {
        var second = DateTimeOffset.UtcNow; var count = 0;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var packet = await _socket!.ReceiveAsync(_stop.Token).ConfigureAwait(false);
                Interlocked.Increment(ref ReceivedPackets);
                var now = DateTimeOffset.UtcNow;
                if ((now - second).TotalSeconds >= 1) { second = now; count = 0; }
                if (++count > 64) { await Task.Delay(100, _stop.Token).ConfigureAwait(false); continue; }
                if (!IsLocalAddress(packet.RemoteEndPoint.Address)) { Interlocked.Increment(ref OffLanPackets); continue; }
                try { Remember(ReadAnnouncement(packet.Buffer, packet.RemoteEndPoint.Address, now), now); }
                catch (Exception ex) when (ex is ArgumentException or InvalidDataException or JsonException or FormatException or InvalidOperationException or NullReferenceException) { Interlocked.Increment(ref InvalidPackets); }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
        catch (SocketException)
        {
            if (!_stop.IsCancellationRequested) Error = "Nearby discovery stopped. Reopen BatCave to restart it.";
            _stop.Cancel(); _socket?.Dispose();
        }
    }

    internal void Remember(NearbyComputer computer, DateTimeOffset now)
    {
        if (computer.DeviceId == _localId) return;
        lock (_gate)
        {
            Expire(now);
            if (_computers.Count >= 64 && !_computers.ContainsKey(computer.DeviceId)) return;
            _computers[computer.DeviceId] = computer;
        }
    }

    private void Expire(DateTimeOffset now)
    {
        foreach (var key in _computers.Where(p => now - p.Value.Seen > TimeSpan.FromSeconds(12)).Select(p => p.Key).ToArray()) _computers.Remove(key);
    }

    public void Dispose() { _stop.Cancel(); _socket?.Dispose(); }
}
