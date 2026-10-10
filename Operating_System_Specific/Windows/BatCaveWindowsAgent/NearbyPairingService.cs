using System.Net;
using System.Net.Sockets;

namespace BatCave.WindowsAgent;

internal sealed class NearbyPairingService : IDisposable
{
    internal const int DefaultPort = 47873;
    internal sealed record Result(NearbyPairingProtocol.Descriptor Peer, IPAddress Address, bool Initiator);
    private readonly DeviceIdentityStore _identity;
    private readonly NearbyPairingProtocol.Descriptor _local;
    private readonly Func<NearbyPairingProtocol.Descriptor, bool> _acceptPeer;
    private readonly Func<NearbyPairingProtocol.Confirmation, CancellationToken, Task<bool>> _confirm;
    private readonly Func<Result, CancellationToken, Task> _completed;
    private readonly Func<IPAddress, bool> _onLan;
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _attempts = new();
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource? _window, _active;
    private TcpListener? _listener;
    private Task? _acceptTask;
    internal event Action<string>? Failed;
    internal bool IsOpen { get { lock (_gate) return _window is { IsCancellationRequested: false }; } }
    internal bool Busy { get { lock (_gate) return _active is not null; } }

    internal NearbyPairingService(DeviceIdentityStore identity, NearbyPairingProtocol.Descriptor local,
        Func<NearbyPairingProtocol.Descriptor, bool> acceptPeer,
        Func<NearbyPairingProtocol.Confirmation, CancellationToken, Task<bool>> confirm,
        Func<Result, CancellationToken, Task> completed, Func<IPAddress, bool> onLan)
    {
        _identity = identity; _local = local; _acceptPeer = acceptPeer;
        _confirm = confirm; _completed = completed; _onLan = onLan;
    }

    internal void Start()
    {
        var listener = new TcpListener(IPAddress.Any, DefaultPort);
        listener.Server.ExclusiveAddressUse = true;
        listener.Start(4); _listener = listener; _acceptTask = AcceptAsync();
    }

    internal void OpenWindow()
    {
        lock (_gate)
        {
            if (_stop.IsCancellationRequested || _active is not null) return;
            _window?.Cancel(); _window?.Dispose();
            _window = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            _window.CancelAfter(TimeSpan.FromSeconds(180));
        }
    }

    internal void CloseWindow() { lock (_gate) { _window?.Cancel(); _active?.Cancel(); } }
    internal void Cancel() { lock (_gate) _active?.Cancel(); }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                CancellationTokenSource? session = null;
                lock (_gate)
                {
                    var now = DateTimeOffset.UtcNow;
                    while (_attempts.TryPeek(out var oldest) && now - oldest > TimeSpan.FromMinutes(1)) _attempts.Dequeue();
                    if (IsOpen && _active is null && _attempts.Count < 6
                        && (_attempts.Count == 0 || now - _attempts.Last() >= TimeSpan.FromSeconds(5))
                        && client.Client.RemoteEndPoint is IPEndPoint endpoint && _onLan(endpoint.Address))
                    {
                        _attempts.Enqueue(now);
                        session = _active = CancellationTokenSource.CreateLinkedTokenSource(_window!.Token);
                    }
                }
                if (session is null) { client.Dispose(); continue; }
                _ = RunAsync(client, false, session, null);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    internal async Task LinkAsync(NearbyComputer computer)
    {
        CancellationTokenSource session;
        lock (_gate)
        {
            if (!IsOpen || _active is not null || !computer.Pairing || !_onLan(computer.Address)
                || DateTimeOffset.UtcNow - computer.Seen > TimeSpan.FromSeconds(12))
                throw new InvalidOperationException("Open BatCave on both computers and search again.");
            session = _active = CancellationTokenSource.CreateLinkedTokenSource(_window!.Token);
        }
        var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(computer.Address, computer.PairingPort, deadline.Token).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose(); End(session);
            throw;
        }
        await RunAsync(client, true, session, new(computer.DeviceId, computer.Fingerprint)).ConfigureAwait(false);
    }

    private async Task RunAsync(TcpClient client, bool initiator, CancellationTokenSource session, ApprovedPeer? expected)
    {
        using (client)
        using (session.Token.Register(client.Dispose))
        {
            try
            {
                client.NoDelay = true;
                var address = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
                var peer = await NearbyPairingProtocol.RunAsync(client.GetStream(), _identity.Certificate, _local,
                    initiator, _confirm, _acceptPeer, session.Token, expected).ConfigureAwait(false);
                session.Token.ThrowIfCancellationRequested();
                await _completed(new(peer, address, initiator), session.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException || session.IsCancellationRequested) { }
            catch (Exception)
            {
                Failed?.Invoke("Could not link this computer. Keep BatCave open on both, then select the computer to try again.");
            }
            finally { End(session); }
        }
    }

    private void End(CancellationTokenSource session)
    {
        lock (_gate) { if (ReferenceEquals(_active, session)) _active = null; }
        session.Dispose();
    }

    public void Dispose()
    {
        _stop.Cancel(); CloseWindow(); _listener?.Stop();
    }
}
