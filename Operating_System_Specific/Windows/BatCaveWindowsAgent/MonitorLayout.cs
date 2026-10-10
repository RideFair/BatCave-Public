using System.Text.Json;

namespace BatCave.WindowsAgent;

internal enum PeerSide { Right, Left, Above, Below }
internal sealed record MonitorDescriptor(string Id, string Label, VirtualDesktopBounds Bounds);
internal sealed record MonitorLayoutSettings(string MonitorId, PeerSide Side = PeerSide.Right, int Alignment = 0);
internal sealed record MonitorSeam(double Start, double End);
internal sealed record MonitorLayoutScene(IReadOnlyList<MonitorDescriptor> LocalMonitors, MonitorDescriptor Local,
    MonitorDescriptor Remote, PeerSide Side, IReadOnlyList<MonitorSeam> Seams)
{
    public string LocalEdge => Side switch { PeerSide.Right => "right", PeerSide.Left => "left", PeerSide.Above => "top", _ => "bottom" };
    public string RemoteEdge => Side switch { PeerSide.Right => "left", PeerSide.Left => "right", PeerSide.Above => "bottom", _ => "top" };
    public bool VerticalEdge => Side is PeerSide.Right or PeerSide.Left;
}

// Inclusive pixel geometry, independent of WinForms, transport, and trust stores.
internal static class MonitorLayout
{
    public static void ValidateBounds(VirtualDesktopBounds b)
    {
        if (b.Width < 2 || b.Height < 2 || b.Width > 100000 || b.Height > 100000
            || Math.Abs((long)b.Left) > 1000000 || Math.Abs((long)b.Top) > 1000000)
            throw new InvalidDataException("Screen geometry is outside the supported range.");
    }
    public static MonitorLayoutScene Build(IReadOnlyList<MonitorDescriptor> monitors, ScreenBounds peer, MonitorLayoutSettings settings, bool allowBlockedPreview = false)
    {
        ValidateSettings(settings);
        if (monitors.Count is < 1 or > 32 || monitors.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != monitors.Count)
            throw new InvalidDataException("Invalid monitor inventory.");
        foreach (var monitor in monitors)
        {
            if (string.IsNullOrWhiteSpace(monitor.Id) || monitor.Id.Length > 256) throw new InvalidDataException("Invalid monitor identifier.");
            ValidateBounds(monitor.Bounds);
        }
        var local = monitors.FirstOrDefault(m => m.Id == settings.MonitorId)
            ?? throw new InvalidDataException("The saved monitor is unavailable. Select a monitor and save the layout again.");
        var nativePeer = new VirtualDesktopBounds(peer.Left, peer.Top, peer.Width, peer.Height);
        ValidateBounds(nativePeer);
        // v0 Mac agents have used CGRect's exclusive maximum while Windows uses
        // inclusive pixels. Accept either convention; derive routing from size.
        if ((peer.Right != nativePeer.Right && peer.Right != nativePeer.Right + 1)
            || (peer.Bottom != nativePeer.Bottom && peer.Bottom != nativePeer.Bottom + 1))
            throw new InvalidDataException("Peer screen geometry is inconsistent.");
        var b = local.Bounds;
        var vertical = settings.Side is PeerSide.Right or PeerSide.Left;
        var localLength = vertical ? b.Height : b.Width;
        var peerLength = vertical ? peer.Height : peer.Width;
        var shiftLimit = Math.Max(0, ((localLength - 1) + (peerLength - 1)) / 2.0 - Math.Min(48, Math.Min(localLength, peerLength) / 2.0));
        var offset = (int)Math.Round((localLength - peerLength) / 2.0 + settings.Alignment / 100.0 * shiftLimit);
        var remote = settings.Side switch
        {
            PeerSide.Right => new VirtualDesktopBounds(b.Right + 1, b.Top + offset, peer.Width, peer.Height),
            PeerSide.Left => new VirtualDesktopBounds(b.Left - peer.Width, b.Top + offset, peer.Width, peer.Height),
            PeerSide.Above => new VirtualDesktopBounds(b.Left + offset, b.Top - peer.Height, peer.Width, peer.Height),
            _ => new VirtualDesktopBounds(b.Left + offset, b.Bottom + 1, peer.Width, peer.Height)
        };
        ValidateBounds(remote);
        var start = Math.Max(vertical ? b.Top : b.Left, vertical ? remote.Top : remote.Left);
        var end = Math.Min(vertical ? b.Bottom : b.Right, vertical ? remote.Bottom : remote.Right);
        var seams = new List<MonitorSeam> { new(start, end) };
        // Never take input away at a boundary between two Windows monitors.
        foreach (var other in monitors.Where(m => m.Id != local.Id))
        {
            var o = other.Bounds;
            var blocks = settings.Side switch
            {
                PeerSide.Right => o.Left <= b.Right + 1 && o.Right > b.Right,
                PeerSide.Left => o.Right >= b.Left - 1 && o.Left < b.Left,
                PeerSide.Above => o.Bottom >= b.Top - 1 && o.Top < b.Top,
                _ => o.Top <= b.Bottom + 1 && o.Bottom > b.Bottom
            };
            if (!blocks) continue;
            seams = seams.SelectMany(s => Subtract(s, vertical ? o.Top : o.Left, vertical ? o.Bottom : o.Right)).ToList();
        }
        seams = seams.Where(s => s.End - s.Start >= 1).ToList();
        if (seams.Count == 0 && !allowBlockedPreview) throw new InvalidDataException("This edge is covered by another Windows monitor. Choose an outside edge.");
        return new(monitors, local, new MonitorDescriptor("desktop", "Other computer", remote), settings.Side, seams);
    }
    private static IEnumerable<MonitorSeam> Subtract(MonitorSeam seam, int low, int high)
    {
        if (high < seam.Start || low > seam.End) { yield return seam; yield break; }
        if (low > seam.Start) yield return new(seam.Start, Math.Min(seam.End, low - 1));
        if (high < seam.End) yield return new(Math.Max(seam.Start, high + 1), seam.End);
    }
    public static void ValidateSettings(MonitorLayoutSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.MonitorId) || settings.MonitorId.Length > 256
            || !Enum.IsDefined(settings.Side) || settings.Alignment is < -100 or > 100)
            throw new InvalidDataException("Invalid saved monitor layout.");
    }
}

