namespace BatCave.WindowsAgent;

internal enum SetupOperation { None, Next, Pair, Start, Pause }
internal sealed record SetupAction(string Label, SetupOperation Operation, bool Enabled = true);
internal sealed record DesktopSetupState(bool ListenerReady, bool PeerSelected, bool Connected, bool LayoutValid, bool Sharing);

internal static class DesktopSetupFlow
{
    public static readonly string[] Titles = ["Permissions", "Pair computers", "Arrange screens", "Link devices"];

    // Navigation only chooses a user action. It never approves a peer or starts input.
    public static SetupAction Action(int step, DesktopSetupState state)
    {
        if (step < 0 || step >= Titles.Length) throw new ArgumentOutOfRangeException(nameof(step));
        if (step == 0) return new("Continue", SetupOperation.Next, state.ListenerReady);
        if (step == 1) return state.PeerSelected ? new("Continue", SetupOperation.Next) : new("Import device card", SetupOperation.Pair);
        if (!state.PeerSelected) return new("Pair computers", SetupOperation.Pair);
        if (!state.ListenerReady || !state.Connected) return new("Waiting for other device…", SetupOperation.None, false);
        if (!state.LayoutValid) return new("Choose an outside edge", SetupOperation.None, false);
        if (step == 2) return new("Link devices", SetupOperation.Next);
        return state.Sharing ? new("Pause sharing", SetupOperation.Pause) : new("Start sharing", SetupOperation.Start);
    }
}
