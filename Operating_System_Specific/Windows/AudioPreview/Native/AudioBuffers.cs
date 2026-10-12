namespace BatCave.AudioPreview.Native;

/// <summary>Canonical preview audio uses interleaved, finite stereo float samples at 48 kHz.</summary>
public static class NativeAudioFormat
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int FramesPerBlock = 480;
    public const int SamplesPerBlock = FramesPerBlock * Channels;
    internal static float Clean(float sample) => float.IsFinite(sample) ? Math.Clamp(sample, -1f, 1f) : 0f;
}

// Bounded 200 ms ring. A slow sink rejects new writes without allocating or
// allowing playback latency to grow indefinitely. Dispose clears retained audio.
internal sealed class BoundedAudioBuffer
{
    private readonly object _sync = new();
    private readonly float[] _samples;
    private int _read;
    private int _count;
    private bool _closed;

    internal BoundedAudioBuffer(int capacityFrames = 9600)
    {
        if (capacityFrames < 1 || capacityFrames > 48000) throw new ArgumentOutOfRangeException(nameof(capacityFrames));
        _samples = new float[checked(capacityFrames * NativeAudioFormat.Channels)];
    }

    internal bool TryWrite(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty || samples.Length % NativeAudioFormat.Channels != 0) return false;
        lock (_sync)
        {
            if (_closed || samples.Length > _samples.Length - _count) return false;
            int index = (_read + _count) % _samples.Length;
            foreach (float sample in samples)
            {
                _samples[index] = NativeAudioFormat.Clean(sample);
                index = (index + 1) % _samples.Length;
            }
            _count += samples.Length;
            return true;
        }
    }

    internal int Read(Span<float> destination)
    {
        if (destination.Length % NativeAudioFormat.Channels != 0) throw new ArgumentException("Audio reads require complete stereo frames.", nameof(destination));
        lock (_sync)
        {
            int count = Math.Min(destination.Length, _count);
            for (int i = 0; i < count; i++)
            {
                destination[i] = _samples[_read];
                _samples[_read] = 0;
                _read = (_read + 1) % _samples.Length;
            }
            _count -= count;
            destination[count..].Clear();
            return count;
        }
    }

    internal void Close()
    {
        lock (_sync) { _closed = true; _samples.AsSpan().Clear(); _read = 0; _count = 0; }
    }
}

internal sealed class AudioBlockAssembler
{
    private float[] _block = new float[NativeAudioFormat.SamplesPerBlock];
    private int _used;

    internal void Push(ReadOnlySpan<float> samples, Action<float[]> deliver)
    {
        if (samples.Length % NativeAudioFormat.Channels != 0) throw new ArgumentException("Capture requires complete stereo frames.", nameof(samples));
        while (!samples.IsEmpty)
        {
            int copy = Math.Min(samples.Length, _block.Length - _used);
            for (int i = 0; i < copy; i++) _block[_used + i] = NativeAudioFormat.Clean(samples[i]);
            _used += copy;
            samples = samples[copy..];
            if (_used != _block.Length) continue;
            float[] complete = _block;
            _block = new float[NativeAudioFormat.SamplesPerBlock];
            _used = 0;
            deliver(complete);
        }
    }

    internal void Reset() { _block.AsSpan().Clear(); _used = 0; }
}

// WASAPI event buffers can be smaller or larger than one protocol block. This
// keeps the fractional block on the owning render worker, so the mix advances
// by the physical audio clock without a scheduler timer or a growing queue.
internal sealed class ProviderAudioBuffer
{
    private Func<float[]?>? _lastProvider;
    private float[]? _block;
    private int _used;

    internal int Read(Span<float> destination, Func<float[]?> provider)
    {
        if (destination.Length % NativeAudioFormat.Channels != 0) throw new ArgumentException("Audio reads require complete stereo frames.", nameof(destination));
        if (!ReferenceEquals(_lastProvider, provider)) { _block = null; _used = 0; _lastProvider = provider; }
        int supplied = 0;
        while (!destination.IsEmpty)
        {
            if (_block is null)
            {
                _block = provider();
                _used = 0;
                if (_block is null) { destination.Clear(); return supplied; }
                if (_block.Length != NativeAudioFormat.SamplesPerBlock)
                    throw new InvalidOperationException("The audio mixer must return exactly 480 stereo frames per block.");
            }
            int copy = Math.Min(destination.Length, _block.Length - _used);
            for (int i = 0; i < copy; i++) destination[i] = NativeAudioFormat.Clean(_block[_used + i]);
            destination = destination[copy..];
            _used += copy;
            supplied += copy;
            if (_used == _block.Length) { _block = null; _used = 0; }
        }
        return supplied;
    }
}
