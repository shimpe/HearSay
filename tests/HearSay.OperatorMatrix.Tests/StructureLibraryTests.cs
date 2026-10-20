using System.Collections.Concurrent;
using HearSay.Analysis;
using HearSay.OperatorMatrix.Priors;
using HearSay.Synthesis.Fm7Compatible.Rendering;
using HearSay.Synthesis.Fmx6Compatible.Rendering;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay.OperatorMatrix.Tests;

public sealed class StructureLibraryTests
{
    private static readonly SynthesisCapabilities Fm7 = SynthesisProfiles.SuperColliderFm7.Capabilities;
    private static readonly Fm7CompatibleOperatorMatrixRenderer Renderer = new();
    private static readonly Lazy<StructureLibrary> Small = new(() => StructureLibrary.Build(Fm7, 600, Renderer));

    private static readonly SynthesisProfile Fmx6 = SynthesisProfiles.SuperColliderFmx6;
    private static readonly Fmx6CompatibleOperatorMatrixRenderer Fmx6Renderer = new();

    private static byte[] Print(OperatorMatrixPatch patch, double hz, IOperatorMatrixRenderer? renderer = null)
    {
        RenderedAudio audio = (renderer ?? Renderer).Render(patch, RenderRequest.Create(hz, 1.0, 0.3));
        return SpectralFingerprint.Of(audio.Samples, audio.SampleRate, hz);
    }

    private sealed class Recorder : IProgress<double>
    {
        public ConcurrentBag<double> Values { get; } = [];

        public void Report(double value) => Values.Add(value);
    }

    [Fact]
    public void Library_GivesItsFingerprintsAndSeedsByIndex_AndReportsBuildProgress()
    {
        var recorder = new Recorder();
        StructureLibrary library = StructureLibrary.Build(Fm7, 300, Renderer, recorder);
        foreach (int i in new[] { 0, 7, 299 })
        {
            byte[] expected = Print(library.Patch(library.Seed(i)), StructureLibrary.ReferenceHz);
            Assert.Equal(expected, library.Fingerprint(i).ToArray());
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => library.Fingerprint(300).Length);
        Assert.NotEmpty(recorder.Values);
        Assert.All(recorder.Values, static v => Assert.InRange(v, 0.0, 1.0));
        Assert.Contains(1.0, recorder.Values);
        Assert.Equal(RenderRequest.Create(StructureLibrary.ReferenceHz, 1.0, 0.3), StructureLibrary.ReferenceRequest);
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
            Assert.Equal(StructureLibrary.FileOf(profile, 50, directory), file);
            Assert.Contains($"-v{profile.StructureLibraryVersion}-50.bin", file, StringComparison.Ordinal);

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
    public void LibraryVersion_IsPerProfile_AndTheFmx6LibraryHasFilters()
    {
        string directory = Path.Combine(Path.GetTempPath(), "hearsay-library-" + Guid.NewGuid().ToString("N"));
        try
        {
            SynthesisProfile fm7 = SynthesisProfiles.SuperColliderFm7 with { Name = "test-fm7-" + Guid.NewGuid().ToString("N") };
            SynthesisProfile fmx6 = SynthesisProfiles.SuperColliderFmx6 with { Name = "test-fmx6-" + Guid.NewGuid().ToString("N") };
            StructureLibrary.For(fm7, 30, Renderer, directory);
            StructureLibrary library = StructureLibrary.For(fmx6, 120, new HearSay.Synthesis.Fmx6Compatible.Rendering.Fmx6CompatibleOperatorMatrixRenderer(), directory);

            Assert.Single(Directory.GetFiles(directory, $"{fm7.Name}-v1-30.bin"));
            Assert.Single(Directory.GetFiles(directory, $"{fmx6.Name}-v8-120.bin"));
            int filtered = 0;
            foreach ((int seed, long _) in library.Nearest(new byte[SpectralFingerprint.Length], 240))
            {
                filtered += library.Patch(seed).Operators.Any(static o => o.Filter is not null) ? 1 : 0;
            }

            Assert.InRange(filtered, 60, 220);
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

    private static bool HasNoise(OperatorMatrixPatch patch) =>
        patch.Operators.Any(static o => o.Enabled && (o.Waveform.IsNoise() || o.NoisePhase != 0.0 || o.NoiseM != 0.0));

    [Fact]
    public void Library_OfAProfileWithNoise_HasAsManyNoiseFreeSoundsAgain()
    {
        StructureLibrary library = StructureLibrary.Build(Fmx6.Capabilities, 60, Fmx6Renderer);

        Assert.Equal(120, library.Count);
        Assert.Equal(120, StructureLibrary.CountFor(Fmx6.Capabilities, 60));
        Assert.Equal(60, StructureLibrary.CountFor(Fm7, 60));
        Assert.Equal(Enumerable.Range(50_000_000, 60), Enumerable.Range(0, 60).Select(library.Seed));
        Assert.Contains(Enumerable.Range(0, 60), i => HasNoise(library.Patch(library.Seed(i))));
        Assert.All(Enumerable.Range(60, 60), i => Assert.False(HasNoise(library.Patch(library.Seed(i))), $"sound {i}"));
        Assert.Equal(Print(library.Patch(library.Seed(90)), StructureLibrary.ReferenceHz, Fmx6Renderer), library.Fingerprint(90).ToArray());
    }

    [Fact]
    public void NoiseFreeSounds_OfNome6_AreNotRoughenedEither()
    {
        SynthesisCapabilities nome6 = SynthesisProfiles.SuperColliderNome6.Capabilities;
        Assert.Contains(Enumerable.Range(0, 200), i => HasNoise(StructureLibrary.Generate(nome6, 50_000_000 + i)));
        Assert.All(Enumerable.Range(0, 200), i => Assert.False(HasNoise(StructureLibrary.Generate(nome6, StructureLibrary.NoiseFreeFirstSeed + i))));
    }

    /// <summary>A digest of the library patches of some seeds: every patch hash, in seed order.</summary>
    private static string Digest(SynthesisCapabilities capabilities, IEnumerable<int> seeds, Func<OperatorMatrixPatch, bool>? keep = null)
    {
        var text = new System.Text.StringBuilder();
        foreach (int seed in seeds)
        {
            OperatorMatrixPatch patch = StructureLibrary.Generate(capabilities, seed);
            if (keep is null || keep(patch))
            {
                text.Append(seed).Append(':').Append(patch.ComputeHash()).Append('\n');
            }
        }

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())));
    }

