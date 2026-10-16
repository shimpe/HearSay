using System.Text.Json;
using System.Text.RegularExpressions;
using HearSay.OperatorMatrix.Priors;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay;

/// <summary>A sound exported earlier: a <c>patch.json</c> and the profile it plays on.</summary>
/// <param name="Name">The folder name, with <c>alt NN</c> for an alternative.</param>
/// <param name="PatchFile">Path of the <c>patch.json</c>.</param>
/// <param name="Patch">The patch.</param>
/// <param name="Profile">The profile it was made for, or the smallest one that plays it.</param>
public sealed record ExportedSound(string Name, string PatchFile, OperatorMatrixPatch Patch, SynthesisProfile Profile);

/// <summary>What a folder of exported sounds held.</summary>
/// <param name="Sounds">Usable sounds, by result folder, each main patch before its alternatives.</param>
/// <param name="Skipped">One line per file that could not be used: <c>name: reason</c>.</param>
public sealed record ExportedSoundScan(IReadOnlyList<ExportedSound> Sounds, IReadOnlyList<string> Skipped);

/// <summary>
/// Finds the sounds in result folders written by Studio, the command line or the library browser: every
/// <c>patch.json</c> and every <c>alternatives/NN-patch.json</c>, with the profile from <c>search-summary.json</c>.
/// </summary>
public static partial class ExportedSounds
{
    /// <summary>Profiles a sound can be played on, smallest first.</summary>
    public static IReadOnlyList<SynthesisProfile> Profiles { get; } = [SynthesisProfiles.SuperColliderFm7, SynthesisProfiles.SuperColliderFmx6, SynthesisProfiles.SuperColliderNome6];

    /// <summary>The sounds under a folder, recursively.</summary>
    /// <param name="directory">Folder to search.</param>
    public static ExportedSoundScan Scan(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var sounds = new List<ExportedSound>();
        var skipped = new List<string>();
        foreach (string file in Directory.EnumerateFiles(directory, "*patch.json", SearchOption.AllDirectories).Where(IsExportedPatch).OrderBy(ResultFolderOf, StringComparer.Ordinal).ThenBy(IsAlternative).ThenBy(static f => f, StringComparer.Ordinal))
        {
            string name = NameOf(file);
            OperatorMatrixPatch patch;
            try
            {
                patch = OperatorMatrixPatch.FromJson(File.ReadAllText(file));
            }
            catch (Exception e) when (e is JsonException or IOException or NotSupportedException or ArgumentException or InvalidOperationException or NullReferenceException)
            {
                skipped.Add($"{name}: unreadable ({e.Message})");
                continue;
            }

            IReadOnlyList<string> problems = patch.Validate();
            if (problems.Count > 0)
            {
                skipped.Add($"{name}: invalid patch ({problems[0]})");
                continue;
            }

            if (ProfileFor(patch, SummaryProfileOf(file)) is not { } profile)
            {
                skipped.Add($"{name}: no profile can play it");
                continue;
            }

            sounds.Add(new ExportedSound(name, file, patch, profile));
        }

        return new ExportedSoundScan(sounds, skipped);
    }

    /// <summary>The profile named in the summary when it plays the patch, otherwise the smallest one that does; null for none.</summary>
    /// <param name="patch">Patch.</param>
    /// <param name="summaryProfile">Profile name from <c>search-summary.json</c>, if any.</param>
    public static SynthesisProfile? ProfileFor(OperatorMatrixPatch patch, string? summaryProfile)
    {
        ArgumentNullException.ThrowIfNull(patch);
        bool Plays(SynthesisProfile p) => PatchCompatibilityValidator.Instance.Validate(patch, p.Capabilities).IsCompatible;
        SynthesisProfile? named = Profiles.FirstOrDefault(p => p.Name == summaryProfile);
        return named is not null && Plays(named) ? named : Profiles.FirstOrDefault(Plays);
    }

    /// <summary>The fingerprint of a sound as the library would take it: rendered with <see cref="StructureLibrary.ReferenceRequest"/>.</summary>
    /// <param name="sound">Sound.</param>
    public static byte[] Fingerprint(ExportedSound sound)
    {
        ArgumentNullException.ThrowIfNull(sound);
        RenderedAudio audio = SynthesisBackends.RendererFor(sound.Profile).Render(sound.Patch, StructureLibrary.ReferenceRequest);
        return SpectralFingerprint.Of(audio.Samples, audio.SampleRate, StructureLibrary.ReferenceHz);
    }

    [GeneratedRegex(@"^\d+-patch\.json$")]
    private static partial Regex AlternativeFile();

    private static bool IsAlternative(string file) => Path.GetFileName(file) != "patch.json";

    private static bool IsExportedPatch(string file) =>
        Path.GetFileName(file) == "patch.json"
        || (Path.GetFileName(Path.GetDirectoryName(file)) == "alternatives" && AlternativeFile().IsMatch(Path.GetFileName(file)));

    private static string ResultFolderOf(string file)
    {
        string folder = Path.GetDirectoryName(file)!;
        return IsAlternative(file) ? Path.GetDirectoryName(folder)! : folder;
    }

    private static string NameOf(string file)
    {
        string name = Path.GetFileName(ResultFolderOf(file));
        return IsAlternative(file) ? $"{name} alt {Path.GetFileName(file).Split('-')[0]}" : name;
    }

    private static string? SummaryProfileOf(string file)
    {
        string summary = Path.Combine(ResultFolderOf(file), "search-summary.json");
        if (!File.Exists(summary))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(summary));
            return Property(Property(document.RootElement, "searchSummary"), "profile") is { ValueKind: JsonValueKind.String } profile ? profile.GetString() : null;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return null;
        }
    }

    private static JsonElement? Property(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value)
        {
            return null;
        }

        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }
}
