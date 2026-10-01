using HearSay.Analysis;
using HearSay.OperatorMatrix.Priors;
using HearSay.OperatorMatrix.Search;
using HearSay.SuperCollider.FM7;
using HearSay.SuperCollider.FMX6;
using HearSay.SuperCollider.Nome6;
using HearSay.Synthesis.Fm7Compatible.Rendering;
using HearSay.Synthesis.Fmx6Compatible.Rendering;
using HearSay.Synthesis.Nome6Compatible.Rendering;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;
using Xunit.Abstractions;

namespace HearSay.IntegrationTests;

/// <summary>
/// End-to-end searches on synthetic targets. The patch that made the target is hidden from the matcher, and nothing
/// here asks for its parameters back: many patches sound alike, so the tests ask for a large reduction of the audio
/// loss and a high similarity instead.
/// </summary>
public sealed class PatchMatchingTests(ITestOutputHelper output)
{
    private static readonly Fm7CompatibleOperatorMatrixRenderer Renderer = new();

    private static AudioTarget TargetOf(string name)
    {
        ReferencePatch reference = ReferencePatches.Find(name)!;
        RenderedAudio audio = Renderer.Render(reference.Patch, RenderRequest.Create(reference.FrequencyHz, reference.NoteSeconds, reference.ReleaseSeconds));
        return new AudioTarget(audio.Samples, audio.SampleRate, reference.FrequencyHz);
    }

    [Theory]
    [InlineData("simple-2op", 5.0)]
    [InlineData("brass-like", 4.0)]
    [InlineData("bell", 2.0)]
    public void RoundTrip_ReducesTheAudioLossStrongly(string name, double minimumGain)
    {
        PatchMatchResult result = new OperatorMatrixPatchMatcher().Match(TargetOf(name), new PatchMatchOptions { EvaluationBudget = 9000 });

        SearchSummary summary = result.SearchSummary;
        output.WriteLine($"{name}: {summary.InitialAudioLoss:F4} -> {summary.FinalAudioLoss:F4}, similarity {result.Comparison.Scores.Similarity:F3}, {summary.EvaluationsUsed} renders, {summary.Elapsed.TotalSeconds:F1} s");
        output.WriteLine($"  {result.AlternativePatches[0].Regime} | {result.AlternativePatches[0].Origin}");
        Assert.True(summary.FinalAudioLoss * minimumGain < summary.InitialAudioLoss, $"{summary.InitialAudioLoss} -> {summary.FinalAudioLoss}");
        Assert.True(result.Comparison.Scores.Similarity > 0.7, $"similarity {result.Comparison.Scores.Similarity}");
        Assert.InRange(summary.EvaluationsUsed, 1000, 9000);
        Assert.True(PatchCompatibilityValidator.Instance.Validate(result.BestPatch, SynthesisProfiles.SuperColliderFm7.Capabilities).IsCompatible);
        Assert.Equal(result.Request.TotalSamples, result.PreviewAudio.Samples.Length);
        Assert.True(result.AlternativePatches.Count >= 2);
        Assert.True(result.AlternativePatches[0].Loss <= result.AlternativePatches[1].Loss);
    }

    [Fact]
    public void Search_IsReproducibleFromItsSeedAndIndependentOfParallelism()
    {
        AudioTarget target = TargetOf("simple-2op");
        var matcher = new OperatorMatrixPatchMatcher();

        PatchMatchResult a = matcher.Match(target, new PatchMatchOptions { EvaluationBudget = 1500, RandomSeed = 42, MaxParallelEvaluations = 2 });
        PatchMatchResult b = matcher.Match(target, new PatchMatchOptions { EvaluationBudget = 1500, RandomSeed = 42, MaxParallelEvaluations = 8 });
        PatchMatchResult c = matcher.Match(target, new PatchMatchOptions { EvaluationBudget = 1500, RandomSeed = 43, MaxParallelEvaluations = 8 });

        Assert.Equal(a.BestPatch.ComputeHash(), b.BestPatch.ComputeHash());
        Assert.Equal(a.SearchSummary.EvaluationsUsed, b.SearchSummary.EvaluationsUsed);
        Assert.Equal(a.SearchSummary.FinalAudioLoss, b.SearchSummary.FinalAudioLoss);
        Assert.Equal(a.SearchSummary.Basins.Select(static x => x.BestLoss), b.SearchSummary.Basins.Select(static x => x.BestLoss));
        Assert.NotEqual(a.BestPatch.ComputeHash(), c.BestPatch.ComputeHash());
    }