    [Fact]
    public void Fm7AndNome6Patches_AreUnchangedByTheThirdFormant()
    {
        // Captured from the generator before formant filters got a third peak. The FM7 and Nome6 libraries are not
        // rebuilt, so their patches must stay exactly what their seeds gave; so must every FMX6 patch without a
        // formant filter, as the extra draws happen only on the formant path.
        IEnumerable<int> regular = Enumerable.Range(50_000_000, 300);
        IEnumerable<int> noiseFree = Enumerable.Range(StructureLibrary.NoiseFreeFirstSeed, 300);
        static bool NoFormant(OperatorMatrixPatch p) => !p.Operators.Any(static o => o.Filter?.Type == OperatorFilterType.Formant);
        Assert.Equal("B17C19F05BF7501AE11FABB7F34F0FD064E6318C90F5A4AD897D67074255846D", Digest(Fm7, regular));
        Assert.Equal("6743B8E21EB980CAA9FBCEED879908E30D3DECAB07100EACFCD068D96C7E6DB3", Digest(SynthesisProfiles.SuperColliderNome6.Capabilities, regular));
        Assert.Equal("7AAA85BB7F051AB4DEDAAB5DC9D251B2E0164D8460CD264C2C980ACADE40470C", Digest(SynthesisProfiles.SuperColliderNome6.Capabilities, noiseFree));

        // 486 of these 600 FMX6 patches have no formant filter.
        Assert.Equal(486, Enumerable.Range(50_000_000, 600).Count(s => NoFormant(StructureLibrary.Generate(Fmx6.Capabilities, s))));
        Assert.Equal("9439CB8A856E6B71D55110BE6B36066237F314E38AD0F93D552FF36D57EF02C2", Digest(Fmx6.Capabilities, Enumerable.Range(50_000_000, 600), NoFormant));
    }

    [Fact]
    public void Nome6_HasANewLibraryVersionThatReusesTheOldOne_AndFm7KeepsVersion1()
    {
        Assert.Equal((2, 1), (SynthesisProfiles.SuperColliderNome6.StructureLibraryVersion, SynthesisProfiles.SuperColliderNome6.StructureLibraryReusedVersion));
        Assert.Equal((1, (int?)null), (SynthesisProfiles.SuperColliderFm7.StructureLibraryVersion, SynthesisProfiles.SuperColliderFm7.StructureLibraryReusedVersion));
    }

