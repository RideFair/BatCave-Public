using System.Runtime.InteropServices;
using BatCave.AudioPreview.Native;

var tests = new (string Name, Action Run)[]
{
    ("PnP classification never guesses from a name", Classification),
    ("Bounded buffer rejects overflow and preserves frame order", Ring),
    ("Non-finite samples and closed buffers remain safe", Sanitization),
    ("Capture packet assembly creates owned 10 ms blocks", Assembly),
    ("Discontinuity discards a partial capture block", Discontinuity),
    ("Render provider follows partial device-buffer boundaries", Provider),
    ("Provider replacement drops old-route leftovers", ProviderReplacement),
    ("Malformed provider output fails before rendering", MalformedProvider),
    ("Process-loopback activation excludes this process tree", ProcessParameters),
    ("Float wire format has the expected native ABI", Format),
    ("Activation callback is an agile COM object", AgileCallback),
    ("Session construction and disposal do not activate audio", NoActivation),
    ("Synthetic stream disposal stops and cleans its worker", WorkerLifecycle),
    ("Synthetic driver failure stops its worker and reports an error", WorkerFailure),
    ("Late activation after cancellation releases safely", LateActivation)
};
foreach (var test in tests)
{
    test.Run();
    Console.WriteLine($"PASS: {test.Name}");
}
if (args.Contains("--enumerate-count", StringComparer.Ordinal))
{
    var outputs = WindowsAudioDevices.EnumerateOutputs();
    // This opt-in smoke check reports counts only. Never print endpoint names or IDs.
    Console.WriteLine($"Read-only endpoint enumeration passed: {outputs.Count} active outputs; {outputs.Count(x => x.Transport == AudioTransport.Bluetooth)} Bluetooth; {outputs.Count(x => x.Transport == AudioTransport.Unknown)} unknown transport.");
}
Console.WriteLine($"All {tests.Length} synthetic/native-layout tests passed. No audio was captured or played.");

static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
static void Equal(ReadOnlySpan<float> actual, ReadOnlySpan<float> expected)
{
    Assert(actual.SequenceEqual(expected), "Sample order or values changed unexpectedly.");
}

static void Classification()
{
    Assert(WindowsAudioDevices.ClassifyInstanceId("BTHENUM\\synthetic") == AudioTransport.Bluetooth, "Bluetooth enumerator not detected.");
    Assert(WindowsAudioDevices.ClassifyInstanceId("bthhfEnum\\synthetic") == AudioTransport.Bluetooth, "Hands-free enumerator not detected.");
    Assert(WindowsAudioDevices.ClassifyInstanceId("BTHLEDEVICE\\synthetic") == AudioTransport.Bluetooth, "Bluetooth LE enumerator not detected.");
    Assert(WindowsAudioDevices.ClassifyInstanceId("USB\\synthetic") == AudioTransport.Other, "USB bus not classified.");
    Assert(WindowsAudioDevices.ClassifyInstanceId("HDAUDIO\\synthetic") == AudioTransport.Other, "HDAUDIO bus not classified.");
    Assert(WindowsAudioDevices.ClassifyInstanceId("Headphones Bluetooth") == AudioTransport.Unknown, "Friendly-name guessing is forbidden.");
    Assert(WindowsAudioDevices.ClassifyInstanceId("SWD\\Bluetooth headset") == AudioTransport.Unknown, "An opaque endpoint name is not bus evidence.");
    Assert(WindowsAudioDevices.ClassifyInstanceId("BTHENUMfake\\synthetic") == AudioTransport.Unknown, "Enumerator must match exactly.");
}

static void Ring()
{
    var ring = new BoundedAudioBuffer(3);
    Assert(ring.TryWrite([.1f, .2f, .3f, .4f]), "Initial write failed.");
    Assert(!ring.TryWrite([.5f, .6f, .7f, .8f]), "Overflow must reject the entire new block.");
    float[] first = new float[2]; ring.Read(first); Equal(first, [.1f, .2f]);
    Assert(ring.TryWrite([.5f, .6f, .7f, .8f]), "Wrap-around write failed.");
    float[] rest = new float[8]; Assert(ring.Read(rest) == 6, "Wrong buffered count.");
    Equal(rest, [.3f, .4f, .5f, .6f, .7f, .8f, 0, 0]);
    Assert(!ring.TryWrite([1]), "Partial stereo frames must be rejected.");
}

