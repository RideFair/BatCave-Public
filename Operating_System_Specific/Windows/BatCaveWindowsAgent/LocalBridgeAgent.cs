using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using BatCave.Universal.Topology;
using static BatCave.WindowsAgent.Win32;

namespace BatCave.WindowsAgent;

internal sealed class LocalBridgeAgent : Form
{
    private static readonly UIntPtr BatCaveInjectedExtraInfo = new(BatCaveInjectedExtraInfoValue);
    private const uint BatCaveInjectedExtraInfoValue = 0x42435456;
    private const int EdgeRearmDistance = 120;

    private enum ActiveTarget
    {
        Local,
        Remote
    }

    private enum MouseButtonKind
    {
        Left,
        Right,
        Middle,
        X1,
        X2
    }

    private sealed record MouseButtonDefinition(
        MouseButtonKind Kind,
        string ProtocolName,
        ushort RawDownFlag,
        ushort RawUpFlag,
        uint SendDownFlag,
        uint SendUpFlag,
        uint MouseData);

    private static readonly MouseButtonDefinition[] MouseButtonDefinitions =
    {
        new(MouseButtonKind.Left, "left", RI_MOUSE_LEFT_BUTTON_DOWN, RI_MOUSE_LEFT_BUTTON_UP, MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP, 0),
        new(MouseButtonKind.Right, "right", RI_MOUSE_RIGHT_BUTTON_DOWN, RI_MOUSE_RIGHT_BUTTON_UP, MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP, 0),
        new(MouseButtonKind.Middle, "middle", RI_MOUSE_MIDDLE_BUTTON_DOWN, RI_MOUSE_MIDDLE_BUTTON_UP, MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP, 0),
        new(MouseButtonKind.X1, "x1", RI_MOUSE_BUTTON_4_DOWN, RI_MOUSE_BUTTON_4_UP, MOUSEEVENTF_XDOWN, MOUSEEVENTF_XUP, 1),
        new(MouseButtonKind.X2, "x2", RI_MOUSE_BUTTON_5_DOWN, RI_MOUSE_BUTTON_5_UP, MOUSEEVENTF_XDOWN, MOUSEEVENTF_XUP, 2)
    };

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer _bridgeTimer = new();
    private readonly LowLevelMouseProc _mouseHookProc;
    private readonly LowLevelKeyboardProc _keyboardHookProc;
    private readonly byte[] _keyboardState = new byte[256];
    private readonly HeldKeyState _physicalKeys = new();
    private readonly HeldKeyState _localKeys = new();
    private readonly HeldKeyState _remoteKeys = new();
    private readonly HashSet<MouseButtonKind> _physicalMouseButtonsDown = new();
    private readonly HashSet<MouseButtonKind> _localMouseButtonsDown = new();
    private readonly HashSet<MouseButtonKind> _remoteMouseButtonsDown = new();
    private readonly LanBridgePeer _lan;

    private IntPtr _mouseHook = IntPtr.Zero;
    private IntPtr _keyboardHook = IntPtr.Zero;
    private bool _clipboardListenerInstalled;
    private bool _applyingRemoteClipboard;
    private bool _localCommandDownBuffered;
    private bool _localCommandShortcutConsumed;
    private ushort _localBufferedCommandVkey;
    private ushort _localBufferedCommandScanCode;
    private bool _localBufferedCommandIsExtended;
    private readonly HashSet<ushort> _localSuppressedKeyUps = new();
    private readonly HeldKeyState _pendingRemoteModifiers = new();
    private readonly HashSet<ushort> _remoteSuppressedKeyUps = new();
    private TopologyMap? _topology;
    private string _topologyStatus = "topology=not-loaded";
    private string? _lastPublishedClipboardText;
    private volatile bool _bridgeActive;
    private bool _handoffTestActive;
    private bool _bridgeTimeoutEnabled;
    private DateTimeOffset _bridgeEndsAt;
    private VirtualDesktopBounds _bounds;
    private int _virtualX;
    private int _virtualY;
    private ActiveTarget _activeTarget = ActiveTarget.Local;
    private string? _remoteTargetDeviceId;
    private string? _remoteTopologyDisplayId;
    private Point2D? _remoteTopologyCursor;
    private TopologyBounds? _remoteTopologyBounds;
    private bool _edgeArmed = true;
    private bool _lastHandoffWasTopology;
    private long _mouseEventCount;
    private long _rawKeyEventCount;
    private long _hookKeyEventCount;
    private DateTimeOffset _lastMouseStatus = DateTimeOffset.MinValue;
    private KeyboardReplayMode _keyboardReplayMode = KeyboardReplayMode.UnicodePrintable;

    private readonly string? _topologyPath;
    public bool IsBridgeActive => _bridgeActive;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool ClipboardSharingEnabled { get; set; }

    public LocalBridgeAgent(LanBridgePeer lan, string? topologyPath = null)
    {
        _lan = lan;
        ClipboardSharingEnabled = !lan.IsSecure;
        _topologyPath = topologyPath;
        _lan.ShortcutInputReceived += HandleRemoteShortcutInput;
        _lan.ClipboardTextReceived += HandleRemoteClipboardText;

        Text = "BatCave Windows Agent";
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        Size = new System.Drawing.Size(1, 1);
        Location = new System.Drawing.Point(-32000, -32000);

        _bounds = GetVirtualDesktopBounds();
        var cursor = GetCursorPositionOrThrow();
        _virtualX = cursor.X;
        _virtualY = cursor.Y;
        _lan.UpdateLocalBounds(_bounds);
        ReloadTopology(printStatus: false);

        _mouseHookProc = MouseHookCallback;
        _keyboardHookProc = KeyboardHookCallback;

        _bridgeTimer.Interval = 100;
        _bridgeTimer.Tick += (_, _) => CheckBridgeTimeout();
    }

    public void PostStartBridge(TimeSpan duration)
    {
        if (!IsDisposed)
        {
            BeginInvoke((Action)(() => StartBridge(duration, handoffTest: false)));
        }
    }

    public void PostStartHandoffTest(TimeSpan duration)
    {
        if (!IsDisposed)
        {
            BeginInvoke((Action)(() => StartBridge(duration, handoffTest: true)));
        }
    }

    public void PostStartPersistentHandoff()
    {
        if (!IsDisposed)
        {
            BeginInvoke((Action)(() =>
            {
                if (_lan.HasPeer) { StartBridge(null, handoffTest: true); }
            }));
        }
    }

    public void PostStopBridge(string reason)
    {
        if (!IsDisposed)
        {
            BeginInvoke((Action)(() => StopBridge(reason)));
        }
    }

    public void PostResetVirtualCursor()
    {
        if (!IsDisposed)
        {
            BeginInvoke((Action)ResetVirtualCursorToRealCursor);
        }
    }

    public void PostPrintState()
    {
        if (!IsDisposed)
        {
            BeginInvoke((Action)(() => PrintState("manual")));
        }
    }

    public void PostCycleKeyboardReplayMode()
    {
        if (!IsDisposed)
        {
            BeginInvoke((Action)CycleKeyboardReplayMode);
        }
    }

    public void PostReloadTopology()
    {
        if (!IsDisposed)
        {
            BeginInvoke((Action)(() => ReloadTopology(printStatus: true)));
        }
    }

    public void PostQuit()
    {
        if (!IsDisposed)
        {
            BeginInvoke((Action)Close);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterForRawInput();
        InstallHooks();
        InstallClipboardListener();

        Console.WriteLine($"Registered Raw Input target hwnd=0x{Handle.ToInt64():X}");
        Console.WriteLine($"Installed mouse hook=0x{_mouseHook.ToInt64():X}");
        Console.WriteLine($"Installed keyboard hook=0x{_keyboardHook.ToInt64():X}");
        Console.WriteLine($"Topology: {_topologyStatus}");
        PrintState("startup");
        Console.WriteLine();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        StopBridge("process closing");
        _lan.ShortcutInputReceived -= HandleRemoteShortcutInput;
        _lan.ClipboardTextReceived -= HandleRemoteClipboardText;
        UninstallClipboardListener();
        UninstallHooks();
        base.OnFormClosed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_INPUT)
        {
            HandleRawInput(m.LParam);
        }
        else if (m.Msg == WM_CLIPBOARDUPDATE)
        {
            HandleClipboardUpdate();
        }

        base.WndProc(ref m);
    }

