using System.Net;

namespace BatCave.WindowsAgent;

internal sealed partial class DesktopWindow
{
    private NearbyDiscovery? _discovery;
    private NearbyPairingService? _pairing;
    private readonly NearbyComputerList _nearbyList = new() { Width = 560, Height = 250, AccessibleName = "Nearby computers" };
    private readonly FlowLayoutPanel _nearbyPanel = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    private readonly Label _nearbyStatus = Body("Looking for computers…");
    private readonly DesktopButton _searchAgain = new() { Text = "Search again", Quiet = true };
    private readonly FlowLayoutPanel _comparisonPanel = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    private readonly Label _comparisonCode = new() { AutoSize = true, Font = new Font("Consolas", 32, FontStyle.Bold), ForeColor = DesktopTheme.Accent, Margin = new Padding(0, 8, 0, 20) };
    private readonly Label _comparisonHelp = Body("Check that these eight digits match on your other computer. Then choose Codes match on both screens.");
    private TaskCompletionSource<bool>? _comparisonAnswer;
    private NearbyPairingProtocol.Confirmation? _comparison;
    private string? _nearbyError;
    private bool _linkingNearby;
    private InitialSharingConsent? _initialSharingConsent;

    private Control BuildNearbyPage()
    {
        var page = new SetupPage(); page.Controls.Add(_pairTitle); page.Controls.Add(_pairExplanation);
        _nearbyPanel.Controls.Add(_nearbyList); _nearbyPanel.Controls.Add(_nearbyStatus);
        _searchAgain.Click += (_, _) => { _nearbyError = null; OpenPairingWindow(); RefreshNearby(); UpdateSetup(); };
        _nearbyPanel.Controls.Add(_searchAgain); page.Controls.Add(_nearbyPanel);
        _nearbyList.SelectedIndexChanged += (_, _) => UpdateSetup();
        _comparisonPanel.Controls.Add(_comparisonCode);
        _comparisonPanel.Controls.Add(_comparisonHelp);
        page.Controls.Add(_comparisonPanel);

        _peers.Width = 420; _peers.Dock = DockStyle.None; _peers.AccessibleName = "Linked computer";
        _pairedPanel.Controls.Add(_peers); page.Controls.Add(_pairedPanel);
        _rolePanel.Controls.Add(new Label { Text = "Keyboard and mouse", AutoSize = true, Margin = new Padding(0, 5, 12, 0) });
        _role.Items.AddRange(["This PC", "The other computer"]); _role.SelectedIndex = 0;
        _role.AccessibleName = "Computer providing the keyboard and mouse";
        _rolePanel.Controls.Add(_role); page.Controls.Add(_rolePanel);
        _role.SelectedIndexChanged += (_, _) => Guard(() => { if (!_loadingConnection) { Pause("input role changed"); SaveSettings(); UpdateSetup(); } });
        _managePeers.Controls.Add(Button("Arrange screens", () => ShowStep(2)));
        _managePeers.Controls.Add(Button("Add another computer", () => { _addingComputer = true; OpenPairingWindow(); RefreshNearby(); UpdateSetup(); }));
        _removePeer.Click += (_, _) => Unpair(); _managePeers.Controls.Add(_removePeer); page.Controls.Add(_managePeers);
        page.Resize += (_, _) =>
        {
            var width = Math.Max(100, page.ClientSize.Width - page.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 8);
            foreach (var panel in new[] { _nearbyPanel, _comparisonPanel, _pairedPanel, _rolePanel, _managePeers })
            {
                panel.MaximumSize = new Size(width, 0);
                foreach (Control child in panel.Controls)
                    if (child is Label label) label.MaximumSize = new Size(width, 0);
            }
            _nearbyList.Width = width; _peers.Width = Math.Min(420, width);
            _nearbyList.Height = Math.Max(145, Math.Min(250, page.ClientSize.Height - 210));
        };
        return page;
    }

