using System.Text;

namespace BatCave.WindowsAgent;

internal sealed class BoundedFrameReader(Stream stream, int maximumFrameBytes = BoundedFrameReader.MaximumFrameBytes, bool allowCarriageReturn = true)
{
    public const int MaximumFrameBytes = 256 * 1024;
    public const int MaximumClipboardBytes = 64 * 1024;
    public const int MaximumQueuedBytes = 1024 * 1024;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private readonly byte[] _buffer = new byte[8192];
    private int _offset, _count;

    public async Task<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        using var frame = new MemoryStream();
        while (true)
        {
            if (_offset == _count)
            {
                _count = await stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
                _offset = 0;
                if (_count == 0)
                {
                    if (frame.Length != 0) { throw new InvalidDataException("Truncated JSON frame."); }
                    return null;
                }
            }
            var end = Array.IndexOf(_buffer, (byte)'\n', _offset, _count - _offset);
            var length = (end < 0 ? _count : end) - _offset;
            if (frame.Length + length > maximumFrameBytes) { throw new InvalidDataException("JSON frame exceeds its size limit."); }
            frame.Write(_buffer, _offset, length);
            _offset += length;
            if (end < 0) { continue; }
            _offset++; // consume newline; keep any following frames buffered
            var bytes = frame.GetBuffer();
            var size = (int)frame.Length;
            if (allowCarriageReturn && size > 0 && bytes[size - 1] == '\r') { size--; }
            return Utf8.GetString(bytes, 0, size);
        }
    }
}