    private void InstallClipboardListener()
    {
        if (_clipboardListenerInstalled)
        {
            return;
        }

        if (AddClipboardFormatListener(Handle))
        {
            _clipboardListenerInstalled = true;
            Console.WriteLine("Registered clipboard listener.");
            return;
        }

        Console.WriteLine($"AddClipboardFormatListener failed. Win32 error={Marshal.GetLastWin32Error()}");
    }

    private void UninstallClipboardListener()
    {
        if (!_clipboardListenerInstalled)
        {
            return;
        }

        if (!RemoveClipboardFormatListener(Handle))
        {
            Console.WriteLine($"RemoveClipboardFormatListener failed. Win32 error={Marshal.GetLastWin32Error()}");
        }

        _clipboardListenerInstalled = false;
    }

    private void HandleClipboardUpdate()
    {
        if (_applyingRemoteClipboard || !ClipboardSharingEnabled
            || (_lan.IsSecure && (!_bridgeActive || !_lan.HasPeer)))
        {
            return;
        }

        try
        {
            if (!Clipboard.ContainsText(TextDataFormat.UnicodeText))
            {
                return;
            }

            var text = Clipboard.GetText(TextDataFormat.UnicodeText);
            if (string.IsNullOrEmpty(text) || string.Equals(text, _lastPublishedClipboardText, StringComparison.Ordinal))
            {
                return;
            }

            _lastPublishedClipboardText = text;
            _lan.PublishClipboardText(text);
            Console.WriteLine($"{_clock.Elapsed:hh\\:mm\\:ss\\.fff} CLIPBOARD local text chars={text.Length}");
        }
        catch (ExternalException ex)
        {
            Console.WriteLine($"Clipboard read failed: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"Clipboard read failed: {ex.Message}");
        }
    }

    private void HandleRemoteClipboardText(string sourceDeviceId, ClipboardTextPayload payload)
    {
        if (string.Equals(sourceDeviceId, _lan.LocalDeviceId, StringComparison.OrdinalIgnoreCase)
            || IsDisposed
            || !IsHandleCreated)
        {
            return;
        }

        try
        {
            var session = _lan.SessionGeneration;
            BeginInvoke((Action)(() =>
            {
                if (ClipboardSharingEnabled && IsCurrentRemoteAction(sourceDeviceId, session))
                    ApplyRemoteClipboardText(sourceDeviceId, payload);
            }));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void ApplyRemoteClipboardText(string sourceDeviceId, ClipboardTextPayload payload)
    {
        var text = payload.Text ?? string.Empty;
        try
        {
            if (Clipboard.ContainsText(TextDataFormat.UnicodeText)
                && string.Equals(Clipboard.GetText(TextDataFormat.UnicodeText), text, StringComparison.Ordinal))
            {
                _lastPublishedClipboardText = text;
                Console.WriteLine($"{_clock.Elapsed:hh\\:mm\\:ss\\.fff} CLIPBOARD remote already current from={sourceDeviceId} chars={text.Length}");
                return;
            }

            _applyingRemoteClipboard = true;
            if (text.Length == 0)
            {
                Clipboard.Clear();
            }
            else
            {
                Clipboard.SetText(text, TextDataFormat.UnicodeText);
            }

            _lastPublishedClipboardText = text;
            Console.WriteLine($"{_clock.Elapsed:hh\\:mm\\:ss\\.fff} CLIPBOARD remote applied from={sourceDeviceId} format={payload.Format} chars={text.Length}");
        }
        catch (ExternalException ex)
        {
            Console.WriteLine($"Clipboard write failed from {sourceDeviceId}: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"Clipboard write failed from {sourceDeviceId}: {ex.Message}");
        }
        finally
        {
            _applyingRemoteClipboard = false;
        }
    }

    private void HandleRemoteShortcutInput(string sourceDeviceId, ShortcutInputPayload payload)
    {
        if (string.Equals(sourceDeviceId, _lan.LocalDeviceId, StringComparison.OrdinalIgnoreCase)
            || IsDisposed
            || !IsHandleCreated)
        {
            return;
        }

        try
        {
            var session = _lan.SessionGeneration;
            BeginInvoke((Action)(() =>
            {
                if (IsCurrentRemoteAction(sourceDeviceId, session) && _activeTarget == ActiveTarget.Local)
                    ApplyRemoteShortcutInput(sourceDeviceId, payload);
            }));
        }
        catch (InvalidOperationException)
        {
        }
    }

    internal void StopSharingNow(string reason)
    {
        if (IsDisposed) { return; }
        if (InvokeRequired) { Invoke((Action)(() => StopBridge(reason))); }
        else { StopBridge(reason); }
    }

    private bool IsCurrentRemoteAction(string sourceDeviceId, long session) =>
        _lan.HasPeer && _lan.SessionGeneration == session && _lan.CurrentPeerDeviceId == sourceDeviceId
        && (!_lan.IsSecure || _bridgeActive);

    private void ApplyRemoteShortcutInput(string sourceDeviceId, ShortcutInputPayload payload)
    {
        if (!TryMapSemanticShortcutAction(payload.Action, out var key))
        {
            Console.WriteLine($"{_clock.Elapsed:hh\\:mm\\:ss\\.fff} SHORTCUT unsupported from={sourceDeviceId} action={payload.Action}");
            return;
        }

        ApplySemanticShortcutLocally(sourceDeviceId, payload.Action, key);
    }

    private void ApplySemanticShortcutLocally(string sourceDeviceId, string action)
    {
        if (!TryMapSemanticShortcutAction(action, out var key))
        {
            Console.WriteLine($"{_clock.Elapsed:hh\\:mm\\:ss\\.fff} SHORTCUT unsupported from={sourceDeviceId} action={action}");
            return;
        }

        ApplySemanticShortcutLocally(sourceDeviceId, action, key);
    }

    private void ApplySemanticShortcutLocally(string sourceDeviceId, string action, ushort key)
    {
        SendVirtualKey(VK_CONTROL, keyUp: false, extended: false);
        SendVirtualKey(key, keyUp: false, extended: false);
        SendVirtualKey(key, keyUp: true, extended: false);
        SendVirtualKey(VK_CONTROL, keyUp: true, extended: false);
        Console.WriteLine($"{_clock.Elapsed:hh\\:mm\\:ss\\.fff} SHORTCUT applied from={sourceDeviceId} action={action}");
    }

    private void StartBridge(TimeSpan? duration, bool handoffTest)
    {
        if (_bridgeActive)
        {
            Console.WriteLine("Bridge mode is already active.");
            return;
        }

        _bounds = GetVirtualDesktopBounds();
        _lan.UpdateLocalBounds(_bounds);
        ReloadTopology(printStatus: true);
        if (handoffTest && (_topology is null || !_topology.HasLocalSourceLinks(_lan.LocalDeviceId)))
        {
            Console.WriteLine("APPSTATUS trouble reason=No configured bridge for this Windows device. Open the topology editor.");
            return;
        }
        ResetVirtualCursorToRealCursor();
        GetKeyboardState(_keyboardState);
        _physicalKeys.ReleaseAll();
        _localKeys.ReleaseAll();
        _remoteKeys.ReleaseAll();
        _activeTarget = ActiveTarget.Local;
        _remoteTargetDeviceId = null;
        ClearMouseButtonTracking();
        SeedHeldInput();
        ClearLocalCommandShortcutState();
        ClearRemoteTracking();
        _handoffTestActive = handoffTest;
        _bridgeTimeoutEnabled = duration.HasValue;
        if (duration is { } timeout)
        {
            _bridgeEndsAt = DateTimeOffset.UtcNow.Add(timeout);
        }

        _bridgeActive = true;
        if (_bridgeTimeoutEnabled)
        {
            _bridgeTimer.Start();
        }

        Console.WriteLine();
        if (duration is { } activeDuration)
        {
            Console.WriteLine($"{(handoffTest ? "HANDOFF TEST" : "BRIDGE")} ON for {activeDuration.TotalSeconds:0} seconds.");
        }
        else
        {
            Console.WriteLine($"{(handoffTest ? "HANDOFF" : "BRIDGE")} ON until stopped.");
        }
        Console.WriteLine("APPSTATUS bridge=on");

        Console.WriteLine("Physical mouse/keyboard events are suppressed, then re-injected from BatCave state.");
        Console.WriteLine($"Keyboard replay mode: {_keyboardReplayMode}");
        Console.WriteLine(_bridgeTimeoutEnabled
            ? "Escape, F12, or Pause exits immediately. Auto-stop is enabled."
            : "Escape, F12, or Pause stops bridge control. Close the app to quit.");
        if (handoffTest)
        {
            Console.WriteLine("Move/type on Windows, cross the configured bridge, then move/type on the peer device.");
            Console.WriteLine("After topology handoff, Windows forwards physical mouse and keyboard input over LAN.");
            if (duration is { } testDuration)
            {
                _lan.PublishControlTestStart(testDuration);
            }
        }
        else
        {
            Console.WriteLine("Topology crossings publish cursor.handoff; outer desktop edge crossings publish cursor.edgeHandoff.");
        }
        Console.WriteLine();
    }

    private void StopBridge(string reason)
    {
        if (!_bridgeActive)
        {
            return;
        }

        ReleaseLocalKeys();
        ReleaseRemoteKeys();
        _physicalKeys.ReleaseAll();
        ReleaseAllTrackedMouseButtons("bridge stop");
        _bridgeActive = false;
        _bridgeTimer.Stop();
        _bridgeTimeoutEnabled = false;
        if (_handoffTestActive)
        {
            _lan.PublishControlTestStop(reason);
        }

        _handoffTestActive = false;
        _activeTarget = ActiveTarget.Local;
        _remoteTargetDeviceId = null;
        ClearLocalCommandShortcutState();
        ClearRemoteTracking();
        Console.WriteLine();
        Console.WriteLine($"BRIDGE OFF ({reason}).");
        Console.WriteLine($"APPSTATUS bridge=off reason={EscapeForStatus(reason)}");
        PrintState("stop");
        Console.WriteLine();
    }

    private void CheckBridgeTimeout()
    {
        if (!_bridgeActive)
        {
            _bridgeTimer.Stop();
            return;
        }

        if (_bridgeTimeoutEnabled && DateTimeOffset.UtcNow >= _bridgeEndsAt)
        {
            StopBridge("timer expired");
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _bridgeActive)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var injected = (info.flags & LLMHF_INJECTED) != 0;
            if (!injected)
            {
                return new IntPtr(1);
            }
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _bridgeActive)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var injected = (info.flags & LLKHF_INJECTED) != 0;
            if (!injected)
            {
                if (info.vkCode == VK_ESCAPE || info.vkCode == VK_F12 || info.vkCode == VK_PAUSE)
                {
                    BeginInvoke((Action)(() => StopBridge($"panic key vk=0x{info.vkCode:X2}")));
                    return new IntPtr(1);
                }

                _physicalKeys.Update(new HeldKey((ushort)info.vkCode, (ushort)info.scanCode,
                    (info.flags & LLKHF_EXTENDED) != 0), (info.flags & LLKHF_UP) != 0);
                if (_activeTarget == ActiveTarget.Remote)
                {
                    ForwardRemoteKeyboardHook(info);
                }
                else
                {
                    ReplayPhysicalKeyboardHook(info);
                }

                return new IntPtr(1);
            }
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private void HandleRawInput(IntPtr rawInputHandle)
    {
        uint dataSize = 0;
        var headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();

        var queryResult = GetRawInputData(rawInputHandle, RID_INPUT, IntPtr.Zero, ref dataSize, headerSize);
        if (queryResult != 0 || dataSize == 0)
        {
            Console.WriteLine($"GetRawInputData size query failed. result={queryResult}, size={dataSize}, win32={Marshal.GetLastWin32Error()}");
            return;
        }

        var buffer = Marshal.AllocHGlobal((int)dataSize);
        try
        {
            var readResult = GetRawInputData(rawInputHandle, RID_INPUT, buffer, ref dataSize, headerSize);
            if (readResult != dataSize)
            {
                Console.WriteLine($"GetRawInputData read failed. result={readResult}, expected={dataSize}, win32={Marshal.GetLastWin32Error()}");
                return;
            }

            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
            if (header.dwType == RIM_TYPEMOUSE)
            {
                var raw = Marshal.PtrToStructure<RAWINPUTMOUSE>(buffer);
                HandleRawMouse(raw.mouse);
            }
            else if (header.dwType == RIM_TYPEKEYBOARD)
            {
                var raw = Marshal.PtrToStructure<RAWINPUTKEYBOARD>(buffer);
                HandleRawKeyboard(raw.keyboard);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void HandleRawMouse(RAWMOUSE mouse)
    {
        if (!_bridgeActive)
        {
            return;
        }

        if (mouse.ulExtraInformation == BatCaveInjectedExtraInfoValue)
        {
            return;
        }

        if ((mouse.usFlags & MOUSE_MOVE_ABSOLUTE) != 0)
        {
            ReplayOrForwardMouseButtonsAndWheel(mouse.buttons);
            return;
        }

        _mouseEventCount++;

        if (_activeTarget == ActiveTarget.Remote)
        {
            ForwardRemoteMouse(mouse);
            return;
        }

        if (mouse.lLastX != 0 || mouse.lLastY != 0)
        {
            var desiredX = _virtualX + mouse.lLastX;
            var desiredY = _virtualY + mouse.lLastY;
            ReportEdgeCrossings(desiredX, desiredY, mouse.lLastX, mouse.lLastY);

            if (_activeTarget == ActiveTarget.Remote)
            {
                ReplayOrForwardMouseButtonsAndWheel(mouse.buttons);
                MaybePrintMouseStatus(mouse.lLastX, mouse.lLastY);
                return;
            }

            _virtualX = Math.Clamp(desiredX, _bounds.Left, _bounds.Right);
            _virtualY = Math.Clamp(desiredY, _bounds.Top, _bounds.Bottom);
            SendAbsoluteMousePosition(_virtualX, _virtualY);
            MaybePrintMouseStatus(mouse.lLastX, mouse.lLastY);
        }

        ReplayOrForwardMouseButtonsAndWheel(mouse.buttons);
    }

    private void HandleRawKeyboard(RAWKEYBOARD keyboard)
    {
        if (!_bridgeActive || keyboard.ExtraInformation == BatCaveInjectedExtraInfoValue)
        {
            return;
        }

        _rawKeyEventCount++;

        var vkey = keyboard.VKey;
        if (vkey == 0 || vkey == 0xFF || vkey == VK_PACKET)
        {
            return;
        }

        var isKeyUp = (keyboard.Flags & RI_KEY_BREAK) != 0;
        UpdateKeyboardState(vkey, isKeyUp);

        var action = isKeyUp ? "UP" : "DOWN";
        Console.WriteLine(
            $"{_clock.Elapsed:hh\\:mm\\:ss\\.fff} RAWKEY {action,-4} " +
            $"vk=0x{vkey:X4} scan=0x{keyboard.MakeCode:X4} rawEvents={_rawKeyEventCount}");
    }

    private void ReplayPhysicalKeyboardHook(KBDLLHOOKSTRUCT info)
    {
        _hookKeyEventCount++;

        var vkey = (ushort)info.vkCode;
        var scanCode = (ushort)info.scanCode;
        var isKeyUp = (info.flags & LLKHF_UP) != 0;
        var isExtended = (info.flags & LLKHF_EXTENDED) != 0;

        UpdateKeyboardState(vkey, isKeyUp);
        if (TryHandleLocalCommandShortcutChord(vkey, scanCode, isKeyUp, isExtended))
        {
            return;
        }

        var replay = ReplayKeyboardHookEvent(vkey, scanCode, isKeyUp, isExtended);

        var action = isKeyUp ? "UP" : "DOWN";
        Console.WriteLine(
            $"{_clock.Elapsed:hh\\:mm\\:ss\\.fff} HOOKKEY {action,-4} " +
            $"vk=0x{vkey:X4} scan=0x{scanCode:X4} replay={replay} hookEvents={_hookKeyEventCount}");
    }

    private bool TryHandleLocalCommandShortcutChord(ushort vkey, ushort scanCode, bool isKeyUp, bool isExtended)
    {
        if (IsCommandKey(vkey))
        {
            if (!isKeyUp)
            {
                BufferLocalCommandDown(vkey, scanCode, isExtended);
                return true;
            }

            if (_localCommandDownBuffered && !_localCommandShortcutConsumed)
            {
                FlushLocalBufferedCommand();
                return false;
            }

            if (_localCommandShortcutConsumed)
            {
                ClearLocalCommandModifierState();
                Console.WriteLine(
                    $"{_clock.Elapsed:hh\\:mm\\:ss\\.fff} LOCALMOD UP   " +
                    $"vk=0x{vkey:X4} consumed=shortcut hookEvents={_hookKeyEventCount}");
                return true;
            }

            return false;
        }

        if (isKeyUp && _localSuppressedKeyUps.Remove(vkey))
        {
            Console.WriteLine(
                $"{_clock.Elapsed:hh\\:mm\\:ss\\.fff} LOCALSHORTCUT UP   " +
                $"vk=0x{vkey:X4} consumed=shortcut hookEvents={_hookKeyEventCount}");
            return true;
        }

        if (!isKeyUp && IsLocalCommandShortcutModifierActive() && TryMapSemanticShortcut(vkey, out var shortcutAction))
        {
            _localCommandShortcutConsumed = true;
            _localSuppressedKeyUps.Add(vkey);
            ApplySemanticShortcutLocally("local-command", shortcutAction);
            Console.WriteLine(
                $"{_clock.Elapsed:hh\\:mm\\:ss\\.fff} LOCALSHORTCUT DOWN " +
                $"action={shortcutAction} vk=0x{vkey:X4} hookEvents={_hookKeyEventCount}");
            return true;
        }

        if (!isKeyUp)
        {
            FlushLocalBufferedCommand();
        }

        return false;
    }

    private void BufferLocalCommandDown(ushort vkey, ushort scanCode, bool isExtended)
    {
        _localCommandDownBuffered = true;
        _localCommandShortcutConsumed = false;
        _localBufferedCommandVkey = vkey;
        _localBufferedCommandScanCode = scanCode;
        _localBufferedCommandIsExtended = isExtended;
        Console.WriteLine(
            $"{_clock.Elapsed:hh\\:mm\\:ss\\.fff} LOCALMOD DOWN " +
            $"vk=0x{vkey:X4} buffered=command-shortcut hookEvents={_hookKeyEventCount}");
    }

    private void FlushLocalBufferedCommand()
    {
        if (!_localCommandDownBuffered)
        {
            return;
        }

        ReplayKeyboardHookEvent(_localBufferedCommandVkey, _localBufferedCommandScanCode, false, _localBufferedCommandIsExtended);
        _localCommandDownBuffered = false;
        _localCommandShortcutConsumed = false;
    }

    private void ClearLocalCommandShortcutState()
    {
        ClearLocalCommandModifierState();
        _localSuppressedKeyUps.Clear();
    }

    private void ClearLocalCommandModifierState()
    {
        _localCommandDownBuffered = false;
        _localCommandShortcutConsumed = false;
        _localBufferedCommandVkey = 0;
        _localBufferedCommandScanCode = 0;
        _localBufferedCommandIsExtended = false;
    }

    private void ForwardRemoteKeyboardHook(KBDLLHOOKSTRUCT info)
    {
        _hookKeyEventCount++;

        var vkey = (ushort)info.vkCode;
        var scanCode = (ushort)info.scanCode;
        var isKeyUp = (info.flags & LLKHF_UP) != 0;
        var isExtended = (info.flags & LLKHF_EXTENDED) != 0;

        UpdateKeyboardState(vkey, isKeyUp);

        if (TryHandleRemoteShortcutChord(vkey, scanCode, isKeyUp, isExtended))
        {
            return;
        }

        string? text = null;
        if (!isKeyUp
            && !IsShortcutModifierActive()
            && TryTranslatePrintableKey(vkey, scanCode, out var translatedText))
        {
            text = translatedText;
        }

        PublishRemoteKey(vkey, scanCode, isKeyUp, isExtended, text);
    }

    private bool TryHandleRemoteShortcutChord(ushort vkey, ushort scanCode, bool isKeyUp, bool isExtended)
    {
        if (IsShortcutModifierKey(vkey))
        {
            if (!isKeyUp)
            {
                BufferRemoteControlDown(vkey, scanCode, isExtended);
                return true;
            }

            _pendingRemoteModifiers.Update(new HeldKey(vkey, scanCode, isExtended), true);
            // A buffered modifier never reached the receiver. A forwarded modifier
            // must still get its matching release, even if another remains held.
            return !_remoteKeys.Snapshot().Any(key => key.VirtualKey == vkey);
        }

        if (isKeyUp && _remoteSuppressedKeyUps.Remove(vkey))
        {
            Console.WriteLine(
                $"{_clock.Elapsed:hh\\:mm\\:ss\\.fff} REMOTESHORTCUT UP   " +
                $"target={_remoteTargetDeviceId ?? "remote"} vk=0x{vkey:X4} consumed=shortcut hookEvents={_hookKeyEventCount}");
            return true;
        }

        if (!isKeyUp && IsRemoteShortcutModifierActive() && TryMapSemanticShortcut(vkey, out var shortcutAction))
        {
            // Mouse forwarding may already have sent Control. Release it before
            // the receiver injects its native semantic shortcut, then retain the
            // physical hold for the next ordinary key or mouse action.
            foreach (var modifier in _remoteKeys.Snapshot().Where(key => IsShortcutModifierKey(key.VirtualKey)))
                PublishRemoteKey(modifier.VirtualKey, modifier.ScanCode, true, modifier.Extended, null);
            foreach (var modifier in _physicalKeys.Snapshot().Where(key => IsShortcutModifierKey(key.VirtualKey)))
                _pendingRemoteModifiers.Update(modifier, false);
            _remoteSuppressedKeyUps.Add(vkey);
            _lan.PublishShortcutInput(shortcutAction);
            Console.WriteLine(
                $"{_clock.Elapsed:hh\\:mm\\:ss\\.fff} REMOTESHORTCUT DOWN " +
                $"target={_remoteTargetDeviceId ?? "remote"} action={shortcutAction} vk=0x{vkey:X4} hookEvents={_hookKeyEventCount}");
            return true;
        }

        if (!isKeyUp)
        {
            FlushRemoteBufferedControl();
        }

        return false;
    }

    private void BufferRemoteControlDown(ushort vkey, ushort scanCode, bool isExtended)
    {
        if (_remoteKeys.Snapshot().Any(key => key.VirtualKey == vkey)) { return; }
        _pendingRemoteModifiers.Update(new HeldKey(vkey, scanCode, isExtended), false);
        if (_remoteMouseButtonsDown.Count > 0) { FlushRemoteBufferedControl(); }
        Console.WriteLine(
            $"{_clock.Elapsed:hh\\:mm\\:ss\\.fff} REMOTEMOD DOWN " +
            $"target={_remoteTargetDeviceId ?? "remote"} vk=0x{vkey:X4} buffered=shortcut hookEvents={_hookKeyEventCount}");
    }

    private void FlushRemoteBufferedControl()
    {
        foreach (var modifier in _pendingRemoteModifiers.ReleaseAll())
            PublishRemoteKey(modifier.VirtualKey, modifier.ScanCode, false, modifier.Extended, null);
    }

    private void ClearRemoteShortcutState()
    {
        ClearRemoteShortcutModifierState();
        _remoteSuppressedKeyUps.Clear();
    }

    private void ClearRemoteShortcutModifierState()
    {
        _pendingRemoteModifiers.ReleaseAll();
    }

    private void PublishRemoteKey(ushort vkey, ushort scanCode, bool isKeyUp, bool isExtended, string? text)
    {
        _remoteKeys.Update(new HeldKey(vkey, scanCode, isExtended), isKeyUp);
        _lan.PublishKeyInput(vkey, scanCode, isKeyUp, isExtended, text);

        var action = isKeyUp ? "UP" : "DOWN";
        Console.WriteLine(
            $"{_clock.Elapsed:hh\\:mm\\:ss\\.fff} REMOTEKEY {action,-4} " +
            $"target={_remoteTargetDeviceId ?? "remote"} vk=0x{vkey:X4} scan=0x{scanCode:X4} text={EscapeForLog(text ?? string.Empty)} hookEvents={_hookKeyEventCount}");
    }

    private bool IsRemoteShortcutModifierActive()
    {
        // Semantic copy/paste is only the simple single-modifier chord. Modified
        // variants and simultaneous Control/Windows keys retain their raw meaning.
        var modifiers = _physicalKeys.Snapshot().Where(key => HeldKeyState.IsModifier(key.VirtualKey)).ToArray();
        return modifiers.Length == 1 && IsShortcutModifierKey(modifiers[0].VirtualKey);
    }

    private bool IsLocalCommandShortcutModifierActive()
    {
        return (IsKeyStateDown(VK_LWIN) || IsKeyStateDown(VK_RWIN))
            && !IsKeyStateDown(VK_CONTROL)
            && !IsKeyStateDown(VK_MENU)
            && !IsKeyStateDown(VK_LMENU)
            && !IsKeyStateDown(VK_RMENU);
    }

    private static bool IsShortcutModifierKey(ushort vkey)
    {
        return vkey is VK_CONTROL or VK_LCONTROL or VK_RCONTROL or VK_LWIN or VK_RWIN;
    }

    private static bool IsCommandKey(ushort vkey)
    {
        return vkey is VK_LWIN or VK_RWIN;
    }

    private static bool TryMapSemanticShortcut(ushort vkey, out string action)
    {
        action = vkey switch
        {
            (ushort)'A' => "selectAll",
            (ushort)'C' => "copy",
            (ushort)'V' => "paste",
            (ushort)'X' => "cut",
            _ => string.Empty
        };

        return action.Length > 0;
    }

    private static bool TryMapSemanticShortcutAction(string action, out ushort vkey)
    {
        vkey = action switch
        {
            "selectAll" => (ushort)'A',
            "copy" => (ushort)'C',
            "paste" => (ushort)'V',
            "cut" => (ushort)'X',
            _ => (ushort)0
        };

        return vkey != 0;
    }

    private string ReplayKeyboardHookEvent(ushort vkey, ushort scanCode, bool isKeyUp, bool isExtended)
    {
        if (_keyboardReplayMode == KeyboardReplayMode.UnicodePrintable
            && !isKeyUp
            && !IsShortcutModifierActive()
            && TryTranslatePrintableKey(vkey, scanCode, out var text))
        {
            SendUnicodeText(text);
            return $"unicode:{EscapeForLog(text)}";
        }

        if (_keyboardReplayMode == KeyboardReplayMode.VirtualKey)
        {
            _localKeys.Update(new HeldKey(vkey, scanCode, isExtended), isKeyUp);
            SendVirtualKey(vkey, isKeyUp, isExtended);
            return "vkey";
        }

        if (scanCode == 0)
        {
            return "none";
        }

        _localKeys.Update(new HeldKey(vkey, scanCode, isExtended), isKeyUp);
        SendScanCode(scanCode, isKeyUp, isExtended);
        return "scan";
    }

    private void ReportEdgeCrossings(int desiredX, int desiredY, int deltaX, int deltaY)
    {
        if (!_edgeArmed)
        {
            if (CanRearmHandoff())
            {
                _edgeArmed = true;
            }
            else
            {
                return;
            }
        }

        if (_topology is not null
            && _topology.TryMapCrossing(
                new Point2D(_virtualX, _virtualY),
                new Point2D(desiredX, desiredY),
                _lan.LocalDeviceId,
                out var handoff))
        {
            if (_handoffTestActive && !string.Equals(_lan.CurrentPeerDeviceId, handoff.TargetDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            _edgeArmed = false;
            _lastHandoffWasTopology = true;
            Console.WriteLine();
            Console.WriteLine(
                $"{_clock.Elapsed:hh\\:mm\\:ss\\.fff} WOULD_HANDOFF_TOPOLOGY " +
                $"link={handoff.LinkId} segment={handoff.SegmentPosition:0.000} " +
                $"source={handoff.SourceDisplayId} {handoff.SourceEdge} {handoff.SourcePosition:0.000} " +
                $"point=({handoff.SourceX},{handoff.SourceY}) -> " +
                $"target={handoff.TargetDeviceId}/{handoff.TargetDisplayId} {handoff.TargetEdge} {handoff.TargetPosition:0.000} " +
                $"point=({handoff.TargetX},{handoff.TargetY})");
            Console.WriteLine();
            _lan.PublishEdgeHandoff(handoff.SourceEdge, desiredX, desiredY, deltaX, deltaY, _bounds);
            _lan.PublishCursorHandoff(handoff);
            if (_handoffTestActive)
            {
                ActivateRemoteTarget(handoff);
            }
            return;
        }

        if (_topologyPath is not null) return;

        string? edge = null;
        if (desiredX < _bounds.Left)
        {
            edge = "LEFT";
        }
        else if (desiredX > _bounds.Right)
        {
            edge = "RIGHT";
        }
        else if (desiredY < _bounds.Top)
        {
            edge = "UP";
        }
        else if (desiredY > _bounds.Bottom)
        {
            edge = "DOWN";
        }

        if (edge is null)
        {
            return;
        }

        _edgeArmed = false;
        _lastHandoffWasTopology = false;
        Console.WriteLine();
        Console.WriteLine($"{_clock.Elapsed:hh\\:mm\\:ss\\.fff} WOULD_HANDOFF_{edge} virtual=({desiredX},{desiredY}) bounds={_bounds}");
        Console.WriteLine();
        _lan.PublishEdgeHandoff(edge, desiredX, desiredY, deltaX, deltaY, _bounds);
    }

    private bool CanRearmHandoff()
    {
        if (_lastHandoffWasTopology && _topology is not null && _topology.HasLocalSourceLinks(_lan.LocalDeviceId))
        {
            return _topology.IsSafelyAwayFromLocalSourceSegments(
                new Point2D(_virtualX, _virtualY),
                _lan.LocalDeviceId,
                EdgeRearmDistance);
        }

        return IsSafelyInside(_virtualX, _virtualY);
    }

    private bool IsSafelyInside(int x, int y)
    {
        return x >= _bounds.Left + EdgeRearmDistance
            && x <= _bounds.Right - EdgeRearmDistance
            && y >= _bounds.Top + EdgeRearmDistance
            && y <= _bounds.Bottom - EdgeRearmDistance;
    }

    private void MaybePrintMouseStatus(int dx, int dy)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastMouseStatus < TimeSpan.FromMilliseconds(250))
        {
            return;
        }

        _lastMouseStatus = now;
        Console.WriteLine(
            $"{_clock.Elapsed:hh\\:mm\\:ss\\.fff} MOUSE " +
            $"dx={dx,5} dy={dy,5} virtual=({_virtualX},{_virtualY}) events={_mouseEventCount}");
    }

    private void ForwardRemoteMouse(RAWMOUSE mouse)
    {
        if (mouse.lLastX != 0 || mouse.lLastY != 0)
        {
            if (TryReturnFromRemote(mouse.lLastX, mouse.lLastY))
            {
                MaybePrintMouseStatus(mouse.lLastX, mouse.lLastY);
                ReplayOrForwardMouseButtonsAndWheel(mouse.buttons);
                return;
            }

            _lan.PublishMouseDelta(mouse.lLastX, mouse.lLastY);
            MaybePrintMouseStatus(mouse.lLastX, mouse.lLastY);
        }

        ReplayOrForwardMouseButtonsAndWheel(mouse.buttons);
    }

    private void ActivateRemoteTarget(TopologyHandoff handoff)
    {
        if (!_lan.HasPeer)
        {
            Console.WriteLine($"{_clock.Elapsed:hh\\:mm\\:ss\\.fff} ACTIVE_TARGET remote ignored: no LAN peer");
            return;
        }

        _activeTarget = ActiveTarget.Remote;
        _remoteTargetDeviceId = handoff.TargetDeviceId;
        _remoteTopologyDisplayId = handoff.TargetDisplayId;
        _remoteTopologyCursor = new Point2D(handoff.TargetX, handoff.TargetY);
        _remoteTopologyBounds = _topology?.Displays
            .FirstOrDefault(display => string.Equals(display.Id, handoff.TargetDisplayId, StringComparison.OrdinalIgnoreCase))
            ?.Bounds;

        Console.WriteLine(
            $"{_clock.Elapsed:hh\\:mm\\:ss\\.fff} ACTIVE_TARGET remote={_remoteTargetDeviceId} " +
            $"remoteCursor=({_remoteTopologyCursor.Value.X:0},{_remoteTopologyCursor.Value.Y:0}) " +
            $"remoteDisplay={_remoteTopologyDisplayId}");

        ReleaseLocalKeys();
        ClearLocalCommandShortcutState();
        ClearRemoteShortcutState();
        RestorePhysicalModifiers(remote: true);
        TransferMouseButtonsToRemote();
    }

    private bool TryReturnFromRemote(int deltaX, int deltaY)
    {
        if (!_handoffTestActive
            || _topology is null
            || _remoteTargetDeviceId is null
            || _remoteTopologyCursor is not { } from)
        {
            return false;
        }

        var desired = new Point2D(from.X + deltaX, from.Y + deltaY);
        if (_topology.TryMapCrossing(from, desired, _remoteTargetDeviceId, out var handoff)
            && string.Equals(handoff.TargetDeviceId, _lan.LocalDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine();
            Console.WriteLine(
                $"{_clock.Elapsed:hh\\:mm\\:ss\\.fff} RETURN_HANDOFF_TOPOLOGY " +
                $"link={handoff.LinkId} segment={handoff.SegmentPosition:0.000} " +
                $"source={handoff.SourceDisplayId} {handoff.SourceEdge} {handoff.SourcePosition:0.000} " +
                $"point=({handoff.SourceX},{handoff.SourceY}) -> " +
                $"target={handoff.TargetDeviceId}/{handoff.TargetDisplayId} {handoff.TargetEdge} {handoff.TargetPosition:0.000} " +
                $"point=({handoff.TargetX},{handoff.TargetY})");
            Console.WriteLine();

            ReleaseRemoteKeys();
            ReleaseRemoteMouseButtons("return handoff");
            _lan.PublishCursorHandoff(handoff);
            _activeTarget = ActiveTarget.Local;
            _remoteTargetDeviceId = null;
            ClearRemoteTracking();
            _virtualX = Math.Clamp(handoff.TargetX, _bounds.Left, _bounds.Right);
            _virtualY = Math.Clamp(handoff.TargetY, _bounds.Top, _bounds.Bottom);
            _edgeArmed = false;
            _lastHandoffWasTopology = true;
            SendAbsoluteMousePosition(_virtualX, _virtualY);
            RestorePhysicalModifiers(remote: false);
            PressPhysicalMouseButtonsLocally("return handoff");
            Console.WriteLine($"{_clock.Elapsed:hh\\:mm\\:ss\\.fff} ACTIVE_TARGET local virtual=({_virtualX},{_virtualY})");
            return true;
        }

        _remoteTopologyCursor = ClampRemoteTopologyCursor(desired);
        return false;
    }

    private void SeedHeldInput()
    {
        // Read native state before capture starts, outside a low-level hook.
        // Keys/buttons held while the peer connects already belong to Windows.
        foreach (var vkey in new ushort[] { VK_LSHIFT, VK_RSHIFT, VK_LCONTROL, VK_RCONTROL,
            VK_LMENU, VK_RMENU, VK_LWIN, VK_RWIN })
        {
            if ((GetAsyncKeyState(vkey) & 0x8000) == 0) { continue; }
            var scan = MapVirtualKey(vkey, 4);
            var key = new HeldKey(vkey, (ushort)(scan & 0xFF), (scan & 0xFF00) != 0);
            _physicalKeys.Update(key, false);
            _localKeys.Update(key, false);
        }
        foreach (var button in MouseButtonDefinitions)
        {
            var vkey = button.Kind switch
            {
                MouseButtonKind.Left => 1, MouseButtonKind.Right => 2,
                MouseButtonKind.Middle => 4, MouseButtonKind.X1 => 5, _ => 6
            };
            if ((GetAsyncKeyState(vkey) & 0x8000) == 0) { continue; }
            _physicalMouseButtonsDown.Add(button.Kind);
            _localMouseButtonsDown.Add(button.Kind);
        }
    }

    private void ReleaseLocalKeys()
    {
        foreach (var key in _localKeys.ReleaseAll())
        {
            if (_keyboardReplayMode == KeyboardReplayMode.VirtualKey || key.ScanCode == 0)
                SendVirtualKey(key.VirtualKey, keyUp: true, key.Extended);
            else
                SendScanCode(key.ScanCode, keyUp: true, key.Extended);
        }
    }

    private void ReleaseRemoteKeys()
    {
        foreach (var key in _remoteKeys.ReleaseAll())
            _lan.PublishKeyInput(key.VirtualKey, key.ScanCode, true, key.Extended, null);
    }

    private void RestorePhysicalModifiers(bool remote)
    {
        foreach (var key in _physicalKeys.Snapshot().Where(key => HeldKeyState.IsModifier(key.VirtualKey)))
        {
            if (remote && IsShortcutModifierKey(key.VirtualKey))
                BufferRemoteControlDown(key.VirtualKey, key.ScanCode, key.Extended);
            else if (remote)
                PublishRemoteKey(key.VirtualKey, key.ScanCode, false, key.Extended, null);
            else if (IsCommandKey(key.VirtualKey))
                BufferLocalCommandDown(key.VirtualKey, key.ScanCode, key.Extended);
            else
                ReplayKeyboardHookEvent(key.VirtualKey, key.ScanCode, false, key.Extended);
        }
    }

    private Point2D ClampRemoteTopologyCursor(Point2D point)
    {
        if (_remoteTopologyBounds is null)
        {
            return point;
        }

        return new Point2D(
            Math.Clamp(point.X, _remoteTopologyBounds.Left, _remoteTopologyBounds.Right),
            Math.Clamp(point.Y, _remoteTopologyBounds.Top, _remoteTopologyBounds.Bottom));
    }

    private void ClearRemoteTracking()
    {
        _remoteTopologyDisplayId = null;
        _remoteTopologyCursor = null;
        _remoteTopologyBounds = null;
        ClearRemoteShortcutState();
    }

    private void ReplayOrForwardMouseButtonsAndWheel(RAWMOUSEBUTTONS buttons)
    {
        var flags = buttons.usButtonFlags;
        if (flags == 0)
        {
            return;
        }

        UpdatePhysicalMouseButtons(flags);

        if (_activeTarget == ActiveTarget.Remote) { FlushRemoteBufferedControl(); }

        foreach (var button in MouseButtonDefinitions)
        {
            if ((flags & button.RawDownFlag) != 0)
            {
                if (_activeTarget == ActiveTarget.Remote)
                {
                    ApplyRemoteMouseButton(button, isDown: true);
                }
                else
                {
                    ApplyLocalMouseButton(button, isDown: true);
                }
            }

            if ((flags & button.RawUpFlag) != 0)
            {
                if (_activeTarget == ActiveTarget.Remote)
                {
                    ApplyRemoteMouseButton(button, isDown: false);
                }
                else
                {
                    ApplyLocalMouseButton(button, isDown: false);
                }
            }
        }

        if (_activeTarget == ActiveTarget.Remote)
        {
            if ((flags & RI_MOUSE_WHEEL) != 0)
            {
                _lan.PublishMouseWheel(unchecked((short)buttons.usButtonData), 0);
            }

            if ((flags & RI_MOUSE_HWHEEL) != 0)
            {
                _lan.PublishMouseWheel(0, unchecked((short)buttons.usButtonData));
            }

            return;
        }

        if ((flags & RI_MOUSE_WHEEL) != 0)
        {
            SendMouseInput(0, 0, unchecked((uint)(short)buttons.usButtonData), MOUSEEVENTF_WHEEL);
        }

        if ((flags & RI_MOUSE_HWHEEL) != 0)
        {
            SendMouseInput(0, 0, unchecked((uint)(short)buttons.usButtonData), MOUSEEVENTF_HWHEEL);
        }
    }

    private void UpdatePhysicalMouseButtons(ushort rawFlags)
    {
        foreach (var button in MouseButtonDefinitions)
        {
            if ((rawFlags & button.RawDownFlag) != 0)
            {
                _physicalMouseButtonsDown.Add(button.Kind);
            }

            if ((rawFlags & button.RawUpFlag) != 0)
            {
                _physicalMouseButtonsDown.Remove(button.Kind);
            }
        }
    }

    private void ApplyLocalMouseButton(MouseButtonDefinition button, bool isDown)
    {
        if (isDown && !_localMouseButtonsDown.Add(button.Kind))
        {
            return;
        }

        if (!isDown)
        {
            _localMouseButtonsDown.Remove(button.Kind);
        }

        SendMouseInput(0, 0, button.MouseData, isDown ? button.SendDownFlag : button.SendUpFlag);
        LogMouseButton("local", button, isDown);
    }

    private void ApplyRemoteMouseButton(MouseButtonDefinition button, bool isDown)
    {
        if (isDown && !_remoteMouseButtonsDown.Add(button.Kind))
        {
            return;
        }

        if (!isDown)
        {
            _remoteMouseButtonsDown.Remove(button.Kind);
        }

        _lan.PublishMouseButton(button.ProtocolName, isDown);
        LogMouseButton("remote", button, isDown);
    }

    private void TransferMouseButtonsToRemote()
    {
        ReleaseLocalMouseButtons("handoff to remote");
        PressPhysicalMouseButtonsRemotely("handoff to remote");
    }

    private void PressPhysicalMouseButtonsLocally(string reason)
    {
        foreach (var button in MouseButtonDefinitions)
        {
            if (_physicalMouseButtonsDown.Contains(button.Kind))
            {
                ApplyLocalMouseButton(button, isDown: true);
            }
        }

        LogMouseButtonState(reason);
    }

    private void PressPhysicalMouseButtonsRemotely(string reason)
    {
        if (_physicalMouseButtonsDown.Count > 0) { FlushRemoteBufferedControl(); }
        foreach (var button in MouseButtonDefinitions)
        {
            if (_physicalMouseButtonsDown.Contains(button.Kind))
            {
                ApplyRemoteMouseButton(button, isDown: true);
            }
        }

        LogMouseButtonState(reason);
    }

    private void ReleaseAllTrackedMouseButtons(string reason)
    {
        ReleaseLocalMouseButtons(reason);
        ReleaseRemoteMouseButtons(reason);
        _physicalMouseButtonsDown.Clear();
    }

    private void ReleaseLocalMouseButtons(string reason)
    {
        var hadButtons = _localMouseButtonsDown.Count > 0;
        foreach (var button in MouseButtonDefinitions)
        {
            if (_localMouseButtonsDown.Contains(button.Kind))
            {
                SendMouseInput(0, 0, button.MouseData, button.SendUpFlag);
            }
        }

        _localMouseButtonsDown.Clear();
        if (hadButtons)
        {
            LogMouseButtonState(reason);
        }
    }

    private void ReleaseRemoteMouseButtons(string reason)
    {
        var hadButtons = _remoteMouseButtonsDown.Count > 0;
        foreach (var button in MouseButtonDefinitions)
        {
            if (_remoteMouseButtonsDown.Contains(button.Kind))
            {
                _lan.PublishMouseButton(button.ProtocolName, isDown: false);
            }
        }

        _remoteMouseButtonsDown.Clear();
        if (hadButtons)
        {
            LogMouseButtonState(reason);
        }
    }

    private void ClearMouseButtonTracking()
    {
        _physicalMouseButtonsDown.Clear();
        _localMouseButtonsDown.Clear();
        _remoteMouseButtonsDown.Clear();
    }

    private void LogMouseButton(string target, MouseButtonDefinition button, bool isDown)
    {
        Console.WriteLine(
            $"{_clock.Elapsed:hh\\:mm\\:ss\\.fff} MOUSEBUTTON {(isDown ? "DOWN " : "UP   ")} " +
            $"button={button.ProtocolName} target={target} " +
            $"physical={FormatMouseButtons(_physicalMouseButtonsDown)} " +
            $"local={FormatMouseButtons(_localMouseButtonsDown)} " +
            $"remote={FormatMouseButtons(_remoteMouseButtonsDown)}");
    }

    private void LogMouseButtonState(string reason)
    {
        Console.WriteLine(
            $"{_clock.Elapsed:hh\\:mm\\:ss\\.fff} MOUSEBUTTON state reason={reason} " +
            $"physical={FormatMouseButtons(_physicalMouseButtonsDown)} " +
            $"local={FormatMouseButtons(_localMouseButtonsDown)} " +
            $"remote={FormatMouseButtons(_remoteMouseButtonsDown)}");
    }

    private static string FormatMouseButtons(HashSet<MouseButtonKind> buttons)
    {
        if (buttons.Count == 0)
        {
            return "none";
        }

        return string.Join(",", MouseButtonDefinitions
            .Where(button => buttons.Contains(button.Kind))
            .Select(button => button.ProtocolName));
    }

    private void ResetVirtualCursorToRealCursor()
    {
        _bounds = GetVirtualDesktopBounds();
        _lan.UpdateLocalBounds(_bounds);
        var cursor = GetCursorPositionOrThrow();
        _virtualX = Math.Clamp(cursor.X, _bounds.Left, _bounds.Right);
        _virtualY = Math.Clamp(cursor.Y, _bounds.Top, _bounds.Bottom);
        _edgeArmed = IsSafelyInside(_virtualX, _virtualY);
        Console.WriteLine($"Virtual cursor reset to real cursor: ({_virtualX},{_virtualY}) bounds={_bounds}");
    }

    private void PrintState(string reason)
    {
        var real = GetCursorPositionOrThrow();
        Console.WriteLine(
            $"STATE {reason}: bridge={(_bridgeActive ? "ON" : "OFF")} " +
            $"real=({real.X},{real.Y}) virtual=({_virtualX},{_virtualY}) " +
            $"target={_activeTarget}{(_remoteTargetDeviceId is null ? string.Empty : $":{_remoteTargetDeviceId}")} " +
            $"keyboardReplay={_keyboardReplayMode} bounds={_bounds}");
        Console.WriteLine($"STATE {reason}: {_topologyStatus}");
    }

    private void CycleKeyboardReplayMode()
    {
        _keyboardReplayMode = _keyboardReplayMode switch
        {
            KeyboardReplayMode.UnicodePrintable => KeyboardReplayMode.ScanCode,
            KeyboardReplayMode.ScanCode => KeyboardReplayMode.VirtualKey,
            _ => KeyboardReplayMode.UnicodePrintable
        };

        Console.WriteLine($"Keyboard replay mode: {_keyboardReplayMode}");
    }

    private void ReloadTopology(bool printStatus)
    {
        TopologyMap? topology;
        string status;
        var loaded = _topologyPath is null
            ? TopologyMap.TryLoadForLocalDevice(_lan.LocalDeviceId, out topology, out status)
            : TopologyMap.TryLoad(_topologyPath, _lan.LocalDeviceId, out topology, out status);
        if (loaded)
        {
            _topology = topology;
        }
        else
        {
            _topology = null;
        }

        _topologyStatus = status;
        if (printStatus)
        {
            Console.WriteLine($"Topology: {_topologyStatus}");
        }
    }

    private void UpdateKeyboardState(ushort vkey, bool isKeyUp)
    {
        SetKeyboardState(vkey, isKeyUp);
        switch (vkey)
        {
            case VK_LSHIFT:
            case VK_RSHIFT:
                SetKeyboardState(VK_SHIFT, isKeyUp);
                break;
            case VK_LCONTROL:
            case VK_RCONTROL:
                SetKeyboardState(VK_CONTROL, isKeyUp);
                break;
            case VK_LMENU:
            case VK_RMENU:
                SetKeyboardState(VK_MENU, isKeyUp);
                break;
        }
    }

    private void SetKeyboardState(ushort vkey, bool isKeyUp)
    {
        if (vkey < _keyboardState.Length)
        {
            _keyboardState[vkey] = isKeyUp ? (byte)0 : (byte)0x80;
        }
    }

    private bool IsShortcutModifierActive()
    {
        return IsKeyStateDown(VK_CONTROL)
            || IsKeyStateDown(VK_MENU)
            || IsKeyStateDown(VK_LWIN)
            || IsKeyStateDown(VK_RWIN);
    }

    private bool IsKeyStateDown(ushort vkey)
    {
        return vkey < _keyboardState.Length && (_keyboardState[vkey] & 0x80) != 0;
    }

    private bool TryTranslatePrintableKey(ushort vkey, ushort scanCode, out string text)
    {
        text = string.Empty;

        var buffer = new StringBuilder(8);
        var keyboardState = new byte[256];
        Array.Copy(_keyboardState, keyboardState, keyboardState.Length);

        var result = ToUnicodeEx(vkey, scanCode, keyboardState, buffer, buffer.Capacity, 0, GetKeyboardLayout(0));
        if (result <= 0)
        {
            return false;
        }

        text = buffer.ToString(0, Math.Min(result, buffer.Length));
        return text.Any(c => !char.IsControl(c));
    }

    private static string EscapeForLog(string text)
    {
        return text
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
    }

    private static string EscapeForStatus(string text)
    {
        return text
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }

    private void RegisterForRawInput()
    {
        var devices = new[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = HID_USAGE_PAGE_GENERIC,
                usUsage = HID_USAGE_GENERIC_MOUSE,
                dwFlags = RIDEV_INPUTSINK,
                hwndTarget = Handle
            },
            new RAWINPUTDEVICE
            {
                usUsagePage = HID_USAGE_PAGE_GENERIC,
                usUsage = HID_USAGE_GENERIC_KEYBOARD,
                dwFlags = RIDEV_INPUTSINK,
                hwndTarget = Handle
            }
        };

        if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            throw new InvalidOperationException($"RegisterRawInputDevices failed. Win32 error={Marshal.GetLastWin32Error()}");
        }
    }

    private void InstallHooks()
    {
        using var currentProcess = Process.GetCurrentProcess();
        using var currentModule = currentProcess.MainModule;
        var moduleHandle = currentModule?.ModuleName is null ? IntPtr.Zero : GetModuleHandle(currentModule.ModuleName);

        _mouseHook = SetWindowsHookExMouse(WH_MOUSE_LL, _mouseHookProc, moduleHandle, 0);
        if (_mouseHook == IntPtr.Zero)
        {
            throw new InvalidOperationException($"SetWindowsHookEx mouse failed. Win32 error={Marshal.GetLastWin32Error()}");
        }

        _keyboardHook = SetWindowsHookExKeyboard(WH_KEYBOARD_LL, _keyboardHookProc, moduleHandle, 0);
        if (_keyboardHook == IntPtr.Zero)
        {
            throw new InvalidOperationException($"SetWindowsHookEx keyboard failed. Win32 error={Marshal.GetLastWin32Error()}");
        }
    }

    private void UninstallHooks()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
    }

    private static void SendAbsoluteMousePosition(int x, int y)
    {
        var bounds = GetVirtualDesktopBounds();
        var normalizedX = NormalizeAbsoluteCoordinate(x, bounds.Left, bounds.Width);
        var normalizedY = NormalizeAbsoluteCoordinate(y, bounds.Top, bounds.Height);
        SendMouseInput(normalizedX, normalizedY, 0, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK);
    }

    private static int NormalizeAbsoluteCoordinate(int value, int origin, int size)
    {
        if (size <= 1)
        {
            return 0;
        }

        var normalized = (int)Math.Round((value - origin) * 65535.0 / (size - 1));
        return Math.Clamp(normalized, 0, 65535);
    }

    private static void SendScanCode(ushort scanCode, bool keyUp, bool extended)
    {
        if (scanCode == 0)
        {
            return;
        }

        var flags = KEYEVENTF_SCANCODE;
        if (keyUp)
        {
            flags |= KEYEVENTF_KEYUP;
        }

        if (extended)
        {
            flags |= KEYEVENTF_EXTENDEDKEY;
        }

        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            union = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = scanCode,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = BatCaveInjectedExtraInfo
                }
            }
        };

        SendInputs(input);
    }

    private static void SendVirtualKey(ushort virtualKey, bool keyUp, bool extended)
    {
        var flags = keyUp ? KEYEVENTF_KEYUP : 0;
        if (extended)
        {
            flags |= KEYEVENTF_EXTENDEDKEY;
        }

        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            union = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = virtualKey,
                    wScan = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = BatCaveInjectedExtraInfo
                }
            }
        };

