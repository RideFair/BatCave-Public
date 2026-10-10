namespace BatCave.WindowsAgent;

internal readonly record struct HeldKey(ushort VirtualKey, ushort ScanCode, bool Extended);

internal sealed class HeldKeyState
{
    private readonly Dictionary<ushort, HeldKey> _keys = new();

    public void Update(HeldKey key, bool keyUp)
    {
        if (keyUp) { _keys.Remove(key.VirtualKey); }
        else { _keys[key.VirtualKey] = key; }
    }

    public HeldKey[] Snapshot() => _keys.Values.ToArray();

    public HeldKey[] ReleaseAll()
    {
        var keys = Snapshot();
        _keys.Clear();
        return keys;
    }

    public static bool IsModifier(ushort key) => key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C
        or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;
}
