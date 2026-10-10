using Microsoft.Win32;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace BatCave.WindowsAgent;

internal sealed class DesktopWindow : Form
{
    private readonly DeviceIdentityStore _identity;
    private readonly ApprovedPeerStore _approvals;
    private readonly LanBridgePeer _lan;
    private readonly LocalBridgeAgent _agent;
    private readonly string _topologyPath;
    private readonly ComboBox _peers = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
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
    private readonly MonitorLayoutStore _layoutStore;
    private readonly ComboBox _monitor = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 270 };
    private readonly TrackBar _alignment = new() { Minimum = -100, Maximum = 100, TickStyle = TickStyle.None, Dock = DockStyle.Fill, AccessibleName = "Screen alignment" };
    private readonly MonitorLayoutCanvas _canvas = new() { Dock = DockStyle.Fill };
    private readonly Label _layoutStatus = new() { AutoSize = true, MaximumSize = new Size(590, 0) };
    private readonly Label _alignmentLabel = new() { AutoSize = true };
    private readonly Button _saveLayout = new() { Text = "Save layout", AutoSize = true };
    private IReadOnlyList<MonitorDescriptor> _monitors = Array.Empty<MonitorDescriptor>();
    private bool _loadingLayout;
    private bool _layoutDirty;
    private bool _layoutValid;
    private string? _legacyLayoutPeer;
    private PeerSide _legacySide = PeerSide.Right;
    private sealed record MonitorItem(MonitorDescriptor Monitor)
    {
        public override string ToString() => Monitor.Label;
    }

    private sealed record Settings(string? SelectedPeerId, bool PeerOnRight, bool ShareClipboard = false);
    private sealed record PeerItem(DeviceIdentityStore.PairingCard Card)
    {
        public override string ToString() => Card.DisplayName;
    }

    public DesktopWindow(DeviceIdentityStore identity, ApprovedPeerStore approvals, string directory, bool inspectionOnly = false)
    {
        _identity = identity;
        _approvals = approvals;
        _topologyPath = Path.Combine(directory, "topology.secure.json");
        _settingsPath = Path.Combine(directory, "desktop-settings.json");
        _layoutStore = new MonitorLayoutStore(Path.Combine(directory, "monitor-layouts.json"));
        _lan = new LanBridgePeer(identity, () => _approvals.Peers.Where(peer => peer.DeviceId == _selectedPeerId).ToArray());
        _agent = new LocalBridgeAgent(_lan, _topologyPath);
        if (!inspectionOnly) _ = _agent.Handle;
        Text = "BatCave Preview";
        var appIcon = Environment.ProcessPath is { } executable ? Icon.ExtractAssociatedIcon(executable) : null;
        if (appIcon is not null) { Icon = appIcon; _tray.Icon = appIcon; }
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(245, 247, 251);
        ForeColor = Color.FromArgb(34, 49, 72);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1060, 810);
        MinimumSize = new Size(920, 690);
        StartPosition = FormStartPosition.CenterScreen;
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(26, 20, 26, 18), ColumnCount = 1, RowCount = 3 };
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 87));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        Controls.Add(shell);
        var header = new Panel { Dock = DockStyle.Fill };
        header.Controls.Add(new Label { Text = "BatCave", Font = new Font(Font.FontFamily, 25, FontStyle.Bold), Location = new Point(0, 0), AutoSize = true });
        header.Controls.Add(new Label { Text = "One keyboard. Every screen.", ForeColor = Color.FromArgb(101, 119, 145), Location = new Point(2, 51), AutoSize = true });
        var badge = new Label { Text = "SECURE PREVIEW", Font = new Font(Font.FontFamily, 9, FontStyle.Bold), ForeColor = Color.FromArgb(43, 106, 192), BackColor = Color.FromArgb(227, 238, 252), AutoSize = true, Padding = new Padding(12, 7, 12, 7) };
        header.Controls.Add(badge);
        header.Resize += (_, _) => badge.Location = new Point(header.ClientSize.Width - badge.PreferredWidth - 4, 12);
        shell.Controls.Add(header, 0, 0);
        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 302));
        shell.Controls.Add(columns, 0, 1);
        var main = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(20), Margin = new Padding(0, 0, 18, 0), ColumnCount = 1, RowCount = 8 };
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 63));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 49));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        columns.Controls.Add(main, 0, 0);
        main.Controls.Add(Title("Arrange your screens"), 0, 0);
        main.Controls.Add(new Label { Text = "Drag the other computer to match your desk.\nChoose the Windows monitor you want to cross from.", AutoSize = true, ForeColor = Color.FromArgb(104, 122, 147) }, 0, 1);
        main.Controls.Add(_canvas, 0, 2);
        var pick = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 7, 0, 0) };
        pick.Controls.Add(new Label { Text = "Cross from", AutoSize = true, Margin = new Padding(0, 4, 10, 0) });
        pick.Controls.Add(_monitor);
        main.Controls.Add(pick, 0, 3);
        _side.Items.AddRange(new object[] { "Right", "Left", "Above", "Below" });
        _side.SelectedIndex = 0;
        var direction = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
        direction.Controls.Add(new Label { Text = "Other computer", AutoSize = true, Margin = new Padding(0, 9, 12, 0) });
        direction.Controls.Add(_side);
        direction.Controls.Add(Button("Center", () => _alignment.Value = 0));
        main.Controls.Add(direction, 0, 4);
        var align = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        align.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        align.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        align.Controls.Add(_alignmentLabel, 0, 0); align.Controls.Add(_alignment, 0, 1);
        main.Controls.Add(align, 0, 5);
        var saveRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        StyleButton(_saveLayout, true); saveRow.Controls.Add(_saveLayout);
        saveRow.Controls.Add(Button("Reset position", () => { _side.SelectedIndex = 0; _alignment.Value = 0; }));
        main.Controls.Add(saveRow, 0, 6); main.Controls.Add(_layoutStatus, 0, 7);
        var sidebar = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(18), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        columns.Controls.Add(sidebar, 1, 0);
        sidebar.Controls.Add(Title("Your computers"));
        sidebar.Controls.Add(new Label { Text = "Choose a paired computer", AutoSize = true, Margin = new Padding(3, 14, 3, 7) });
        _peers.Width = 252; _peers.Dock = DockStyle.None; sidebar.Controls.Add(_peers);
        _start.Width = 252; _start.AutoSize = false; StyleButton(_start, true); _start.Margin = new Padding(3, 18, 3, 6); sidebar.Controls.Add(_start);
        var pause = Button("Pause sharing", () => Pause("paused by user")); pause.Width = 252; pause.AutoSize = false; sidebar.Controls.Add(pause);
        _status.MaximumSize = new Size(246, 0); _status.Margin = new Padding(3, 12, 3, 15); sidebar.Controls.Add(_status);
        sidebar.Controls.Add(Title("Pair a computer"));
        sidebar.Controls.Add(new Label { Text = "Exchange cards through a trusted channel. Approve each computer's card on the other.", MaximumSize = new Size(246, 0), AutoSize = true, ForeColor = Color.FromArgb(104, 122, 147), Margin = new Padding(3, 10, 3, 10) });
        foreach (var b in new[] { Button("Export my pairing card…", ExportCard), Button("Import a pairing card…", ImportCard), Button("Connection details…", ShowConnectionDetails), Button("Unpair selected computer", Unpair) })
        { b.Width = 252; b.AutoSize = false; sidebar.Controls.Add(b); }
        var login = new CheckBox { Text = "Open when I sign in", AutoSize = true, Margin = new Padding(3, 20, 3, 8) };
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            login.Checked = key?.GetValue("BatCaveSecure") is not null;
        login.CheckedChanged += (_, _) => Guard(() =>
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (login.Checked) key.SetValue("BatCaveSecure", "\"" + Environment.ProcessPath + "\" --desktop");
            else key.DeleteValue("BatCaveSecure", false);
        });
        sidebar.Controls.Add(login);
        var clipboard = new CheckBox { Text = "Share text clipboard", AutoSize = true };
        sidebar.Controls.Add(clipboard);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        footer.Controls.Add(new Label { Text = "Escape, F12, or Pause returns control to this computer.", AutoSize = true, ForeColor = Color.FromArgb(101, 119, 145), Margin = new Padding(0, 14, 0, 0) }, 0, 0);
        footer.Controls.Add(Button("Quit BatCave", Quit), 1, 0); shell.Controls.Add(footer, 0, 2);
        var settings = ReadSettings();
        _selectedPeerId = settings?.SelectedPeerId;
        _legacyLayoutPeer = settings?.SelectedPeerId;
        _legacySide = settings?.PeerOnRight == false ? PeerSide.Left : PeerSide.Right;
        _side.SelectedIndex = settings?.PeerOnRight == false ? 1 : 0;
        clipboard.Checked = settings?.ShareClipboard == true;
        _agent.ClipboardSharingEnabled = clipboard.Checked;
        _monitors = DiscoverMonitors(); RefreshPeers(); LoadPeerLayout();
        _status.Text = _selectedPeerId is null ? "Pair another computer to begin." : "Waiting for the selected computer…";
        clipboard.CheckedChanged += (_, _) => Guard(() => { _agent.ClipboardSharingEnabled = clipboard.Checked; SaveSettings(); });
        _peers.SelectedIndexChanged += (_, _) => Guard(() =>
        {
            if (_refreshingPeers) return;
            Pause("active computer changed");
            _selectedPeerId = (_peers.SelectedItem as PeerItem)?.Card.DeviceId; _hello = null;
            _lan.DisconnectUnapprovedPeer(); LoadPeerLayout(); SaveSettings();
        });
        _monitor.SelectedIndexChanged += (_, _) => LayoutEdited();
        _side.SelectedIndexChanged += (_, _) => LayoutEdited();
        _alignment.ValueChanged += (_, _) => LayoutEdited();
        _canvas.PositionChanged += (side, alignment) =>
        {
            _loadingLayout = true; _side.SelectedIndex = (int)side; _alignment.Value = alignment; _loadingLayout = false; LayoutEdited();
        };
        _saveLayout.Click += (_, _) => Guard(SaveLayout);
        _start.Click += (_, _) => Guard(() =>
        {
            if (!_lan.HasPeer || _hello is null || _lan.CurrentPeerDeviceId != _selectedPeerId) return;
            var agents = System.Diagnostics.Process.GetProcessesByName("BatCave.WindowsAgent");
            try
            {
                if (agents.Any(process => process.Id != Environment.ProcessId))
                {
                    MessageBox.Show(this, "Another BatCave Windows agent is running. Quit the development or previous app before starting this preview."); return;
                }
            }
            finally { foreach (var process in agents) process.Dispose(); }
            SaveLayout(); _sharing = true; _agent.PostStartPersistentHandoff();
        });
        _lan.PeerHello += hello => PostUi(() =>
        {
            if (hello.DeviceId == _selectedPeerId && _lan.HasPeer && _lan.CurrentPeerDeviceId == hello.DeviceId)
            { Pause("screen geometry received"); _hello = hello; RefreshLayout(); }
        });
        _lan.PeerDisconnected += _ =>
        {
            _agent.PostStopBridge("peer disconnected");
            PostUi(() => { if (!_lan.HasPeer) { _hello = null; _sharing = false; RefreshLayout(); } });
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open BatCave", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        menu.Items.Add("Pause", null, (_, _) => Pause("paused from tray")); menu.Items.Add("Quit", null, (_, _) => Quit());
        _tray.ContextMenuStrip = menu; _tray.DoubleClick += (_, _) => { Show(); Activate(); };
        _timer.Tick += (_, _) =>
        {
            _start.Enabled = _lan.HasPeer && _hello is not null && _layoutValid;
            _sharing = _agent.IsBridgeActive;
            _status.Text = _lan.HasPeer ? (_sharing ? "● Sharing securely" : "● Connected securely\nReady when you are.")
                : _selectedPeerId is null ? "Pair another computer to begin." : "Waiting for the selected computer…";
            _status.ForeColor = _lan.HasPeer ? Color.FromArgb(29, 137, 112) : Color.FromArgb(104, 122, 147);
        };
        Shown += (_, _) => Guard(() =>
        {
            if (!inspectionOnly)
            {
                var area = Screen.FromControl(this).WorkingArea;
                Size = new Size(Math.Min(Width, area.Width - 20), Math.Min(Height, area.Height - 20));
                _lan.StartListeningAsync(LanBridgePeer.SecurePort).GetAwaiter().GetResult();
            }
            _timer.Start();
        });
        SystemEvents.PowerModeChanged += OnPowerModeChanged; SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        FormClosing += (_, e) => { if (!_quitting) { e.Cancel = true; Hide(); } };
        FormClosed += (_, _) =>
        {
            _timer.Stop(); SystemEvents.PowerModeChanged -= OnPowerModeChanged; SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _agent.Close(); _lan.FlushAsync().GetAwaiter().GetResult(); _lan.Dispose(); _agent.Dispose();
            _tray.Visible = false; _tray.Dispose(); _timer.Dispose();
        };
    }

    private static Label Title(string text) => new() { Text = text, Font = new Font("Segoe UI", 13, FontStyle.Bold), AutoSize = true, Margin = new Padding(3, 3, 3, 8) };
    private static void StyleButton(Button button, bool primary = false)
    {
        button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderColor = Color.FromArgb(214, 223, 236);
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.BackColor = primary ? Color.FromArgb(41, 111, 224) : Color.White;
        button.ForeColor = primary ? Color.White : Color.FromArgb(53, 74, 103);
        button.Padding = new Padding(9, 4, 9, 4); button.MinimumSize = new Size(0, 36); button.Height = 39;
        button.Cursor = Cursors.Hand; button.Margin = new Padding(3, 3, 8, 6);
        if (primary)
        {
            void Update() => button.BackColor = button.Enabled ? Color.FromArgb(41, 111, 224) : Color.FromArgb(232, 238, 247);
            button.EnabledChanged += (_, _) => Update(); Update();
        }
    }

    private static Button Button(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true };
        StyleButton(button);
        button.Click += (_, _) => action();
        return button;
    }

    private void ExportCard() => Guard(() =>
    {
        using var dialog = new SaveFileDialog { Filter = "BatCave public pairing card|*.batcave-peer.json", FileName = Environment.MachineName + ".batcave-peer.json" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            File.WriteAllText(dialog.FileName, DeviceIdentityStore.SerializePublicCard(_identity.ExportPublicCard(Environment.MachineName)));
    });

    private void ShowConnectionDetails() => Guard(() =>
    {
        var addresses = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
            .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a))
            .Select(a => a.ToString()).Distinct().ToArray();
        var card = _identity.ExportPublicCard("This computer");
        MessageBox.Show(this, "Enter a Windows address on the other computer:\n\n" + (addresses.Length == 0 ? "No network address available." : string.Join("\n", addresses))
            + "\n\nEncrypted port: " + LanBridgePeer.SecurePort + "\n\nPairing-card SHA-256:\n" + card.CertificateSha256
            + "\n\nCompare this fingerprint when approving this computer's card.", "Connection details", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        LoadPeerLayout();
        SaveSettings();
    });

    private void Unpair() => Guard(() =>
    {
        if (_peers.SelectedItem is not PeerItem item) { return; }
        Pause("device unpaired");
        _approvals.Remove(item.Card.DeviceId);
        _layoutStore.Remove(item.Card.DeviceId);
        _lan.DisconnectUnapprovedPeer();
        _selectedPeerId = null;
        _hello = null;
        RefreshPeers();
        LoadPeerLayout();
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

    private static IReadOnlyList<MonitorDescriptor> DiscoverMonitors() => Screen.AllScreens
        .OrderBy(screen => screen.DeviceName, StringComparer.Ordinal).Select((screen, i) =>
        {
            var b = screen.Bounds;
            var id = "monitor-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(screen.DeviceName)))[..16].ToLowerInvariant();
            return new MonitorDescriptor(id, "Monitor " + (i + 1) + (screen.Primary ? " · Primary" : ""), new(b.Left, b.Top, b.Width, b.Height));
        }).ToArray();
    private MonitorLayoutSettings DraftLayout() => new((_monitor.SelectedItem as MonitorItem)?.Monitor.Id ?? "missing", (PeerSide)_side.SelectedIndex, _alignment.Value);
    private void LoadPeerLayout()
    {
        _loadingLayout = true;
        var saved = _selectedPeerId is { } peer ? _layoutStore.Get(peer) : null;
        _monitor.Items.Clear();
        foreach (var monitor in _monitors) _monitor.Items.Add(new MonitorItem(monitor));
        var preferred = saved?.MonitorId ?? _monitors.OrderByDescending(m => m.Bounds.Right).FirstOrDefault()?.Id;
        foreach (MonitorItem item in _monitor.Items) if (item.Monitor.Id == preferred) _monitor.SelectedItem = item;
        _side.SelectedIndex = (int)(saved?.Side ?? (_selectedPeerId is not null && _selectedPeerId == _legacyLayoutPeer ? _legacySide : PeerSide.Right)); _alignment.Value = saved?.Alignment ?? 0;
        _loadingLayout = false; _layoutDirty = false; RefreshLayout();
    }
    private void LayoutEdited()
    {
        if (_loadingLayout) return;
        Pause("layout edited"); _layoutDirty = true; RefreshLayout();
    }
    private void RefreshLayout()
    {
        _canvas.PeerConnected = _hello is not null;
        _canvas.PeerLabel = (_peers.SelectedItem as PeerItem)?.Card.DisplayName ?? "Other computer";
        _alignmentLabel.Text = _alignment.Value == 0 ? "Alignment · centered" : "Alignment · " + (_alignment.Value < 0 ? "up / left " : "down / right ") + Math.Abs(_alignment.Value) + "%";
        try
        {
            // Offline placeholder is for arrangement only; never written to the
            // routing profile or used to start input sharing.
            var bounds = _hello?.VirtualDesktopBounds ?? ScreenBounds.From(new(0, 0, 1920, 1080));
            _canvas.Scene = MonitorLayout.Build(_monitors, bounds, DraftLayout(), allowBlockedPreview: true);
            _layoutValid = _canvas.Scene.Seams.Count > 0;
            _layoutStatus.ForeColor = _layoutValid ? Color.FromArgb(102, 121, 147) : Color.FromArgb(173, 76, 55);
            _layoutStatus.Text = !_layoutValid ? "That edge meets another Windows monitor. Choose an outside edge."
                : _selectedPeerId is null ? "Pair a computer to save an arrangement."
                : _layoutDirty ? "Unsaved layout. Sharing is paused until you start it again."
                : _hello is null ? "Arrangement saved per computer. Connect to preview its screen size." : "Layout ready. Cross along the green edge.";
        }
        catch (InvalidDataException ex)
        {
            _canvas.Scene = null; _layoutValid = false; _layoutStatus.ForeColor = Color.FromArgb(173, 76, 55); _layoutStatus.Text = ex.Message;
        }
        _saveLayout.Enabled = _selectedPeerId is not null && _layoutValid;
        _start.Enabled = _lan.HasPeer && _hello is not null && _layoutValid;
        _canvas.Invalidate();
    }
    private void SaveLayout()
    {
        if (_selectedPeerId is not { } peer || !_layoutValid) throw new InvalidDataException("Choose a paired computer and a valid layout.");
        Pause("layout saved");
        if (_hello is not null) WriteTopology();
        _layoutStore.Save(peer, DraftLayout()); _layoutDirty = false; RefreshLayout();
    }
    private void WriteTopology() => SecureTopology.Write(_topologyPath, _identity.DeviceId, _monitors, _hello!, DraftLayout());
    private Settings? ReadSettings()
    {
        if (!File.Exists(_settingsPath)) return null;
        try
        {
            if (new FileInfo(_settingsPath).Length > 16384) return null;
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(_settingsPath));
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }
    private void SaveSettings()
    {
        File.WriteAllText(_settingsPath + ".new", JsonSerializer.Serialize(new Settings(_selectedPeerId, _side.SelectedIndex != (int)PeerSide.Left, _agent.ClipboardSharingEnabled)));
        File.Move(_settingsPath + ".new", _settingsPath, overwrite: true);
    }
    private void PostUi(Action action)
    {
        try { if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)(() => { if (!IsDisposed) action(); })); }
        catch (InvalidOperationException) when (IsDisposed || !IsHandleCreated) { }
    }
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => PostUi(() =>
    {
        Pause("monitors changed"); _lan.Disconnect(); _hello = null; _monitors = DiscoverMonitors(); LoadPeerLayout();
        var b = SystemInformation.VirtualScreen;
        _lan.UpdateLocalBounds(new(b.Left, b.Top, b.Width, b.Height));
    });
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
