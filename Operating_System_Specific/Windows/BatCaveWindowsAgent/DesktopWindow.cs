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
    private readonly DesktopButton _start = new() { Text = "Start sharing", Enabled = false, Primary = true };
    private readonly NotifyIcon _tray = new() { Visible = true, Text = "BatCave", Icon = SystemIcons.Application };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private DeviceHelloPayload? _hello;
    private bool _listenerReady;
    private string? _notice;
    private DateTimeOffset _noticeUntil;
    private int _setupStep;
    private readonly Panel _pages = new() { Dock = DockStyle.Fill };
    private readonly List<Control> _setupPages = [];
    private readonly List<SetupStepButton> _stepButtons = [];
    private readonly DesktopButton _back = new() { Text = "Back" };
    private readonly DesktopButton _saveSetup = new() { Text = "Save setup" };
    private readonly Label _permissionStatus = new() { AutoSize = true };
    private readonly Label _linkStatus = new() { AutoSize = true };
    private readonly Label _stepTitle = new() { AutoSize = true };
    private readonly Label _pageTitle = new() { AutoSize = true };
    private readonly Label _sidebarStatus = new() { AutoSize = true };
    private readonly DesktopButton _pause = new() { Text = "Pause sharing", Quiet = true, BackColor = DesktopTheme.Sidebar };
    private readonly Label _pairTitle = Heading("Add your first computer");
    private readonly Label _sendInstructions = Body("");
    private readonly Label _receiveInstructions = Body("");
    private readonly Label _connectInstructions = Body("");
    private readonly Label _sendTitle = SectionTitle("1", "Send this PC’s file");
    private readonly Label _receiveTitle = SectionTitle("2", "Bring the other computer’s file here");
    private readonly Label _pairExplanation = Body("Exchange one connection file each to approve your computers. Keep BatCave open on both.");
    private readonly DesktopButton _skipExport = new() { Text = "I already have the other computer’s file", Quiet = true };
    private readonly DesktopButton _exportAgain = new() { Text = "Save Windows file again…", Quiet = true };
    private readonly FlowLayoutPanel _pairedPanel = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    private readonly FlowLayoutPanel _fileGuide = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    private readonly FlowLayoutPanel _connectPanel = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    private readonly FlowLayoutPanel _platformRow = new() { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 12) };
    private readonly FlowLayoutPanel _managePeers = new() { AutoSize = true, WrapContents = true, Margin = new Padding(0, 10, 0, 8) };
    private readonly ComboBox _platform = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    private readonly ComboBox _role = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly FlowLayoutPanel _rolePanel = new() { AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 18) };
    private readonly Label _roleHelp = Body("");
    private readonly FlowLayoutPanel _outgoingPanel = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Visible = false };
    private readonly FlowLayoutPanel _incomingPanel = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    private readonly DesktopButton _connectionDirection = new() { Text = "Connect from this PC instead", Quiet = true };
    private readonly TextBox _host = new() { Width = 280, PlaceholderText = "Other computer’s address", AccessibleName = "Other computer’s address" };
    private readonly DesktopButton _connect = new() { Text = "Connect securely" };
    private readonly Dictionary<string, ConnectionSettings> _connectionSettings = new(StringComparer.Ordinal);
    private CancellationTokenSource? _connectCancellation;
    private bool _loadingConnection, _connectWanted, _connecting, _reconnectSuspended;
    private DateTimeOffset _nextConnectAttempt;
    private readonly ComboBox _addresses = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 295 };
    private readonly DesktopButton _copyAddress = new() { Text = "Copy address" };
    private readonly DesktopButton _removePeer = new() { Text = "Remove computer", Quiet = true };
    private readonly bool _inspectionOnly;
    private DateTimeOffset _addressRefresh;
    private bool _addingComputer;
    private bool _windowsFileShared;
    private bool _sharing;
    private volatile string? _selectedPeerId;
    private bool _refreshingPeers;
    private readonly string _settingsPath;
    private readonly MonitorLayoutStore _layoutStore;
    private readonly ComboBox _monitor = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 270 };
    private readonly TrackBar _alignment = new() { Minimum = -100, Maximum = 100, TickStyle = TickStyle.None, Dock = DockStyle.Fill, AutoSize = false, AccessibleName = "Screen alignment" };
    private readonly MonitorLayoutCanvas _canvas = new() { Dock = DockStyle.Fill };
    private readonly SetupPage _receiverArrangement = new();
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

    private sealed record ConnectionSettings(string Host = "", bool ReceiveInput = false);
    private sealed record Settings(string? SelectedPeerId, bool PeerOnRight, bool ShareClipboard = false,
        Dictionary<string, ConnectionSettings>? Connections = null);
    private bool ReceiveInput => _role.SelectedIndex == 1;
    private sealed record PeerItem(DeviceIdentityStore.PairingCard Card)
    {
        public override string ToString() => Card.DisplayName;
    }

    protected override bool ShowWithoutActivation => _inspectionOnly;

    public DesktopWindow(DeviceIdentityStore identity, ApprovedPeerStore approvals, string directory, bool inspectionOnly = false)
    {
        _identity = identity;
        _approvals = approvals;
        _inspectionOnly = inspectionOnly;
        _topologyPath = Path.Combine(directory, "topology.secure.json");
        _settingsPath = Path.Combine(directory, "desktop-settings.json");
        _layoutStore = new MonitorLayoutStore(Path.Combine(directory, "monitor-layouts.json"));
        _lan = new LanBridgePeer(identity, () => _approvals.Peers.Where(peer => peer.DeviceId == _selectedPeerId).ToArray());
        _agent = new LocalBridgeAgent(_lan, _topologyPath, inspectionOnly: inspectionOnly);
        if (!inspectionOnly) _ = _agent.Handle;
        Text = "BatCave";
        _listenerReady = inspectionOnly;
        var appIcon = Environment.ProcessPath is { } executable ? Icon.ExtractAssociatedIcon(executable) : null;
        if (appIcon is not null) { Icon = appIcon; _tray.Icon = appIcon; }
        Font = new Font("Segoe UI", 10);
        BackColor = DesktopTheme.Background;
        ForeColor = DesktopTheme.Ink;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1000, 760);
        MinimumSize = new Size(880, 640);
        StartPosition = FormStartPosition.CenterScreen;
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 226));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(shell);
        var sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = DesktopTheme.Sidebar, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 144));
        shell.Controls.Add(sidebar, 0, 0);
        var brand = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 27, 12, 0), FlowDirection = FlowDirection.TopDown, WrapContents = false };
        brand.Controls.Add(new Label { Text = "BatCave", Font = new Font(Font.FontFamily, 23, FontStyle.Bold), ForeColor = Color.White, AutoSize = true, Margin = Padding.Empty });
        brand.Controls.Add(new Label { Text = "Your computers, together.", Font = new Font(Font.FontFamily, 9), ForeColor = Color.FromArgb(151, 162, 187), AutoSize = true, Margin = new Padding(2, 8, 0, 0) });
        sidebar.Controls.Add(brand, 0, 0);
        var navigation = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 8, 10, 16), FlowDirection = FlowDirection.TopDown, WrapContents = false };
        sidebar.Controls.Add(navigation, 0, 1);
        string[] descriptions = ["Get ready", "Add your other computer", "Match your desk", "Start sharing"];
        for (var i = 0; i < DesktopSetupFlow.Titles.Length; i++)
        {
            var step = i;
            var button = new SetupStepButton(i, DesktopSetupFlow.Titles[i], descriptions[i]) { Text = DesktopSetupFlow.Titles[i], AutoSize = false, Size = new Size(202, 67), Margin = new Padding(0, 0, 0, 8) };
            button.Click += (_, _) => ShowStep(step);
            navigation.Controls.Add(button); _stepButtons.Add(button);
        }
        var sidebarFooter = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 10, 8, 12), FlowDirection = FlowDirection.TopDown, WrapContents = false };
        _sidebarStatus.ForeColor = Color.FromArgb(151, 162, 187); _sidebarStatus.Margin = new Padding(0, 0, 0, 10);
        sidebarFooter.Controls.Add(_sidebarStatus);
        _pause.Click += (_, _) => { Pause("paused by user"); UpdateSetup(); }; sidebarFooter.Controls.Add(_pause);
        var quit = Button("Quit BatCave", Quit); ((DesktopButton)quit).Quiet = true; quit.BackColor = DesktopTheme.Sidebar;
        sidebarFooter.Controls.Add(quit); sidebar.Controls.Add(sidebarFooter, 0, 2);
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(26, 24, 26, 20), ColumnCount = 1, RowCount = 5, Margin = Padding.Empty };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 83));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        shell.Controls.Add(content, 1, 0);
        _stepTitle.Font = new Font(Font.FontFamily, 9, FontStyle.Bold);
        _stepTitle.ForeColor = DesktopTheme.Muted;
        var header = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        _stepTitle.Margin = new Padding(0, 0, 0, 5);
        _pageTitle.Font = new Font(Font.FontFamily, 23, FontStyle.Bold); _pageTitle.Margin = Padding.Empty;
        header.Controls.Add(_stepTitle); header.Controls.Add(_pageTitle);
        content.Controls.Add(header, 0, 0); content.Controls.Add(_pages, 0, 1);

        var permissions = Page("One keyboard. Both computers.", "Use this computer’s keyboard and mouse across your linked devices.");
        permissions.Controls.Add(_permissionStatus);
        permissions.Controls.Add(Body("Windows needs no extra input permission. If Windows asks about network access, allow BatCave on your private network."));
        permissions.Controls.Add(Body("Input starts only when you enable it in Link devices. Pause sharing stops input sharing on this PC."));
        permissions.Controls.Add(Body("Minimize this window to keep BatCave running. Closing it stops sharing and exits."));
        AddPage(permissions);

        AddPage(BuildPairingPage());

        var arrangementScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var arrangement = new TableLayoutPanel { Dock = DockStyle.Top, Height = 330, BackColor = Color.White, Padding = new Padding(20, 16, 20, 12), ColumnCount = 1, RowCount = 8 };
        var positionExpanded = false;
        arrangementScroll.Controls.Add(arrangement);
        arrangementScroll.Resize += (_, _) => arrangement.Height = Math.Max(positionExpanded ? 440 : 330, arrangementScroll.ClientSize.Height);
        arrangement.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        arrangement.RowStyles.Add(new RowStyle(SizeType.Absolute, 41));
        arrangement.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        arrangement.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        arrangement.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        arrangement.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        arrangement.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        arrangement.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        var arrangeTitle = Heading("Match your desk"); arrangeTitle.Margin = Padding.Empty;
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
            arrangement.Height = Math.Max(positionExpanded ? 440 : 330, arrangementScroll.ClientSize.Height);
            positionControls!.Text = positionExpanded ? "Hide position controls" : "Position controls";
        });
        positionControls.FlatAppearance.BorderSize = 0;
        arrangement.Controls.Add(positionControls, 0, 4);
        _layoutStatus.MaximumSize = new Size(600, 0); arrangement.Controls.Add(_layoutStatus, 0, 7);
        var arrangementPage = new Panel { Dock = DockStyle.Fill };
        _receiverArrangement.Controls.Add(Heading("Arrange on your other computer"));
        _receiverArrangement.Controls.Add(Body("Use Arrange screens on the computer supplying the keyboard and mouse. Place this PC where it sits on your desk."));
        _receiverArrangement.Controls.Add(Body("This PC follows that arrangement. Continue to Link devices to enable input here."));
        arrangementPage.Controls.Add(arrangementScroll); arrangementPage.Controls.Add(_receiverArrangement);
        AddPage(arrangementPage);

        var linking = new SetupPage(); linking.Controls.Add(Heading("Ready when you are"));
        linking.Controls.Add(_roleHelp);
        linking.Controls.Add(_linkStatus);
        linking.Controls.Add(Body("Move your mouse through the shared screen edge to use your other computer. Escape, F12, or Pause sharing restores local control."));
        var login = new CheckBox { Text = "Open BatCave at login", AutoSize = true, Margin = new Padding(0, 16, 0, 8) };
        if (!inspectionOnly)
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                login.Checked = key?.GetValue("BatCaveSecure") is not null;
        login.CheckedChanged += (_, _) => Guard(() =>
        {
            if (_inspectionOnly) return;
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (login.Checked) key.SetValue("BatCaveSecure", "\"" + Environment.ProcessPath + "\" --desktop");
            else key.DeleteValue("BatCaveSecure", false);
        });
        linking.Controls.Add(login);
        var clipboard = new CheckBox { Text = "Share text clipboard", AutoSize = true };
        linking.Controls.Add(clipboard); linking.Controls.Add(Body("A lost connection pauses sharing. Reconnecting never starts input by itself."));
        AddPage(linking);
        var details = Button("Address & security details", ShowConnectionDetails);
        ((DesktopButton)details).Quiet = true; details.BackColor = BackColor;
        content.Controls.Add(details, 0, 2);
        _status.Dock = DockStyle.Fill; _status.AutoSize = false; _status.ForeColor = DesktopTheme.Muted;
        content.Controls.Add(_status, 0, 3);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1 };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        StyleButton(_back); _back.Click += (_, _) =>
        {
            if (_setupStep == 1 && _addingComputer) { _addingComputer = false; UpdateSetup(); }
            else ShowStep(_setupStep - 1);
        }; actions.Controls.Add(_back, 0, 0);
        StyleButton(_saveSetup); _saveSetup.Click += (_, _) => Guard(() => { if (!ReceiveInput) SaveLayout(); SaveSettings(); UpdateSetup(); });
        actions.Controls.Add(_saveSetup, 2, 0); StyleButton(_start, true); actions.Controls.Add(_start, 3, 0);
        content.Controls.Add(actions, 0, 4);
        var settings = ReadSettings();
        if (settings?.Connections is { Count: <= 128 } connections)
            foreach (var entry in connections) if (entry.Value is not null) _connectionSettings[entry.Key] = entry.Value;
        _selectedPeerId = settings?.SelectedPeerId;
        _legacyLayoutPeer = settings?.SelectedPeerId;
        _legacySide = settings?.PeerOnRight == false ? PeerSide.Left : PeerSide.Right;
        _side.SelectedIndex = settings?.PeerOnRight == false ? 1 : 0;
        clipboard.Checked = settings?.ShareClipboard == true;
        _agent.ClipboardSharingEnabled = clipboard.Checked;
        _monitors = inspectionOnly ? [new("inspection-monitor", "Monitor 1 · Primary", new(0, 0, 1920, 1080))] : DiscoverMonitors();
        RefreshPeers(); LoadPeerLayout(); LoadConnectionSettings();
        _status.Text = _selectedPeerId is null ? "Pair another computer to begin." : "Waiting for the selected computer…";
        clipboard.CheckedChanged += (_, _) => Guard(() => { _agent.ClipboardSharingEnabled = clipboard.Checked; SaveSettings(); });
        _peers.SelectedIndexChanged += (_, _) => Guard(() =>
        {
            if (_refreshingPeers) return;
            CancelConnect();
            Pause("active computer changed");
            _selectedPeerId = (_peers.SelectedItem as PeerItem)?.Card.DeviceId; _hello = null;
            _platform.SelectedIndex = PlatformIndex(_selectedPeerId);
            _lan.DisconnectUnapprovedPeer(); LoadPeerLayout(); LoadConnectionSettings(); SaveSettings(); UpdateSetup();
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
        _timer.Tick += (_, _) =>
        {
            UpdateSetup();
            if (!_inspectionOnly && _connectWanted && !_connecting && !_lan.HasPeer && DateTimeOffset.UtcNow >= _nextConnectAttempt)
                _ = ConnectPeerAsync();
        };
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
        // Accept Windows Restart Manager's shutdown query. WinForms waits for
        // WM_ENDSESSION before closing; a cancelled update stays safely paused.
        FormClosing += (_, _) => { CancelConnect(); Pause("application closing"); };
        FormClosed += (_, _) =>
        {
            _timer.Stop(); SystemEvents.PowerModeChanged -= OnPowerModeChanged; SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _agent.Close(); _lan.FlushAsync().GetAwaiter().GetResult(); _lan.Dispose(); _agent.Dispose();
            _tray.Visible = false; _tray.Dispose(); _timer.Dispose();
        };
    }

    private static Label Heading(string text) => new() { Text = text, Font = new Font("Segoe UI", 18, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
    private static Label Body(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(565, 0), ForeColor = DesktopTheme.Muted, Margin = new Padding(0, 0, 0, 14) };
    private static FlowLayoutPanel Page(string title, string explanation)
    {
        var page = new SetupPage();
        page.Controls.Add(Heading(title)); page.Controls.Add(Body(explanation));
        return page;
    }

    private Control BuildPairingPage()
    {
        var page = new SetupPage();
        page.Controls.Add(_pairTitle);
        page.Controls.Add(_pairExplanation);
        _platformRow.Controls.Add(new Label { Text = "Other computer", AutoSize = true, Margin = new Padding(0, 5, 10, 0) });
        _platform.Items.AddRange(["Windows", "Mac", "Linux"]); _platform.SelectedIndex = 0;
        _platform.AccessibleName = "Other computer's operating system";
        _platform.SelectedIndexChanged += (_, _) => UpdatePairingInstructions();
        _platformRow.Controls.Add(_platform); page.Controls.Add(_platformRow);

        _peers.Width = 400; _peers.Dock = DockStyle.None; _peers.AccessibleName = "Approved computer";
        _pairedPanel.Controls.Add(_peers); page.Controls.Add(_pairedPanel);
        _managePeers.Controls.Add(Button("Add another computer", () => { _addingComputer = true; UpdateSetup(); }));
        _removePeer.Click += (_, _) => Unpair(); _managePeers.Controls.Add(_removePeer);

        _fileGuide.Controls.Add(_sendTitle);
        _sendInstructions.Margin = new Padding(0, 0, 0, 9); _fileGuide.Controls.Add(_sendInstructions);
        _skipExport.Click += (_, _) => { _windowsFileShared = true; UpdateSetup(); };
        _skipExport.Margin = new Padding(0, 10, 0, 8); _fileGuide.Controls.Add(_skipExport);
        _fileGuide.Controls.Add(_receiveTitle);
        _receiveInstructions.Margin = new Padding(0, 0, 0, 10); _fileGuide.Controls.Add(_receiveInstructions);
        _exportAgain.Click += (_, _) => ExportCard(); _fileGuide.Controls.Add(_exportAgain);
        var alternatives = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Visible = false, Margin = new Padding(0, 5, 0, 8) };
        alternatives.Controls.Add(Button("Copy Windows file", CopyCard));
        alternatives.Controls.Add(Button("Paste other computer’s file", PasteCard));
        var alternativeHelp = Body("If you already have a way to copy text between your computers, you can copy and paste the connection files instead of saving them.");
        alternativeHelp.Visible = false;
        var alternative = Button("Use copy & paste instead", () =>
        {
            alternativeHelp.Visible = alternatives.Visible = !alternatives.Visible;
            page.PerformLayout();
        });
        ((DesktopButton)alternative).Quiet = true;
        _fileGuide.Controls.Add(alternative); _fileGuide.Controls.Add(alternativeHelp); _fileGuide.Controls.Add(alternatives);
        page.Controls.Add(_fileGuide);

        _rolePanel.Controls.Add(new Label { Text = "Keyboard and mouse", AutoSize = true, Margin = new Padding(0, 5, 12, 0) });
        _role.Items.AddRange(["This PC", "The other computer"]); _role.SelectedIndex = 0;
        _role.AccessibleName = "Computer providing the keyboard and mouse";
        _rolePanel.Controls.Add(_role); page.Controls.Add(_rolePanel);
        _role.SelectedIndexChanged += (_, _) => Guard(() =>
        {
            if (_loadingConnection) return;
            Pause("input role changed"); SaveSettings(); UpdateSetup();
        });
        _connectPanel.Controls.Add(SectionTitle("3", "Connect your computers"));
        _connectInstructions.Margin = new Padding(0, 0, 0, 10); _incomingPanel.Controls.Add(_connectInstructions);
        var addressRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 14) };
        _addresses.AccessibleName = "This PC’s local network address";
        _addresses.Margin = new Padding(0, 8, 12, 0); addressRow.Controls.Add(_addresses);
        _copyAddress.Click += (_, _) => Guard(() =>
        {
            if (_addresses.SelectedItem is NetworkAddress address) { Clipboard.SetText(address.Address); Notice("PC address copied. Enter it in BatCave on your other computer."); }
        }); addressRow.Controls.Add(_copyAddress);
        _incomingPanel.Controls.Add(addressRow); _connectPanel.Controls.Add(_incomingPanel);
        _outgoingPanel.Controls.Add(Body("Enter the address shown in BatCave on the other computer:"));
        var connectRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 12) };
        connectRow.Controls.Add(_host); connectRow.Controls.Add(_connect); _outgoingPanel.Controls.Add(connectRow);
        _connectPanel.Controls.Add(_outgoingPanel);
        _connectionDirection.Click += (_, _) => SetConnectionDirection(!_outgoingPanel.Visible);
        _connectPanel.Controls.Add(_connectionDirection);
        _connect.Click += async (_, _) =>
        {
            _connectWanted = true; await ConnectPeerAsync();
        };
        _host.TextChanged += (_, _) => { if (!_loadingConnection) CancelConnect(); };
        page.Controls.Add(_connectPanel);
        page.Controls.Add(_managePeers);
        page.Resize += (_, _) =>
        {
            var width = Math.Max(100, page.ClientSize.Width - page.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 8);
            foreach (var panel in new[] { _fileGuide, _pairedPanel, _connectPanel, _rolePanel, _outgoingPanel, _incomingPanel })
            {
                panel.MaximumSize = new Size(width, 0);
                foreach (Control child in panel.Controls)
                {
                    if (child is Label label) label.MaximumSize = new Size(width, 0);
                    else if (child is FlowLayoutPanel row) row.MaximumSize = new Size(width, 0);
                }
            }
        };
        UpdatePairingInstructions();
        return page;
    }

    private static Label SectionTitle(string number, string title) => new()
    {
        Text = number + "   " + title, AutoSize = true, Font = new Font("Segoe UI", 11, FontStyle.Bold),
        ForeColor = DesktopTheme.Ink, Margin = new Padding(0, 0, 0, 8)
    };

    private void UpdatePairingInstructions()
    {
        var name = _platform.SelectedIndex switch { 1 => "Mac", 2 => "Linux computer", _ => "other Windows PC" };
        var import = _platform.SelectedIndex switch
        {
            1 => "Pair computers → Use a card file → Import Windows card",
            2 => "Pair computers → Add Windows computer (Import Windows card in 0.2.0)",
            _ => "Pair computers → I already have the other computer’s file → Add computer"
        };
        var export = _platform.SelectedIndex switch { 1 => "Save Mac card", 2 => "Save Linux file (Export device card in 0.2.0)", _ => "Save Windows file again" };
        _sendInstructions.Text = $"Choose Save Windows file below and send it to your {name} by email or USB.\nOn that computer: {import}. Compare the approval code and approve this PC there.";
        _receiveInstructions.Text = $"On your {name}, import this PC’s file first. Then choose {export}.\n\nSend that computer’s file back here and choose Add computer below. Both computers need to approve each other.";
        _connectInstructions.Text = _platform.SelectedIndex == 0
            ? "On the other PC, open Pair computers → Connect from this PC instead. Enter the address below, then choose Connect securely. Keep both PCs on the same network."
            : $"On your {name}, open Arrange screens. Enter the address below and choose Connect securely. Keep both computers on the same network.";
    }

    private static int PlatformIndex(string? deviceId) => deviceId?.StartsWith("macos:", StringComparison.Ordinal) == true ? 1
        : deviceId?.StartsWith("linux:", StringComparison.Ordinal) == true ? 2 : 0;

    private void CancelConnect()
    {
        _connectWanted = false;
        _connectCancellation?.Cancel();
    }
    private void SetConnectionDirection(bool outgoing)
    {
        _outgoingPanel.Visible = outgoing; _incomingPanel.Visible = !outgoing;
        _connectionDirection.Text = outgoing ? "Use this PC’s address instead" : "Connect from this PC instead";
    }

    private void LoadConnectionSettings()
    {
        CancelConnect(); _loadingConnection = true;
        try
        {
            var saved = _selectedPeerId is { } id ? _connectionSettings.GetValueOrDefault(id) : null;
            _host.Text = saved?.Host ?? "";
            SetConnectionDirection(_host.Text.Length > 0);
            _role.SelectedIndex = saved?.ReceiveInput == true ? 1 : 0;
            _connectWanted = _host.Text.Length > 0;
        }
        finally { _loadingConnection = false; }
    }

    private async Task ConnectPeerAsync()
    {
        if (_inspectionOnly || _reconnectSuspended || _connecting || _lan.HasPeer || _peers.SelectedItem is not PeerItem selected) return;
        var host = _host.Text.Trim();
        if (host.Length == 0 || host.Length > 253 || Uri.CheckHostName(host) == UriHostNameType.Unknown)
        { _connectWanted = false; Notice("Enter the local address shown in BatCave on your other computer."); return; }
        var peerId = selected.Card.DeviceId;
        var attempt = new CancellationTokenSource(); _connectCancellation = attempt;
        _connecting = true; _nextConnectAttempt = DateTimeOffset.UtcNow.AddSeconds(5);
        try
        {
            SaveSettings(); UpdateSetup();
            Notice("Connecting securely… Keep BatCave open on the other computer.");
            await _lan.ConnectSecureAsync(host, new ApprovedPeer(peerId, selected.Card.CertificateSha256), cancellationToken: attempt.Token);
            if (!IsDisposed && peerId == _selectedPeerId && !attempt.IsCancellationRequested)
                Notice("Checking the approved computer’s identity…");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (IsDisposed || peerId != _selectedPeerId || attempt.IsCancellationRequested) return;
            if (ex is System.Security.Authentication.AuthenticationException or InvalidOperationException or IOException)
                _connectWanted = false;
            Notice("Could not link computers. Check the address, keep BatCave open, and approve both connection files.");
        }
        finally
        {
            if (ReferenceEquals(_connectCancellation, attempt)) { _connectCancellation = null; _connecting = false; }
            attempt.Dispose(); _nextConnectAttempt = DateTimeOffset.UtcNow.AddSeconds(5);
            if (!IsDisposed) UpdateSetup();
        }
    }

    private sealed record NetworkAddress(string Name, string Address)
    {
        public override string ToString() => Address + "  ·  " + Name;
    }

    private static NetworkAddress[] LocalAddresses()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                    && n.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Ethernet or System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211)
                .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any)))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                        && !System.Net.IPAddress.IsLoopback(a.Address) && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                    .Select(a => new NetworkAddress(n.Name, a.Address.ToString())))
                .DistinctBy(a => a.Address).ToArray();
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { return []; }
    }

    private void RefreshAddresses()
    {
        if (DateTimeOffset.UtcNow >= _addressRefresh)
        {
            _addressRefresh = DateTimeOffset.UtcNow.AddSeconds(10);
            var selected = (_addresses.SelectedItem as NetworkAddress)?.Address;
            var addresses = _inspectionOnly ? new[] { new NetworkAddress("Wi-Fi", "192.0.2.10") } : LocalAddresses();
            _addresses.Items.Clear(); _addresses.Items.AddRange(addresses);
            _addresses.SelectedIndex = Array.FindIndex(addresses, a => a.Address == selected);
            if (_addresses.SelectedIndex < 0 && addresses.Length > 0) _addresses.SelectedIndex = 0;
        }
        _addresses.Visible = _copyAddress.Enabled = _addresses.Items.Count > 0;
        if (_addresses.Items.Count == 0) _connectInstructions.Text = "Connect this PC to your home or office network first. Its local address will appear here.";
    }
    private void AddPage(Control page) { _setupPages.Add(page); _pages.Controls.Add(page); }
    internal void ShowStep(int step)
    {
        if (step < 0 || step >= DesktopSetupFlow.Titles.Length) return;
        _setupStep = step; _notice = null;
        if (step != 1) _addingComputer = false;
        for (var i = 0; i < _setupPages.Count; i++) _setupPages[i].Visible = i == step;
        _setupPages[step].BringToFront(); UpdateSetup();
    }
    private DesktopSetupState SetupState() => new(_listenerReady, _peers.SelectedItem is PeerItem selected && selected.Card.DeviceId == _selectedPeerId,
        _lan.HasPeer && _hello?.DeviceId == _selectedPeerId && _lan.CurrentPeerDeviceId == _selectedPeerId,
        _layoutValid, _agent.IsBridgeActive || _agent.IsReceivingEnabled, _addingComputer, _windowsFileShared, ReceiveInput);
    private void UpdateSetup()
    {
        var state = SetupState();
        _sharing = state.Sharing;
        var action = DesktopSetupFlow.Action(_setupStep, state);
        _start.Text = action.Label; _start.Enabled = action.Enabled;
        _back.Visible = _setupStep > 0; _saveSetup.Visible = _setupStep == 3;
        _back.Text = _setupStep == 1 && _addingComputer ? "Cancel" : "Back";
        _saveSetup.Enabled = state.PeerSelected && (state.ReceiveInput || state.LayoutValid);
        _stepTitle.Text = "STEP " + (_setupStep + 1) + " OF 4";
        _pageTitle.Text = DesktopSetupFlow.Titles[_setupStep];
        for (var i = 0; i < _stepButtons.Count; i++)
        {
            _stepButtons[i].Selected = i == _setupStep;
        }
        var showGuide = !state.PeerSelected || _addingComputer;
        _pairTitle.Text = showGuide ? state.PeerSelected ? "Add another computer" : "Add your first computer" : (_peers.SelectedItem as PeerItem)!.Card.DisplayName;
        _fileGuide.Visible = _platformRow.Visible = showGuide;
        _pairedPanel.Visible = !showGuide && _peers.Items.Count > 1;
        _managePeers.Visible = !showGuide;
        _rolePanel.Visible = !showGuide;
        _roleHelp.Text = ReceiveInput
            ? "Using the other computer’s keyboard and mouse. Change this in Pair computers."
            : "Using this PC’s keyboard and mouse. Change this in Pair computers.";
        _receiverArrangement.Visible = ReceiveInput;
        if (ReceiveInput) _receiverArrangement.BringToFront();
        _connect.Text = _connecting ? "Connecting…" : "Connect securely";
        _connect.Enabled = !_connecting && state.PeerSelected;
        _host.Enabled = !_connecting;
        _fileGuide.SuspendLayout();
        _sendTitle.Visible = _sendInstructions.Visible = _skipExport.Visible = !_windowsFileShared;
        _receiveTitle.Visible = _receiveInstructions.Visible = _exportAgain.Visible = _windowsFileShared;
        // Showing native children can change their z-order. FlowLayout uses that
        // order, so keep the instruction ahead of the secondary export action.
        _fileGuide.Controls.SetChildIndex(_receiveTitle, 3);
        _fileGuide.Controls.SetChildIndex(_receiveInstructions, 4);
        _fileGuide.Controls.SetChildIndex(_exportAgain, 5);
        _fileGuide.ResumeLayout(true); _fileGuide.PerformLayout();
        _pairExplanation.Text = showGuide ? "Exchange one connection file each to approve your computers. Keep BatCave open on both."
            : state.Connected ? "Connected securely. Continue to arrange your screens." : "Approved · waiting for a connection";
        _pairExplanation.ForeColor = state.Connected && !showGuide ? DesktopTheme.Green : DesktopTheme.Muted;
        _connectPanel.Visible = !showGuide && !state.Connected;
        _peers.Visible = _peers.Items.Count > 1;
        _removePeer.Enabled = state.PeerSelected;
        if (!showGuide) { UpdatePairingInstructions(); RefreshAddresses(); }
        _sidebarStatus.Text = state.Sharing ? "●  Sharing securely" : "●  Sharing is paused";
        _pause.Enabled = state.Sharing;
        _pause.Visible = state.Sharing;
        _permissionStatus.Text = state.ListenerReady ? "Windows is ready to link your devices." : "Waiting for network access. Reopen BatCave if the connection service could not start.";
        _linkStatus.Text = !state.PeerSelected ? "Add your other computer in Pair computers first."
            : state.Sharing ? ReceiveInput ? "This PC is ready for the other computer’s mouse and keyboard." : "Your keyboard and mouse are shared."
            : state.Connected ? ReceiveInput ? "Choose Enable input on this PC. Then start sharing on the other computer."
                : "Enable input on the other computer, then choose Start sharing here."
            : "Open Pair computers to connect your devices.";
        _status.Text = DesktopSetupFlow.Status(state);
        _status.ForeColor = state.Connected ? DesktopTheme.Green : DesktopTheme.Muted;
        if (_notice is not null && DateTimeOffset.UtcNow < _noticeUntil) _status.Text = _notice;
    }
    private void ActivatePrimary()
    {
        var action = DesktopSetupFlow.Action(_setupStep, SetupState());
        if (!action.Enabled) return;
        _notice = null;
        switch (action.Operation)
        {
            case SetupOperation.Export: ExportCard(); break;
            case SetupOperation.Next:
                if (_setupStep == 2 && !ReceiveInput) SaveLayout();
                ShowStep(_setupStep + 1); break;
            case SetupOperation.Pair:
                if (_setupStep != 1) ShowStep(1); else ImportCard();
                break;
            case SetupOperation.Pause: Pause("paused by user"); break;
            case SetupOperation.Start:
                if (ReceiveInput) { _agent.EnableReceiving(); break; }
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
        _windowsFileShared = true; UpdateSetup();
        Notice("Windows connection file copied. Paste it into BatCave on your other computer.");
    });
    private void Notice(string text) { _notice = text; _noticeUntil = DateTimeOffset.UtcNow.AddSeconds(12); _status.Text = text; }
    private void PasteCard() => Guard(() =>
    {
        if (!Clipboard.ContainsText()) throw new InvalidDataException("Copy the connection file from BatCave on your other computer first.");
        ApproveCard(Clipboard.GetText()); UpdateSetup();
    });

    private static void StyleButton(Button button, bool primary = false)
    {
        if (button is DesktopButton desktop) desktop.Primary = primary;
    }

    private static Button Button(string text, Action action)
    {
        var button = new DesktopButton { Text = text };
        StyleButton(button);
        button.Click += (_, _) => action();
        return button;
    }

    private void ExportCard() => Guard(() =>
    {
        using var dialog = new SaveFileDialog { Title = "Save this PC’s connection file", Filter = "BatCave connection file|*.batcave-peer.json", FileName = "BatCave-Windows.batcave-peer.json" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            File.WriteAllText(dialog.FileName, DeviceIdentityStore.SerializePublicCard(_identity.ExportPublicCard(Environment.MachineName)));
            _windowsFileShared = true; UpdateSetup();
            Notice("File saved. Send it to your other computer and import it there.");
        }
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
        using var dialog = new OpenFileDialog { Title = "Choose your other computer’s connection file", Filter = "BatCave connection file|*.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) { return; }
        if (new FileInfo(dialog.FileName).Length > 16384) { throw new InvalidDataException("Pairing card exceeds 16 KiB."); }
        var json = File.ReadAllText(dialog.FileName);
        ApproveCard(json); UpdateSetup();
    });

    private void ApproveCard(string json)
    {
        var peer = DeviceIdentityStore.ReadPublicCard(json);
        if (peer.DeviceId == _identity.DeviceId) { throw new InvalidDataException("This file belongs to this PC. Choose the file exported by BatCave on your other computer."); }
        var card = JsonSerializer.Deserialize<DeviceIdentityStore.PairingCard>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        if (MessageBox.Show(this, "Approve " + card.DisplayName + "?\n\nDevice: " + card.DeviceId
            + "\n\nApproval code (SHA-256 fingerprint):\n" + card.CertificateSha256
            + "\n\nOn your other computer, open its security details and compare the entire code. Choose Yes only if they match. This approves the computer for input and optional clipboard sharing; sharing still needs Start sharing.",
            "Approve a computer", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) { return; }
        ApplyApproval(card);
        Notice("Computer approved. Connect using an address shown in BatCave on either computer.");
    }

    private void ApplyApproval(DeviceIdentityStore.PairingCard card)
    {
        CancelConnect();
        Pause("pairing changed");
        _approvals.Approve(card);
        _selectedPeerId = card.DeviceId;
        _platform.SelectedIndex = PlatformIndex(card.DeviceId);
        _hello = null; _addingComputer = false;
        _lan.DisconnectUnapprovedPeer();
        RefreshPeers();
        LoadPeerLayout();
        LoadConnectionSettings();
        SaveSettings();
    }

    private void Unpair() => Guard(() =>
    {
        if (_peers.SelectedItem is not PeerItem item) { return; }
        CancelConnect();
        Pause("device unpaired");
        _approvals.Remove(item.Card.DeviceId);
        _layoutStore.Remove(item.Card.DeviceId);
        _connectionSettings.Remove(item.Card.DeviceId);
        _lan.DisconnectUnapprovedPeer();
        _selectedPeerId = null;
        _hello = null;
        _addingComputer = false;
        RefreshPeers();
        LoadPeerLayout();
        LoadConnectionSettings();
        SaveSettings();
    });

    private void RefreshPeers()
    {
        _refreshingPeers = true;
        try
        {
            _peers.Items.Clear();
            if (!_approvals.Cards.Any(card => card.DeviceId == _selectedPeerId))
                _selectedPeerId = _approvals.Cards.FirstOrDefault()?.DeviceId;
            foreach (var card in _approvals.Cards)
            {
                var item = new PeerItem(card);
                _peers.Items.Add(item);
                if (card.DeviceId == _selectedPeerId) { _peers.SelectedItem = item; }
            }
            if (_peers.SelectedItem is PeerItem selected)
                _platform.SelectedIndex = PlatformIndex(selected.Card.DeviceId);
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
            if (new FileInfo(_settingsPath).Length > 131072) return null;
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(_settingsPath));
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }
    private void SaveSettings()
    {
        if (_selectedPeerId is { } peer) _connectionSettings[peer] = new(_host.Text.Trim(), ReceiveInput);
        File.WriteAllText(_settingsPath + ".new", JsonSerializer.Serialize(new Settings(_selectedPeerId,
            _side.SelectedIndex != (int)PeerSide.Left, _agent.ClipboardSharingEnabled, _connectionSettings)));
        File.Move(_settingsPath + ".new", _settingsPath, overwrite: true);
    }
    private void PostUi(Action action)
    {
        try { if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)(() => { if (!IsDisposed) action(); })); }
        catch (InvalidOperationException) when (IsDisposed || !IsHandleCreated) { }
    }
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => PostUi(() =>
    {
        CancelConnect();
        Pause("monitors changed"); _lan.Disconnect(); _hello = null; _monitors = DiscoverMonitors(); LoadPeerLayout();
        var b = SystemInformation.VirtualScreen;
        _lan.UpdateLocalBounds(new(b.Left, b.Top, b.Width, b.Height));
        ResumeConnections();
    });
    private void Pause(string reason) { _sharing = false; _agent.StopSharingNow(reason); }
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend && !IsDisposed)
            ReleaseAndDisconnect("computer sleeping");
        else if (e.Mode == PowerModes.Resume) PostUi(ResumeConnections);
    }
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff && !IsDisposed)
            ReleaseAndDisconnect("session locked");
        else if (e.Reason == SessionSwitchReason.SessionUnlock) PostUi(ResumeConnections);
    }
    private void ResumeConnections()
    {
        _reconnectSuspended = false;
        _connectWanted = _selectedPeerId is not null && _host.Text.Trim().Length > 0;
        _nextConnectAttempt = DateTimeOffset.UtcNow;
        UpdateSetup();
    }
    private void ReleaseAndDisconnect(string reason)
    {
        Action cleanup = () =>
        {
            _reconnectSuspended = true;
            CancelConnect();
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
    private void Quit() => Close();
    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "BatCave", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