    [Fact]
    public void Search_RespectsTheBudgetAndReportsItsCourse()
    {
        var coordinator = new OperatorMatrixPatchSearchCoordinator(Renderer, [new SinePhaseModulationPriorProvider()]);
        var reports = new List<SearchProgress>();

        SearchOutcome outcome = coordinator.Search(TargetOf("bass"), new PatchMatchOptions { EvaluationBudget = 1200 }, new Collector(reports));

        SearchSummary summary = outcome.Summary;
        Assert.InRange(summary.EvaluationsUsed, 600, 1200);
        Assert.Equal(summary.EvaluationsUsed, summary.Stages.Sum(static s => s.Evaluations));
        Assert.Equal(
            ["target analysis", "hypothesis screening", "survivor optimization", "deep optimization", "finalist optimization", "local refinement"],
            summary.Stages.Select(static s => s.Name));
        Assert.NotEmpty(reports);
        Assert.All(reports, r => Assert.InRange(r.EvaluationsUsed, 0, 1200));
        Assert.True(summary.Basins.Count > outcome.Candidates.Count, "basins that were retired are reported too");
        Assert.NotEmpty(outcome.Models.Regimes);
        Assert.All(outcome.Candidates, static c => Assert.Equal(c.Regime, OperatorMatrix.Canonicalization.PatchCanonicalizer.CanonicalKey(c.Patch)));
    }

    [Fact]
    public void Search_NeverLeavesTheProfile()
    {
        // A renderer that can do less than the profile asks for is refused up front.
        var coordinator = new OperatorMatrixPatchSearchCoordinator(Renderer, [new SinePhaseModulationPriorProvider()]);
        Assert.Throws<ArgumentException>(() => coordinator.Search(TargetOf("bass"), new PatchMatchOptions { SynthesisProfile = SynthesisProfiles.ExperimentalSineTriangle, EvaluationBudget = 500 }));

        // A profile without feedback yields patches without feedback, even for a target that was made with it.
        SynthesisProfile plain = SynthesisProfiles.SuperColliderFm7 with
        {
            Name = "fm7-feed-forward",
            Capabilities = SynthesisProfiles.SuperColliderFm7.Capabilities with { SupportsSelfFeedback = false, SupportsCircularFeedback = false },
        };
        PatchMatchResult result = new OperatorMatrixPatchMatcher().Match(TargetOf("feedback-heavy"), new PatchMatchOptions { SynthesisProfile = plain, EvaluationBudget = 2500 });

        Assert.All(result.AlternativePatches, c =>
        {
            Assert.True(PatchCompatibilityValidator.Instance.Validate(c.Patch, plain.Capabilities).IsCompatible);
            Assert.DoesNotContain(c.Patch.ModulationMatrix.Edges, static e => e.Enabled && e.IsSelfFeedback);
        });
    }

