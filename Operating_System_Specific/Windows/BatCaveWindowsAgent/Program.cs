using System.Text;
using System.Windows.Forms;

namespace BatCave.WindowsAgent;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--verify-install")
        {
            // Packaging smoke check: exercises the native app host and bundled
            // WinForms runtime without creating identities or intercepting input.
            _ = SystemInformation.VirtualScreen;
            Console.WriteLine("BATCAVE_INSTALL_OK runtime=" + Environment.Version);
            return;
        }
#if SECURE_DESKTOP_ONLY
        RunSecureDesktop();
        return;
#else
        if (args.Length == 0 || args.Contains("--desktop"))
        {
            RunSecureDesktop();
            return;
        }
        if (!args.Contains("--insecure-loopback-development"))
            throw new ArgumentException("Legacy commands require --insecure-loopback-development; plaintext is loopback-only.");
        var launchOptions = ParseLaunchOptions(args);
        using var consoleLog = InstallConsoleLog();

        Console.WriteLine("BatCave Windows Agent");
        Console.WriteLine($"Console log: {consoleLog.Path}");
        Console.WriteLine();
        Console.WriteLine("Local commands:");
        Console.WriteLine("  A [port]  Start automatic 60-second topology handoff test after a 5-second countdown.");
        Console.WriteLine("  B  Start bridge mode for 30 seconds.");
        Console.WriteLine("  S  Stop bridge mode.");
        Console.WriteLine("  R  Reset virtual cursor to current Windows cursor.");
        Console.WriteLine("  P  Print current state.");
        Console.WriteLine("  K  Cycle keyboard replay mode.");
        Console.WriteLine("  T  Reload topology.local.json.");
        Console.WriteLine("  L [port]  Start LAN listener.");
        Console.WriteLine("  C <host> [port]  Connect to LAN peer.");
        Console.WriteLine("  N  Print LAN state.");
        Console.WriteLine("  Q  Quit.");
        Console.WriteLine();
        Console.WriteLine("Safety:");
        Console.WriteLine("  Escape, F12, or Pause stops bridge mode immediately.");
        Console.WriteLine("  Bridge mode auto-stops after 30 seconds.");
        Console.WriteLine("  Automatic handoff tests auto-stop after 60 seconds.");
        Console.WriteLine();
        Console.WriteLine("Launch options:");
        Console.WriteLine("  --auto-handoff-test [--port 47870] [--start-delay 5] [--duration 60]");
        Console.WriteLine("  --app-backend [--port 47870]");
        Console.WriteLine();

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var lan = new LanBridgePeer();
        using var agent = new LocalBridgeAgent(lan);
        // Create the native handle on its owning thread before any LAN or stdin
        // callback can post work to the Windows message loop.
        _ = agent.Handle;

        if (launchOptions.AppBackend)
        {
            StartAppBackend(agent, lan, launchOptions.Port);
            new Thread(() => ReadAppCommands(agent, lan))
            {
                IsBackground = true, Name = "AppCommandReader"
            }.Start();
        }
        else
        {
            var commandThread = new Thread(() => ReadCommands(agent, lan))
            {
                IsBackground = true,
                Name = "CommandReader"
            };
            commandThread.Start();
        }

        if (launchOptions.AutoHandoffTest)
        {
            StartAutoHandoffSequence(agent, lan, launchOptions);
        }

        Application.Run(agent);
        // OnFormClosed queues remote key/button releases and control.testStop.
        // Let those reach the peer before disposing its socket.
        lan.FlushAsync().GetAwaiter().GetResult();
