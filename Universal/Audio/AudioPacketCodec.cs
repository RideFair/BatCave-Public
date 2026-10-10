using System.Buffers.Binary;
using System.Text;

namespace BatCave.Audio;

// Wire format BAA1: magic(4), device session(16), stream(16), sequence u64le(8),
// origin length u8, visited count u8, sample count u16le, origin ASCII, followed
// by length-prefixed visited device IDs, then exactly 960 float32le samples.
// Every frame represents 10 ms, 48 kHz, stereo, interleaved left/right PCM.
public static class AudioPacketCodec
{
    private static ReadOnlySpan<byte> Magic => "BAA1"u8;

    public static byte[] Encode(AudioPacket packet)
    {
        if (!AudioValidation.Packet(packet)) throw new ArgumentException("Invalid audio packet.", nameof(packet));
        var length = 48 + packet.OriginDeviceId.Length
            + packet.VisitedDeviceIds.Sum(id => 1 + id.Length) + AudioLimits.SamplesPerPacket * sizeof(float);
        var bytes = new byte[length];
        Magic.CopyTo(bytes);
        packet.DeviceSessionId.TryWriteBytes(bytes.AsSpan(4, 16), bigEndian: true, out _);
        packet.StreamId.TryWriteBytes(bytes.AsSpan(20, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(36, 8), packet.Sequence);
        bytes[44] = (byte)packet.OriginDeviceId.Length;
        bytes[45] = (byte)packet.VisitedDeviceIds.Count;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(46, 2), AudioLimits.SamplesPerPacket);
        var offset = 48;
        offset += Encoding.ASCII.GetBytes(packet.OriginDeviceId, bytes.AsSpan(offset));
        foreach (var id in packet.VisitedDeviceIds)
        {
            bytes[offset++] = (byte)id.Length;
            offset += Encoding.ASCII.GetBytes(id, bytes.AsSpan(offset));
        }
        foreach (var sample in packet.Samples)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset, 4), sample);
            offset += 4;
        }
        return bytes;
    }

    public static bool TryDecode(ReadOnlySpan<byte> bytes, out AudioPacket? packet)
    {
        packet = null;
        if (bytes.Length < 48 + AudioLimits.SamplesPerPacket * sizeof(float)
            || bytes.Length > AudioLimits.MaxPacketBytes || !bytes[..4].SequenceEqual(Magic)
            || bytes[44] is 0 or > AudioLimits.MaxIdentifierLength || bytes[45] is < 1 or > AudioLimits.MaxHops + 1
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(46, 2)) != AudioLimits.SamplesPerPacket) return false;
        var offset = 48;
        if (!ReadIdentifier(bytes, bytes[44], ref offset, out var origin)) return false;
        var visited = new string[bytes[45]];
        for (var i = 0; i < visited.Length; i++)
        {
            if (offset >= bytes.Length) return false;
            var length = bytes[offset++];
            if (!ReadIdentifier(bytes, length, ref offset, out var id)) return false;
            visited[i] = id;
        }
        if (bytes.Length - offset != AudioLimits.SamplesPerPacket * sizeof(float)) return false;
        var samples = new float[AudioLimits.SamplesPerPacket];
        for (var i = 0; i < samples.Length; i++, offset += 4)
            samples[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(offset, 4));
        var value = new AudioPacket(origin, new Guid(bytes.Slice(4, 16), bigEndian: true),
            new Guid(bytes.Slice(20, 16), bigEndian: true), BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(36, 8)),
            Array.AsReadOnly(visited), samples);
        if (!AudioValidation.Packet(value)) return false;
        packet = value;
        return true;
    }

    // Provenance records one final playback hop. It does not grant relay permission.
    public static bool TryMarkPlayback(AudioPacket packet, string destinationDeviceId, out AudioPacket? played)
    {
        played = null;
        if (!AudioValidation.Packet(packet) || !AudioValidation.Identifier(destinationDeviceId)
            || packet.VisitedDeviceIds.Count != 1) return false;
        played = packet with { VisitedDeviceIds = Array.AsReadOnly(new[] { packet.OriginDeviceId, destinationDeviceId }) };
        return true;
    }

    private static bool ReadIdentifier(ReadOnlySpan<byte> bytes, int length, ref int offset, out string id)
    {
        id = string.Empty;
        if (length is < 1 or > AudioLimits.MaxIdentifierLength || length > bytes.Length - offset) return false;
        var value = bytes.Slice(offset, length);
        foreach (var b in value) if (b > 127) return false;
        id = Encoding.ASCII.GetString(value);
        offset += length;
        return AudioValidation.Identifier(id);
    }
}