    [Fact]
    public void FutureWaveformProof_WaveformIsStructuralAndBackendsRejectWhatTheyCannotPlay()
    {
        // Target made with a triangle operator, rendered by the experimental renderer.
        SynthesisProfile profile = SynthesisProfiles.ExperimentalSineTriangle;
        var extended = new ExtendedOperatorMatrixRenderer(profile, oversampling: 2);
        OperatorMatrixPatch hidden = OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.005, 0.3, 0.7, 0.2)) with { Waveform = OperatorWaveform.Triangle })
            .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.005, 0.4, 0.5, 0.2)))
            .WithOutputLevel(0, 0.8)
            .WithEdge(new ModulationEdge(1, 0, true, 0.4));
        RenderedAudio audio = extended.Render(hidden, RenderRequest.Create(196, 0.7, 0.25));
        var target = new AudioTarget(audio.Samples, audio.SampleRate, 196.0);

        // The same coordinator, given another renderer and another provider, searches the larger space.
        var coordinator = new OperatorMatrixPatchSearchCoordinator(extended, [new WaveformVariantPriorProvider(new SinePhaseModulationPriorProvider())]);
        SearchOutcome outcome = coordinator.Search(target, new PatchMatchOptions { SynthesisProfile = profile, EvaluationBudget = 3000 });

        // Waveform separates basins: sine and triangle structures were optimized apart, never blended.
        string[] regimes = [.. outcome.Summary.Basins.Select(static b => b.Regime)];
        Assert.Contains(regimes, static r => r.Contains("/Triangle/", StringComparison.Ordinal));
        Assert.Contains(regimes, static r => !r.Contains("/Triangle/", StringComparison.Ordinal));
        Assert.All(outcome.Candidates, static c => Assert.All(c.Patch.Operators, static o => Assert.True(o.Waveform is OperatorWaveform.Sine or OperatorWaveform.Triangle)));
        OperatorMatrixPatch best = outcome.Candidates[0].Patch;
        output.WriteLine($"best: {outcome.Candidates[0].Regime} ({outcome.Candidates[0].AudioLoss:F4})");
        Assert.Contains(best.Operators, static o => o.Enabled && o.Waveform == OperatorWaveform.Triangle);

        // FM7 cannot play it, and says so instead of approximating.
        Assert.False(PatchCompatibilityValidator.Instance.Validate(best, SynthesisProfiles.SuperColliderFm7.Capabilities).IsCompatible);
        Assert.Throws<PatchNotSupportedException>(() => new SuperColliderFm7Exporter().Export(best));
        Assert.Throws<PatchNotSupportedException>(() => Renderer.Render(best, RenderRequest.Create(196, 0.2, 0.1)));
        Assert.Null(SynthesisBackends.ExporterFor(profile));

        // Under the FM7 profile the very same target is searched with sine operators only.
        PatchMatchResult fm7 = new OperatorMatrixPatchMatcher().Match(target, new PatchMatchOptions { EvaluationBudget = 1500 });
        Assert.All(fm7.AlternativePatches, static c => Assert.All(c.Patch.Operators, static o => Assert.Equal(OperatorWaveform.Sine, o.Waveform)));
        Assert.NotNull(new SuperColliderFm7Exporter().Export(fm7.BestPatch));
    }

    [Fact]
    public void Fmx6Profile_FindsBandLimitedOperatorsThatFm7CannotPlay()
    {
        // Target made with a saw operator under a little modulation, rendered by the FMX6-compatible renderer.
        SynthesisProfile profile = SynthesisProfiles.SuperColliderFmx6;
        var fmx6 = new Fmx6CompatibleOperatorMatrixRenderer();
        OperatorMatrixPatch hidden = OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.005, 0.3, 0.7, 0.2)) with { Waveform = OperatorWaveform.Saw, HarmonicLimit = 48 })
            .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(2), OperatorEnvelope.Adsr(0.005, 0.4, 0.5, 0.2)))
            .WithOutputLevel(0, 0.8)
            .WithEdge(new ModulationEdge(1, 0, true, 0.3));
        RenderedAudio audio = fmx6.Render(hidden, RenderRequest.Create(196, 0.7, 0.25));
        var target = new AudioTarget(audio.Samples, audio.SampleRate, 196.0);

        PatchMatchResult result = new OperatorMatrixPatchMatcher().Match(target, new PatchMatchOptions { SynthesisProfile = profile, EvaluationBudget = 3000 });

        output.WriteLine($"best: {result.AlternativePatches[0].Regime} ({result.AlternativePatches[0].AudioLoss:F4})");
        Assert.Same(profile, result.Profile);
        Assert.Contains(result.BestPatch.Operators, static o => o.Enabled && o.Waveform != OperatorWaveform.Sine);
        Assert.All(result.AlternativePatches, static c => Assert.All(c.Patch.Operators, static o => Assert.Contains(o.Waveform, SynthesisProfiles.SuperColliderFmx6.Capabilities.SupportedWaveforms)));

        // FMX6 exports it, FM7 refuses it; the FMX6 exporter is what the facade hands out for this profile.
        Assert.IsType<SuperColliderFmx6Exporter>(SynthesisBackends.ExporterFor(profile));
        string scd = SynthesisBackends.ExporterFor(profile)!.Export(result.BestPatch);
        Assert.Contains("FMX6.ar(ctlMatrix, modMatrix,", scd);
        Assert.Throws<PatchNotSupportedException>(() => new SuperColliderFm7Exporter().Export(result.BestPatch));
        Assert.Throws<PatchNotSupportedException>(() => Renderer.Render(result.BestPatch, RenderRequest.Create(196, 0.2, 0.1)));

        // Every FM7 patch is an FMX6 patch: the sine-only search of the same target exports to both.
        PatchMatchResult fm7 = new OperatorMatrixPatchMatcher().Match(target, new PatchMatchOptions { EvaluationBudget = 1500 });
        Assert.Contains("waves: [0, 0, 0, 0, 0, 0]", new SuperColliderFmx6Exporter().Export(fm7.BestPatch));
    }

    [Fact]
    public void Nome6Profile_FindsAnEllipticOperatorThatFm7CannotPlay()
    {
        // Target made with one bright elliptic operator under a little modulation, rendered by the Nome6-compatible renderer.
        SynthesisProfile profile = SynthesisProfiles.SuperColliderNome6;
        var nome6 = new Nome6CompatibleOperatorMatrixRenderer();
        OperatorMatrixPatch hidden = OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.005, 0.3, 0.7, 0.2)) with { Waveform = OperatorWaveform.Elliptic, Shape = 0.999 })
            .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(2), OperatorEnvelope.Adsr(0.005, 0.4, 0.5, 0.2)))
            .WithOutputLevel(0, 0.8)
            .WithEdge(new ModulationEdge(1, 0, true, 0.3));
        RenderedAudio audio = nome6.Render(hidden, RenderRequest.Create(196, 0.7, 0.25));
        var target = new AudioTarget(audio.Samples, audio.SampleRate, 196.0);

        Assert.IsType<Nome6CompatibleOperatorMatrixRenderer>(SynthesisBackends.RendererFor(profile));
        PatchMatchResult result = new OperatorMatrixPatchMatcher().Match(target, new PatchMatchOptions { SynthesisProfile = profile, EvaluationBudget = 3000 });

        output.WriteLine($"best: {result.AlternativePatches[0].Regime} ({result.AlternativePatches[0].AudioLoss:F4})");
        Assert.Same(profile, result.Profile);
        Assert.Contains(result.BestPatch.Operators, static o => o.Enabled && o.Waveform == OperatorWaveform.Elliptic);
        Assert.True(result.AlternativePatches[0].AudioLoss < 0.1, $"audio loss {result.AlternativePatches[0].AudioLoss:F4}");

        // Nome6 exports it, FM7 refuses it; the Nome6 exporter is what the facade hands out for this profile.
        Assert.IsType<SuperColliderNome6Exporter>(SynthesisBackends.ExporterFor(profile));
        Assert.Contains("operators = Nome6.ar(", SynthesisBackends.ExporterFor(profile)!.Export(result.BestPatch));
        Assert.Throws<PatchNotSupportedException>(() => new SuperColliderFm7Exporter().Export(result.BestPatch));
    }

    [Fact]
    public void Fmx6Profile_MatchesANoiseBurstWithANoiseOperator()
    {
        // A sine with a short white-noise burst at the attack, rendered by the FMX6-compatible renderer. The search
        // never sees the same noise sequence: the operator that made the target sits in another slot than the one a
        // canonical patch will use, so this is a test of structure, not of a lucky realization.
        SynthesisProfile profile = SynthesisProfiles.SuperColliderFmx6;
        var fmx6 = new Fmx6CompatibleOperatorMatrixRenderer();
        OperatorMatrixPatch hidden = OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.005, 0.5, 0.0, 0.2)))
            .WithOperator(4, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.001, 0.08, 0.0, 0.05)) with { Waveform = OperatorWaveform.Noise })
            .WithOutputLevel(0, 0.7)
            .WithOutputLevel(4, 0.4);
        RenderedAudio audio = fmx6.Render(hidden, RenderRequest.Create(196, 0.7, 0.25));
        var target = new AudioTarget(audio.Samples, audio.SampleRate, 196.0);

        PatchMatchResult fmx6Result = new OperatorMatrixPatchMatcher().Match(target, new PatchMatchOptions { SynthesisProfile = profile, EvaluationBudget = 3000 });
        PatchMatchResult fm7Result = new OperatorMatrixPatchMatcher().Match(target, new PatchMatchOptions { EvaluationBudget = 3000 });

        output.WriteLine($"FMX6 best: {fmx6Result.AlternativePatches[0].Regime} ({fmx6Result.AlternativePatches[0].AudioLoss:F4}); FM7 best: {fm7Result.AlternativePatches[0].Regime} ({fm7Result.AlternativePatches[0].AudioLoss:F4})");
        Assert.Contains(fmx6Result.BestPatch.Operators, static o => o.Enabled && o.Waveform is OperatorWaveform.Noise or OperatorWaveform.SampleHoldNoise);
        Assert.True(fmx6Result.AlternativePatches[0].AudioLoss < fm7Result.AlternativePatches[0].AudioLoss, "a noise operator should beat the sine-only match of a noise burst");
    }

    [Fact]
    public void MatchBestOf_KeepsTheWinningSeedAndMergesTheFinalistsOfAllSeeds()
    {
        AudioTarget target = TargetOf("simple-2op");
        var options = new PatchMatchOptions { EvaluationBudget = 600, RandomSeed = 7 };

        PatchMatchResult first = new OperatorMatrixPatchMatcher().Match(target, options);
        PatchMatchResult second = new OperatorMatrixPatchMatcher().Match(target, options with { RandomSeed = 8 });
        PatchMatchResult merged = new OperatorMatrixPatchMatcher().MatchBestOf(target, 2, options);

        PatchMatchResult winner = first.AlternativePatches[0].Loss <= second.AlternativePatches[0].Loss ? first : second;
        Assert.Equal(winner.BestPatch.ToJson(), merged.BestPatch.ToJson());
        Assert.Equal(winner.BestPatch.ToJson(), merged.AlternativePatches[0].Patch.ToJson());
        Assert.Equal(winner.SearchSummary.RandomSeed, merged.SearchSummary.RandomSeed);

        // Every structure of either seed is there once, best first, marked with its seed.
        var expected = first.AlternativePatches.Select(static c => c.Regime.Value).Concat(second.AlternativePatches.Select(static c => c.Regime.Value)).ToHashSet();
        Assert.Equal(expected, merged.AlternativePatches.Select(static c => c.Regime.Value).ToHashSet());
        Assert.Equal(expected.Count, merged.AlternativePatches.Count);
        Assert.Equal(merged.AlternativePatches.Select(static c => c.Loss).OrderBy(static l => l), merged.AlternativePatches.Select(static c => c.Loss));
        Assert.Contains(merged.AlternativePatches, static c => c.Origin.StartsWith("seed 7: ", StringComparison.Ordinal));
        Assert.Contains(merged.AlternativePatches, static c => c.Origin.StartsWith("seed 8: ", StringComparison.Ordinal));
        output.WriteLine(string.Join(Environment.NewLine, merged.AlternativePatches.Select(static c => $"{c.Loss:F4} {c.Origin}")));
    }

    [Fact]
    public void ResultWriter_ProducesTheDocumentedFolder()
    {
        string directory = Path.Combine(Path.GetTempPath(), "hearsay-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            PatchMatchResult result = new OperatorMatrixPatchMatcher().Match(TargetOf("pure-sine"), new PatchMatchOptions { EvaluationBudget = 1200 });

            IReadOnlyList<string> files = PatchMatchResultWriter.Write(result, directory);

            foreach (string name in new[] { "patch.json", "preview.wav", "patch.scd", "analysis.json", "comparison.json", "search-summary.json" })
            {
                Assert.True(File.Exists(Path.Combine(directory, name)), name);
            }

            Assert.True(File.Exists(Path.Combine(directory, "alternatives", "01-patch.json")));
            Assert.True(File.Exists(Path.Combine(directory, "alternatives", "01-preview.wav")));
            Assert.True(File.Exists(Path.Combine(directory, "alternatives", "01-patch.scd")));
            Assert.Equal(files.Count, files.Distinct().Count());
            OperatorMatrixPatch loaded = OperatorMatrixPatch.FromJson(File.ReadAllText(Path.Combine(directory, "patch.json")));
            Assert.Equal(result.BestPatch.ComputeHash(), loaded.ComputeHash());
            Assert.Contains("FM7.ar(ctlMatrix, modMatrix)", File.ReadAllText(Path.Combine(directory, "patch.scd")));
            Assert.Contains("\"fundamentalHz\"", File.ReadAllText(Path.Combine(directory, "analysis.json")));
            Assert.Contains("\"randomSeed\": 1234", File.ReadAllText(Path.Combine(directory, "search-summary.json")));

            // A plain sine needs one operator; the simplicity bias keeps the winner small.
            Assert.True(result.BestPatch.FindAudibleOperators().Count(static a => a) <= 2);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Matcher_RefusesUnusableTargetsAndReservedProfiles()
    {
        var matcher = new OperatorMatrixPatchMatcher();

        Assert.Throws<ArgumentException>(() => matcher.Match(new AudioTarget(new float[48000], 48000, 220.0), new PatchMatchOptions { EvaluationBudget = 200 }));
        Assert.Throws<NotSupportedException>(() => matcher.Match(TargetOf("bass"), new PatchMatchOptions { SynthesisProfile = SynthesisProfiles.HearSayExtended, EvaluationBudget = 200 }));
    }

    private sealed class Collector(List<SearchProgress> reports) : IProgress<SearchProgress>
    {
        public void Report(SearchProgress value) => reports.Add(value);
    }
}