#endif
    }

    private static void RunSecureDesktop()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatCave", "SecurePreview");
        using var mutex = new Mutex(true, "Local\\BatCaveSecureDesktop", out var firstInstance);
        if (!firstInstance) { MessageBox.Show("BatCave is already running. Open it from the system tray."); return; }
        try
        {
            Directory.CreateDirectory(directory);
            using var consoleLog = InstallConsoleLog(Path.Combine(directory, "Logs"));
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var identity = DeviceIdentityStore.OpenOrCreate(directory);
            var approvals = new ApprovedPeerStore(directory);
            using var window = new DesktopWindow(identity, approvals, directory);
            Application.Run(window);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "BatCave could not start", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private static void ReadCommands(LocalBridgeAgent agent, LanBridgePeer lan)
    {
        while (!agent.IsDisposed)
        {
            Console.Write("Command [A/B/S/R/P/K/T/L/C/N/Q]: ");
            var line = Console.ReadLine();
            if (line is null)
            {
                return;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            try
            {
                switch (parts[0].ToUpperInvariant())
                {
                    case "A":
                        StartAutoHandoffSequence(
                            agent,
                            lan,
                            new LaunchOptions(
                                AutoHandoffTest: true,
                                AppBackend: false,
                                Port: ParsePort(parts, 1),
                                StartDelay: TimeSpan.FromSeconds(5),
                                Duration: TimeSpan.FromSeconds(60)));
                        break;
                    case "B":
                        agent.PostStartBridge(TimeSpan.FromSeconds(30));
                        break;
                    case "S":
                        agent.PostStopBridge("command");
                        break;
                    case "R":
                        agent.PostResetVirtualCursor();
                        break;
                    case "P":
                        agent.PostPrintState();
                        break;
                    case "K":
                        agent.PostCycleKeyboardReplayMode();
                        break;
                    case "T":
                        agent.PostReloadTopology();
                        break;
                    case "L":
                        lan.StartListeningAsync(ParsePort(parts, 1)).GetAwaiter().GetResult();
                        break;
                    case "C":
                        if (parts.Length < 2)
                        {
                            Console.WriteLine($"Usage: C <host> [port]");
                            break;
                        }

                        lan.ConnectAsync(parts[1], ParsePort(parts, 2)).GetAwaiter().GetResult();
                        break;
                    case "N":
                        lan.PrintState();
                        break;
                    case "Q":
                        agent.PostQuit();
                        return;
                    default:
                        Console.WriteLine("Unknown command.");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Command failed: {ex.Message}");
            }
        }
    }

    private static int ParsePort(string[] parts, int index)
    {
        if (parts.Length <= index)
        {
            return LanBridgePeer.DefaultPort;
        }

        if (!int.TryParse(parts[index], out var port) || port <= 0 || port > 65535)
        {
            throw new ArgumentException($"Invalid TCP port: {parts[index]}");
        }

        return port;
    }

    private static LaunchOptions ParseLaunchOptions(string[] args)
    {
        var autoHandoffTest = false;
        var appBackend = false;
        var port = LanBridgePeer.DefaultPort;
        var startDelay = TimeSpan.FromSeconds(5);
        var duration = TimeSpan.FromSeconds(60);

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--auto-handoff-test":
                    autoHandoffTest = true;
                    break;
                case "--app-backend":
                    appBackend = true;
                    break;
                case "--port":
                    port = ParsePortValue(ReadOptionValue(args, ref i, "--port"));
                    break;
                case "--start-delay":
                    startDelay = TimeSpan.FromSeconds(ParsePositiveSeconds(ReadOptionValue(args, ref i, "--start-delay")));
                    break;
                case "--duration":
                    duration = TimeSpan.FromSeconds(ParsePositiveSeconds(ReadOptionValue(args, ref i, "--duration")));
                    break;
            }
        }

        return new LaunchOptions(autoHandoffTest, appBackend, port, startDelay, duration);
    }

    private static string ReadOptionValue(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{optionName} requires a value.");
        }

        index++;
        return args[index];
    }

    private static int ParsePortValue(string value)
    {
        if (!int.TryParse(value, out var port) || port <= 0 || port > 65535)
        {
            throw new ArgumentException($"Invalid TCP port: {value}");
        }

        return port;
    }

    private static int ParsePositiveSeconds(string value)
    {
        if (!int.TryParse(value, out var seconds) || seconds <= 0)
        {
            throw new ArgumentException($"Invalid seconds value: {value}");
        }

        return seconds;
    }

    private static void StartAutoHandoffSequence(LocalBridgeAgent agent, LanBridgePeer lan, LaunchOptions options)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await lan.StartListeningAsync(options.Port).ConfigureAwait(false);
                Console.WriteLine();
                Console.WriteLine($"AUTO HANDOFF TEST armed on tcp://127.0.0.1:{options.Port}.");
                Console.WriteLine($"Test starts in {options.StartDelay.TotalSeconds:0} seconds and runs for {options.Duration.TotalSeconds:0} seconds.");
                Console.WriteLine("User action during the test: move across the configured bridge and type on each screen.");
                Console.WriteLine("Escape, F12, or Pause terminates early.");
                Console.WriteLine();

                for (var remaining = (int)options.StartDelay.TotalSeconds; remaining > 0; remaining--)
                {
                    Console.WriteLine($"HANDOFF TEST STARTS IN {remaining}");
                    await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }

                agent.PostStartHandoffTest(options.Duration);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Automatic handoff test failed: {ex.Message}");
            }
        });
    }

    private static void ReadAppCommands(LocalBridgeAgent agent, LanBridgePeer lan)
    {
        while (Console.ReadLine() is { } line)
        {
            switch (line.Trim().ToUpperInvariant())
            {
                case "STOP": agent.PostStopBridge("paused by user"); break;
                case "START":
                    if (lan.HasPeer) { agent.PostStartPersistentHandoff(); }
                    else { Console.WriteLine("APPSTATUS waiting reason=no-peer"); }
                    break;
                case "QUIT": agent.PostQuit(); return;
            }
        }
    }

    private static void StartAppBackend(LocalBridgeAgent agent, LanBridgePeer lan, int port)
    {
        lan.PeerConnected += peerLabel =>
        {
            Console.WriteLine($"APPSTATUS peer-ready peer={EscapeForStatus(peerLabel)}");
            agent.PostStartPersistentHandoff();
        };
        lan.PeerDisconnected += peerLabel =>
        {
            agent.PostStopBridge($"peer disconnected: {peerLabel}");
            Console.WriteLine($"APPSTATUS waiting reason=peer-disconnected peer={EscapeForStatus(peerLabel)}");
        };
        try
        {
            Console.WriteLine($"APPSTATUS starting port={port}");
            lan.StartListeningAsync(port).GetAwaiter().GetResult();
            Console.WriteLine($"APPSTATUS listening port={port}");
            Console.WriteLine("APPSTATUS waiting reason=no-peer");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"APPSTATUS trouble reason={EscapeForStatus(ex.Message)}");
        }
    }

    private static string EscapeForStatus(string value)
    {
        return value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }

    private sealed record LaunchOptions(
        bool AutoHandoffTest,
        bool AppBackend,
        int Port,
        TimeSpan StartDelay,
        TimeSpan Duration);

    private static ConsoleLog InstallConsoleLog(string? logDirectory = null)
    {
        var directory = logDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "Logs");
        Directory.CreateDirectory(directory);
        foreach (var old in Directory.EnumerateFiles(directory, "windows-agent-*.log")
            .OrderByDescending(File.GetLastWriteTimeUtc).Skip(7))
        {
            try { File.Delete(old); }
            catch (IOException) { } // another instance may still hold its log open
            catch (UnauthorizedAccessException) { }
        }
        var path = Path.Combine(directory, $"windows-agent-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log");
        var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };

        Console.SetOut(new TeeTextWriter(Console.Out, writer));
        return new ConsoleLog(path, writer);
    }

    private sealed class ConsoleLog : IDisposable
    {
        private readonly TextWriter _writer;

        public ConsoleLog(string path, TextWriter writer)
        {
            Path = path;
            _writer = writer;
        }

        public string Path { get; }

        public void Dispose()
        {
            _writer.Dispose();
        }
    }

    private sealed class TeeTextWriter : TextWriter
    {
        private readonly TextWriter _primary;
        private readonly TextWriter _secondary;
        private readonly object _lock = new();
        private int _logBytes;
        private const int MaximumLogBytes = 8 * 1024 * 1024;

        private void WriteBoundedLog(string value)
        {
            var bytes = Encoding.UTF8.GetByteCount(value);
            if (bytes > MaximumLogBytes - _logBytes) { return; }
            _secondary.Write(value);
            _logBytes += bytes;
        }

        public TeeTextWriter(TextWriter primary, TextWriter secondary)
        {
            _primary = primary;
            _secondary = secondary;
        }

        public override Encoding Encoding => _primary.Encoding;

        public override void Write(char value)
        {
            lock (_lock)
            {
                _primary.Write(value);
                WriteBoundedLog(value.ToString());
            }
        }

        public override void Write(string? value)
        {
            lock (_lock)
            {
                _primary.Write(value);
                if (value is not null) { WriteBoundedLog(value); }
            }
        }

        public override void WriteLine()
        {
            lock (_lock)
            {
                _primary.WriteLine();
                WriteBoundedLog(Environment.NewLine);
            }
        }

        public override void WriteLine(string? value)
        {
            // Routine diagnostics must not become a recording of typed content.
            // The prototype's per-key logs can reveal passwords even without text.
            if (value is not null && (value.Contains("vk=", StringComparison.Ordinal)
                || value.Contains("scan=", StringComparison.Ordinal)
                || value.Contains("text=", StringComparison.Ordinal))) { return; }
            lock (_lock)
            {
                _primary.WriteLine(value);
                WriteBoundedLog((value ?? string.Empty) + Environment.NewLine);
            }
        }

        public override void Flush()
        {
            lock (_lock)
            {
                _primary.Flush();
                _secondary.Flush();
            }
        }
    }
}
