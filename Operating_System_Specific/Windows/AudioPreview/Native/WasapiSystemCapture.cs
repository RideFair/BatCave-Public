using System.Runtime.InteropServices;

namespace BatCave.AudioPreview.Native;

/// <summary>Explicitly captures other applications' playback, excluding this process tree so incoming audio cannot feed back.</summary>
public sealed class WasapiSystemCapture : WasapiSession
{
    /// <summary>480 interleaved stereo frames at 48 kHz. Callbacks run on the capture worker and must return promptly.</summary>
    public event Action<float[]>? BlockAvailable;

    protected override void RunAudio()
    {
        IAudioClient? audio = null;
        object? captureObject = null;
        IntPtr format = IntPtr.Zero;
        bool started = false;
        using var sampleReady = new AutoResetEvent(false);
        var assembler = new AudioBlockAssembler();
        try
        {
            // Windows process loopback captures playback independently of the
            // physical render endpoint. Excluding this app and its descendants
            // is essential when this device also plays another device's mix.
            audio = ActivateExcludingOwnProcess();
            format = CoreAudioInterop.CreateWireFormat();
            CoreAudioInterop.Check(audio.Initialize(0, CoreAudioInterop.Loopback | CoreAudioInterop.EventCallback |
                CoreAudioInterop.AutoConvertPcm | CoreAudioInterop.HighQualityConversion, 0, 0, format, IntPtr.Zero), "Initialize system audio capture");
            CoreAudioInterop.Check(audio.SetEventHandle(sampleReady.SafeWaitHandle.DangerousGetHandle()), "Set capture event");
            CoreAudioInterop.Check(audio.GetBufferSize(out uint capacity), "Read capture buffer capacity");
            if (capacity == 0 || capacity > NativeAudioFormat.SampleRate) throw new InvalidOperationException("The capture driver returned an invalid buffer capacity.");
            CoreAudioInterop.Check(audio.GetService(typeof(IAudioCaptureClient).GUID, out captureObject), "Open system capture buffer");
            var capture = (IAudioCaptureClient)captureObject;
            var samples = new float[checked((int)capacity * NativeAudioFormat.Channels)];
            StopToken.ThrowIfCancellationRequested();
            CoreAudioInterop.Check(audio.Start(), "Start system audio capture");
            started = true;
            ReportStarted();
            var waits = new[] { StopToken.WaitHandle, sampleReady };
            while (WaitHandle.WaitAny(waits, 250) != 0)
            {
                StopToken.ThrowIfCancellationRequested();
                while (!StopToken.IsCancellationRequested)
                {
                    CoreAudioInterop.Check(capture.GetNextPacketSize(out uint next), "Check capture availability");
                    if (next == 0) break;
                    CoreAudioInterop.Check(capture.GetBuffer(out IntPtr source, out uint frames, out uint flags, out _, out _), "Acquire capture packet");
                    if (frames == 0) break;
                    int sampleCount = 0;
                    try
                    {
                        if (frames > capacity) throw new InvalidOperationException("The capture driver returned an oversized packet.");
                        sampleCount = checked((int)frames * NativeAudioFormat.Channels);
                        if ((flags & CoreAudioInterop.SilentBuffer) != 0) samples.AsSpan(0, sampleCount).Clear();
                        else
                        {
                            if (source == IntPtr.Zero) throw new InvalidOperationException("The capture driver returned an empty audio pointer.");
                            Marshal.Copy(source, samples, 0, sampleCount);
                        }
                    }
                    finally { CoreAudioInterop.Check(capture.ReleaseBuffer(frames), "Release capture packet"); }
                    // Release native memory before delivering any managed event.
                    // Never concatenate samples across a reported discontinuity.
                    if ((flags & CoreAudioInterop.DataDiscontinuity) != 0) assembler.Reset();
                    if (!StopToken.IsCancellationRequested)
                        assembler.Push(samples.AsSpan(0, sampleCount), block =>
                        {
                            if (!StopToken.IsCancellationRequested) BlockAvailable?.Invoke(block);
                        });
                }
            }
        }
        finally
        {
            if (started) audio?.Stop();
            assembler.Reset();
            if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
            CoreAudioInterop.Release(captureObject);
            CoreAudioInterop.Release(audio);
        }
    }