internal sealed class MonitorLayoutStore
{
    private readonly string _path;
    private readonly Dictionary<string, MonitorLayoutSettings> _layouts;
    public MonitorLayoutStore(string path)
    {
        _path = path;
        if (!File.Exists(path)) { _layouts = new(StringComparer.Ordinal); return; }
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Saved layout file is too large.");
        _layouts = JsonSerializer.Deserialize<Dictionary<string, MonitorLayoutSettings>>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Saved layouts are invalid.");
        if (_layouts.Count > 64) throw new InvalidDataException("Too many saved layouts.");
        foreach (var layout in _layouts)
        {
            if (string.IsNullOrWhiteSpace(layout.Key) || layout.Key.Length > 256 || layout.Value is null) throw new InvalidDataException("Invalid layout identity.");
            MonitorLayout.ValidateSettings(layout.Value);
        }
    }
    public MonitorLayoutSettings? Get(string peerId) => _layouts.GetValueOrDefault(peerId);
    public void Save(string peerId, MonitorLayoutSettings settings)
    {
        MonitorLayout.ValidateSettings(settings);
        if (string.IsNullOrWhiteSpace(peerId) || peerId.Length > 256) throw new InvalidDataException("Invalid peer identity.");
        var next = new Dictionary<string, MonitorLayoutSettings>(_layouts, StringComparer.Ordinal) { [peerId] = settings };
        if (next.Count > 64) throw new InvalidDataException("Too many saved layouts.");
        File.WriteAllText(_path + ".new", JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(_path + ".new", _path, overwrite: true);
        _layouts[peerId] = settings;
    }
    public void Remove(string peerId)
    {
        if (!_layouts.ContainsKey(peerId)) return;
        var next = new Dictionary<string, MonitorLayoutSettings>(_layouts, StringComparer.Ordinal);
        next.Remove(peerId);
        File.WriteAllText(_path + ".new", JsonSerializer.Serialize(next));
        File.Move(_path + ".new", _path, overwrite: true);
        _layouts.Remove(peerId);
    }
}
