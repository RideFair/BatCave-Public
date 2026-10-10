using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BatCave.WindowsAgent;

internal static partial class ModifierRegression
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        CheckSetupActions();
        Run().GetAwaiter().GetResult();
        CheckPairingUi();
        CheckWindowShutdown();
        CheckReceivingInput();
        CheckQueuedInput();
        CheckPlatformRoles();
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
        Require(DesktopSetupFlow.Action(1, new(true, true, false, true, false, true, true)).Operation == SetupOperation.Pair, "adding a second computer imports rather than skipping the guide");
        Require(DesktopSetupFlow.Action(1, new(true, false, false, false, false)).Operation == SetupOperation.Export, "first-computer setup begins with the PC file");
        Require(DesktopSetupFlow.Status(new(true, false, false, false, false)).StartsWith("No computer added"), "empty setup never claims to be ready");
        Console.WriteLine("PASS: device-linking navigation preserves explicit input activation.");
    }

    private static void CheckPairingUi()
    {
        WithPairingFixture((window, approvals, first, second) =>
        {
            window.ShowStep(1); Application.DoEvents();
            Require(!((ComboBox)Get(window, "_peers")).Visible, "empty peer dropdown is hidden");
            Require(!((Control)Get(window, "_removePeer")).Visible, "empty state has no removal action");
            Require(((Control)Get(window, "_fileGuide")).Visible, "first-computer file exchange guide is visible");
            Require(((Button)Get(window, "_start")).Text == "Save Windows file…", "empty setup begins with the PC file");
            ((Button)Get(window, "_skipExport")).PerformClick(); Application.DoEvents();
            Require(((Button)Get(window, "_start")).Text == "Add computer…" && ((Control)Get(window, "_receiveInstructions")).Visible,
                "existing file advances to the receiving instruction and import action");
            Require(((Control)Get(window, "_receiveTitle")).Top < ((Control)Get(window, "_receiveInstructions")).Top,
                "receiving instruction heading remains before its body after changing stages");
            Require(!approvals.Peers.Any(), "navigation never approves a computer");
            Call(window, "ApplyApproval", first); Application.DoEvents();
            Require((string)Get(window, "_selectedPeerId") == first.DeviceId, "first approved computer is selected");
            Require(!((ComboBox)Get(window, "_peers")).Visible, "single computer needs no selector");
            Require(((Control)Get(window, "_connectPanel")).Visible, "approval reveals the address and receiving-device connection instructions");
            Call(window, "ApplyApproval", second); Application.DoEvents();
            Require((string)Get(window, "_selectedPeerId") == second.DeviceId, "new approval replaces old selection");
            Require(((ComboBox)Get(window, "_peers")).Visible, "multiple approved computers have a selector");
            Require(Get(window, "_hello") is null, "new approval clears old screen geometry");
            Call(window, "Unpair"); Application.DoEvents();
            Require((string)Get(window, "_selectedPeerId") == first.DeviceId && approvals.Peers.Length == 1, "removing one selects the remaining approved computer");
            Call(window, "Unpair"); Application.DoEvents();
            Require(((Control)Get(window, "_fileGuide")).Visible && !((ComboBox)Get(window, "_peers")).Visible, "removing last computer restores the guide");
            Require(!((LocalBridgeAgent)Get(window, "_agent")).IsBridgeActive, "pairing actions never start input");
        });
        Console.WriteLine("PASS: first-computer instructions, approved selection, second approval and removal preserve explicit activation.");
    }

    private static void WithPairingFixture(Action<DesktopWindow, ApprovedPeerStore, DeviceIdentityStore.PairingCard, DeviceIdentityStore.PairingCard> inspect)
    {
        var directory = Path.Combine(Path.GetTempPath(), "BatCavePairingSmoke-" + Guid.NewGuid().ToString("N"));
        var firstDirectory = Path.Combine(directory, "first"); var secondDirectory = Path.Combine(directory, "second");
        using var identity = DeviceIdentityStore.OpenOrCreate(directory);
        using var first = DeviceIdentityStore.OpenOrCreate(firstDirectory);
        using var second = DeviceIdentityStore.OpenOrCreate(secondDirectory);
        try
        {
            var approvals = new ApprovedPeerStore(directory);
            using var window = new DesktopWindow(identity, approvals, directory, inspectionOnly: true);
            window.StartPosition = FormStartPosition.Manual; window.Location = new(-30000, -30000); window.Show();
            try { inspect(window, approvals, first.ExportPublicCard("Studio PC"), second.ExportPublicCard("Laptop PC")); }
            finally { if (!window.IsDisposed) Call(window, "Quit"); }
            Require(!((LocalBridgeAgent)Get(window, "_agent")).IsHandleCreated, "inspection installs no input hooks");
        }
        finally
        {
            first.DeleteIdentity(firstDirectory); second.DeleteIdentity(secondDirectory); identity.DeleteIdentity(directory);
            Directory.Delete(firstDirectory); Directory.Delete(secondDirectory);
            foreach (var name in new[] { "approved-peers.json", "desktop-settings.json", "monitor-layouts.json" }) File.Delete(Path.Combine(directory, name));
            Directory.Delete(directory);
        }
    }

    private static void CheckWindowShutdown()
    {
        const uint queryEndSession = 0x0011, endSession = 0x0016, close = 0x0010;
        var closeApp = new IntPtr(1); // ENDSESSION_CLOSEAPP: Windows Restart Manager.
        WithPairingFixture((window, _, _, _) =>
        {
            var agent = PrepareSharing(window);
            window.Close();
            Require(window.IsDisposed && agent.IsDisposed, "closing the main window exits instead of hiding");
            Require(!agent.IsBridgeActive, "window close stops sharing before disposing input resources");
            Require(!((NotifyIcon)Get(window, "_tray")).Visible, "window close removes the tray icon");
        });
        WithPairingFixture((window, _, _, _) =>
        {
            var agent = PrepareSharing(window);
            SendMessage(window.Handle, close, IntPtr.Zero, IntPtr.Zero);
            Require(window.IsDisposed && agent.IsDisposed && !agent.IsBridgeActive, "native WM_CLOSE exits and stops sharing");
        });
        WithPairingFixture((window, _, _, _) =>
        {
            var agent = PrepareSharing(window);
            var handle = window.Handle;
            Require(SendMessage(handle, queryEndSession, IntPtr.Zero, closeApp) != IntPtr.Zero,
                "Restart Manager shutdown query is accepted");
            Require(!window.IsDisposed && window.Visible, "shutdown query waits for Windows to confirm the update");
            Require(!agent.IsBridgeActive && !(bool)Get(window, "_sharing"), "shutdown query pauses sharing safely");
            SendMessage(handle, endSession, IntPtr.Zero, closeApp);
            Require(!window.IsDisposed && window.Visible, "cancelled update keeps the window available");
            Require(!agent.IsBridgeActive, "cancelled update never resumes input automatically");
            Require(SendMessage(handle, queryEndSession, IntPtr.Zero, closeApp) != IntPtr.Zero, "a subsequent update is accepted");
            SendMessage(handle, endSession, new IntPtr(1), closeApp);
            Require(window.IsDisposed && agent.IsDisposed, "confirmed Restart Manager shutdown releases the app window");
            Require(!((NotifyIcon)Get(window, "_tray")).Visible, "confirmed update removes the tray icon");
        });
        Console.WriteLine("PASS: window close and native update shutdown exit cleanly; cancelled updates keep the app paused and visible.");

        static LocalBridgeAgent PrepareSharing(DesktopWindow window)
        {
            // Synthetic active state only: inspection mode has no hooks, socket,
            // or held keys. The real pause/close handlers perform cleanup.
            var agent = (LocalBridgeAgent)Get(window, "_agent");
            Set(agent, "_bridgeActive", true); Set(window, "_sharing", true);
            return agent;
        }
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

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
        WithPairingFixture((window, approvals, first, second) =>
        {
            foreach (var small in new[] { false, true })
            {
                if (small) window.Size = window.MinimumSize;
                window.ShowStep(1); Application.DoEvents();
                Set(window, "_windowsFileShared", false); Call(window, "UpdateSetup");
                var platforms = (ComboBox)Get(window, "_platform");
                for (var platform = 0; platform < 3; platform++)
                {
                    platforms.SelectedIndex = platform; Application.DoEvents();
                    CaptureWindow(window, output, "platform-" + platform + (small ? "-small" : ""));
                }
                Set(window, "_windowsFileShared", true); Call(window, "UpdateSetup"); Application.DoEvents();
                CaptureWindow(window, output, "receive" + (small ? "-small" : ""));
                Call(window, "ApplyApproval", first); Application.DoEvents();
                CaptureWindow(window, output, "paired" + (small ? "-small" : ""));
                Call(window, "SetConnectionDirection", true); Application.DoEvents();
                CaptureWindow(window, output, "connect-outgoing" + (small ? "-small" : ""));
                Call(window, "SetConnectionDirection", false);
                var lan = (LanBridgePeer)Get(window, "_lan");
                // Only a UI fixture: no socket, TLS session, listener or input hooks.
                Set(lan, "_writer", new StreamWriter(Stream.Null)); Set(lan, "_peerDeviceId", first.DeviceId);
                Set(window, "_hello", new DeviceHelloPayload(first.DeviceId, "Studio PC", "Windows", "BatCave", new(0, 0, 2560, 1440, 2559, 1439)));
                Call(window, "RefreshLayout"); window.ShowStep(3); Application.DoEvents();
                CaptureWindow(window, output, "connected" + (small ? "-small" : ""));
                window.ShowStep(2); Application.DoEvents();
                CaptureWindow(window, output, "layout" + (small ? "-small" : ""));
                ((ComboBox)Get(window, "_role")).SelectedIndex = 1; Application.DoEvents();
                CaptureWindow(window, output, "receiver-layout" + (small ? "-small" : ""));
                window.ShowStep(3); Application.DoEvents();
                CaptureWindow(window, output, "receiver-link" + (small ? "-small" : ""));
                ((ComboBox)Get(window, "_role")).SelectedIndex = 0;
                Set(lan, "_writer", null!); Set(lan, "_peerDeviceId", null!); Set(window, "_hello", null!);
                Call(window, "Unpair");
            }
        });
    }

    private static void CaptureWindow(DesktopWindow window, string output, string name)
    {
        using var bitmap = new System.Drawing.Bitmap(window.Width, window.Height);
        window.DrawToBitmap(bitmap, new(0, 0, bitmap.Width, bitmap.Height));
        bitmap.Save(Path.Combine(output, name + ".png"));
        Require(!((LocalBridgeAgent)Get(window, "_agent")).IsHandleCreated, "rendered states install no input hooks");
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
