using System.Text.Json;
using BatCave.WindowsAgent;

internal static partial class ModifierRegression
{
    private sealed class RecordingInput : IWindowsInputSink
    {
        public readonly List<string> Events = [];
        public void Move(int x, int y) => Events.Add($"move:{x},{y}");
        public void Key(HeldKey key, bool up) => Events.Add($"key:{key.VirtualKey}:{up}");
        public void Text(string text) => Events.Add("text:" + text);
        public void Button(string button, bool down) => Events.Add($"button:{button}:{down}");
        public void Wheel(int vertical, int horizontal) => Events.Add($"wheel:{vertical},{horizontal}");
    }
    private static JsonElement Payload(object data) => JsonSerializer.SerializeToElement(data);
    private static JsonElement Handoff(string target, int x = 0, int y = 100) => Payload(new
    {
        targetDeviceId = target, targetDisplayId = target + ":desktop", targetEdge = "left",
        targetPosition = 0.5, targetX = x, targetY = y
    });
    private static JsonElement Key(ushort vk, bool up = false, string? text = null) => Payload(new
    { virtualKey = vk, scanCode = 0, isKeyUp = up, isExtended = false, text });

    private static void CheckReceivingInput()
    {
        var sink = new RecordingInput(); var receiver = new WindowsInputReceiver("windows:receiver", sink);
        void Send(string type, JsonElement data) => receiver.Handle("windows:sender", 7, type, data);
        void Enable() => receiver.Enable("windows:sender", 7, new(0, 0, 1920, 1080));
        Send("cursor.handoff", Handoff("windows:receiver")); Send("input.key", Key(65));
        Send("input.shortcut", Payload(new { action = "paste" }));
        Require(sink.Events.Count == 0, "network traffic cannot enable receiving");
        Enable(); Send("input.key", Key(65));
        receiver.Handle("windows:sender", 6, "cursor.handoff", Handoff("windows:receiver"));
        receiver.Handle("macos:other", 7, "cursor.handoff", Handoff("windows:receiver"));
        Require(sink.Events.Count == 0, "input needs ownership from the current authenticated session");
        Send("cursor.handoff", Handoff("windows:receiver"));
        Send("input.key", Key(0xA2)); Send("input.mouseButton", Payload(new { button = "left", isDown = true }));
        Send("input.shortcut", Payload(new { action = "copy" }));
        Require(!sink.Events.Contains("key:162:True"), "semantic shortcut preserves a held Control key");
        Send("cursor.handoff", Handoff("windows:sender"));
        Require(receiver.Enabled && !receiver.OwnsCursor && sink.Events.Contains("key:162:True") && sink.Events.Contains("button:left:False"),
            "return handoff releases held input without revoking local enable");
        var returned = sink.Events.Count; Send("input.key", Key(66));
        Require(sink.Events.Count == returned, "input after returning ownership is ignored");
        Send("cursor.handoff", Handoff("windows:receiver"));
        Send("input.key", Key(65, text: "é")); Send("input.key", Key(65, text: "é")); Send("input.key", Key(65, up: true));
        Require(sink.Events.Count(e => e == "text:é") == 2 && !sink.Events.Any(e => e.StartsWith("key:65:")), "Unicode repeats have no unmatched physical key-up");
        Send("input.key", Key(0x5B)); Send("input.mouseButton", Payload(new { button = "x1", isDown = true }));
        Send("input.mouseDelta", Payload(new { deltaX = 50, deltaY = 10 }));
        Send("input.mouseWheel", Payload(new { verticalDelta = 120, horizontalDelta = -120 }));
        receiver.Pause();
        Require(!receiver.Enabled && sink.Events.Contains("key:91:True") && sink.Events.Contains("button:x1:False"), "pause releases Windows keys and extra mouse buttons");
        var paused = sink.Events.Count; Send("cursor.handoff", Handoff("windows:receiver"));
        Require(sink.Events.Count == paused, "remote handoff cannot resume paused input");
        Enable(); Send("cursor.handoff", Handoff("windows:receiver")); Send("input.key", Key(0xA0));
        try { Send("input.mouseButton", Payload(new { button = "invalid", isDown = true })); throw new Exception("invalid button accepted"); }
        catch (InvalidDataException) { }
        Require(!receiver.Enabled && sink.Events.Contains("key:160:True"), "invalid input fails closed and releases held state");
        Enable();
        Send("cursor.handoff", Handoff("windows:receiver", 3000));
        Require(receiver.OwnsCursor && sink.Events.Last() == "move:0,540", "sender topology coordinates are projected onto the receiver's native screen");
        try { Send("cursor.handoff", Handoff("windows:receiver", int.MaxValue)); throw new Exception("unbounded handoff accepted"); }
        catch (InvalidDataException) { }
        Require(!receiver.Enabled, "unbounded handoff fails closed");
        Enable(); Send("cursor.handoff", Handoff("windows:receiver")); Send("input.key", Key(0xA4));
        Send("control.testStop", Payload(new { reason = "user paused" }));
        Require(!receiver.Enabled && sink.Events.Contains("key:164:True"), "peer stop releases and disarms receiver");
        Console.WriteLine("PASS: Windows receiving requires local enable and session-bound ownership; returns, Unicode, modifiers, invalid input and stop balance held state.");
    }

