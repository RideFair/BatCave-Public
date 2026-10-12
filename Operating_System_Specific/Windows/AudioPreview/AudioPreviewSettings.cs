using System.Text.Json;

namespace BatCave.AudioPreview;

internal sealed record AudioPreviewSettings(string DisplayName = "This Windows PC", bool IsPrimary = true)
{
    public static AudioPreviewSettings Load(string directory)
    {
        var path = Path.Combine(directory, "audio-settings.json");
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 4096) throw new InvalidDataException("Audio settings exceed their limit.");
        var settings = JsonSerializer.Deserialize<AudioPreviewSettings>(File.ReadAllText(path)) ?? new();
        Validate(settings); return settings;
    }
    public void Save(string directory)
    {
        Validate(this);
        var path = Path.Combine(directory, "audio-settings.json");
        File.WriteAllText(path + ".new", JsonSerializer.Serialize(this));
        File.Move(path + ".new", path, true);
    }
    private static void Validate(AudioPreviewSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.DisplayName) || settings.DisplayName.Length > 128 || settings.DisplayName.Any(char.IsControl))
            throw new InvalidDataException("Choose a device name of 1–128 characters.");
    }
}
