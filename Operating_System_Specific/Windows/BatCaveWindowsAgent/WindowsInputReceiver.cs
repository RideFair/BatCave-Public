using System.Text;
using System.Text.Json;

namespace BatCave.WindowsAgent;

internal interface IWindowsInputSink
{
    void Move(int x, int y);
    void Key(HeldKey key, bool up);
    void Text(string text);
    void Button(string button, bool down);
    void Wheel(int vertical, int horizontal);
}

// Owned by the UI thread. The transport and UI dispatcher independently bind
// each message to the approved socket and the current local input activation.
internal sealed class WindowsInputReceiver(string localId, IWindowsInputSink sink)
{
    private readonly Dictionary<ushort, HeldKey> _keys = [];
    private readonly HashSet<ushort> _textKeys = [];
    private readonly HashSet<string> _buttons = [];
    private string? _peer;
    private long _session;
    private VirtualDesktopBounds _bounds;
    private int _x, _y;
    public bool Enabled { get; private set; }
    public bool OwnsCursor { get; private set; }

    public void Enable(string peer, long session, VirtualDesktopBounds bounds)
    {
        Pause();
        if (string.IsNullOrWhiteSpace(peer) || peer == localId || bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidOperationException("A different connected computer and valid screen are required.");
        _peer = peer; _session = session; _bounds = bounds; Enabled = true;
    }

    public void Pause()
    {
        Enabled = false; OwnsCursor = false; _peer = null;
        ReleaseHeld();
    }

    public void Handle(string peer, long session, string type, JsonElement payload)
    {
        if (!Enabled || peer != _peer || session != _session) return;
        try
        {
            if (type == "control.testStop") { Pause(); return; }
            if (type == "cursor.handoff")
            {
                // Return crossings also arrive here. Always release previous
                // ownership before deciding whether this handoff targets us.
                OwnsCursor = false; ReleaseHeld();
                var target = Label(payload, "targetDeviceId", 128);
                if (target != localId) return;
                if (Label(payload, "targetDisplayId", 256) != localId + ":desktop")
                    throw new InvalidDataException("Unknown receiving display.");
                var edge = Label(payload, "targetEdge", 8);
                if (edge is not ("left" or "right" or "top" or "bottom")) throw new InvalidDataException("Invalid handoff edge.");
                if (!payload.TryGetProperty("targetPosition", out var position) || !position.TryGetDouble(out var p)
                    || !double.IsFinite(p) || p < 0 || p > 1) throw new InvalidDataException("Invalid handoff position.");
                // X/Y describe the sender's arranged topology, not this PC's
                // native desktop. Project the normalized edge onto our bounds.
                _ = Integer(payload, "targetX", -2_000_000, 2_000_000);
                _ = Integer(payload, "targetY", -2_000_000, 2_000_000);
                _x = edge switch { "left" => _bounds.Left, "right" => _bounds.Right,
                    _ => _bounds.Left + (int)Math.Round(p * (_bounds.Width - 1)) };
                _y = edge switch { "top" => _bounds.Top, "bottom" => _bounds.Bottom,
                    _ => _bounds.Top + (int)Math.Round(p * (_bounds.Height - 1)) };
                sink.Move(_x, _y); OwnsCursor = true; return;
            }
            if (!OwnsCursor) return;
            switch (type)
            {
                case "input.mouseDelta":
                    _x = (int)Math.Clamp((long)_x + Integer(payload, "deltaX", -1_000_000, 1_000_000), _bounds.Left, _bounds.Right);
                    _y = (int)Math.Clamp((long)_y + Integer(payload, "deltaY", -1_000_000, 1_000_000), _bounds.Top, _bounds.Bottom);
                    sink.Move(_x, _y); break;
                case "input.mouseButton":
                    var button = Label(payload, "button", 8);
                    if (button == "center") button = "middle";
                    if (button is not ("left" or "right" or "middle" or "x1" or "x2")) throw new InvalidDataException("Invalid mouse button.");
                    var down = Boolean(payload, "isDown");
                    if (down ? _buttons.Add(button) : _buttons.Remove(button)) sink.Button(button, down);
                    break;
                case "input.mouseWheel":
                    sink.Wheel(Integer(payload, "verticalDelta", -1_000_000, 1_000_000), Integer(payload, "horizontalDelta", -1_000_000, 1_000_000)); break;
                case "input.key": HandleKey(payload); break;
                case "input.shortcut":
                    var key = Label(payload, "action", 16) switch
                    {
                        "copy" => (ushort)'C', "paste" => (ushort)'V', "cut" => (ushort)'X', "selectAll" => (ushort)'A',
                        _ => throw new InvalidDataException("Unknown shortcut.")
                    };
                    // A shortcut must not release a modifier that is already held.
                    var controlHeld = _keys.Keys.Any(k => k is 0x11 or 0xA2 or 0xA3);
                    var letterHeld = _keys.ContainsKey(key);
                    var control = new HeldKey(0xA2, 0, false); var letter = new HeldKey(key, 0, false);
                    if (!controlHeld) { _keys[control.VirtualKey] = control; sink.Key(control, false); }
                    if (!letterHeld) { _keys[key] = letter; sink.Key(letter, false); sink.Key(letter, true); _keys.Remove(key); }
                    if (!controlHeld) { sink.Key(control, true); _keys.Remove(control.VirtualKey); }
                    break;
            }
        }
        catch { Pause(); throw; }
    }

    private void HandleKey(JsonElement payload)
    {
        var vk = (ushort)Integer(payload, "virtualKey", 1, 255);
        var scan = (ushort)Integer(payload, "scanCode", 0, 255);
        var up = Boolean(payload, "isKeyUp"); var extended = Boolean(payload, "isExtended");
        string? text = null;
        if (payload.TryGetProperty("text", out var value) && value.ValueKind != JsonValueKind.Null)
        {
            if (value.ValueKind != JsonValueKind.String || Encoding.UTF8.GetByteCount(value.GetString()!) > 8192)
                throw new InvalidDataException("Invalid key text.");
            text = value.GetString();
        }
        if (up)
        {
            if (_textKeys.Remove(vk)) return;
            if (_keys.Remove(vk, out var held)) sink.Key(held, true);
            return;
        }
        if (_textKeys.Contains(vk)) { if (!string.IsNullOrEmpty(text)) sink.Text(text); return; }
        if (!_keys.ContainsKey(vk) && !string.IsNullOrEmpty(text) && !HeldKeyState.IsModifier(vk)
            && !_keys.Keys.Any(k => k is 0x11 or 0x12 or 0x5B or 0x5C or 0xA2 or 0xA3 or 0xA4 or 0xA5))
        { _textKeys.Add(vk); sink.Text(text); return; }
        var key = _keys.TryGetValue(vk, out var existing) ? existing : new HeldKey(vk, scan, extended);
        _keys[vk] = key; sink.Key(key, false);
    }

    private void ReleaseHeld()
    {
        Exception? failure = null;
        foreach (var key in _keys.Values.ToArray()) { try { sink.Key(key, true); } catch (Exception ex) { failure ??= ex; } }
        foreach (var button in _buttons.ToArray()) { try { sink.Button(button, false); } catch (Exception ex) { failure ??= ex; } }
        _keys.Clear(); _textKeys.Clear(); _buttons.Clear();
        if (failure is not null) throw new InvalidOperationException("Windows could not release remote input.", failure);
    }

    private static int Integer(JsonElement obj, string name, int min, int max)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var result) || result < min || result > max) throw new InvalidDataException("Invalid input field: " + name);
        return result;
    }
    private static bool Boolean(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("Invalid input field: " + name);
        return value.GetBoolean();
    }
    private static string Label(JsonElement obj, string name, int max)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Missing input field: " + name);
        var result = value.GetString()!;
        if (result.Length == 0 || result.Length > max || result.Any(char.IsControl)) throw new InvalidDataException("Invalid input field: " + name);
        return result;
    }
}