    private static void CheckQueuedInput()
    {
        WithPairingFixture((window, _, first, _) =>
        {
            var lan = (LanBridgePeer)Get(window, "_lan"); var sink = new RecordingInput();
            Set(lan, "_writer", new StreamWriter(Stream.Null)); Set(lan, "_peerDeviceId", first.DeviceId);
            using var agent = new LocalBridgeAgent(lan, inspectionOnly: true, inputSink: sink);
            _ = agent.Handle;
            Set(agent, "_bridgeActive", true);
            Call(agent, "HandleRemoteShortcutInput", first.DeviceId, new ShortcutInputPayload("paste"));
            Require((int)Get(agent, "_pendingRemoteActions") == 0, "secure source role cannot use the legacy shortcut path to inject on this PC");
            Set(agent, "_bridgeActive", false);
            var receiver = (WindowsInputReceiver)Get(agent, "_receiver");
            void Enable() { agent.EnableReceiving(); receiver.Enable(first.DeviceId, lan.SessionGeneration, new(0, 0, 1920, 1080)); }
            void Queue(string type, JsonElement data) => Call(agent, "HandleRemoteInputMessage", first.DeviceId, lan.SessionGeneration, type, data);
            Queue("cursor.handoff", Handoff(lan.LocalDeviceId)); Enable(); Application.DoEvents();
            Require(sink.Events.Count == 0, "input queued before enabling cannot gain ownership afterward");
            Queue("cursor.handoff", Handoff(lan.LocalDeviceId)); Queue("input.key", Key(0xA2)); Application.DoEvents();
            Require(sink.Events.Contains("key:162:False"), "current queued input reaches the receiver");
            Queue("input.key", Key(65)); agent.StopSharingNow("test pause"); Enable(); Application.DoEvents();
            Require(!sink.Events.Any(e => e.StartsWith("key:65:")), "pause and re-enable discard old queued input");
            Require(sink.Events.Contains("key:162:True"), "pause releases held input before re-enable");
            for (var i = 0; i < 514; i++) Queue("input.mouseDelta", Payload(new { deltaX = 1, deltaY = 0 }));
            Require((int)Get(agent, "_pendingRemoteActions") <= 512, "UI queue has a bounded count");
            Application.DoEvents(); Require(!agent.IsReceivingEnabled, "queue overflow stops input instead of replaying stale events");
            var original = Console.Out; using var log = new StringWriter();
            try { Console.SetOut(log); Call(agent, "PublishRemoteKey", (ushort)65, (ushort)30, false, false, "private typing fixture"); }
            finally { Console.SetOut(original); }
            Require(!log.ToString().Contains("private typing fixture") && !log.ToString().Contains("vk="), "ordinary forwarding does not log typed input or key codes");
            Require((IntPtr)Get(agent, "_mouseHook") == IntPtr.Zero && (IntPtr)Get(agent, "_keyboardHook") == IntPtr.Zero,
                "queue inspection never installs hooks or injects real input");
            var physical = (HeldKeyState)Get(agent, "_physicalKeys");
            physical.Update(new HeldKey(0xA2, 29, false), false);
            physical.Update(new HeldKey(0x5B, 91, true), false);
            Call(agent, "RestorePhysicalModifiers", true);
            var remote = ((HeldKeyState)Get(agent, "_remoteKeys")).Snapshot();
            Require(remote.Any(k => k.VirtualKey == 0xA2) && remote.Any(k => k.VirtualKey == 0x5B)
                && ((HeldKeyState)Get(agent, "_pendingRemoteModifiers")).Snapshot().Length == 0,
                "Windows handoff restores held Control and Windows keys without semantic buffering");
            Call(agent, "RestorePhysicalModifiers", false);
            Require(sink.Events.Contains("key:91:False"), "return to this PC restores the native Windows key");
            Call(agent, "ReleaseLocalKeys");
            Require(sink.Events.Contains("key:91:True"), "local restoration has a balanced release");
            ((HashSet<ushort>)Get(agent, "_localTextKeys")).Add(65);
            var beforeUp = sink.Events.Count;
            Call(agent, "ReplayKeyboardHookEvent", (ushort)65, (ushort)30, true, false);
            Require(sink.Events.Count == beforeUp, "local Unicode text never emits an unmatched physical release");
            agent.Close();
        });
        Console.WriteLine("PASS: actual UI dispatch rejects input queued before enable or pause; overflow stops input, and typing is absent from logs.");
    }

