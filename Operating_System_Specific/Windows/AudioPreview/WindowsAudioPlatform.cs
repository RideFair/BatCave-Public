using System.Security.Cryptography;
using System.Text;
using BatCave.Audio;
using BatCave.AudioPreview.Native;

namespace BatCave.AudioPreview;

internal sealed class WindowsAudioPlatform : IAudioPlatform
{
    private readonly object _gate = new();
    private Dictionary<string, string> _nativeIds = new(StringComparer.Ordinal);
    public AudioDeviceInventory Enumerate(string deviceId, string name, bool primary, long revision)
    {
        var native = WindowsAudioDevices.EnumerateOutputs().Take(AudioLimits.MaxOutputsPerDevice).ToArray();
        var ids = native.ToDictionary(e => PublicId(e.Id), e => e.Id, StringComparer.Ordinal);
        var outputs = native.Select(e => new AudioOutputDescriptor(PublicId(e.Id), e.Name,
            e.Transport == AudioTransport.Bluetooth ? AudioOutputKind.Bluetooth : e.IsSpeakers ? AudioOutputKind.Speakers :
            e.Transport == AudioTransport.Other ? AudioOutputKind.Other : AudioOutputKind.Unknown,
            true, true, e.IsDefault)).ToArray();
        lock (_gate) _nativeIds = ids;
        var supported = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
        return new(deviceId, name, primary, revision, outputs, supported, supported);
    }
    private static string PublicId(string nativeId) => "endpoint-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(nativeId)));
    public IAudioCapture CreateCapture() => new Capture();
    public IAudioOutput CreateOutput(string publicEndpointId)
    {
        lock (_gate)
        {
            if (!_nativeIds.TryGetValue(publicEndpointId, out var nativeId)) throw new InvalidOperationException("The selected output is no longer connected.");
            return new Output(nativeId);
        }
    }
    private sealed class Capture : IAudioCapture
    {
        private readonly WasapiSystemCapture _capture = new();
        public event Action<float[]>? BlockAvailable { add => _capture.BlockAvailable += value; remove => _capture.BlockAvailable -= value; }
        public event Action<Exception>? Faulted { add => _capture.Faulted += value; remove => _capture.Faulted -= value; }
        public void Start() => _capture.Start();
        public void Dispose() => _capture.Dispose();
    }
    private sealed class Output(string nativeId) : IAudioOutput
    {
        private readonly WasapiOutput _output = new(nativeId);
        public event Action<Exception>? Faulted { add => _output.Faulted += value; remove => _output.Faulted -= value; }
        public void SetFrameProvider(Func<float[]?> provider) => _output.SetFrameProvider(provider);
        public void Start() => _output.Start();
        public void Dispose() => _output.Dispose();
    }
}
