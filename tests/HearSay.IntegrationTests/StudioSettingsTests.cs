using HearSay.Playing;

namespace HearSay.IntegrationTests;

/// <summary>The Studio settings file.</summary>
public sealed class StudioSettingsTests
{
    [Fact]
    public void Settings_SaveAndLoad()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hearsay-settings-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "nested", "studio-settings.json");
        try
        {
            new StudioSettings("Keystation 49").Save(path);
            Assert.Equal("Keystation 49", StudioSettings.Load(path).MidiInput);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public void Settings_MissingOrBrokenFile_GivesDefaults()
    {
        string path = Path.Combine(Path.GetTempPath(), "hearsay-settings-" + Guid.NewGuid().ToString("N") + ".json");
        Assert.Null(StudioSettings.Load(path).MidiInput);

        File.WriteAllText(path, "{ not json");
        try
        {
            Assert.Null(StudioSettings.Load(path).MidiInput);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