    [Fact]
    public void Fmx6LibraryVersion_IsBumped_AndDoesNotReuseOldFormantSounds()
    {
        // Version 8: every operator with a formant filter is a rich source (version 7 made every tonal one the 1 % voice
        // source, version 6 gave it at least 64 harmonics, version 5 every formant filter a third peak, version 4 half
        // of them), so the sounds of versions 2 to 7 (whose formant sounds differ) must not be read for it.
        Assert.Equal((8, (int?)null), (Fmx6.StructureLibraryVersion, Fmx6.StructureLibraryReusedVersion));

        string directory = Path.Combine(Path.GetTempPath(), "hearsay-library-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            SynthesisProfile profile = Fmx6 with { Name = "test-fmx6-" + Guid.NewGuid().ToString("N") };
            WriteOldLibrary(StructureLibrary.FileOf(profile with { StructureLibraryVersion = 2, StructureLibraryReusedVersion = null }, 40, directory), 2, 40, marker: 7);
            WriteOldLibrary(StructureLibrary.FileOf(profile with { StructureLibraryVersion = 3, StructureLibraryReusedVersion = null }, 40, directory), 3, 40, marker: 9);
            WriteOldLibrary(StructureLibrary.FileOf(profile with { StructureLibraryVersion = 4, StructureLibraryReusedVersion = null }, 40, directory), 4, 40, marker: 11);
            WriteOldLibrary(StructureLibrary.FileOf(profile with { StructureLibraryVersion = 5, StructureLibraryReusedVersion = null }, 40, directory), 5, 40, marker: 13);
            WriteOldLibrary(StructureLibrary.FileOf(profile with { StructureLibraryVersion = 6, StructureLibraryReusedVersion = null }, 40, directory), 6, 40, marker: 15);
            WriteOldLibrary(StructureLibrary.FileOf(profile with { StructureLibraryVersion = 7, StructureLibraryReusedVersion = null }, 40, directory), 7, 40, marker: 17);

            // Every sound of version 8 is rendered, none is read from the old files.
            Assert.False(StructureLibrary.IsAvailable(profile, 40, directory));
            Assert.Equal(StructureLibrary.CountFor(profile.Capabilities, 40), StructureLibrary.SoundsToRender(profile, 40, directory));
            Assert.EndsWith("-v8-40.bin", StructureLibrary.FileOf(profile, 40, directory), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Writes a library file of an older version by hand: every fingerprint byte is <paramref name="marker"/>.</summary>
    private static void WriteOldLibrary(string file, int version, int size, byte marker)
    {
        using var writer = new BinaryWriter(File.Create(file));
        writer.Write(version);
        writer.Write(size);
        writer.Write(SpectralFingerprint.Length);
        for (int i = 0; i < size; i++)
        {
            writer.Write(50_000_000 + i);
        }

        writer.Write(Enumerable.Repeat(marker, size * SpectralFingerprint.Length).ToArray());
    }

    [Fact]
    public void NewVersion_ReusesTheSoundsOfTheOldFile_AndRendersOnlyTheNoiseFreeOnes()
    {
        string directory = Path.Combine(Path.GetTempPath(), "hearsay-library-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // The reuse as FMX6 version 3 had it (Nome6 still does): the sounds of version 2 are the regular ones of 3.
            SynthesisProfile profile = Fmx6 with { Name = "test-fmx6-" + Guid.NewGuid().ToString("N"), StructureLibraryVersion = 3, StructureLibraryReusedVersion = 2 };
            SynthesisProfile old = profile with { StructureLibraryVersion = 2, StructureLibraryReusedVersion = null };
            Assert.Equal(80, StructureLibrary.SoundsToRender(profile, 40, directory));
            WriteOldLibrary(StructureLibrary.FileOf(old, 40, directory), 2, 40, marker: 7);
            Assert.Equal(40, StructureLibrary.SoundsToRender(profile, 40, directory));

            var recorder = new Recorder();
            StructureLibrary library = StructureLibrary.For(profile, 40, Fmx6Renderer, directory, recorder);

            Assert.Equal(80, library.Count);
            Assert.All(library.Fingerprint(0).ToArray(), static b => Assert.Equal(7, b));
            Assert.Equal(Print(library.Patch(library.Seed(50)), StructureLibrary.ReferenceHz, Fmx6Renderer), library.Fingerprint(50).ToArray());
            Assert.Contains(1.0, recorder.Values);
            Assert.Equal(0, StructureLibrary.SoundsToRender(profile, 40, directory));

            // Saved whole: another process loads all 80 sounds from the new file.
            Assert.True(File.Exists(StructureLibrary.FileOf(profile, 40, directory)));
            Assert.True(new FileInfo(StructureLibrary.FileOf(profile, 40, directory)).Length > 80 * SpectralFingerprint.Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ABuildInProgress_ReportsToEveryCallerThatWaitsForIt()
    {
        string directory = Path.Combine(Path.GetTempPath(), "hearsay-library-" + Guid.NewGuid().ToString("N"));
        try
        {
            SynthesisProfile profile = SynthesisProfiles.SuperColliderFm7 with { Name = "test-" + Guid.NewGuid().ToString("N") };
            var first = new Recorder();
            var second = new Recorder();
            Task<StructureLibrary> building = Task.Run(() => StructureLibrary.For(profile, 2000, Renderer, directory, first));
            SpinWait.SpinUntil(() => !first.Values.IsEmpty, TimeSpan.FromSeconds(30));
            StructureLibrary waited = StructureLibrary.For(profile, 2000, Renderer, directory, second);

            Assert.Same(await building, waited);
            Assert.Contains(1.0, second.Values);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