static void Sanitization()
{
    var ring = new BoundedAudioBuffer(3);
    ring.TryWrite([float.NaN, float.PositiveInfinity, 3, -3]);
    float[] actual = new float[4]; ring.Read(actual); Equal(actual, [0, 0, 1, -1]);
    ring.TryWrite([.4f, .4f]); ring.Close();
    Assert(!ring.TryWrite([1, 1]), "Closed buffer accepted audio.");
    Assert(ring.Read(actual) == 0 && actual.All(x => x == 0), "Closing must discard queued audio.");
}

static void Assembly()
{
    var assembler = new AudioBlockAssembler();
    var blocks = new List<float[]>();
    float[] packet = Enumerable.Repeat(.25f, 2000).ToArray();
    assembler.Push(packet.AsSpan(0, 200), blocks.Add);
    assembler.Push(packet.AsSpan(200, 1800), blocks.Add);
    Assert(blocks.Count == 2 && blocks.All(x => x.Length == 960), "Packets must assemble into exact 10 ms blocks.");
    Assert(!ReferenceEquals(blocks[0], blocks[1]), "Published block storage must not be reused.");
    blocks[0][0] = .9f;
    Assert(blocks[1][0] == .25f, "Mutating one delivered block must not change another.");
}

static void Discontinuity()
{
    var assembler = new AudioBlockAssembler(); var blocks = new List<float[]>();
    assembler.Push(Enumerable.Repeat(.8f, 200).ToArray(), blocks.Add);
    assembler.Reset();
    assembler.Push(new float[960], blocks.Add);
    Assert(blocks.Count == 1 && blocks[0].All(x => x == 0), "A discontinuity must discard prior partial samples.");
}

static void Provider()
{
    var buffer = new ProviderAudioBuffer(); int calls = 0;
    Func<float[]?> provider = () => Enumerable.Repeat(++calls / 10f, 960).ToArray();
    float[] first = new float[240]; buffer.Read(first, provider);
    float[] next = new float[1200]; buffer.Read(next, provider);
    Assert(calls == 2, "Provider must advance by exact blocks across short render buffers.");
    Assert(first.All(x => x == .1f) && next.Take(720).All(x => x == .1f) && next.Skip(720).All(x => x == .2f), "Partial provider blocks lost their position.");
    var silent = new ProviderAudioBuffer(); float[] result = Enumerable.Repeat(1f, 100).ToArray();
    Assert(silent.Read(result, () => null) == 0 && result.All(x => x == 0), "Null provider must produce silence.");
}

static void ProviderReplacement()
{
    var buffer = new ProviderAudioBuffer(); float[] result = new float[200];
    Func<float[]?> oldRoute = () => Enumerable.Repeat(.1f, 960).ToArray();
    Func<float[]?> newRoute = () => Enumerable.Repeat(.2f, 960).ToArray();
    buffer.Read(result, oldRoute); buffer.Read(result, newRoute);
    Assert(result.All(x => x == .2f), "Changing route must not replay old partial data.");
}

static void MalformedProvider()
{
    try { new ProviderAudioBuffer().Read(new float[960], () => new float[959]); }
    catch (InvalidOperationException) { return; }
    throw new Exception("Malformed mixer output was accepted.");
}

static void ProcessParameters()
{
    using var parameters = new ProcessCaptureParameters(1234);
    Assert(Marshal.ReadInt16(parameters.Pointer) == 65, "Activation must use VT_BLOB.");
    Assert(Marshal.ReadInt32(parameters.Pointer, 8) == 12, "Wrong activation payload size.");
    IntPtr payload = Marshal.ReadIntPtr(parameters.Pointer, IntPtr.Size == 8 ? 16 : 12);
    Assert(Marshal.ReadInt32(payload) == 1 && Marshal.ReadInt32(payload, 4) == 1234 && Marshal.ReadInt32(payload, 8) == 1,
        "Capture must exclude the supplied process and its descendants.");
}

