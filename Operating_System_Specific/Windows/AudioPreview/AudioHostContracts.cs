using System.Text.Json;
using BatCave.Audio;

namespace BatCave.AudioPreview;

internal interface IAudioPeerLink
{
    event Action<AudioPeerSession, AudioDeviceInventory>? InventoryReceived;
    event Action<AudioPeerSession, string, JsonElement>? ControlReceived;
    event Action<AudioPeerSession, AudioPacket>? PacketReceived;
    event Action<string>? PeerDisconnected;
    AudioPeerSession? Session(string deviceId);
    string[] ConnectedIds { get; }
    bool SendControl(string destination, string type, object payload);
    bool SendAudio(string destination, Guid streamId, ulong sequence, float[] samples);
}

internal interface IAudioCapture : IDisposable
{
    event Action<float[]>? BlockAvailable;
    event Action<Exception>? Faulted;
    void Start();
}

internal interface IAudioOutput : IDisposable
{
    event Action<Exception>? Faulted;
    void SetFrameProvider(Func<float[]?> provider);
    void Start();
}

internal interface IAudioPlatform
{
    IAudioCapture CreateCapture();
    IAudioOutput CreateOutput(string publicEndpointId);
}

internal sealed record AudioPreviewState(bool IsEnabled, string Status,
    AudioDeviceInventory[] Devices, AudioRoute[] Routes, int ActiveSendTargets);