    private void StartNearby()
    {
        var card = _identity.ExportPublicCard(Environment.MachineName);
        _discovery = new(_identity.DeviceId, () => NearbyDiscovery.Announcement(card, LanBridgePeer.SecurePort, NearbyPairingService.DefaultPort, _pairing?.IsOpen == true));
        _pairing = new(_identity, new(card, LanBridgePeer.SecurePort), AcceptNearbyIdentity, ConfirmNearbyAsync, CompleteNearbyAsync, _discovery.IsLocalAddress);
        _pairing.Failed += message => PostUi(() => { _nearbyError = message; ClearComparison(); UpdateSetup(); });
        try
        {
            _discovery.Start(); _pairing.Start();
            if (_setupStep == 1 && !_reconnectSuspended) _pairing.OpenWindow();
        }
        catch (Exception)
        {
            _pairing.Dispose(); _pairing = null;
            _nearbyError = "Nearby linking could not start. Quit any other BatCave copy, allow network access, then reopen BatCave.";
        }
    }

    private bool AcceptNearbyIdentity(NearbyPairingProtocol.Descriptor peer)
    {
        var cards = _approvals.Cards;
        return (cards.Length < 64 || cards.Any(c => c.DeviceId == peer.Card.DeviceId))
            && cards.All(c => c.DeviceId == peer.Card.DeviceId
                ? c.CertificateSha256.Equals(peer.Card.CertificateSha256, StringComparison.OrdinalIgnoreCase)
                : !c.CertificateSha256.Equals(peer.Card.CertificateSha256, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> ConfirmNearbyAsync(NearbyPairingProtocol.Confirmation comparison, CancellationToken token)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = token.Register(() =>
        {
            answer.TrySetCanceled(token);
            PostUi(() => { if (ReferenceEquals(_comparisonAnswer, answer)) { ClearComparison(); UpdateSetup(); } });
        });
        PostUi(() =>
        {
            if (token.IsCancellationRequested || _setupStep != 1 || _reconnectSuspended || _pairing?.IsOpen != true)
            { answer.TrySetResult(false); return; }
            _comparisonAnswer = answer; _comparison = comparison;
            UpdateSetup();
        });
        return await answer.Task.WaitAsync(token).ConfigureAwait(false);
    }

    private Task CompleteNearbyAsync(NearbyPairingService.Result result, CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PostUi(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (_setupStep != 1 || _reconnectSuspended || !AcceptNearbyIdentity(result.Peer))
                    throw new OperationCanceledException("Pairing is no longer active.");
                // Both explicit approvals are complete. Existing keys/profiles stay intact.
                var hadPeer = _lan.HasPeer;
                var previousSession = _lan.SessionGeneration;
                _connectionSettings[result.Peer.Card.DeviceId] = new(result.Address.ToString(), !result.Initiator, result.Peer.InputPort);
                ApplyApproval(result.Peer.Card); ClearComparison();
                if (!hadPeer)
                    _initialSharingConsent = new(result.Peer.Card.DeviceId, result.Peer.Card.CertificateSha256,
                        !result.Initiator, previousSession, DateTimeOffset.UtcNow.AddSeconds(30));
                _connectWanted = string.CompareOrdinal(_identity.DeviceId, result.Peer.Card.DeviceId) < 0;
                _nextConnectAttempt = DateTimeOffset.UtcNow;
                Notice("Computer linked. Connecting automatically…"); UpdateSetup();
                completion.TrySetResult();
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        return completion.Task.WaitAsync(token);
    }

    private void ClearComparison() { _comparison = null; _comparisonAnswer = null; }
    private void OpenPairingWindow()
    {
        try { _pairing?.OpenWindow(); }
        catch (Exception) { _nearbyError = "Nearby linking could not start. Quit any other BatCave copy, then search again."; }
    }
    private void ClosePairingWindow()
    {
        Interlocked.Exchange(ref _initialSharingConsent, null);
        _pairing?.CloseWindow(); _comparisonAnswer?.TrySetResult(false); ClearComparison();
    }

    private void RefreshNearby()
    {
        if (_inspectionOnly) return;
        if (_comparison is not null && _pairing?.Busy == false)
        { ClearComparison(); _nearbyError = "Linking ended. Select your computer to try again."; }
        if (_discovery is null) return;
        var computers = _discovery.Computers;
        var selected = (_nearbyList.SelectedItem as NearbyComputer)?.DeviceId;
        // Avoid rebuilding native selection/focus on every timer tick.
        var existing = _nearbyList.Items.Cast<NearbyComputer>().ToArray();
        if (!existing.Select(p => p with { Seen = default }).SequenceEqual(computers.Select(p => p with { Seen = default })))
        {
            _nearbyList.BeginUpdate(); _nearbyList.Items.Clear(); _nearbyList.Items.AddRange(computers);
            _nearbyList.SelectedIndex = Array.FindIndex(computers, p => p.DeviceId == selected);
            _nearbyList.EndUpdate();
        }
        else for (var i = 0; i < computers.Length; i++) _nearbyList.Items[i] = computers[i];
        if (_peers.SelectedItem is not PeerItem approved || _lan.HasPeer || _connecting || _reconnectSuspended) return;
        var reachable = computers.FirstOrDefault(p => p.DeviceId == approved.Card.DeviceId
            && p.Fingerprint.Equals(approved.Card.CertificateSha256, StringComparison.OrdinalIgnoreCase));
        if (reachable is null) return;
        _loadingConnection = true;
        try { _host.Text = reachable.Address.ToString(); _inputPort = reachable.InputPort; }
        finally { _loadingConnection = false; }
        _connectWanted = string.CompareOrdinal(_identity.DeviceId, approved.Card.DeviceId) < 0;
    }

    private void UpdateNearbyPage(DesktopSetupState state)
    {
        var comparing = _comparison is not null;
        var browsing = !state.PeerSelected || _addingComputer;
        _pairTitle.Text = comparing ? "Link with " + _comparison!.Peer.Card.DisplayName + "?" : browsing ? "Nearby computers" : (_peers.SelectedItem as PeerItem)!.Card.DisplayName;
        _pairExplanation.Visible = !comparing;
        _pairExplanation.Text = comparing ? "A quick check keeps your connection private."
            : browsing ? "Open BatCave on your other computer. Choose it below to link."
            : state.Connected ? ReceiveInput ? "Connected securely. Enable input here to use your other computer's keyboard and mouse."
                : "Connected securely. Your other screen is on the right by default. You can change that in Arrange screens."
            : "Looking for this computer. Keep BatCave open on both devices; we'll connect automatically.";
        _nearbyPanel.Visible = browsing && !comparing;
        _comparisonPanel.Visible = comparing;
        _pairedPanel.Visible = !browsing && !comparing && _peers.Items.Count > 1;
        _peers.Visible = _peers.Items.Count > 1;
        _rolePanel.Visible = _managePeers.Visible = !browsing && !comparing;
        _removePeer.Enabled = state.PeerSelected;
        _nearbyList.Visible = _nearbyList.Items.Count > 0;
        _nearbyStatus.Text = _nearbyError ?? _discovery?.Error ?? (_nearbyList.Items.Count == 0
            ? "Looking for your other computer…\n\nKeep both computers on the same Wi-Fi or wired network, with BatCave open."
            : "Select the computer you want to link. Windows, Mac and Linux work the same way.");
        if (!_inspectionOnly && _pairing is { IsOpen: false } && browsing)
            _nearbyStatus.Text = "Nearby linking is paused. Choose Search again when both computers are ready.";
        _searchAgain.Enabled = _pairing?.Busy != true && !_linkingNearby;
        if (_setupStep != 1) return;
        if (comparing)
        {
            _back.Visible = true; _back.Text = "Cancel linking";
            _comparisonCode.Text = _comparison!.Code;
            _comparisonHelp.Text = "Check that these eight digits match on your other computer. Approve on both screens to link and start sharing.\n\n"
                + (_comparison.Initiator ? "This PC will share its keyboard and mouse." : "This PC will use the other computer's keyboard and mouse.");
            var waiting = _comparisonAnswer?.Task.IsCompleted == true;
            _start.Text = waiting ? "Waiting for the other computer…" : "Codes match · Link and share";
            _start.Enabled = !waiting;
        }
        else if (browsing)
        {
            _start.Text = _linkingNearby || _pairing?.Busy == true ? "Linking…" : "Link computer";
            _start.Enabled = !_linkingNearby && _pairing?.Busy != true && (_inspectionOnly || _pairing?.IsOpen == true)
                && _nearbyList.SelectedItem is NearbyComputer { Pairing: true };
        }
        else if (!state.Connected) { _start.Text = "Waiting for your computer…"; _start.Enabled = false; }
    }

    private bool ActivateNearbyPrimary()
    {
        if (_comparison is not null) { _comparisonAnswer?.TrySetResult(true); UpdateSetup(); return true; }
        if (_peers.SelectedItem is PeerItem && !_addingComputer) return false;
        if (_nearbyList.SelectedItem is NearbyComputer computer && _start.Enabled) _ = LinkNearbyAsync(computer);
        return true;
    }

    private async Task LinkNearbyAsync(NearbyComputer computer)
    {
        if (_inspectionOnly || _pairing is null || _linkingNearby) return;
        _linkingNearby = true; _nearbyError = null; UpdateSetup();
        try { await _pairing.LinkAsync(computer); }
        catch (Exception) { _nearbyError = "Could not reach this computer. Keep BatCave open on both, then search again."; }
        finally { _linkingNearby = false; ClearComparison(); if (!IsDisposed) UpdateSetup(); }
    }
}

internal sealed class NearbyComputerList : ListBox
{
    internal NearbyComputerList()
    {
        DrawMode = DrawMode.OwnerDrawFixed; ItemHeight = 72; IntegralHeight = false;
        BorderStyle = BorderStyle.None; BackColor = Color.White; Margin = new Padding(0, 8, 0, 16);
    }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || Items[e.Index] is not NearbyComputer computer) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using var fill = new SolidBrush(selected ? Color.FromArgb(239, 236, 255) : Color.FromArgb(247, 248, 251));
        var bounds = new Rectangle(e.Bounds.X, e.Bounds.Y + 3, e.Bounds.Width - 2, e.Bounds.Height - 6);
        e.Graphics.FillRectangle(fill, bounds);
        using var ink = new Pen(selected ? DesktopTheme.Accent : DesktopTheme.Muted, 2);
        var screen = new Rectangle(bounds.Left + 17, bounds.Top + 16, 29, 20);
        e.Graphics.DrawRectangle(ink, screen);
        e.Graphics.DrawLine(ink, screen.Left + 14, screen.Bottom, screen.Left + 14, screen.Bottom + 7);
        e.Graphics.DrawLine(ink, screen.Left + 6, screen.Bottom + 7, screen.Right - 6, screen.Bottom + 7);
        using var heading = new Font(Font, FontStyle.Bold);
        TextRenderer.DrawText(e.Graphics, computer.DisplayName, heading, new Rectangle(bounds.Left + 63, bounds.Top + 11, bounds.Width - 74, 25), DesktopTheme.Ink,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        var platform = computer.Platform == "macos" ? "Mac" : computer.Platform == "linux" ? "Linux" : "Windows";
        TextRenderer.DrawText(e.Graphics, platform + (computer.Pairing ? "  ·  Ready to link" : "  ·  Open Nearby computers there"), Font,
            new Rectangle(bounds.Left + 63, bounds.Top + 36, bounds.Width - 74, 23), DesktopTheme.Muted,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (selected && Focused) e.DrawFocusRectangle();
    }
}
