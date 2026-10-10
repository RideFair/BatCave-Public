namespace BatCave.WindowsAgent;

internal readonly record struct VirtualDesktopBounds(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Math.Max(0, Width - 1);
    public int Bottom => Top + Math.Max(0, Height - 1);

    public override string ToString()
    {
        return $"left={Left}, top={Top}, width={Width}, height={Height}, right={Right}, bottom={Bottom}";
    }
}