static void Format()
{
    IntPtr pointer = CoreAudioInterop.CreateWireFormat();
    try
    {
        var format = Marshal.PtrToStructure<WaveFormat>(pointer);
        Assert(Marshal.SizeOf<WaveFormat>() == 18, "WAVEFORMATEX must use the SDK's packed ABI.");
        Assert(format.FormatTag == 3 && format.Channels == 2 && format.SamplesPerSecond == 48000 && format.BlockAlign == 8 &&
            format.BitsPerSample == 32 && format.AverageBytesPerSecond == 384000 && format.ExtraSize == 0, "Wrong native format.");
    }
    finally { Marshal.FreeCoTaskMem(pointer); }
}

static void AgileCallback()
{
    var handler = new AudioActivationHandler();
    IntPtr unknown = Marshal.GetIUnknownForObject(handler);
    try
    {
        var callbackId = typeof(IActivateAudioInterfaceCompletionHandler).GUID;
        Assert(Marshal.QueryInterface(unknown, callbackId, out IntPtr callback) >= 0, "Activation callback is not COM-visible.");
        Marshal.Release(callback);
        var agileId = typeof(IAgileObject).GUID;
        Assert(Marshal.QueryInterface(unknown, agileId, out IntPtr agile) >= 0, "Activation callback must be agile across apartments.");
        Marshal.Release(agile);
    }
    finally { Marshal.Release(unknown); }
}

static void NoActivation()
{
    var capture = new WasapiSystemCapture();
    Assert(!capture.IsRunning, "Constructing a capture must not start it.");
    capture.Dispose(); capture.Dispose();
    var output = new WasapiOutput("synthetic-endpoint");
    output.SetFrameProvider(() => new float[960]);
    Assert(!output.IsRunning && !output.TryWrite(new float[960]), "Constructing an output must not play audio.");
    output.Dispose(); output.Dispose();
}

static void WorkerLifecycle()
{
    using var session = new SyntheticAudioSession(false);
    session.Start();
    Assert(session.IsRunning, "Synthetic worker did not start.");
    session.Dispose(); session.Dispose();
    Assert(!session.IsRunning && session.Cleaned, "Disposal did not wait for worker cleanup.");
    try { session.Start(); } catch (ObjectDisposedException) { return; }
    throw new Exception("Disposed session must not restart.");
}

static void WorkerFailure()
{
    using var session = new SyntheticAudioSession(true);
    using var failed = new ManualResetEventSlim(false);
    session.Faulted += _ => failed.Set();
    try { session.Start(); } catch (AudioDeviceException) { }
    Assert(failed.Wait(TimeSpan.FromSeconds(1)), "Driver failure was not reported.");
    session.Dispose();
    Assert(!session.IsRunning && session.Cleaned, "Faulted worker did not clean up.");
}

static void LateActivation()
{
    var handler = new AudioActivationHandler();
    handler.Abandon();
    var operation = new SyntheticActivationOperation();
    Assert(handler.ActivateCompleted(operation) == 0, "A canceled COM callback must complete without throwing.");
    try { handler.TakeInterface(CancellationToken.None); } catch (OperationCanceledException) { return; }
    throw new Exception("Canceled activation returned an interface.");
}

sealed class SyntheticAudioSession(bool fail) : WasapiSession
{
    public bool Cleaned { get; private set; }
    protected override void RunAudio()
    {
        try
        {
            if (fail) throw new AudioDeviceException("Synthetic unplug.", unchecked((int)0x88890004));
            ReportStarted();
            StopToken.WaitHandle.WaitOne();
        }
        finally { Cleaned = true; }
    }
}

sealed class SyntheticActivationOperation : IActivateAudioInterfaceAsyncOperation
{
    public int GetActivateResult(out int result, out IntPtr audioInterface)
    {
        result = 0;
        audioInterface = Marshal.GetIUnknownForObject(new AudioActivationHandler());
        return 0;
    }
}
