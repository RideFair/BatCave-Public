using System.Runtime.InteropServices;

namespace BatCave.AudioPreview.Native;

/// <summary>Renders to one selected connected output, without changing system defaults or muting other applications.</summary>
public sealed class WasapiOutput : WasapiSession
{
    private readonly string _endpointId;
    private readonly BoundedAudioBuffer _buffer = new();
    private Func<float[]?>? _frameProvider;

    public WasapiOutput(string endpointId)
    {
        if (string.IsNullOrWhiteSpace(endpointId) || endpointId.Length > 4096) throw new ArgumentException("Choose a valid connected audio endpoint.", nameof(endpointId));
        _endpointId = endpointId;
    }

    /// <summary>Nonblocking, bounded write. Returns false when stopped, malformed or full. The caller must drop the rejected block.</summary>
    public bool TryWrite(ReadOnlySpan<float> stereo48k) => IsRunning && Volatile.Read(ref _frameProvider) is null && _buffer.TryWrite(stereo48k);

    /// <summary>Pulls exact 480-frame stereo blocks on the render endpoint's audio clock. The provider must be bounded and must never wait for the network or capture.</summary>
    public void SetFrameProvider(Func<float[]?>? provider)
    {
        ObjectDisposedException.ThrowIf(DisposalRequested, this);
        Volatile.Write(ref _frameProvider, provider);
    }

    protected override void RunAudio()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? endpoint = null;
        object? audioObject = null;
        object? renderObject = null;
        IAudioClient? audio = null;
        IntPtr format = IntPtr.Zero;
        bool started = false;
        using var sampleReady = new AutoResetEvent(false);
        try
        {
            enumerator = CoreAudioInterop.CreateEnumerator();
            CoreAudioInterop.Check(enumerator.GetDevice(_endpointId, out endpoint), "Open selected output");
            CoreAudioInterop.Check(endpoint.GetState(out uint state), "Check selected output");
            if ((state & CoreAudioInterop.ActiveDevice) == 0) throw new InvalidOperationException("The selected output is disconnected or disabled. Refresh the output list.");
            CoreAudioInterop.Check(endpoint.Activate(typeof(IAudioClient).GUID, 23, IntPtr.Zero, out audioObject), "Activate selected output");
            audio = (IAudioClient)audioObject;
            format = CoreAudioInterop.CreateWireFormat();
            CoreAudioInterop.Check(audio.Initialize(0, CoreAudioInterop.EventCallback | CoreAudioInterop.NoPersist |
                CoreAudioInterop.AutoConvertPcm | CoreAudioInterop.HighQualityConversion, 0, 0, format, IntPtr.Zero), "Initialize selected output");
            CoreAudioInterop.Check(audio.SetEventHandle(sampleReady.SafeWaitHandle.DangerousGetHandle()), "Set output event");
            CoreAudioInterop.Check(audio.GetBufferSize(out uint capacity), "Read output buffer capacity");
            if (capacity == 0 || capacity > NativeAudioFormat.SampleRate) throw new InvalidOperationException("The output driver returned an invalid buffer capacity.");
            CoreAudioInterop.Check(audio.GetService(typeof(IAudioRenderClient).GUID, out renderObject), "Open output buffer");
            var render = (IAudioRenderClient)renderObject;
            var samples = new float[checked((int)capacity * NativeAudioFormat.Channels)];
            var providerBuffer = new ProviderAudioBuffer();
            CoreAudioInterop.Check(render.GetBuffer(capacity, out _), "Prime output buffer");
            CoreAudioInterop.Check(render.ReleaseBuffer(capacity, CoreAudioInterop.SilentBuffer), "Prime output silence");
            StopToken.ThrowIfCancellationRequested();
            CoreAudioInterop.Check(audio.Start(), "Start selected output");
            started = true;
            ReportStarted();
            var waits = new[] { StopToken.WaitHandle, sampleReady };
            while (WaitHandle.WaitAny(waits, 250) != 0)
            {
                StopToken.ThrowIfCancellationRequested();
                CoreAudioInterop.Check(audio.GetCurrentPadding(out uint padding), "Check output availability");
                if (padding > capacity) throw new InvalidOperationException("The output driver returned invalid padding.");
                uint frames = capacity - padding;
                if (frames == 0) continue;
                int count = checked((int)frames * NativeAudioFormat.Channels);
                var provider = Volatile.Read(ref _frameProvider);
                int supplied = provider is null ? _buffer.Read(samples.AsSpan(0, count)) :
                    providerBuffer.Read(samples.AsSpan(0, count), provider);
                CoreAudioInterop.Check(render.GetBuffer(frames, out IntPtr destination), "Acquire output buffer");
                try
                {
                    if (supplied > 0) Marshal.Copy(samples, 0, destination, count);
                }
                finally
                {
                    CoreAudioInterop.Check(render.ReleaseBuffer(frames, supplied == 0 ? CoreAudioInterop.SilentBuffer : 0), "Release output buffer");
                }
            }
        }
        finally
        {
            if (started) audio?.Stop();
            if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
            CoreAudioInterop.Release(renderObject);
            CoreAudioInterop.Release(audioObject);
            CoreAudioInterop.Release(endpoint);
            CoreAudioInterop.Release(enumerator);
            _buffer.Close();
        }
    }

    protected override void OnDisposed()
    {
        _buffer.Close();
        Volatile.Write(ref _frameProvider, null);
    }
}
