namespace BatCave.Audio;

internal sealed class AudioJitterBuffer
{
    private const ulong MaxSequenceGap = 1_024;
    private readonly SortedDictionary<ulong, float[]> _packets = new();
    private ulong? _next;
    private ulong? _lastConsumed;
    public int Count => _packets.Count;

    public bool TryEnqueue(ulong sequence, float[] samples)
    {
        if (_lastConsumed.HasValue && sequence <= _lastConsumed.Value || _packets.ContainsKey(sequence)) return false;
        if (_next.HasValue && sequence > _next.Value && sequence - _next.Value > MaxSequenceGap) return false;
        if (_packets.Count > 0)
        {
            var earliest = _packets.First().Key;
            if (sequence > earliest && sequence - earliest > MaxSequenceGap) return false;
            if (sequence < earliest && earliest - sequence > MaxSequenceGap) return false;
        }
        if (_packets.Count >= AudioLimits.MaxBufferedPackets)
        {
            // Drop the oldest sample to bound latency as well as memory. Preserve the
            // most recent 80 ms rather than letting a slow renderer accumulate audio.
            if (sequence < _packets.First().Key) return false;
            var dropped = _packets.First().Key;
            _packets.Remove(dropped);
            _lastConsumed = dropped;
            if (_next.HasValue && _next.Value <= dropped) _next = dropped + 1;
        }
        _packets.Add(sequence, (float[])samples.Clone());
        return true;
    }

    public float[]? Dequeue()
    {
        if (!_next.HasValue)
        {
            if (_packets.Count < AudioLimits.JitterPrefillPackets) return null;
            _next = _packets.First().Key;
        }
        if (_packets.Count > 0 && _packets.First().Key > _next.Value
            && _packets.First().Key - _next.Value > AudioLimits.MaxBufferedPackets)
            _next = _packets.First().Key; // skip a long loss burst instead of accumulating delay
        var sequence = _next.Value;
        _next = sequence + 1;
        _lastConsumed = sequence;
        if (_packets.Remove(sequence, out var samples)) return samples;
        if (_packets.Count == 0) _next = null; // refill after underrun
        return null;
    }
}
