using HearSay.OperatorMatrix.Priors;
using HearSay.Synthesis.Fm7Compatible.Rendering;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.IntegrationTests;

/// <summary>Reading folders of exported sounds and saving library sounds; fast, no search.</summary>
public sealed class ExportedSoundsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hearsay-exported-" + Guid.NewGuid().ToString("N"));

    public ExportedSoundsTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static OperatorMatrixPatch Sine => ReferencePatches.Find("electric-piano")!.Patch;

    private static OperatorMatrixPatch With(OperatorWaveform waveform) =>
        OperatorMatrixPatch.Silent(6).WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1)) with { Waveform = waveform }).WithOutputLevel(0, 1.0);

    private string Folder(string relative, OperatorMatrixPatch patch, string? summaryProfile = null)
    {
        string folder = Path.Combine(_directory, relative);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "patch.json"), patch.ToJson());
        if (summaryProfile is not null)
        {
            File.WriteAllText(Path.Combine(folder, "search-summary.json"), $$"""{ "searchSummary": { "profile": "{{summaryProfile}}", "randomSeed": 1 }, "candidates": [] }""");
        }

        return folder;
    }

    [Fact]
    public void Save_ThenScan_GivesTheSamePatchAndProfile()
    {
        OperatorMatrixPatch patch = With(OperatorWaveform.Saw).WithOperator(0, With(OperatorWaveform.Saw).Operators[0] with { Filter = OperatorFilter.LowPass(4) });
        IReadOnlyList<string> files = LibrarySoundWriter.Write(patch, SynthesisProfiles.SuperColliderFmx6, Path.Combine(_directory, "kept"), ["HearSay library supercollider-fmx6 v2, seed 7."]);
        Assert.Contains(files, static f => f.EndsWith("patch.scd", StringComparison.Ordinal));
        Assert.Contains(files, static f => f.EndsWith("preview.wav", StringComparison.Ordinal));
        Assert.Contains("seed 7", File.ReadAllText(Path.Combine(_directory, "kept", "patch.scd")), StringComparison.Ordinal);

        ExportedSound sound = Assert.Single(ExportedSounds.Scan(_directory).Sounds);
        Assert.Equal("kept", sound.Name);
        Assert.Equal(SynthesisProfiles.SuperColliderFmx6.Name, sound.Profile.Name);
        Assert.Equal(patch.ComputeHash(), sound.Patch.ComputeHash());
    }

    [Fact]
    public void Scan_ReadsAlternatives_AndTheProfileFromTheSummary()
    {
        string folder = Folder("piano-c4", Sine, SynthesisProfiles.SuperColliderFmx6.Name);
        string alternatives = Path.Combine(folder, "alternatives");
        Directory.CreateDirectory(alternatives);
        File.WriteAllText(Path.Combine(alternatives, "01-patch.json"), Sine.ToJson());
        File.WriteAllText(Path.Combine(alternatives, "02-patch.json"), Sine.ToJson());
        File.WriteAllText(Path.Combine(alternatives, "01-patch.scd"), "// not a patch");
        File.WriteAllText(Path.Combine(folder, "analysis.json"), "{}");

        ExportedSoundScan scan = ExportedSounds.Scan(_directory);
        Assert.Empty(scan.Skipped);
        Assert.Equal(new[] { "piano-c4", "piano-c4 alt 01", "piano-c4 alt 02" }, scan.Sounds.Select(static s => s.Name));
        Assert.All(scan.Sounds, static s => Assert.Equal(SynthesisProfiles.SuperColliderFmx6.Name, s.Profile.Name));
    }

    [Fact]
    public void Scan_FallsBackToTheSmallestProfileThatPlaysThePatch()
    {
        Folder("sine", Sine);
        Folder("saw", With(OperatorWaveform.Saw));
        Folder("elliptic", With(OperatorWaveform.Elliptic));
        Dictionary<string, string> profiles = ExportedSounds.Scan(_directory).Sounds.ToDictionary(static s => s.Name, static s => s.Profile.Name);
        Assert.Equal(SynthesisProfiles.SuperColliderFm7.Name, profiles["sine"]);
        Assert.Equal(SynthesisProfiles.SuperColliderFmx6.Name, profiles["saw"]);
        Assert.Equal(SynthesisProfiles.SuperColliderNome6.Name, profiles["elliptic"]);
    }

    [Fact]
    public void Scan_FallsBackWhenTheSummaryProfileCannotPlayThePatch()
    {
        Folder("saw-said-fm7", With(OperatorWaveform.Saw), SynthesisProfiles.SuperColliderFm7.Name);
        Folder("unknown-profile", Sine, "some-synth");
        Dictionary<string, string> profiles = ExportedSounds.Scan(_directory).Sounds.ToDictionary(static s => s.Name, static s => s.Profile.Name);
        Assert.Equal(SynthesisProfiles.SuperColliderFmx6.Name, profiles["saw-said-fm7"]);
        Assert.Equal(SynthesisProfiles.SuperColliderFm7.Name, profiles["unknown-profile"]);
    }

    [Fact]
    public void Scan_SkipsWhatItCannotUse_WithTheReason()
    {
        Folder("good", Sine);
        Directory.CreateDirectory(Path.Combine(_directory, "broken"));
        File.WriteAllText(Path.Combine(_directory, "broken", "patch.json"), "{ not json");
        Folder("invalid", Sine.WithOperator(0, Sine.Operators[0] with { HarmonicLimit = 0 }));

        ExportedSoundScan scan = ExportedSounds.Scan(_directory);
        Assert.Equal("good", Assert.Single(scan.Sounds).Name);
        Assert.Contains(scan.Skipped, static s => s.StartsWith("broken: unreadable", StringComparison.Ordinal));
        Assert.Contains(scan.Skipped, static s => s.StartsWith("invalid: invalid patch", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_OfAFolderWithoutPatches_FindsNothing()
    {
        File.WriteAllText(Path.Combine(_directory, "notes.txt"), "nothing here");
        ExportedSoundScan scan = ExportedSounds.Scan(_directory);
        Assert.Empty(scan.Sounds);
        Assert.Empty(scan.Skipped);
    }

    [Fact]
    public void Fingerprint_IsTheOneTheLibraryWouldGive()
    {
        StructureLibrary library = StructureLibrary.Build(SynthesisProfiles.SuperColliderFm7.Capabilities, 20, new Fm7CompatibleOperatorMatrixRenderer());
        var sound = new ExportedSound("member", "patch.json", library.Patch(library.Seed(3)), SynthesisProfiles.SuperColliderFm7);
        Assert.Equal(library.Fingerprint(3).ToArray(), ExportedSounds.Fingerprint(sound));
    }
}
