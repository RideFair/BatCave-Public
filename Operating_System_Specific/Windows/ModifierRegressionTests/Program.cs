using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BatCave.WindowsAgent;

internal static class ModifierRegression
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        CheckSetupActions();
        Run().GetAwaiter().GetResult();
        if (args.Length == 2 && args[0] == "--render-preview") { RenderPreview(args[1]); }
        if (args.Length == 2 && args[0] == "--render-setup") { RenderSetup(args[1]); }
    }

    private static void CheckSetupActions()
    {
        for (var bits = 0; bits < 32; bits++)
        {
            var state = new DesktopSetupState((bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0, (bits & 8) != 0, (bits & 16) != 0);
            for (var step = 0; step < 4; step++)
            {
                var action = DesktopSetupFlow.Action(step, state);
                if (action.Operation == SetupOperation.Start)
                    Require(step == 3 && state.ListenerReady && state.PeerSelected && state.Connected && state.LayoutValid && !state.Sharing, "setup starts only a ready approved connection after explicit final-page action");
                if (step < 3) Require(action.Operation is not SetupOperation.Start, "navigation never starts sharing");
            }
        }
        Require(DesktopSetupFlow.Action(3, new(true, true, false, true, false)).Enabled == false, "lost connection cannot start through saved setup");
        Require(DesktopSetupFlow.Action(3, new(true, true, true, true, false)).Label == "Start sharing", "fresh reconnection needs an explicit start");
        Require(DesktopSetupFlow.Action(3, new(true, true, true, true, true)).Operation == SetupOperation.Pause, "active sharing offers pause");
        Console.WriteLine("PASS: device-linking navigation preserves explicit input activation.");
    }

    private static void RenderSetup(string output)
    {
        Directory.CreateDirectory(output);
        var directory = Path.Combine(Path.GetTempPath(), "BatCaveSetupSmoke-" + Guid.NewGuid().ToString("N"));
        using var identity = DeviceIdentityStore.OpenOrCreate(directory);
        try
        {
            var approvals = new ApprovedPeerStore(directory);
            using var window = new DesktopWindow(identity, approvals, directory, inspectionOnly: true);
            window.StartPosition = FormStartPosition.Manual;
            window.Location = new System.Drawing.Point(-30000, -30000);
            window.Show();
            foreach (var small in new[] { false, true })
            {
                if (small) window.Size = window.MinimumSize;
                for (var step = 0; step < 4; step++)
                {
                    window.ShowStep(step); Application.DoEvents();
                    using var bitmap = new System.Drawing.Bitmap(window.Width, window.Height);
                    window.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height));
                    bitmap.Save(Path.Combine(output, $"step-{step + 1}{(small ? "-small" : "")}.png"), System.Drawing.Imaging.ImageFormat.Png);
                    Require(!((LocalBridgeAgent)Get(window, "_agent")).IsHandleCreated, "setup navigation creates no input hooks in inspection mode");
                    Require(!approvals.Peers.Any(), "navigating setup never approves a peer");
                }
            }
            Call(window, "Quit");
            Console.WriteLine("PASS: all four native setup screens rendered at default and minimum sizes without input hooks or a listener.");
        }
        finally { identity.DeleteIdentity(directory); Directory.Delete(directory); }
    }

    private static void RenderPreview(string path)
    {
        var directory = Path.Combine(Path.GetTempPath(), "BatCaveDesktopSmoke-" + Guid.NewGuid().ToString("N"));
        using var identity = DeviceIdentityStore.OpenOrCreate(directory);
        try
        {
            var approvals = new ApprovedPeerStore(directory);
            using var window = new DesktopWindow(identity, approvals, directory, inspectionOnly: true);
            window.StartPosition = FormStartPosition.Manual;
            window.Location = new System.Drawing.Point(-30000, -30000);
            window.Show();
            Application.DoEvents();
            Require(!((LocalBridgeAgent)Get(window, "_agent")).IsHandleCreated, "inspection renderer installs no input hooks");
            using var bitmap = new System.Drawing.Bitmap(window.Width, window.Height);
            window.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height));
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Call(window, "Quit");
            Require(!approvals.Peers.Any(), "UI does not approve any peer on first run");
            Console.WriteLine("PASS: first-run desktop UI rendered and exited with no approved peer.");
        }
        finally
        {
            identity.DeleteIdentity(directory);
            Directory.Delete(directory);
        }
    }

    private static async Task Run()
    {
        // No Form handle is created: no hooks are installed and no real input is
        // suppressed or injected. Exercise the actual forwarding methods on wire.
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var lan = new LanBridgePeer();
        using var agent = new LocalBridgeAgent(lan);
        // Constructing a Form installs a WinForms context; this headless test has
        // no UI message loop, so network continuations use the thread pool.
        SynchronizationContext.SetSynchronizationContext(null);
        Set(agent, "_activeTarget", Enum.Parse(typeof(LocalBridgeAgent).GetNestedType("ActiveTarget", BindingFlags.NonPublic)!, "Remote"));
        var physical = (HeldKeyState)Get(agent, "_physicalKeys");
        await lan.StartListeningAsync(port);
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, port);
        using var reader = new StreamReader(socket.GetStream(), Encoding.UTF8, false, leaveOpen: true);
        using var writer = new StreamWriter(socket.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
        await reader.ReadLineAsync();
        await writer.WriteLineAsync("{\"protocol\":\"batcave.input.v0\",\"type\":\"device.hello\",\"sourceDeviceId\":\"macos:test\",\"payload\":{\"deviceId\":\"macos:test\"}}");
        for (var i = 0; i < 500 && !lan.HasPeer; i++) { await Task.Delay(10); }
        Require(lan.HasPeer, "test peer connected");

        Key(0xA2, false); // left Control
        Key(0x5B, false); // left Windows / Command
        Mouse(1); // left button down
        await ExpectKey(0xA2, false);
        await ExpectKey(0x5B, false);
        await ExpectType("input.mouseButton");
        Key(0xA2, true);
        await ExpectKey(0xA2, true);
        Key(0x5B, true);
        await ExpectKey(0x5B, true);
        Mouse(2);
        await ExpectType("input.mouseButton");

        // A modifier held before crossing is flushed before the restored drag.
        Key(0xA2, false);
        Call(agent, "UpdatePhysicalMouseButtons", (ushort)1);
        Call(agent, "PressPhysicalMouseButtonsRemotely", "test handoff");
        await ExpectKey(0xA2, false);
        await ExpectType("input.mouseButton");
        // Copy after a mouse action must release native Control before the Mac
        // performs its semantic Command-C, then restore the hold on next mouse use.
        Key((ushort)'C', false);
        await ExpectKey(0xA2, true);
        await ExpectType("input.shortcut");
        Key((ushort)'C', true); // consumed key-up, no unmatched raw release
        Mouse(2);
        await ExpectKey(0xA2, false);
        await ExpectType("input.mouseButton");
        Key(0xA2, true);
        await ExpectKey(0xA2, true);
        Require(((HeldKeyState)Get(agent, "_remoteKeys")).Snapshot().Length == 0, "all forwarded modifiers released");
        await lan.FlushAsync();
        Console.WriteLine("PASS: two held shortcut modifiers precede click, independent releases, modifier-before-handoff drag, semantic copy after clicking, consumed key-up and restored physical hold.");

        void Key(ushort vk, bool up)
        {
            physical.Update(new HeldKey(vk, 0, false), up);
            var consumed = (bool)Call(agent, "TryHandleRemoteShortcutChord", vk, (ushort)0, up, false)!;
            if (!consumed) { Call(agent, "PublishRemoteKey", vk, (ushort)0, up, false, null); }
        }
        void Mouse(ushort flags)
        {
            var type = typeof(LocalBridgeAgent).GetMethod("ReplayOrForwardMouseButtonsAndWheel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetParameters()[0].ParameterType;
            var buttons = Activator.CreateInstance(type)!;
            type.GetField("usButtonFlags")!.SetValue(buttons, flags);
            Call(agent, "ReplayOrForwardMouseButtonsAndWheel", buttons);
        }
        async Task<JsonElement> ExpectType(string type)
        {
            var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using var doc = JsonDocument.Parse(line ?? throw new Exception("peer closed"));
            Require(doc.RootElement.GetProperty("type").GetString() == type, "expected wire event " + type);
            return doc.RootElement.GetProperty("payload").Clone();
        }
        async Task ExpectKey(int key, bool up)
        {
            var payload = await ExpectType("input.key");
            Require(payload.GetProperty("virtualKey").GetInt32() == key && payload.GetProperty("isKeyUp").GetBoolean() == up,
                "correct modifier and transition order");
        }
    }
    private static object Get(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void Require(bool condition, string message) { if (!condition) { throw new Exception(message); } }
}