    private IAudioClient ActivateExcludingOwnProcess()
    {
        IActivateAudioInterfaceAsyncOperation? operation = null;
        var handler = new AudioActivationHandler();
        try
        {
            using var parameters = new ProcessCaptureParameters(checked((uint)Environment.ProcessId));
            CoreAudioInterop.Check(CoreAudioInterop.ActivateAudioInterfaceAsync("VAD\\Process_Loopback", typeof(IAudioClient).GUID,
                parameters.Pointer, handler, out operation), "Activate feedback-safe system capture");
            IntPtr unknown = handler.TakeInterface(StopToken);
            try { return (IAudioClient)Marshal.GetObjectForIUnknown(unknown); }
            finally { Marshal.Release(unknown); }
        }
        catch
        {
            handler.Abandon();
            throw;
        }
        finally
        {
            CoreAudioInterop.Release(operation);
            // The OS retains its own callback reference until completion, even
            // after cancellation. A late completion releases its interface.
            GC.KeepAlive(handler);
        }
    }
}

internal sealed class ProcessCaptureParameters : IDisposable
{
    private IntPtr _payload;
    internal IntPtr Pointer { get; private set; }

    internal ProcessCaptureParameters(uint processId)
    {
        try
        {
            _payload = Marshal.AllocCoTaskMem(12);
            Pointer = Marshal.AllocCoTaskMem(IntPtr.Size == 8 ? 24 : 16);
            Marshal.WriteInt32(_payload, 0, 1); // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            Marshal.WriteInt32(_payload, 4, unchecked((int)processId));
            Marshal.WriteInt32(_payload, 8, 1); // PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE
            for (int i = 0; i < (IntPtr.Size == 8 ? 24 : 16); i++) Marshal.WriteByte(Pointer, i, 0);
            Marshal.WriteInt16(Pointer, 0, 65); // VT_BLOB
            Marshal.WriteInt32(Pointer, 8, 12);
            Marshal.WriteIntPtr(Pointer, IntPtr.Size == 8 ? 16 : 12, _payload);
        }
        catch { Dispose(); throw; }
    }

    public void Dispose()
    {
        if (Pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(Pointer);
        if (_payload != IntPtr.Zero) Marshal.FreeCoTaskMem(_payload);
        Pointer = IntPtr.Zero;
        _payload = IntPtr.Zero;
    }
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class AudioActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
{
    private readonly object _sync = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IntPtr _interface;
    private bool _abandoned;

    public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
    {
        IntPtr unknown = IntPtr.Zero;
        try
        {
            CoreAudioInterop.Check(operation.GetActivateResult(out int result, out unknown), "Read system capture activation");
            CoreAudioInterop.Check(result, "Activate feedback-safe system capture");
            if (unknown == IntPtr.Zero) throw new InvalidOperationException("Windows returned no process-loopback audio interface.");
            lock (_sync)
            {
                if (!_abandoned) { _interface = unknown; unknown = IntPtr.Zero; }
            }
            _completion.TrySetResult();
        }
        catch (Exception error) { _completion.TrySetException(error); }
        finally { if (unknown != IntPtr.Zero) Marshal.Release(unknown); }
        return 0;
    }

    internal IntPtr TakeInterface(CancellationToken stopToken)
    {
        _completion.Task.WaitAsync(TimeSpan.FromSeconds(10), stopToken).GetAwaiter().GetResult();
        lock (_sync)
        {
            if (_abandoned || _interface == IntPtr.Zero) throw new OperationCanceledException(stopToken);
            IntPtr result = _interface;
            _interface = IntPtr.Zero;
            return result;
        }
    }

    internal void Abandon()
    {
        lock (_sync)
        {
            _abandoned = true;
            if (_interface != IntPtr.Zero) Marshal.Release(_interface);
            _interface = IntPtr.Zero;
        }
    }
}