    private static void CheckPlatformRoles()
    {
        WithPairingFixture((window, _, first, second) =>
        {
            window.ShowStep(1); Application.DoEvents();
            var platforms = (ComboBox)Get(window, "_platform");
            Require(platforms.Items.Cast<string>().SequenceEqual(new[] { "Windows", "Mac", "Linux" }), "all three other-computer platforms are offered");
            for (var i = 0; i < 3; i++)
            {
                platforms.SelectedIndex = i; Application.DoEvents();
                Require(((Label)Get(window, "_sendInstructions")).Text.Contains(i == 0 ? "Windows PC" : i == 1 ? "Mac" : "Linux"), "platform choice changes the instructions");
            }
            Call(window, "ApplyApproval", first); Call(window, "UpdateSetup");
            Require(platforms.SelectedIndex == 0, "a Windows identity selects Windows even if another platform was picked before import");
            var role = (ComboBox)Get(window, "_role"); role.SelectedIndex = 1;
            ((TextBox)Get(window, "_host")).Text = "192.0.2.20"; Call(window, "SaveSettings");
            Call(window, "ApplyApproval", second);
            Require(role.SelectedIndex == 0 && ((TextBox)Get(window, "_host")).Text == "", "new computer does not inherit another computer's role or address");
            Call(window, "ApplyApproval", first);
            Require(role.SelectedIndex == 1 && ((TextBox)Get(window, "_host")).Text == "192.0.2.20", "returning to a computer restores its own connection settings");
            Require(!((LocalBridgeAgent)Get(window, "_agent")).IsReceivingEnabled, "restored receiver role never enables input");
            Call(window, "ReleaseAndDisconnect", "sleep fixture");
            Require((bool)Get(window, "_reconnectSuspended") && !(bool)Get(window, "_connectWanted"), "sleep suspends outgoing retry");
            Call(window, "ResumeConnections");
            Require(!(bool)Get(window, "_reconnectSuspended") && (bool)Get(window, "_connectWanted")
                && !((LocalBridgeAgent)Get(window, "_agent")).IsReceivingEnabled, "wake reconnects the saved computer without enabling input");
            var state = new DesktopSetupState(true, true, true, false, false, ReceiveInput: true);
            Require(DesktopSetupFlow.Action(2, state).Operation == SetupOperation.Next, "receiver does not need to configure source monitor edges");
            Require(DesktopSetupFlow.Action(3, state).Label == "Enable input on this PC", "receiver requires an explicit local enable action");
        });
        Console.WriteLine("PASS: Windows/Mac/Linux choices, per-computer roles and addresses, receiver navigation, and explicit activation.");
    }
}
