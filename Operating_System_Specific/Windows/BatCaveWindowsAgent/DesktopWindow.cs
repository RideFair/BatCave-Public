using Microsoft.Win32;
using System.Text.Json;

namespace BatCave.WindowsAgent;

internal sealed class DesktopWindow : Form
{
    private readonly DeviceIdentityStore _identity;
    private readonly ApprovedPeerStore _approvals;
    private readonly LanBridgePeer _lan;
    private readonly LocalBridgeAgent _agent;
    private readonly string _topologyPath;
    private readonly ComboBox _peers = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 530 };
    private readonly ComboBox _side = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private readonly Label _status = new() { AutoSize = true };
    private readonly Button _start = new() { Text = "Start sharing", AutoSize = true, Enabled = false };
    private readonly NotifyIcon _tray = new() { Visible = true, Text = "BatCave", Icon = SystemIcons.Application };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private DeviceHelloPayload? _hello;
    private bool _quitting;
    private bool _sharing;
    private volatile string? _selectedPeerId;
    private bool _refreshingPeers;
    private readonly string _settingsPath;
    private sealed record Settings(string? SelectedPeerId, bool PeerOnRight, bool ShareClipboard = false);
    private sealed record PeerItem(DeviceIdentityStore.PairingCard Card)
    {
        public override string ToString() => Card.DisplayName + " — " + Card.DeviceId;
    }

    public DesktopWindow(DeviceIdentityStore identity, ApprovedPeerStore approvals, string directory)
    {
        _identity = identity;
        _approvals = approvals;
        _topologyPath = Path.Combine(directory, "topology.secure.json");
        _settingsPath = Path.Combine(directory, "desktop-settings.json");
        _lan = new LanBridgePeer(identity, () => _approvals.Peers.Where(peer => peer.DeviceId == _selectedPeerId).ToArray());
        _agent = new LocalBridgeAgent(_lan, _topologyPath);
        _ = _agent.Handle;
        Text = "BatCave — secure desktop preview";
        var appIcon = Environment.ProcessPath is { } executable ? Icon.ExtractAssociatedIcon(executable) : null;
        if (appIcon is not null) { Icon = appIcon; _tray.Icon = appIcon; }
        Font = new Font("Segoe UI", 10);
        ClientSize = new Size(640, 510);
        MinimumSize = new Size(660, 550);
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(20), AutoScroll = true };
        Controls.Add(layout);
        layout.Controls.Add(new Label { Text = "Pair your computers once", Font = new Font(Font.FontFamily, 17, FontStyle.Bold), AutoSize = true });
        layout.Controls.Add(new Label { Text = "Exchange pairing cards through a trusted channel. Import the Mac's card here,\nthen import this computer's card on the Mac. Each computer must approve the other.", AutoSize = true, Margin = new Padding(3, 12, 3, 12) });
        var cardButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        cardButtons.Controls.Add(Button("Export this computer's card…", ExportCard));
        cardButtons.Controls.Add(Button("Import and approve a card…", ImportCard));
        layout.Controls.Add(cardButtons);
        layout.Controls.Add(new Label { Text = "Active computer", AutoSize = true, Margin = new Padding(3, 14, 3, 3) });
        layout.Controls.Add(_peers);
        layout.Controls.Add(Button("Unpair selected computer", Unpair));
        _side.Items.AddRange(new object[] { "Mac on the right", "Mac on the left" });
        _side.SelectedIndex = 0;
        layout.Controls.Add(_side);
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(3, 12, 3, 8) };
        actions.Controls.Add(_start);
        actions.Controls.Add(Button("Pause", () => Pause("paused by user")));
        layout.Controls.Add(actions);
        layout.Controls.Add(_status);
        layout.Controls.Add(new Label { Text = "Escape, F12, or Pause restores local control.\nIf Windows asks, allow BatCave on your private network to connect the Mac.", AutoSize = true, Margin = new Padding(3, 12, 3, 8) });
        var login = new CheckBox { Text = "Start BatCave when I sign in", AutoSize = true };
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            login.Checked = key?.GetValue("BatCaveSecure") is not null;
        login.CheckedChanged += (_, _) => Guard(() =>
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (login.Checked) { key.SetValue("BatCaveSecure", "\"" + Environment.ProcessPath + "\" --desktop"); }
            else { key.DeleteValue("BatCaveSecure", false); }
        });
        layout.Controls.Add(login);
        var clipboard = new CheckBox { Text = "Share text clipboard while sharing is active", AutoSize = true };
        layout.Controls.Add(clipboard);
        clipboard.CheckedChanged += (_, _) =>
        {
            _agent.ClipboardSharingEnabled = clipboard.Checked;
            SaveSettings();
        };
        layout.Controls.Add(Button("Quit BatCave", Quit));
        Settings? settings = null;
        if (File.Exists(_settingsPath))
            settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_settingsPath));
        _selectedPeerId = settings?.SelectedPeerId;
        _side.SelectedIndex = settings?.PeerOnRight == false ? 1 : 0;
        clipboard.Checked = settings?.ShareClipboard == true;
        RefreshPeers();
        _peers.SelectedIndexChanged += (_, _) => Guard(() =>
        {
            if (_refreshingPeers) { return; }
            Pause("active computer changed");
            _selectedPeerId = (_peers.SelectedItem as PeerItem)?.Card.DeviceId;
            _hello = null;
            _lan.DisconnectUnapprovedPeer();
            SaveSettings();
        });
        _side.SelectedIndexChanged += (_, _) => Guard(() =>
        {
            Pause("layout changed");
            if (_hello is not null) { WriteTopology(); }
            SaveSettings();
        });
        _start.Click += (_, _) => Guard(() =>
        {
            if (!_lan.HasPeer || _hello is null) { MessageBox.Show(this, "Open BatCave on the selected Mac and connect it to this PC first."); return; }
            var agents = System.Diagnostics.Process.GetProcessesByName("BatCave.WindowsAgent");
            try
            {
                if (agents.Any(process => process.Id != Environment.ProcessId))
                {
                    MessageBox.Show(this, "Another BatCave Windows agent is running. Quit the development or previous app before starting this preview.");
                    return;
                }
            }
            finally { foreach (var process in agents) { process.Dispose(); } }
            WriteTopology();
            _sharing = true;
            _agent.PostStartPersistentHandoff();
        });
        _lan.PeerHello += hello => BeginInvoke((Action)(() =>
        {
            if (hello.DeviceId == _selectedPeerId) { _hello = hello; }
        }));
        _lan.PeerDisconnected += _ =>
        {
            _agent.PostStopBridge("peer disconnected");
            if (!IsDisposed) { BeginInvoke((Action)(() => { _hello = null; _sharing = false; })); }
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open BatCave", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        menu.Items.Add("Pause", null, (_, _) => Pause("paused from tray"));
        menu.Items.Add("Quit", null, (_, _) => Quit());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => { Show(); Activate(); };
        _timer.Tick += (_, _) =>
        {
            _start.Enabled = _lan.HasPeer && _hello is not null;
            _sharing = _agent.IsBridgeActive;
            _status.Text = _lan.HasPeer ? (_sharing ? "Sharing — TLS 1.3 connected" : "Connected securely. Click Start sharing.")
                : "Waiting for the selected Mac — encrypted port 47871";
        };
        Shown += (_, _) => Guard(() => { _lan.StartListeningAsync(LanBridgePeer.SecurePort).GetAwaiter().GetResult(); _timer.Start(); });
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        FormClosing += (_, e) =>
        {
            if (!_quitting) { e.Cancel = true; Hide(); }
        };
        FormClosed += (_, _) =>
        {
            _timer.Stop();
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            _agent.Close(); // releases held input before transport disposal
            _lan.FlushAsync().GetAwaiter().GetResult();
            _lan.Dispose();
            _agent.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _timer.Dispose();
        };
    }

    private static Button Button(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += (_, _) => action();
        return button;
    }

    private void ExportCard() => Guard(() =>
    {
        using var dialog = new SaveFileDialog { Filter = "BatCave public pairing card|*.batcave-peer.json", FileName = Environment.MachineName + ".batcave-peer.json" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            File.WriteAllText(dialog.FileName, DeviceIdentityStore.SerializePublicCard(_identity.ExportPublicCard(Environment.MachineName)));
    });

    private void ImportCard() => Guard(() =>
    {
        using var dialog = new OpenFileDialog { Filter = "BatCave public pairing card|*.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) { return; }
        if (new FileInfo(dialog.FileName).Length > 16384) { throw new InvalidDataException("Pairing card exceeds 16 KiB."); }
        var json = File.ReadAllText(dialog.FileName);
        var peer = DeviceIdentityStore.ReadPublicCard(json);
        if (peer.DeviceId == _identity.DeviceId) { throw new InvalidDataException("This is your own pairing card."); }
        var card = JsonSerializer.Deserialize<DeviceIdentityStore.PairingCard>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        if (MessageBox.Show(this, "Approve " + card.DisplayName + "?\n\nDevice: " + card.DeviceId
            + "\n\nSHA-256:\n" + card.CertificateSha256
            + "\n\nConfirm this full fingerprint matches the card exported on your other computer. Approval allows that computer to share input and clipboard.",
            "Approve a computer", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) { return; }
        Pause("pairing changed");
        _approvals.Approve(card);
        _lan.DisconnectUnapprovedPeer();
        if (_selectedPeerId is null) { _selectedPeerId = card.DeviceId; }
        RefreshPeers();
        SaveSettings();
    });

    private void Unpair() => Guard(() =>
    {
        if (_peers.SelectedItem is not PeerItem item) { return; }
        Pause("device unpaired");
        _approvals.Remove(item.Card.DeviceId);
        _lan.DisconnectUnapprovedPeer();
        _selectedPeerId = null;
        _hello = null;
        RefreshPeers();
        SaveSettings();
    });

    private void RefreshPeers()
    {
        _refreshingPeers = true;
        try
        {
            _peers.Items.Clear();
            foreach (var card in _approvals.Cards)
            {
                var item = new PeerItem(card);
                _peers.Items.Add(item);
                if (card.DeviceId == _selectedPeerId) { _peers.SelectedItem = item; }
            }
        }
        finally { _refreshingPeers = false; }
    }

    private void WriteTopology()
    {
        var bounds = SystemInformation.VirtualScreen;
        SecureTopology.Write(_topologyPath, _identity.DeviceId,
            new VirtualDesktopBounds(bounds.Left, bounds.Top, bounds.Width, bounds.Height), _hello!, _side.SelectedIndex == 0);
    }
    private void SaveSettings() => File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new Settings(_selectedPeerId, _side.SelectedIndex == 0, _agent.ClipboardSharingEnabled)));
    private void Pause(string reason) { _sharing = false; _agent.StopSharingNow(reason); }
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend && !IsDisposed)
            ReleaseAndDisconnect("computer sleeping");
    }
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff && !IsDisposed)
            ReleaseAndDisconnect("session locked");
    }
    private void ReleaseAndDisconnect(string reason)
    {
        Action cleanup = () =>
        {
            Pause(reason);
            _lan.FlushAsync().GetAwaiter().GetResult();
            _lan.Disconnect();
        };
        try
        {
            if (InvokeRequired) { Invoke(cleanup); }
            else { cleanup(); }
        }
        catch (InvalidOperationException) when (IsDisposed || !IsHandleCreated) { }
    }
    private void Quit() { _quitting = true; Close(); }
    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "BatCave", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
