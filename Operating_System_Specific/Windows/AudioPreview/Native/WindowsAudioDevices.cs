using System.Runtime.InteropServices;
using System.Text;

namespace BatCave.AudioPreview.Native;

public enum AudioTransport { Unknown, Bluetooth, Other }

/// <summary>An active, connected Windows playback endpoint. IDs and names belong to this device and must not be logged or published.</summary>
public sealed record AudioOutputEndpoint(string Id, string Name, bool IsDefault, AudioTransport Transport)
{
    public bool IsSpeakers { get; init; }
}

public static class WindowsAudioDevices
{
    /// <summary>Read-only enumeration. Does not activate recording, pair Bluetooth devices or change the default output.</summary>
    public static IReadOnlyList<AudioOutputEndpoint> EnumerateOutputs()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows audio endpoints require Windows.");
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;
        IMMDevice? defaultEndpoint = null;
        try
        {
            enumerator = CoreAudioInterop.CreateEnumerator();
            string? defaultId = null;
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out defaultEndpoint) >= 0)
                CoreAudioInterop.Check(defaultEndpoint.GetId(out defaultId), "Read default audio endpoint");
            CoreAudioInterop.Check(enumerator.EnumAudioEndpoints(0, CoreAudioInterop.ActiveDevice, out collection), "Enumerate audio outputs");
            CoreAudioInterop.Check(collection.GetCount(out uint count), "Count audio outputs");
            if (count > 1024) throw new InvalidOperationException("Windows reported too many audio endpoints.");
            var outputs = new List<AudioOutputEndpoint>();
            for (uint i = 0; i < count; i++)
            {
                IMMDevice? endpoint = null;
                try
                {
                    if (collection.Item(i, out endpoint) < 0) continue; // May disappear during enumeration.
                    if (endpoint.GetState(out uint state) < 0 || (state & CoreAudioInterop.ActiveDevice) == 0) continue;
                    CoreAudioInterop.Check(endpoint.GetId(out string id), "Read audio endpoint identifier");
                    string name = ReadProperty(endpoint, CoreAudioInterop.FriendlyName) ?? "Audio output";
                    name = new string(name.Where(c => !char.IsControl(c)).Take(160).ToArray());
                    if (string.IsNullOrWhiteSpace(name)) name = "Audio output";
                    outputs.Add(new AudioOutputEndpoint(id, name, StringComparer.Ordinal.Equals(id, defaultId), IdentifyTransport(enumerator, endpoint))
                    { IsSpeakers = ReadSpeakersFormFactor(endpoint) });
                }
                catch (COMException) { /* A hot-unplugged endpoint will appear on the next refresh. */ }
                catch (AudioDeviceException) { }
                finally { CoreAudioInterop.Release(endpoint); }
            }
            return outputs.OrderByDescending(item => item.IsDefault).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        finally
        {
            CoreAudioInterop.Release(defaultEndpoint);
            CoreAudioInterop.Release(collection);
            CoreAudioInterop.Release(enumerator);
        }
    }

    private static string? ReadProperty(IMMDevice device, in PropertyKey key)
    {
        IPropertyStore? store = null;
        PropVariant value = default;
        try
        {
            if (device.OpenPropertyStore(0, out store) < 0 || store.GetValue(key, out value) < 0) return null;
            return value.Type == 31 && value.StringPointer != IntPtr.Zero ? Marshal.PtrToStringUni(value.StringPointer) : null;
        }
        finally
        {
            CoreAudioInterop.PropVariantClear(ref value);
            CoreAudioInterop.Release(store);
        }
    }

    private static AudioTransport IdentifyTransport(IMMDeviceEnumerator enumerator, IMMDevice endpoint)
    {
        // A render endpoint's SWD/MMDEVAPI ID is opaque. Find its physical adapter
        // via DeviceTopology, then inspect configured PnP ancestry. Friendly names
        // and endpoint-ID substrings are never treated as Bluetooth evidence.
        object? topologyObject = null;
        IConnector? connector = null;
        IMMDevice? adapter = null;
        try
        {
            var result = ClassifyPnpTree(ReadProperty(endpoint, CoreAudioInterop.InstanceId));
            if (result == AudioTransport.Bluetooth) return result;
            var topologyId = typeof(IDeviceTopology).GUID;
            if (endpoint.Activate(topologyId, 23, IntPtr.Zero, out topologyObject) < 0) return result;
            var topology = (IDeviceTopology)topologyObject;
            if (topology.GetConnectorCount(out uint count) < 0 || count == 0 || topology.GetConnector(0, out connector) < 0)
                return result;
            if (connector.GetDeviceIdConnectedTo(out string adapterId) < 0 || enumerator.GetDevice(adapterId, out adapter) < 0)
                return result;
            return ClassifyPnpTree(ReadProperty(adapter, CoreAudioInterop.InstanceId));
        }
        catch (COMException) { return AudioTransport.Unknown; }
        finally
        {
            CoreAudioInterop.Release(adapter);
            CoreAudioInterop.Release(connector);
            CoreAudioInterop.Release(topologyObject);
        }
    }

    private static bool ReadSpeakersFormFactor(IMMDevice endpoint)
    {
        IPropertyStore? store = null;
        PropVariant value = default;
        try
        {
            if (endpoint.OpenPropertyStore(0, out store) < 0 || store.GetValue(CoreAudioInterop.FormFactor, out value) < 0) return false;
            return value.Type == 19 && value.UnsignedValue == 1; // VT_UI4, EndpointFormFactor.Speakers
        }
        finally { CoreAudioInterop.PropVariantClear(ref value); CoreAudioInterop.Release(store); }
    }

    private static AudioTransport ClassifyPnpTree(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId) || instanceId.Length > 4096 ||
            CoreAudioInterop.CM_Locate_DevNodeW(out uint node, instanceId, 0) != 0)
            return AudioTransport.Unknown;
        bool otherBus = false;
        var seen = new HashSet<uint>();
        for (int depth = 0; depth < 32 && seen.Add(node); depth++)
        {
            var buffer = new StringBuilder(4096);
            if (CoreAudioInterop.CM_Get_Device_IDW(node, buffer, (uint)buffer.Capacity, 0) != 0) break;
            var transport = ClassifyInstanceId(buffer.ToString());
            if (transport == AudioTransport.Bluetooth) return transport;
            otherBus |= transport == AudioTransport.Other;
            if (CoreAudioInterop.CM_Get_Parent(out node, node, 0) != 0) break;
        }
        return otherBus ? AudioTransport.Other : AudioTransport.Unknown;
    }

    internal static AudioTransport ClassifyInstanceId(string instanceId)
    {
        var separator = instanceId.IndexOf('\\');
        if (separator <= 0) return AudioTransport.Unknown;
        var enumerator = instanceId[..separator];
        // Windows Bluetooth audio profiles use BTHENUM and BTHHFENUM; LE Audio
        // descendants use BTHLEDEVICE. The BTH transport can itself have USB
        // ancestry, so Bluetooth evidence wins over any encountered USB parent.
        if (enumerator.Equals("BTHENUM", StringComparison.OrdinalIgnoreCase) ||
            enumerator.Equals("BTHHFENUM", StringComparison.OrdinalIgnoreCase) ||
            enumerator.Equals("BTHLEDEVICE", StringComparison.OrdinalIgnoreCase) ||
            enumerator.Equals("BTH", StringComparison.OrdinalIgnoreCase))
            return AudioTransport.Bluetooth;
        if (enumerator.Equals("USB", StringComparison.OrdinalIgnoreCase) ||
            enumerator.Equals("HDAUDIO", StringComparison.OrdinalIgnoreCase) ||
            enumerator.Equals("INTELAUDIO", StringComparison.OrdinalIgnoreCase))
            return AudioTransport.Other;
        return AudioTransport.Unknown;
    }
}
