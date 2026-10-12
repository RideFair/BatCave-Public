using System.Reflection;
using BatCave.Audio;
using BatCave.WindowsAgent;
using Microsoft.Win32;

namespace BatCave.AudioPreview;

internal sealed class AudioPreviewForm : Form
{
    private static readonly Color Background = Color.FromArgb(16, 22, 32);
    private static readonly Color Surface = Color.FromArgb(25, 34, 47);
    private static readonly Color TextColor = Color.FromArgb(230, 236, 244);
    private static readonly Color Muted = Color.FromArgb(153, 170, 190);
    private static readonly Color Accent = Color.FromArgb(117, 227, 186);
    private readonly DeviceIdentityStore? _identity;
    private readonly ApprovedPeerStore? _approvals;
    private readonly AudioPeerNetwork? _network;
    private readonly AudioCoordinator? _coordinator;
    private readonly Action<string, bool>? _refresh;
    private readonly string? _directory;
    private AudioPreviewSettings _settings = new();
    private readonly CheckedListBox _sources = List();
    private readonly CheckedListBox _outputs = List();
    private readonly CheckBox _send = Check("Allow this computer to share its application audio");
    private readonly CheckBox _primary = Check("Primary computer — prefer its Bluetooth output");
    private readonly TextBox _name = Input();
    private readonly TextBox _host = Input();
    private readonly ComboBox _peers = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, BackColor = Color.FromArgb(36, 47, 62), ForeColor = TextColor };
    private readonly ListBox _trusted = new() { Dock = DockStyle.Fill, BackColor = Surface, ForeColor = TextColor, BorderStyle = BorderStyle.None };
    private readonly TextBox _fingerprint = Input();
    private readonly Label _state = Label("PAUSED", 11, Accent);
    private readonly Label _status = Label("Choose sources and outputs, then start shared audio.", 10, Muted);
    private readonly Label _routes = Label("Normal playback stays on each computer.", 10, Muted);
    private readonly Label _volumeLabel = Label("This computer’s shared volume · 70%", 10, TextColor);
    private readonly TrackBar _volume = new() { Minimum = 0, Maximum = 100, Value = 70, TickStyle = TickStyle.None, Dock = DockStyle.Fill, BackColor = Surface };
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer _devices = new() { Interval = 5000 };
    private AudioRoute[]? _suggested;
    private AudioDeviceInventory[] _inventories = [];
    private bool _updating;
    private bool _refreshing;
    private string _viewSignature = "";
    private string _connectionStatus = "Not listening. Audio connections use mutual TLS.";
    private readonly Label _connectionLabel = Label("Not listening. Audio connections use mutual TLS.", 10, Muted);
    private readonly AudioPreviewState _demo = DemoState();
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Padding = new(18, 7) };
    private sealed record SourceItem(AudioDeviceInventory Device)
    { public override string ToString() => Device.DisplayName + (Device.DeviceIsPrimary ? "   ·   Primary" : "") + (!Device.SupportsSystemCapture ? "   ·   Audio sharing unavailable" : ""); }
    private sealed record OutputItem(AudioDeviceInventory Device, AudioOutputDescriptor Output)
    {
        public AudioOutputKey Key => new(Device.DeviceId, Output.EndpointId);
        public override string ToString() => $"{Device.DisplayName}   /   {Output.DisplayName}" +
            (Output.Kind == AudioOutputKind.Bluetooth ? "   ·   Bluetooth" : Output.Kind == AudioOutputKind.Speakers ? "   ·   Speakers" : Output.Kind == AudioOutputKind.Unknown ? "   ·   Transport unknown" : "") +
            (Output.IsDefault ? "   ·   Default" : "");
    }
    private sealed record PeerItem(DeviceIdentityStore.PairingCard Card)
    { public override string ToString() => Card.DisplayName; }

    // Used only by the synthetic screenshot command. It cannot initialize audio or trust.
    public AudioPreviewForm() { Build(); UpdateView(_demo); _sources.SetItemChecked(0, true); _sources.SetItemChecked(1, true); _sources.SetItemChecked(2, true); _outputs.SetItemChecked(0, true); UpdateSelectionText(); }
    public AudioPreviewForm(DeviceIdentityStore identity, ApprovedPeerStore approvals, AudioPeerNetwork network,
        AudioCoordinator coordinator, AudioPreviewSettings settings, string directory, Action<string, bool> refresh)
    {
        _identity = identity; _approvals = approvals; _network = network; _coordinator = coordinator;
        _settings = settings; _directory = directory; _refresh = refresh;
        Build(); _name.Text = settings.DisplayName; _primary.Checked = settings.IsPrimary;
        RefreshPeers(); UpdateView(coordinator.State);
        _poll.Tick += (_, _) => UpdateView(coordinator.State); _poll.Start();
        _devices.Tick += async (_, _) => await RefreshOutputs(); _devices.Start();
        network.Status += NetworkStatus;
        SystemEvents.SessionSwitch += SessionChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
    }

    private void Build()
    {
        Text = "BatCave · Audio Preview"; ClientSize = new(1140, 790); MinimumSize = new(990, 760);
        BackColor = Background; ForeColor = TextColor; Font = new("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterScreen;
        using var iconStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BatCave.icon");
        if (iconStream is not null) Icon = new Icon(iconStream);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(26, 22, 26, 18), ColumnCount = 1, RowCount = 4 };
        root.RowStyles.Add(new(SizeType.Absolute, 97)); root.RowStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.Absolute, 86)); root.RowStyles.Add(new(SizeType.Absolute, 58)); Controls.Add(root);
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Margin = Padding.Empty };
        heading.ColumnStyles.Add(new(SizeType.Percent, 100)); heading.ColumnStyles.Add(new(SizeType.Absolute, 120));
        heading.RowStyles.Add(new(SizeType.Absolute, 23)); heading.RowStyles.Add(new(SizeType.Absolute, 49)); heading.RowStyles.Add(new(SizeType.Absolute, 25));
        heading.Controls.Add(Label("BATCAVE   /   AUDIO PREVIEW", 10, Accent), 0, 0);
        heading.Controls.Add(Label("Hear your computers together.", 25, TextColor, FontStyle.Bold), 0, 1);
        heading.Controls.Add(Label("Mix application audio on a connected headset or the speakers you choose.", 10, Muted), 0, 2);
        _state.TextAlign = ContentAlignment.MiddleRight; heading.Controls.Add(_state, 1, 1); root.Controls.Add(heading, 0, 0);
        var routing = new TabPage("Audio routing") { BackColor = Surface, ForeColor = TextColor, Padding = new(18) };
        var pairing = new TabPage("Devices & trust") { BackColor = Surface, ForeColor = TextColor, Padding = new(18) };
        _tabs.TabPages.Add(routing); _tabs.TabPages.Add(pairing); root.Controls.Add(_tabs, 0, 1);
        BuildRouting(routing); BuildPairing(pairing);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new(0, 10, 0, 0) };
        actions.ColumnStyles.Add(new(SizeType.Percent, 100)); actions.ColumnStyles.Add(new(SizeType.Absolute, 305));
        actions.RowStyles.Add(new(SizeType.Absolute, 45)); actions.RowStyles.Add(new(SizeType.Percent, 100));
        actions.Controls.Add(_send, 0, 0); actions.Controls.Add(_routes, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        buttons.Controls.Add(Button("Start shared audio", StartAudio, true, 174));
        buttons.Controls.Add(Button("Pause", (_, _) => _coordinator?.Pause("Paused by you."), false, 105));
        actions.Controls.Add(buttons, 1, 0); actions.SetRowSpan(buttons, 2); root.Controls.Add(actions, 0, 2);
        _status.Dock = DockStyle.Fill; _status.MaximumSize = new(0, 58); root.Controls.Add(_status, 0, 3);
        _send.CheckedChanged += (_, _) => { if (!_updating) _coordinator?.Pause("Sharing permission changed. Press Start to apply it."); };
    }

    private void BuildRouting(Control page)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        layout.ColumnStyles.Add(new(SizeType.Percent, 44)); layout.ColumnStyles.Add(new(SizeType.Percent, 56));
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 81)); layout.RowStyles.Add(new(SizeType.Absolute, 65));
        layout.Controls.Add(Card("Sources", "Choose the computers you want to hear.", _sources), 0, 0);
        layout.Controls.Add(Card("Play on", "Connected outputs across your paired computers.", _outputs), 1, 0);
        var volume = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Padding = new(0, 13, 12, 0) };
        volume.Controls.Add(_volumeLabel, 0, 0); volume.Controls.Add(_volume, 0, 1); layout.Controls.Add(volume, 0, 1);
        var suggested = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(12, 15, 0, 0), WrapContents = false };
        suggested.Controls.Add(Button("Use suggested routing", Suggest, false, 205));
        suggested.Controls.Add(Button("Refresh", async (_, _) => await RefreshOutputs(), false, 93)); layout.Controls.Add(suggested, 1, 1);
        var note = Label("Local playback stays unchanged. Streamed audio stops if its selected output disconnects.\nThis preview mirrors application audio; it does not capture microphones.", 10, Muted);
        note.Dock = DockStyle.Fill; layout.Controls.Add(note, 0, 2); layout.SetColumnSpan(note, 2); page.Controls.Add(layout);
        _sources.ItemCheck += SelectionChanged; _outputs.ItemCheck += SelectionChanged;
        _volume.ValueChanged += (_, _) =>
        {
            _volumeLabel.Text = $"This computer’s shared volume · {_volume.Value}%";
            if (!_updating) { _suggested = null; _coordinator?.Pause("Shared volume changed. Press Start to apply it."); }
        };
    }

    private void BuildPairing(Control page)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        layout.ColumnStyles.Add(new(SizeType.Percent, 50)); layout.ColumnStyles.Add(new(SizeType.Percent, 50));
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 90));
        var local = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new(0, 0, 20, 0) };
        local.RowStyles.Add(new(SizeType.Absolute, 35)); local.RowStyles.Add(new(SizeType.Absolute, 35));
        local.RowStyles.Add(new(SizeType.Absolute, 38)); local.RowStyles.Add(new(SizeType.Absolute, 50));
        local.RowStyles.Add(new(SizeType.Absolute, 40)); local.RowStyles.Add(new(SizeType.Absolute, 45)); local.RowStyles.Add(new(SizeType.Percent, 100));
        local.Controls.Add(Label("This computer", 18, TextColor, FontStyle.Bold), 0, 0);
        _name.Text = _settings.DisplayName; _name.MaxLength = 128; local.Controls.Add(_name, 0, 1);
        _primary.Checked = _settings.IsPrimary; local.Controls.Add(_primary, 0, 2);
        var identityActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        identityActions.Controls.Add(Button("Save device details", async (_, _) => await SaveIdentity(), false, 155));
        identityActions.Controls.Add(Button("Export public card", ExportCard, false, 165)); local.Controls.Add(identityActions, 0, 3);
        var listen = Button("Listen for paired devices", Listen, false, 227); local.Controls.Add(listen, 0, 4);
        local.Controls.Add(_connectionLabel, 0, 5);
        local.Controls.Add(Label("Approve public cards on both computers after comparing their full fingerprints.\n\nThis preview uses a separate profile. Your working BatCave app stays available.", 10, Muted), 0, 6);
        layout.Controls.Add(local, 0, 0);
        var peers = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new(12, 0, 0, 0) };
        peers.RowStyles.Add(new(SizeType.Absolute, 35)); peers.RowStyles.Add(new(SizeType.Percent, 100)); peers.RowStyles.Add(new(SizeType.Absolute, 48)); peers.RowStyles.Add(new(SizeType.Absolute, 90));
        peers.Controls.Add(Label("Approved devices", 18, TextColor, FontStyle.Bold), 0, 0); peers.Controls.Add(_trusted, 0, 1);
        var peerActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        peerActions.Controls.Add(Button("Approve public card", ImportCard, false, 184)); peerActions.Controls.Add(Button("Remove device", RemovePeer, false, 145)); peers.Controls.Add(peerActions, 0, 2);
        _fingerprint.Multiline = true; _fingerprint.ReadOnly = true; _fingerprint.Font = new("Consolas", 9); peers.Controls.Add(_fingerprint, 0, 3);
        _trusted.SelectedIndexChanged += (_, _) => _fingerprint.Text = _trusted.SelectedItem is PeerItem item
            ? "SHA-256 fingerprint\r\n" + item.Card.CertificateSha256 + "\r\n" + item.Card.DeviceId : "";
        layout.Controls.Add(peers, 1, 0);
        var connect = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Padding = new(0, 15, 0, 0) };
        connect.ColumnStyles.Add(new(SizeType.Percent, 40)); connect.ColumnStyles.Add(new(SizeType.Percent, 60)); connect.ColumnStyles.Add(new(SizeType.Absolute, 126));
        connect.Controls.Add(Label("Approved device", 9, Muted), 0, 0); connect.Controls.Add(Label("Host name or address", 9, Muted), 1, 0);
        connect.Controls.Add(_peers, 0, 1); _host.PlaceholderText = "Enter the other computer’s address"; _host.MaxLength = 253; connect.Controls.Add(_host, 1, 1);
        connect.Controls.Add(Button("Connect", Connect, true, 110), 2, 1); layout.Controls.Add(connect, 0, 1); layout.SetColumnSpan(connect, 2); page.Controls.Add(layout);
    }

    private void StartAudio(object? sender, EventArgs e)
    {
        if (_coordinator is null) return;
        try
        {
            var routes = _suggested ?? SelectedRoutes();
            if (routes.Length == 0) { _status.Text = "Select at least one source and output."; return; }
            var allowSending = _send.Checked;
            _coordinator.Start(routes, allowSending);
        }
        catch (Exception) { _status.Text = "The selected routes could not start. Refresh devices and check the receiving computer’s audio permission."; }
    }
    private AudioRoute[] SelectedRoutes()
    {
        var sources = _sources.CheckedItems.Cast<SourceItem>().ToArray();
        var outputs = _outputs.CheckedItems.Cast<OutputItem>().ToArray();
        if (sources.Length * outputs.Length > AudioLimits.MaxRoutes) throw new InvalidOperationException("Select fewer sources or outputs.");
        return sources.SelectMany(s => outputs.Select(o => new AudioRoute(s.Device.DeviceId, o.Key, _volume.Value / 100f))).ToArray();
    }
    private void SelectionChanged(object? sender, ItemCheckEventArgs e)
    {
        if (_updating) return;
        _suggested = null; _coordinator?.Pause("Selection changed. Press Start to apply it.");
        if (IsHandleCreated) BeginInvoke((Action)UpdateSelectionText);
    }
    private void UpdateSelectionText()
    {
        if (_suggested is not null) return;
        _routes.Text = $"{_sources.CheckedItems.Count} sources → {_outputs.CheckedItems.Count} outputs. Enable sharing on every sending and receiving computer.";
    }
    private void Suggest(object? sender, EventArgs e)
    {
        var policy = AudioRoutingPolicy.SuggestDefaults(_inventories);
        _coordinator?.Pause("Suggested routing selected. Press Start when ready.");
        _updating = true;
        try
        {
            var gain = _volume.Value / 100f;
            _suggested = policy.SuggestedRoutes.Select(r => r with { Gain = gain }).ToArray();
            for (var i = 0; i < _sources.Items.Count; i++) _sources.SetItemChecked(i, _suggested.Any(r => r.SourceDeviceId == ((SourceItem)_sources.Items[i]).Device.DeviceId));
            for (var i = 0; i < _outputs.Items.Count; i++) _outputs.SetItemChecked(i, _suggested.Any(r => r.Destination == ((OutputItem)_outputs.Items[i]).Key));
            _routes.Text = policy.Reason == AudioDefaultReason.PrimaryBluetooth
                ? "Suggested: mix on the primary computer’s Bluetooth output. Press Start on receiving computers too."
                : "Suggested: each computer keeps its own normal playback. Choose custom routes to share audio.";
        }
        finally { _updating = false; }
    }
    private void UpdateView(AudioPreviewState state)
    {
        if (IsDisposed) return;
        _state.Text = state.IsEnabled ? "ENABLED" : "PAUSED"; _status.Text = state.Status;
        _connectionLabel.Text = _connectionStatus;
        _inventories = state.Devices;
        var signature = string.Join("|", state.Devices.OrderBy(d => d.DeviceId).Select(d => $"{d.DeviceId}:{d.Revision}"));
        if (signature == _viewSignature) return;
        var sources = _sources.CheckedItems.Cast<SourceItem>().Select(i => i.Device.DeviceId).ToHashSet();
        var outputs = _outputs.CheckedItems.Cast<OutputItem>().Select(i => i.Key).ToHashSet();
        var first = _sources.Items.Count == 0;
        _updating = true;
        try
        {
            _sources.Items.Clear(); _outputs.Items.Clear();
            foreach (var device in state.Devices.OrderByDescending(d => d.DeviceIsPrimary).ThenBy(d => d.DisplayName))
            {
                _sources.Items.Add(new SourceItem(device), sources.Contains(device.DeviceId) || first && device.DeviceId == _identity?.DeviceId);
                foreach (var output in device.Outputs.Where(o => o.IsConnected && o.IsActive && o.CanReceiveStream))
                    _outputs.Items.Add(new OutputItem(device, output), outputs.Contains(new(device.DeviceId, output.EndpointId)) || first && output.IsDefault && device.DeviceId == _identity?.DeviceId);
            }
            _viewSignature = signature; _suggested = null; UpdateSelectionText();
        }
        finally { _updating = false; }
    }
    private async Task RefreshOutputs()
    {
        if (_refresh is null || _refreshing) return;
        _refreshing = true;
        try { var settings = _settings; await Task.Run(() => _refresh(settings.DisplayName, settings.IsPrimary)); }
        catch (Exception) { _coordinator?.Pause("Output discovery failed. Refresh before sharing again."); }
        finally { _refreshing = false; }
    }
    private async Task SaveIdentity()
    {
        if (_directory is null) return;
        try { var settings = new AudioPreviewSettings(_name.Text.Trim(), _primary.Checked); settings.Save(_directory); _settings = settings; _coordinator?.Pause("Device name or role changed. Press Start when ready."); await RefreshOutputs(); }
        catch (Exception) { MessageBox.Show(this, "Choose a device name of 1–128 characters.", "Device settings", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    }
    private void Listen(object? sender, EventArgs e)
    {
        if (_network is null) return;
        try { _network.Listen(); _connectionStatus = $"Listening on audio port {AudioPeerNetwork.Port}. Audio remains paused until you start it."; if (sender is Button button) button.Enabled = false; }
        catch (Exception) { _connectionStatus = "The audio port is unavailable. Close another audio preview and retry."; }
    }
    private async void Connect(object? sender, EventArgs e)
    {
        if (_network is null || _peers.SelectedItem is not PeerItem item || string.IsNullOrWhiteSpace(_host.Text)) return;
        var button = sender as Button; if (button is not null) button.Enabled = false;
        try { await _network.Connect(_host.Text.Trim(), new(item.Card.DeviceId, item.Card.CertificateSha256)); _connectionStatus = "Connected to an approved device. Select audio routes on both computers."; }
        catch (Exception) { _connectionStatus = "Connection failed. Check the address, listener, firewall and public-card approvals on both computers."; }
        finally { if (button is not null && !button.IsDisposed) button.Enabled = true; }
    }
    private void ExportCard(object? sender, EventArgs e)
    {
        if (_identity is null) return;
        using var dialog = new SaveFileDialog { Filter = "BatCave public card|*.batcave-peer.json", FileName = "audio-preview.batcave-peer.json", AddExtension = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllText(dialog.FileName, DeviceIdentityStore.SerializePublicCard(_identity.ExportPublicCard(_settings.DisplayName))); }
        catch (Exception) { MessageBox.Show(this, "The public card could not be saved.", "Export public card"); }
    }
    private void ImportCard(object? sender, EventArgs e)
    {
        if (_approvals is null || _identity is null) return;
        using var dialog = new OpenFileDialog { Filter = "BatCave public card|*.batcave-peer.json|JSON|*.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 16384) throw new InvalidDataException();
            var json = File.ReadAllText(dialog.FileName); var approved = DeviceIdentityStore.ReadPublicCard(json);
            if (approved.DeviceId == _identity.DeviceId) throw new InvalidDataException();
            // Audio's opaque identifier grammar is deliberately narrower than legacy input cards.
            if (!approved.DeviceId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':' or '.')) throw new InvalidDataException();
            var card = System.Text.Json.JsonSerializer.Deserialize<DeviceIdentityStore.PairingCard>(json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
            var confirmation = "Compare this fingerprint with the card exported on the other computer before approving.\n\n" + card.DisplayName + "\n" + card.DeviceId + "\n\nSHA-256\n" + card.CertificateSha256 + "\n\nApprove this device for an audio connection? Audio still requires a separate Start action.";
            if (MessageBox.Show(this, confirmation, "Approve audio peer", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            _coordinator?.Pause("Peer approvals changed. Press Start when ready."); _network?.Disconnect(card.DeviceId); _approvals.Approve(card); RefreshPeers();
        }
        catch (Exception) { MessageBox.Show(this, "This public card is invalid, expired, or could not be approved.", "Approve audio peer", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    private void RemovePeer(object? sender, EventArgs e)
    {
        if (_approvals is null || _trusted.SelectedItem is not PeerItem item) return;
        try { _coordinator?.Pause("A device was unpaired. Audio permission has been cleared."); _approvals.Remove(item.Card.DeviceId); _network?.Disconnect(item.Card.DeviceId); RefreshPeers(); }
        catch (Exception) { MessageBox.Show(this, "The device approval could not be removed.", "Remove device"); }
    }
    private void RefreshPeers()
    {
        _trusted.Items.Clear(); _peers.Items.Clear();
        foreach (var card in _approvals?.Cards ?? []) { _trusted.Items.Add(new PeerItem(card)); _peers.Items.Add(new PeerItem(card)); }
        if (_peers.Items.Count > 0) _peers.SelectedIndex = 0;
    }
    internal void ShowTrustPreview() => _tabs.SelectedIndex = 1;
    private void NetworkStatus(string status) => Volatile.Write(ref _connectionStatus, status);
    private void SessionChanged(object sender, SessionSwitchEventArgs e)
    { if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.ConsoleDisconnect) _coordinator?.Pause("Session ended or locked. Press Start to share again."); }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    { if (e.Mode is PowerModes.Suspend or PowerModes.Resume) _coordinator?.Pause("Sleep or wake cleared audio permission. Press Start to share again."); }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _poll.Stop(); _devices.Stop(); _coordinator?.Pause("Audio preview closed.");
        SystemEvents.SessionSwitch -= SessionChanged; SystemEvents.PowerModeChanged -= PowerChanged;
        if (_network is not null) _network.Status -= NetworkStatus;
        base.OnFormClosing(e);
    }
    protected override void Dispose(bool disposing)
    { if (disposing) { _poll.Dispose(); _devices.Dispose(); } base.Dispose(disposing); }

    private static Control Card(string title, string subtitle, Control content)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new(0, 0, 14, 0), Margin = Padding.Empty };
        panel.RowStyles.Add(new(SizeType.Absolute, 37)); panel.RowStyles.Add(new(SizeType.Absolute, 37)); panel.RowStyles.Add(new(SizeType.Percent, 100));
        panel.Controls.Add(Label(title, 18, TextColor, FontStyle.Bold), 0, 0); panel.Controls.Add(Label(subtitle, 10, Muted), 0, 1); panel.Controls.Add(content, 0, 2); return panel;
    }
    private static Label Label(string text, float size, Color color, FontStyle style = FontStyle.Regular)
        => new() { Text = text, Dock = DockStyle.Fill, ForeColor = color, Font = new("Segoe UI", size, style), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private static TextBox Input() => new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(36, 47, 62), ForeColor = TextColor, BorderStyle = BorderStyle.FixedSingle, Margin = new(3, 5, 8, 5) };
    private static CheckBox Check(string text) => new() { Text = text, Dock = DockStyle.Fill, ForeColor = TextColor, AutoSize = true };
    private static CheckedListBox List() => new() { Dock = DockStyle.Fill, BackColor = Surface, ForeColor = TextColor, BorderStyle = BorderStyle.None, CheckOnClick = true, IntegralHeight = false, HorizontalScrollbar = true };
    private static Button Button(string text, EventHandler click, bool accent, int width)
    {
        var button = new Button { Text = text, Width = width, Height = 37, FlatStyle = FlatStyle.Flat, BackColor = accent ? Accent : Color.FromArgb(39, 53, 71), ForeColor = accent ? Background : TextColor, Margin = new(3, 3, 6, 3), Cursor = Cursors.Hand };
        button.FlatAppearance.BorderSize = 0; button.Click += click; return button;
    }
    private static AudioPreviewState DemoState()
    {
        AudioDeviceInventory[] devices = [
            new("demo:desktop", "Primary PC", true, 1, [new("bt-headset", "Bluetooth headphones", AudioOutputKind.Bluetooth, true, true, true), new("desktop-speakers", "Desktop speakers", AudioOutputKind.Speakers, true, true, false)], true, true),
            new("demo:mac", "MacBook", false, 1, [new("mac-speakers", "Built-in speakers", AudioOutputKind.Speakers, true, true, true)], true, true),
            new("demo:laptop", "Windows laptop", false, 1, [new("laptop-speakers", "Laptop speakers", AudioOutputKind.Speakers, true, true, true), new("laptop-bt", "Bluetooth speaker", AudioOutputKind.Bluetooth, true, true, false)], true, true)
        ];
        return new(false, "Preview: audio from three computers can mix on your selected output. Enable sharing on each computer when ready.", devices, [], 0);
    }
}
