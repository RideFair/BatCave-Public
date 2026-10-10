namespace BatCave.WindowsAgent;

internal static class BatCaveProtocol
{
    public const string Name = "batcave.input.v0";
}

internal sealed record BatCaveEnvelope(
    string Protocol,
    string Type,
    string MessageId,
    string SourceDeviceId,
    DateTimeOffset SentAtUtc,
    object Payload)
{
    public static BatCaveEnvelope Create(string sourceDeviceId, string type, object payload)
    {
        return new BatCaveEnvelope(
            BatCaveProtocol.Name,
            type,
            Guid.NewGuid().ToString("N"),
            sourceDeviceId,
            DateTimeOffset.UtcNow,
            payload);
    }
}

internal sealed record ScreenBounds(int Left, int Top, int Width, int Height, int Right, int Bottom)
{
    public static ScreenBounds From(VirtualDesktopBounds bounds)
    {
        return new ScreenBounds(bounds.Left, bounds.Top, bounds.Width, bounds.Height, bounds.Right, bounds.Bottom);
    }
}

internal sealed record DeviceHelloPayload(
    string DeviceId,
    string DeviceName,
    string OperatingSystem,
    string AgentName,
    ScreenBounds VirtualDesktopBounds);

internal sealed record EdgeHandoffPayload(
    string Edge,
    int VirtualX,
    int VirtualY,
    int DeltaX,
    int DeltaY,
    ScreenBounds LocalBounds);

internal sealed record CursorHandoffPayload(
    string LinkId,
    double SegmentPosition,
    string SourceDisplayId,
    string SourceEdge,
    double SourcePosition,
    int SourceX,
    int SourceY,
    string TargetDeviceId,
    string TargetDisplayId,
    string TargetEdge,
    double TargetPosition,
    int TargetX,
    int TargetY);

internal sealed record MouseDeltaPayload(int DeltaX, int DeltaY);

internal sealed record MouseButtonPayload(string Button, bool IsDown);

internal sealed record MouseWheelPayload(int VerticalDelta, int HorizontalDelta);

internal sealed record KeyInputPayload(
    ushort VirtualKey,
    ushort ScanCode,
    bool IsKeyUp,
    bool IsExtended,
    string? Text);

internal sealed record ShortcutInputPayload(string Action);

internal sealed record ClipboardTextPayload(string Format, string Text);

internal sealed record ControlTestStartPayload(int DurationSeconds);

internal sealed record ControlTestStopPayload(string Reason);
