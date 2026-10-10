using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BatCave.WindowsAgent;
using BatCave.Universal.Topology;

var output = Console.Out;
Console.SetOut(TextWriter.Synchronized(new StringWriter()));
try
{
    var held = new HeldKeyState();
    var shift = new HeldKey(0xA0, 0x2A, false);
    held.Update(shift, false);
    held.Update(shift, false); // keyboard autorepeat must not create duplicate releases
    held.Update(new HeldKey(0x41, 0x1E, false), false);
    held.Update(new HeldKey(0x41, 0x1E, false), true);
    Require(held.ReleaseAll().SequenceEqual(new[] { shift }), "held keys release exactly once");
    Require(held.ReleaseAll().Length == 0, "second key cleanup is empty");
    Require(HeldKeyState.IsModifier(0xA0) && !HeldKeyState.IsModifier(0x41), "only modifiers restore across a handoff");

    var reservation = new TcpListener(IPAddress.Loopback, 0);
    reservation.Start();
    var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
    reservation.Stop();
    using var lan = new LanBridgePeer();
    lan.UpdateLocalBounds(new VirtualDesktopBounds(0, 0, 1920, 1080));
    var connected = 0;
    var disconnected = 0;
    var clipboard = 0;
    lan.PeerConnected += _ => Interlocked.Increment(ref connected);
    lan.PeerDisconnected += _ => Interlocked.Increment(ref disconnected);
    lan.ClipboardTextReceived += (_, _) => Interlocked.Increment(ref clipboard);
    await lan.StartListeningAsync(port);

    using var first = new TcpClient();
    await first.ConnectAsync(IPAddress.Loopback, port);
    using var reader = new StreamReader(first.GetStream(), Encoding.UTF8, false, leaveOpen: true);
    using var writer = new StreamWriter(first.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
    var hello = await Read(reader);
    Require(hello.GetProperty("type").GetString() == "device.hello", "hello is first on wire");
    Require(!lan.HasPeer && connected == 0, "TCP alone cannot arm input control");
    await writer.WriteLineAsync("[]");
    await writer.WriteLineAsync("{\"protocol\":7}");
    await writer.WriteLineAsync("broken JSON");
    await Send(writer, "device.hello", "macos:test", new { deviceId = "macos:wrong" });
    await Send(writer, "clipboard.text", "macos:test", new { format = "text/plain", text = "before hello" });
    await Send(writer, "device.hello", "macos:test", new { deviceId = "macos:test" });
    await Until(() => lan.HasPeer && Volatile.Read(ref connected) == 1);
    Require(lan.CurrentPeerDeviceId == "macos:test" && clipboard == 0, "hello identity is required");
    await Send(writer, "clipboard.text", "macos:other", new { format = "text/plain", text = "wrong source" });
    await Send(writer, "clipboard.text", "macos:test", new { format = "text/plain", text = "right source" });
    await Until(() => Volatile.Read(ref clipboard) == 1);

    // Exercise sustained high-rate input followed by button/key releases and return
    // handoff. The receiving OS must see all releases before it changes ownership.
    var handoff = new TopologyHandoff("link", .5, "windows:display", "right", .5,
        1920, 540, "macos:test", "mac:display", "left", .5, 0, 540);
    lan.PublishCursorHandoff(handoff);
    for (var i = 0; i < 1500; i++) { lan.PublishMouseDelta(i, -i); }
    lan.PublishMouseButton("left", false);
    lan.PublishKeyInput(0xA0, 0x2A, true, false, null);
    lan.PublishCursorHandoff(handoff with { TargetDeviceId = lan.LocalDeviceId });
    Require((await Read(reader)).GetProperty("type").GetString() == "cursor.handoff", "handoff precedes input");
    for (var i = 0; i < 1500; i++)
    {
        var message = await Read(reader);
        Require(message.GetProperty("payload").GetProperty("deltaX").GetInt32() == i, "mouse events retain order");
    }
    Require((await Read(reader)).GetProperty("type").GetString() == "input.mouseButton", "button release precedes return");
    Require((await Read(reader)).GetProperty("type").GetString() == "input.key", "key release precedes return");
    Require((await Read(reader)).GetProperty("type").GetString() == "cursor.handoff", "return follows releases");

    using var second = new TcpClient();
    await second.ConnectAsync(IPAddress.Loopback, port);
    using var reader2 = new StreamReader(second.GetStream(), Encoding.UTF8, false, leaveOpen: true);
    using var writer2 = new StreamWriter(second.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
    Require((await Read(reader2)).GetProperty("type").GetString() == "device.hello", "replacement gets fresh hello");
    await Until(() => !lan.HasPeer && Volatile.Read(ref disconnected) == 1);
    await Send(writer2, "device.hello", "macos:replacement", new { deviceId = "macos:replacement" });
    await Until(() => lan.HasPeer && Volatile.Read(ref connected) == 2);
    second.Close();
    await Until(() => !lan.HasPeer && Volatile.Read(ref disconnected) == 2);
    Require(lan.CurrentPeerDeviceId is null, "disconnect clears identity");

    using var probe = new TcpClient();
    await probe.ConnectAsync(IPAddress.Loopback, port);
    await Until(() => Volatile.Read(ref disconnected) == 3, 8000);
    Require(connected == 2 && !lan.HasPeer, "silent TCP probe times out without activating bridge");

    output.WriteLine("PASS: held-key cleanup, hello gating, malformed messages, source identity, 1500-event ordering, connection replacement, disconnect, handshake timeout.");
}
catch (Exception ex)
{
    output.WriteLine(ex.ToString());
    Environment.ExitCode = 1;
}
finally { Console.SetOut(output); }

static void Require(bool condition, string reason)
{
    if (!condition) { throw new Exception(reason); }
}

static async Task Until(Func<bool> condition, int timeout = 3000)
{
    var ends = DateTime.UtcNow.AddMilliseconds(timeout);
    while (!condition())
    {
        if (DateTime.UtcNow >= ends) { throw new TimeoutException("Expected session state was not reached"); }
        await Task.Delay(10);
    }
}

static async Task<JsonElement> Read(StreamReader reader)
{
    var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
    using var doc = JsonDocument.Parse(line ?? throw new Exception("Unexpected disconnect"));
    return doc.RootElement.Clone();
}

static Task Send(StreamWriter writer, string type, string source, object payload) =>
    writer.WriteLineAsync(JsonSerializer.Serialize(BatCaveEnvelope.Create(source, type, payload), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
