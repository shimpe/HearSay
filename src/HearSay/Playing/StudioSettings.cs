using System.Text.Json;

namespace HearSay.Playing;

/// <summary>The settings the Studio remembers between runs, kept in <c>%LOCALAPPDATA%\HearSay\studio-settings.json</c>.</summary>
/// <param name="MidiInput">Name of the MIDI input device, or null for none.</param>
public sealed record StudioSettings(string? MidiInput)
{
    /// <summary>The default location of the settings file.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HearSay", "studio-settings.json");

    /// <summary>Reads the settings; a missing or unreadable file gives the defaults.</summary>
    /// <param name="path">File to read; the default location when null.</param>
    public static StudioSettings Load(string? path = null)
    {
        try
        {
            return JsonSerializer.Deserialize<StudioSettings>(File.ReadAllText(path ?? DefaultPath), HearSayJson.Options)
                ?? new StudioSettings((string?)null);
        }
        catch (Exception)
        {
            return new StudioSettings((string?)null);
        }
    }

    /// <summary>Writes the settings, creating the folder if needed.</summary>
    /// <param name="path">File to write; the default location when null.</param>
    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, HearSayJson.Serialize(this));
    }
}
