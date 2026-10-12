namespace BatCave.Audio;

public static class AudioRoutingPolicy
{
    public static AudioRoutingSuggestion SuggestDefaults(IEnumerable<AudioDeviceInventory> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        // Enumerate with a limit so a lazy hostile inventory cannot allocate unbounded storage.
        var list = devices.Take(AudioLimits.MaxDevices + 1).ToArray();
        if (list.Length > AudioLimits.MaxDevices || list.Any(d => !AudioValidation.Inventory(d))
            || list.Select(d => d.DeviceId).Distinct(StringComparer.Ordinal).Count() != list.Length)
            throw new ArgumentException("Invalid audio inventory.", nameof(devices));

        var primary = list.Where(d => d.DeviceIsPrimary).ToArray();
        if (primary.Length != 1)
            return RetainLocal(list, primary.Length > 1 ? AudioDefaultReason.AmbiguousPrimary : AudioDefaultReason.NoPrimaryBluetooth);

        var bluetooth = primary[0].Outputs.Where(o => o.Kind == AudioOutputKind.Bluetooth
            && o.IsConnected && o.IsActive && o.CanReceiveStream).ToArray();
        if (bluetooth.Length > 1)
        {
            var defaultBluetooth = bluetooth.Where(o => o.IsDefault).ToArray();
            if (defaultBluetooth.Length == 1) bluetooth = defaultBluetooth;
        }
        if (bluetooth.Length != 1)
            return RetainLocal(list, bluetooth.Length > 1 ? AudioDefaultReason.AmbiguousBluetooth : AudioDefaultReason.NoPrimaryBluetooth);

        var destination = new AudioOutputKey(primary[0].DeviceId, bluetooth[0].EndpointId);
        // The primary's existing OS output keeps playing. A default Bluetooth output
        // already receives that audio; a different selected output receives a mirror.
        var routes = list.Where(d => (d.DeviceId != primary[0].DeviceId || !bluetooth[0].IsDefault) && d.SupportsSystemCapture
                && d.ExcludesPlaybackFromCapture)
            .Select(d => new AudioRoute(d.DeviceId, destination)).ToArray();
        return new(AudioDefaultReason.PrimaryBluetooth, Array.AsReadOnly(routes),
            Array.AsReadOnly(new[] { primary[0].DeviceId }));
    }

    private static AudioRoutingSuggestion RetainLocal(AudioDeviceInventory[] devices, AudioDefaultReason reason)
        => new(reason, Array.Empty<AudioRoute>(), Array.AsReadOnly(devices.Select(d => d.DeviceId).ToArray()));
}