        SendInputs(input);
    }

    private static void SendUnicodeText(string text)
    {
        foreach (var character in text)
        {
            SendInputs(UnicodeInput(character, keyUp: false), UnicodeInput(character, keyUp: true));
        }
    }

    private static INPUT UnicodeInput(char character, bool keyUp)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            union = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = character,
                    dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0),
                    time = 0,
                    dwExtraInfo = BatCaveInjectedExtraInfo
                }
            }
        };
    }

    private static void SendMouseInput(int dx, int dy, uint mouseData, uint flags)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            union = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    dx = dx,
                    dy = dy,
                    mouseData = mouseData,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = BatCaveInjectedExtraInfo
                }
            }
        };

        SendInputs(input);
    }

    private static void SendInputs(params INPUT[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            Console.WriteLine($"SendInput failed. sent={sent}/{inputs.Length}, win32={Marshal.GetLastWin32Error()}");
        }
    }

    private static POINT GetCursorPositionOrThrow()
    {
        if (!GetCursorPos(out var point))
        {
            throw new InvalidOperationException($"GetCursorPos failed. Win32 error={Marshal.GetLastWin32Error()}");
        }

        return point;
    }

    private static VirtualDesktopBounds GetVirtualDesktopBounds()
    {
        var left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        return new VirtualDesktopBounds(left, top, width, height);
    }
}
