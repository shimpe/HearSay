using HearSay.Analysis;
using HearSay.OperatorMatrix.Priors;
using HearSay.Synthesis.Fm7Compatible.Rendering;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay.OperatorMatrix.Tests;

public sealed class StructureLibraryTests
{
    private static readonly SynthesisCapabilities Fm7 = SynthesisProfiles.SuperColliderFm7.Capabilities;
    private static readonly Fm7CompatibleOperatorMatrixRenderer Renderer = new();
    private static readonly Lazy<StructureLibrary> Small = new(() => StructureLibrary.Build(Fm7, 600, Renderer));

    private static byte[] Print(OperatorMatrixPatch patch, double hz)
    {
        RenderedAudio audio = Renderer.Render(patch, RenderRequest.Create(hz, 1.0, 0.3));
        return SpectralFingerprint.Of(audio.Samples, audio.SampleRate, hz);
    }

    [Fact]
    public void Fingerprint_FollowsTheSoundNotThePitch()
    {
        OperatorMatrixPatch bell = ReferencePatches.Find("bell")!.Patch;
        OperatorMatrixPatch piano = ReferencePatches.Find("electric-piano")!.Patch;
        byte[] bellLow = Print(bell, 196), bellHigh = Print(bell, 294), pianoLow = Print(piano, 196);

        Assert.Equal(SpectralFingerprint.Length, bellLow.Length);
        Assert.True(SpectralFingerprint.Distance(bellLow, bellHigh) < SpectralFingerprint.Distance(bellLow, pianoLow) / 3, "the same patch a fifth higher should be much closer than another patch");
        Assert.Equal(0, SpectralFingerprint.Distance(bellLow, Print(bell, 196)));
    }

    [Fact]
    public void Library_FindsAPatchOfItsOwnAsItsNearestNeighbour_AndRegeneratesItFromItsSeed()
    {
        StructureLibrary library = Small.Value;
        Assert.Equal(600, library.Count);

        // Every library patch is musical: a carrier on the note, nothing heard below it.
        (int seed, long _) = library.Nearest(new byte[SpectralFingerprint.Length], 1)[0];
        OperatorMatrixPatch member = library.Patch(seed);
        Assert.Equal(member.ComputeHash(), library.Patch(seed).ComputeHash());
        bool[] audible = member.FindAudibleOperators();
        Assert.Contains(Enumerable.Range(0, member.OperatorCount), i => audible[i] && member.OutputLevels[i] != 0.0 && member.Operators[i].Frequency.Ratio == 1.0);
        Assert.DoesNotContain(Enumerable.Range(0, member.OperatorCount), i => audible[i] && member.Operators[i].Frequency.Ratio < 1.0);

        IReadOnlyList<(int Seed, long Distance)> nearest = library.Nearest(Print(member, StructureLibrary.ReferenceHz), 3);
        Assert.Equal(seed, nearest[0].Seed);
        Assert.Equal(0, nearest[0].Distance);
        Assert.True(nearest[1].Distance >= nearest[0].Distance && nearest[2].Distance >= nearest[1].Distance);
    }

    [Fact]
    public void Library_IsSavedAndLoaded()
    {
        string directory = Path.Combine(Path.GetTempPath(), "hearsay-library-" + Guid.NewGuid().ToString("N"));
        try
        {
            SynthesisProfile profile = SynthesisProfiles.SuperColliderFm7 with { Name = "test-" + Guid.NewGuid().ToString("N") };
            StructureLibrary built = StructureLibrary.For(profile, 50, Renderer, directory);
            string file = Assert.Single(Directory.GetFiles(directory));
            Assert.Contains($"-v{StructureLibrary.FormatVersion}-50.bin", file, StringComparison.Ordinal);

            // Another process would load it; within this one the same instance is shared.
            Assert.Same(built, StructureLibrary.For(profile, 50, Renderer, directory));
            byte[] probe = Print(built.Patch(built.Nearest(new byte[SpectralFingerprint.Length], 1)[0].Seed), StructureLibrary.ReferenceHz);
            Assert.Equal(0, built.Nearest(probe, 1)[0].Distance);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Provider_ProposesEachNeighbourAsItIsAndWithTheEnvelopesOfTheTarget()
    {
        StructureLibrary library = Small.Value;
        OperatorMatrixPatch hidden = ReferencePatches.Find("electric-piano")!.Patch;
        RenderedAudio audio = Renderer.Render(hidden, RenderRequest.Create(220, 1.0, 0.3));
        TargetAnalysis analysis = new TargetAnalyzer().Analyze(new AudioTarget(audio.Samples, audio.SampleRate, 220.0));

        IReadOnlyList<PatchHypothesis> hypotheses = new NearestStructurePriorProvider(library, 4).Generate(analysis, Fm7);

        Assert.Equal(8, hypotheses.Count);
        Assert.All(hypotheses, h => Assert.True(PatchCompatibilityValidator.Instance.Validate(h.Patch, Fm7).IsCompatible));
        Assert.Contains("with the envelopes of the target", hypotheses[1].Origin, StringComparison.Ordinal);
        Assert.Equal(hypotheses[0].Patch.ModulationMatrix, hypotheses[1].Patch.ModulationMatrix);
        Assert.True(hypotheses[0].PriorScore > hypotheses[2].PriorScore);
    }
}
