using System.Globalization;
using HearSay.Analysis;
using HearSay.Objective;
using HearSay.OperatorMatrix.Basins;
using HearSay.OperatorMatrix.Parameters;
using HearSay.OperatorMatrix.Priors;
using HearSay.OperatorMatrix.Search;
using HearSay.Synthesis.Fm7Compatible.Rendering;
using HearSay.Synthesis.Fmx6Compatible.Rendering;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay.OperatorMatrix.Tests;

/// <summary>Fine-tuning moves the values of operator filters with the spectral parameters.</summary>
public sealed class FilterTuningTests
{
    private static readonly SynthesisCapabilities Fmx6 = SynthesisProfiles.SuperColliderFmx6.Capabilities;
    private static readonly Fmx6CompatibleOperatorMatrixRenderer Fmx6Renderer = new();
    private static readonly RenderRequest Request = RenderRequest.Create(220, 0.8, 0.3);

    private static PatchEvaluator Evaluator(OperatorMatrixPatch target, int budget)
    {
        RenderedAudio audio = Fmx6Renderer.Render(target, Request);
        PreparedObjectiveTarget prepared = new AudioObjective().Prepare(new AudioTarget(audio.Samples, audio.SampleRate, 220.0));
        return new PatchEvaluator(Fmx6Renderer, new AudioObjective(), prepared, Request, budget, 0.02, null, 4);
    }

    /// <summary>A saw through a low-pass, modulated by a sine through a formant filter with three peaks and a sweep.</summary>
    private static OperatorMatrixPatch Filtered(double cutoff = 4.0)
    {
        var sweep = new OperatorEnvelope([new EnvelopePoint(0, 0), new EnvelopePoint(0.02, 1), new EnvelopePoint(0.3, 0), new EnvelopePoint(0.1, 0)], SustainPoint: 2, ReleasePoint: 3);
        OperatorFilter formant = OperatorFilter.Formants(500, 6, new(2.4, 0.5, 12), new(5.6, 0.2, 18)) with { EnvelopeOctaves = 1.0, Envelope = sweep };
        return ParameterTests.Stack(1.0)
            .WithOperator(0, ParameterTests.Stack().Operators[0] with { Waveform = OperatorWaveform.Saw, Filter = OperatorFilter.LowPass(cutoff, OperatorFilter.ButterworthQ, 2) })
            .WithOperator(1, ParameterTests.Stack().Operators[1] with { Waveform = OperatorWaveform.Saw, Filter = formant });
    }

    private static readonly string[] LowPassIds = ["op1.operatorFilter.cutoff", "op1.operatorFilter.q"];

    private static readonly string[] FormantIds =
    [
        "op2.operatorFilter.fixedHz", "op2.operatorFilter.q", "op2.operatorFilter.formantRatio", "op2.operatorFilter.formantLevel",
        "op2.operatorFilter.formantQ2", "op2.operatorFilter.formant3Ratio", "op2.operatorFilter.formant3Level", "op2.operatorFilter.formant3Q",
        "op2.operatorFilter.octaves",
    ];

    [Fact]
    public void Basin_TunesOperatorFilterValues()
    {
        OperatorMatrixPatch patch = Filtered();
        var optimizer = new BasinOptimizer(Evaluator(patch, 10), Fmx6, new PatchMatchOptions { SynthesisProfile = SynthesisProfiles.SuperColliderFmx6, StructureLibrarySize = 0 });

        string[] spectral = [.. optimizer.SelectSubsets(patch, ContinuousStage.Spectral, tuningMatters: false).SelectMany(static s => s.Parameters).Select(static p => p.Id)];
        Assert.All(LowPassIds, id => Assert.Contains(id, spectral));
        Assert.All(FormantIds, id => Assert.Contains(id, spectral));
        Assert.Contains("edge.2>1.amount", spectral);

        // The sweep's points are envelope shape, not spectral; values a filter type does not use are not searched.
        Assert.DoesNotContain(spectral, static id => id.Contains(".operatorFilter.env.", StringComparison.Ordinal));
        Assert.DoesNotContain("op1.operatorFilter.fixedHz", spectral);
        Assert.DoesNotContain("op2.operatorFilter.cutoff", spectral);
        Assert.DoesNotContain(spectral, static id => id.EndsWith(".peakGain", StringComparison.Ordinal));
        string[] temporal = [.. optimizer.SelectSubsets(patch, ContinuousStage.TemporalDetail, tuningMatters: false).SelectMany(static s => s.Parameters).Select(static p => p.Id)];
        Assert.Contains("op2.operatorFilter.env.p1.time", temporal);
        Assert.Contains("op2.operatorFilter.env.p2.level", temporal);

        // Per component and jointly, the filter values go with the operator they belong to.
        IReadOnlyList<ActiveParameterSet> components = optimizer.SelectSubsets(patch, ContinuousStage.Components, tuningMatters: false);
        Assert.Contains(components, static s => s.Parameters.Any(static p => p.Id == "op1.operatorFilter.cutoff") && s.Parameters.Any(static p => p.Id == "op2.operatorFilter.formant3Q"));
        ActiveParameterSet joint = Assert.Single(optimizer.SelectSubsets(patch, ContinuousStage.Joint, tuningMatters: false));
        Assert.Contains(joint.Parameters, static p => p.Id == "op2.operatorFilter.formantQ2");

        // The third formant's values are searched only while it is on, and a peak filter's gain is.
        OperatorMatrixPatch twoPeaks = patch.WithOperator(1, patch.Operators[1] with { Filter = patch.Operators[1].Filter! with { Formant3Level = 0.0 } });
        string[] withoutF3 = [.. optimizer.SelectSubsets(twoPeaks, ContinuousStage.Spectral, tuningMatters: false).SelectMany(static s => s.Parameters).Select(static p => p.Id)];
        Assert.Contains("op2.operatorFilter.formantQ2", withoutF3);
        Assert.DoesNotContain(withoutF3, static id => id.Contains(".formant3", StringComparison.Ordinal));
        OperatorMatrixPatch peak = patch.WithOperator(0, patch.Operators[0] with { Filter = OperatorFilter.PeakAt(1200, 6, 2) });
        Assert.Contains("op1.operatorFilter.peakGain", optimizer.SelectSubsets(peak, ContinuousStage.Spectral, tuningMatters: false).SelectMany(static s => s.Parameters).Select(static p => p.Id));
    }

