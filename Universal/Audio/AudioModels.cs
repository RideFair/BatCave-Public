namespace BatCave.Audio;

public static class AudioLimits
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
    public const int FramesPerPacket = 480;
    public const int SamplesPerPacket = FramesPerPacket * Channels;
    public const int MaxDevices = 32;
    public const int MaxOutputsPerDevice = 32;
    public const int MaxRoutes = 128;
    public const int MaxBufferedPackets = 8;
    public const int JitterPrefillPackets = 2;
    public const int MaxIdentifierLength = 128;
    public const int MaxDisplayNameLength = 160;
    public const int MaxPacketBytes = 4_274;
    public const int MaxStreamsPerPeerSession = 1_024;
    public const int MaxHops = 1;
    public const float MaxGain = 2f;
}

public enum AudioOutputKind { Unknown, Bluetooth, Speakers, Other }

// EndpointId is an opaque public identifier. Never publish the native OS endpoint path.
public sealed record AudioOutputDescriptor(string EndpointId, string DisplayName,
    AudioOutputKind Kind, bool IsConnected, bool IsActive, bool IsDefault,
    bool CanReceiveStream = true);

public sealed record AudioDeviceInventory(string DeviceId, string DisplayName,
    bool DeviceIsPrimary, long Revision, IReadOnlyList<AudioOutputDescriptor> Outputs,
    bool SupportsSystemCapture = false, bool ExcludesPlaybackFromCapture = false);

public readonly record struct AudioOutputKey(string DeviceId, string EndpointId);
public sealed record AudioRoute(string SourceDeviceId, AudioOutputKey Destination, float Gain = 1f);
public readonly record struct AudioPeerSession(string DeviceId, Guid SessionId);

// DeviceSessionId is bound to the mutually authenticated connection. StreamId changes
// for every capture start; neither input permission nor a previous stream grants consent.
public sealed record AudioPacket(string OriginDeviceId, Guid DeviceSessionId,
    Guid StreamId, ulong Sequence, IReadOnlyList<string> VisitedDeviceIds, float[] Samples);

public enum AudioDefaultReason
{
    PrimaryBluetooth,
    NoPrimaryBluetooth,
    AmbiguousPrimary,
    AmbiguousBluetooth
}

// A suggestion never enables capture, playback, or changes an OS default output.
public sealed record AudioRoutingSuggestion(AudioDefaultReason Reason,
    IReadOnlyList<AudioRoute> SuggestedRoutes, IReadOnlyList<string> RetainLocalDeviceIds);

internal static class AudioValidation
{
    public static bool Identifier(string? value) => !string.IsNullOrEmpty(value)
        && value.Length <= AudioLimits.MaxIdentifierLength
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':' or '.');

    public static bool DisplayName(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= AudioLimits.MaxDisplayNameLength && !value.Any(char.IsControl);

    public static bool Gain(float value) => float.IsFinite(value) && value >= 0 && value <= AudioLimits.MaxGain;

    public static bool Inventory(AudioDeviceInventory? inventory)
    {
        if (inventory is null || !Identifier(inventory.DeviceId) || !DisplayName(inventory.DisplayName)
            || inventory.Revision < 0 || inventory.Outputs is null
            || inventory.Outputs.Count > AudioLimits.MaxOutputsPerDevice) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var output in inventory.Outputs)
        {
            if (output is null || !Identifier(output.EndpointId) || !DisplayName(output.DisplayName)
                || !Enum.IsDefined(output.Kind) || !ids.Add(output.EndpointId)) return false;
        }
        return true;
    }

    public static bool Packet(AudioPacket? packet)
    {
        if (packet is null || !Identifier(packet.OriginDeviceId) || packet.DeviceSessionId == Guid.Empty
            || packet.StreamId == Guid.Empty || packet.Sequence >= long.MaxValue
            || packet.VisitedDeviceIds is null || packet.VisitedDeviceIds.Count is < 1 or > AudioLimits.MaxHops + 1
            || packet.VisitedDeviceIds[0] != packet.OriginDeviceId
            || packet.Samples is null || packet.Samples.Length != AudioLimits.SamplesPerPacket) return false;
        foreach (var device in packet.VisitedDeviceIds)
            if (!Identifier(device)) return false;
        foreach (var sample in packet.Samples)
            if (!float.IsFinite(sample) || sample < -1 || sample > 1) return false;
        return true;
    }
}
