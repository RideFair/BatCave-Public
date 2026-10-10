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
    private bool _listenerReady;
    private string? _notice;
    private DateTimeOffset _noticeUntil;
    private int _setupStep;
    private readonly Panel _pages = new() { Dock = DockStyle.Fill };
    private readonly List<Control> _setupPages = [];
    private readonly List<Button> _stepButtons = [];
    private readonly Button _back = new() { Text = "Back", AutoSize = true };
    private readonly Button _saveSetup = new() { Text = "Save setup", AutoSize = true };
    private readonly Label _permissionStatus = new() { AutoSize = true };
    private readonly Label _pairStatus = new() { AutoSize = true };
    private readonly Label _linkStatus = new() { AutoSize = true };
    private readonly Label _stepTitle = new() { AutoSize = true };
    private bool _quitting;
    private bool _sharing;
    private volatile string? _selectedPeerId;
    private bool _refreshingPeers;
    private readonly string _settingsPath;
    private readonly MonitorLayoutStore _layoutStore;
    private readonly ComboBox _monitor = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 270 };
    private readonly TrackBar _alignment = new() { Minimum = -100, Maximum = 100, TickStyle = TickStyle.None, Dock = DockStyle.Fill, AutoSize = false, AccessibleName = "Screen alignment" };
    private readonly MonitorLayoutCanvas _canvas = new() { Dock = DockStyle.Fill };
    private readonly Label _layoutStatus = new() { AutoSize = true, MaximumSize = new Size(590, 0) };
    private readonly Label _alignmentLabel = new() { AutoSize = true };
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
        Text = "BatCave";
        _listenerReady = inspectionOnly;
        var appIcon = Environment.ProcessPath is { } executable ? Icon.ExtractAssociatedIcon(executable) : null;
        if (appIcon is not null) { Icon = appIcon; _tray.Icon = appIcon; }
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(244, 247, 251);
        ForeColor = Color.FromArgb(24, 40, 61);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1000, 760);
        MinimumSize = new Size(880, 640);
        StartPosition = FormStartPosition.CenterScreen;
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        Controls.Add(shell);
        var header = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(24, 16, 24, 12) };
        header.Controls.Add(new Label { Text = "BatCave", Font = new Font(Font.FontFamily, 25, FontStyle.Bold), Location = new Point(24, 12), AutoSize = true });
        header.Controls.Add(new Label { Text = "Your computers, together", ForeColor = Color.FromArgb(86, 103, 127), Location = new Point(26, 56), AutoSize = true });
        var pause = Button("Pause sharing", () => { Pause("paused by user"); UpdateSetup(); });
        header.Controls.Add(pause);
        header.Resize += (_, _) => pause.Location = new Point(header.ClientSize.Width - pause.PreferredSize.Width - 24, 25);
        shell.Controls.Add(header, 0, 0);
        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 215));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.Controls.Add(columns, 0, 1);
        var navigation = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 20, 8, 16), FlowDirection = FlowDirection.TopDown, WrapContents = false };
        columns.Controls.Add(navigation, 0, 0);
        for (var i = 0; i < DesktopSetupFlow.Titles.Length; i++)
        {
            var step = i;
            var button = Button((i + 1) + "  " + DesktopSetupFlow.Titles[i], () => ShowStep(step));
            button.TextAlign = ContentAlignment.MiddleLeft; button.AutoSize = false;
            button.Size = new Size(191, 48); button.Margin = new Padding(0, 0, 0, 8);
            button.FlatAppearance.BorderSize = 0;
            navigation.Controls.Add(button); _stepButtons.Add(button);
        }
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 20, 24, 18), ColumnCount = 1, RowCount = 5 };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 37));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        columns.Controls.Add(content, 1, 0);
        _stepTitle.Font = new Font(Font.FontFamily, 9, FontStyle.Bold);
        _stepTitle.ForeColor = Color.FromArgb(86, 103, 127);
        content.Controls.Add(_stepTitle, 0, 0); content.Controls.Add(_pages, 0, 1);

        var permissions = Page("One keyboard. Both computers.", "Use this computer’s keyboard and mouse across your linked devices.");
        permissions.Controls.Add(_permissionStatus);
        permissions.Controls.Add(Body("Windows needs no extra input permission. If Windows asks about network access, allow BatCave on your private network."));
        permissions.Controls.Add(Body("Sharing starts only when you choose Start sharing. Pause sharing always returns control to this computer."));
        AddPage(permissions);

        var pairing = Page("Pair your computers", "Exchange public device cards and approve the fingerprint shown on the other computer.");
        var cardActions = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Width = 560 };
        cardActions.Controls.Add(Button("Copy Windows card", CopyCard));
        cardActions.Controls.Add(Button("Paste device card", PasteCard));
        cardActions.Controls.Add(Button("Save Windows card…", ExportCard));
        pairing.Controls.Add(cardActions);
        pairing.Controls.Add(Body("Paired computer"));
        _peers.Width = 450; _peers.Dock = DockStyle.None; pairing.Controls.Add(_peers);
        pairing.Controls.Add(_pairStatus);
        pairing.Controls.Add(Body("Approve this Windows card on the other device too. Device cards contain public information; your private key stays here."));
        var moreCards = new FlowLayoutPanel { AutoSize = true, Width = 560 };
        moreCards.Controls.Add(Button("Import another card…", ImportCard));
        moreCards.Controls.Add(Button("Remove paired device", Unpair)); pairing.Controls.Add(moreCards);
        AddPage(pairing);

        var arrangementScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var arrangement = new TableLayoutPanel { Dock = DockStyle.Top, Height = 440, BackColor = Color.White, Padding = new Padding(20, 16, 20, 12), ColumnCount = 1, RowCount = 8 };
        var positionExpanded = false;
        arrangementScroll.Controls.Add(arrangement);
        arrangementScroll.Resize += (_, _) => arrangement.Height = Math.Max(positionExpanded ? 545 : 440, arrangementScroll.ClientSize.Height);
        arrangement.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        arrangement.RowStyles.Add(new RowStyle(SizeType.Absolute, 41));
        arrangement.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        arrangement.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        arrangement.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        arrangement.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        arrangement.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        arrangement.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        var arrangeTitle = Heading("Arrange screens"); arrangeTitle.Margin = Padding.Empty;
        arrangement.Controls.Add(arrangeTitle, 0, 0);
        arrangement.Controls.Add(Body("Drag the other computer to match your desk."), 0, 1);
        arrangement.Controls.Add(_canvas, 0, 2);
        var pick = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        pick.Controls.Add(new Label { Text = "Cross from", AutoSize = true, Margin = new Padding(0, 9, 12, 0) });
        _monitor.Width = 285; pick.Controls.Add(_monitor); arrangement.Controls.Add(pick, 0, 3);
        _side.Items.AddRange(new object[] { "Right", "Left", "Above", "Below" }); _side.SelectedIndex = 0;
        var direction = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        direction.Controls.Add(new Label { Text = "Other computer", AutoSize = true, Margin = new Padding(0, 9, 12, 0) });
        direction.Controls.Add(_side); direction.Controls.Add(Button("Center", () => _alignment.Value = 0));
        direction.Visible = false; arrangement.Controls.Add(direction, 0, 5);
        var align = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        align.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); align.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        align.Controls.Add(_alignmentLabel, 0, 0); align.Controls.Add(_alignment, 0, 1); align.Visible = false; arrangement.Controls.Add(align, 0, 6);
        Button? positionControls = null;
        positionControls = Button("Position controls", () =>
        {
            positionExpanded = !positionExpanded;
            direction.Visible = align.Visible = positionExpanded;
            arrangement.RowStyles[5].Height = positionExpanded ? 48 : 0;
            arrangement.RowStyles[6].Height = positionExpanded ? 57 : 0;
            arrangement.Height = Math.Max(positionExpanded ? 545 : 440, arrangementScroll.ClientSize.Height);
            positionControls!.Text = positionExpanded ? "Hide position controls" : "Position controls";
        });
        positionControls.FlatAppearance.BorderSize = 0;
        arrangement.Controls.Add(positionControls, 0, 4);
        _layoutStatus.MaximumSize = new Size(600, 0); arrangement.Controls.Add(_layoutStatus, 0, 7);
        AddPage(arrangementScroll);

        var linking = Page("Link devices", "Enable input on your other device, then start sharing here.");
        linking.Controls.Add(_linkStatus);
        linking.Controls.Add(Body("Move your mouse through the shared screen edge to use your other computer. Escape, F12, or Pause sharing restores local control."));
        var login = new CheckBox { Text = "Open BatCave at login", AutoSize = true, Margin = new Padding(0, 16, 0, 8) };
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            login.Checked = key?.GetValue("BatCaveSecure") is not null;
        login.CheckedChanged += (_, _) => Guard(() =>
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (login.Checked) key.SetValue("BatCaveSecure", "\"" + Environment.ProcessPath + "\" --desktop");
            else key.DeleteValue("BatCaveSecure", false);
        });
        linking.Controls.Add(login);
        var clipboard = new CheckBox { Text = "Share text clipboard", AutoSize = true };
        linking.Controls.Add(clipboard); linking.Controls.Add(Body("A lost connection pauses sharing. Reconnecting never starts input by itself."));
        AddPage(linking);
        var details = Button("Connection details…", ShowConnectionDetails);
        details.FlatAppearance.BorderSize = 0; details.BackColor = BackColor;
        content.Controls.Add(details, 0, 2);
        _status.Dock = DockStyle.Fill; _status.AutoSize = false; _status.ForeColor = Color.FromArgb(66, 91, 121);
        content.Controls.Add(_status, 0, 3);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1 };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        StyleButton(_back); _back.Click += (_, _) => ShowStep(_setupStep - 1); actions.Controls.Add(_back, 0, 0);
        StyleButton(_saveSetup); _saveSetup.Click += (_, _) => Guard(() => { SaveLayout(); SaveSettings(); UpdateSetup(); });
        actions.Controls.Add(_saveSetup, 2, 0); StyleButton(_start, true); actions.Controls.Add(_start, 3, 0);
        content.Controls.Add(actions, 0, 4);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var quit = Button("Quit BatCave", Quit); quit.FlatAppearance.BorderSize = 0; quit.BackColor = BackColor; footer.Controls.Add(quit);
        shell.Controls.Add(footer, 0, 2);
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
        _start.Click += (_, _) => Guard(ActivatePrimary);
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
        _timer.Tick += (_, _) => UpdateSetup();
        ShowStep(_selectedPeerId is null ? 0 : 3);
        Shown += (_, _) => Guard(() =>
        {
            if (!inspectionOnly)
            {
                var area = Screen.FromControl(this).WorkingArea;
                Size = new Size(Math.Min(Width, area.Width - 20), Math.Min(Height, area.Height - 20));
                _lan.StartListeningAsync(LanBridgePeer.SecurePort).GetAwaiter().GetResult();
                _listenerReady = true;
            }
            UpdateSetup(); _timer.Start();
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

    private static Label Heading(string text) => new() { Text = text, Font = new Font("Segoe UI", 18, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
    private static Label Body(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(565, 0), ForeColor = Color.FromArgb(86, 103, 127), Margin = new Padding(0, 0, 0, 14) };
    private static FlowLayoutPanel Page(string title, string explanation)
    {
        var page = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(22), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        page.Controls.Add(Heading(title)); page.Controls.Add(Body(explanation));
        page.Resize += (_, _) =>
        {
            var width = Math.Max(100, page.ClientSize.Width - page.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 6);
            foreach (Control child in page.Controls)
            {
                if (child is Label label) label.MaximumSize = new Size(width, 0);
                else if (child is FlowLayoutPanel row) row.Width = width;
            }
        };
        return page;
    }
    private void AddPage(Control page) { _setupPages.Add(page); _pages.Controls.Add(page); }
    internal void ShowStep(int step)
    {
        if (step < 0 || step >= DesktopSetupFlow.Titles.Length) return;
        _setupStep = step; _notice = null;
        for (var i = 0; i < _setupPages.Count; i++) _setupPages[i].Visible = i == step;
        _setupPages[step].BringToFront(); UpdateSetup();
    }
    private DesktopSetupState SetupState() => new(_listenerReady, _peers.SelectedItem is PeerItem selected && selected.Card.DeviceId == _selectedPeerId,
        _lan.HasPeer && _hello?.DeviceId == _selectedPeerId && _lan.CurrentPeerDeviceId == _selectedPeerId, _layoutValid, _agent.IsBridgeActive);
    private void UpdateSetup()
    {
        var state = SetupState();
        _sharing = state.Sharing;
        var action = DesktopSetupFlow.Action(_setupStep, state);
        _start.Text = action.Label; _start.Enabled = action.Enabled;
        _back.Visible = _setupStep > 0; _saveSetup.Visible = _setupStep == 3;
        _saveSetup.Enabled = state.PeerSelected && state.LayoutValid;
        _stepTitle.Text = "STEP " + (_setupStep + 1) + " OF 4";
        for (var i = 0; i < _stepButtons.Count; i++)
        {
            _stepButtons[i].BackColor = i == _setupStep ? Color.FromArgb(231, 238, 251) : BackColor;
            _stepButtons[i].ForeColor = i == _setupStep ? Color.FromArgb(36, 84, 150) : Color.FromArgb(102, 119, 142);
        }
        _permissionStatus.Text = state.ListenerReady ? "Windows is ready to link your devices." : "Waiting for network access. Reopen BatCave if the connection service could not start.";
        _pairStatus.Text = (_peers.SelectedItem as PeerItem) is { } peer ? peer.Card.DisplayName + " is approved." : "Add the computer you want to link.";
        _linkStatus.Text = state.Sharing ? "Your keyboard and mouse are shared." : state.Connected ? "Your device is connected. Enable its input access, then choose Start sharing." : "Open BatCave on your other device and connect to this Windows computer.";
        _status.Text = state.Connected ? (state.Sharing ? "Sharing securely" : "Connected securely. Sharing is paused.")
            : state.PeerSelected ? "Waiting for the other device to connect." : "Setup is ready. Continue to link your computers.";
        _status.ForeColor = state.Connected ? Color.FromArgb(29, 137, 112) : Color.FromArgb(66, 91, 121);
        if (_notice is not null && DateTimeOffset.UtcNow < _noticeUntil) _status.Text = _notice;
    }
    private void ActivatePrimary()
    {
        var action = DesktopSetupFlow.Action(_setupStep, SetupState());
        if (!action.Enabled) return;
        _notice = null;
        switch (action.Operation)
        {
            case SetupOperation.Next:
                if (_setupStep == 2) SaveLayout();
                ShowStep(_setupStep + 1); break;
            case SetupOperation.Pair:
                if (_setupStep != 1) ShowStep(1); else ImportCard();
                break;
            case SetupOperation.Pause: Pause("paused by user"); break;
            case SetupOperation.Start:
                var agents = System.Diagnostics.Process.GetProcessesByName("BatCave.WindowsAgent");
                try
                {
                    if (agents.Any(process => process.Id != Environment.ProcessId))
                    {
                        MessageBox.Show(this, "Another BatCave Windows app is running. Quit it before starting sharing in this app."); return;
                    }
                }
                finally { foreach (var process in agents) process.Dispose(); }
                SaveLayout(); _agent.PostStartPersistentHandoff(); break;
        }
        UpdateSetup();
    }
    private void CopyCard() => Guard(() =>
    {
        Clipboard.SetText(DeviceIdentityStore.SerializePublicCard(_identity.ExportPublicCard(Environment.MachineName)));
        Notice("Windows device card copied. Paste it into BatCave on your other device.");
    });
    private void Notice(string text) { _notice = text; _noticeUntil = DateTimeOffset.UtcNow.AddSeconds(12); _status.Text = text; }
    private void PasteCard() => Guard(() =>
    {
        if (!Clipboard.ContainsText()) throw new InvalidDataException("Copy a BatCave device card on your other computer first.");
        ApproveCard(Clipboard.GetText()); UpdateSetup();
    });

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
        ApproveCard(json); UpdateSetup();
    });

    private void ApproveCard(string json)
    {
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
    }

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
                : _hello is null ? "Connect the other device to show its screen size." : "Layout ready. Cross along the green edge.";
        }
        catch (InvalidDataException ex)
        {
            _canvas.Scene = null; _layoutValid = false; _layoutStatus.ForeColor = Color.FromArgb(173, 76, 55); _layoutStatus.Text = ex.Message;
        }
        if (_stepButtons.Count != 0) UpdateSetup();
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