    [Fact]
    public void Refiner_TunesOperatorFilterValues()
    {
        OperatorMatrixPatch patch = Filtered();
        PatchParameterSchema schema = PatchParameterSchema.Build(patch, Fmx6);

        // With room for all of them: the amounts and levels first, then the filter values, then envelope times.
        string[] ids = [.. LocalRefiner.SelectParameters(schema, patch, 40).Select(static p => p.Id)];
        Assert.All(LowPassIds, id => Assert.Contains(id, ids));
        Assert.All(FormantIds, id => Assert.Contains(id, ids));
        Assert.True(Array.IndexOf(ids, "edge.2>1.amount") < Array.IndexOf(ids, "op1.operatorFilter.cutoff"));
        Assert.True(Array.IndexOf(ids, "op2.operatorFilter.octaves") < Array.IndexOf(ids, "op1.env.p1.time"));

        // With little room, the amounts and levels are kept before any filter value.
        string[] small = [.. LocalRefiner.SelectParameters(schema, patch, 3).Select(static p => p.Id)];
        Assert.Equal(["edge.2>1.amount", "op1.operatorFilter.cutoff", "op1.output.level"], small.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Refinement_MovesACutoffTowardsTheTarget()
    {
        // Target: a saw through a low-pass at 3 times the note. Start: the same patch with the cutoff at 6.
        OperatorMatrixPatch Saw(double cutoff) =>
            OperatorMatrixPatch.Silent(6)
                .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.005, 0.2, 0.7, 0.2)) with { Waveform = OperatorWaveform.Saw, Filter = OperatorFilter.LowPass(cutoff, OperatorFilter.ButterworthQ, 2) })
                .WithOutputLevel(0, 0.8);
        PatchEvaluator evaluator = Evaluator(Saw(3.0), 120);
        var basin = new SearchBasin(1, "test", evaluator.Evaluate(Saw(6.0), EvaluationLevel.Normal)!);
        double before = basin.Best.AudioLoss;

        int accepted = new LocalRefiner(evaluator, Fmx6, new RegimeModelStore()).Refine(basin, 100, EvaluationLevel.Normal);

        double cutoff = basin.Best.Patch.Operators[0].Filter!.CutoffRatio;
        Assert.True(accepted > 0 || basin.Best.AudioLoss < before, "the refinement found nothing better");
        Assert.True(basin.Best.AudioLoss < before, $"loss {basin.Best.AudioLoss} from {before}");
        Assert.True(cutoff < 6.0, $"cutoff {cutoff}");
        Assert.InRange(cutoff, 2.0, 4.5);
    }

    [Fact]
    public void Fm7Search_IsUnchanged()
    {
        // Captured before filter values were tuned: FM7 has no filters, so its search must give the same result.
        var renderer = new Fm7CompatibleOperatorMatrixRenderer();
        ReferencePatch bell = ReferencePatches.Find("bell")!;
        RenderedAudio audio = renderer.Render(bell.Patch, RenderRequest.Create(bell.FrequencyHz, 0.8, 0.3));
        ISynthesisPriorProvider sine = new SinePhaseModulationPriorProvider();
        var coordinator = new OperatorMatrixPatchSearchCoordinator(renderer, [sine, new PartialTrackPriorProvider(sine)]);

        SearchOutcome outcome = coordinator.Search(
            new AudioTarget(audio.Samples, audio.SampleRate, bell.FrequencyHz),
            new PatchMatchOptions { EvaluationBudget = 600, RandomSeed = 5, StructureLibrarySize = 0 });

        string result = string.Join(" ", outcome.Candidates.Select(static c => c.Patch.ComputeHash() + "@" + c.Loss.ToString("R", CultureInfo.InvariantCulture)));
        Assert.Equal("A827AF6B53B560E0DC0D54E3C2FB393C@0.29851082327657774 4D44D3BDF09BAEB5C2BEF87220DA74D1@0.31178206263980823 5F76C34D33C60667AB17B0E87647F540@0.3120797354081999", result);
    }
}
