using HearSay.Analysis;
using HearSay.Objective;
using HearSay.OperatorMatrix.Basins;
using HearSay.OperatorMatrix.Optimization;
using HearSay.OperatorMatrix.Parameters;
using HearSay.OperatorMatrix.Priors;
using HearSay.OperatorMatrix.Search;
using HearSay.Synthesis.Fm7Compatible.Rendering;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay.OperatorMatrix.Tests;

public sealed class SearchComponentTests
{
    private static readonly SynthesisCapabilities Fm7 = SynthesisProfiles.SuperColliderFm7.Capabilities;
    private static readonly Fm7CompatibleOperatorMatrixRenderer Renderer = new();
    private static readonly Lazy<PreparedObjectiveTarget> StackTarget = new(() =>
    {
        RenderedAudio audio = Renderer.Render(ParameterTests.Stack(2.5), RenderRequest.Create(220, 0.8, 0.3));
        return new AudioObjective().Prepare(new AudioTarget(audio.Samples, audio.SampleRate, 220.0));
    });

    private static PatchEvaluator Evaluator(int budget, int parallel = 4) =>
        new(Renderer, new AudioObjective(), StackTarget.Value, RenderRequest.Create(220, 0.8, 0.3), budget, 0.02, null, parallel);

    [Fact]
    public void CmaEs_MinimizesACoupledFunctionInsideTheUnitCube()
    {
        // Rotated, badly conditioned quadratic with its optimum at (0.7, 0.2, 0.55, 0.4).
        double[] optimum = [0.7, 0.2, 0.55, 0.4];
        double Loss(double[] x)
        {
            double a = (x[0] - optimum[0]) + (x[1] - optimum[1]);
            double b = (x[0] - optimum[0]) - (x[1] - optimum[1]);
            double c = (x[2] - optimum[2]) + (2 * (x[3] - optimum[3]));
            double d = x[3] - optimum[3];
            return (a * a) + (100 * b * b) + (c * c) + (30 * d * d);
        }

        var cma = new CmaEs([0.5, 0.5, 0.5, 0.5], 0.2, seed: 7);
        double best = double.PositiveInfinity;
        for (int generation = 0; generation < 150; generation++)
        {
            double[][] population = cma.Ask();
            Assert.All(population, x => Assert.All(x, v => Assert.InRange(v, 0.0, 1.0)));
            double[] losses = [.. population.Select(Loss)];
            best = Math.Min(best, losses.Min());
            cma.Tell(losses);
        }

        Assert.True(best < 1e-8, $"best {best}");
        for (int i = 0; i < optimum.Length; i++)
        {
            Assert.Equal(optimum[i], cma.Mean[i], 3);
        }
    }

    [Fact]
    public void CmaEs_IsReproducibleFromItsSeed()
    {
        static double[] Run(int seed)
        {
            var cma = new CmaEs([0.3, 0.8, 0.5], 0.2, seed);
            for (int g = 0; g < 20; g++)
            {
                double[][] population = cma.Ask();
                cma.Tell([.. population.Select(static x => x.Sum(static v => (v - 0.4) * (v - 0.4)))]);
            }

            return cma.Mean.ToArray();
        }

        Assert.Equal(Run(5), Run(5));
        Assert.NotEqual(Run(5), Run(6));
    }

    [Fact]
    public void SymmetricEigen_ReconstructsTheMatrix()
    {
        double[,] m = { { 4, 1, 0.5 }, { 1, 3, 0.2 }, { 0.5, 0.2, 1 } };
        (double[] values, double[,] vectors) = SymmetricEigen.Decompose(m);

        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                double sum = 0;
                for (int k = 0; k < 3; k++)
                {
                    sum += vectors[i, k] * values[k] * vectors[j, k];
                }

                Assert.Equal(m[i, j], sum, 9);
            }
        }
    }

    [Fact]
    public void SinePriors_ContainTheRightStackAndNeverLeaveTheProfile()
    {
        TargetAnalysis analysis = StackTarget.Value.Analysis;
        var provider = new SinePhaseModulationPriorProvider();

        IReadOnlyList<PatchHypothesis> hypotheses = provider.Generate(analysis, Fm7);

        Assert.InRange(hypotheses.Count, 10, provider.MaxHypotheses);
        Assert.Contains(hypotheses, static h => h.Origin == "stack 1:2");
        Assert.Contains(hypotheses, static h => h.Origin.Contains("additive", StringComparison.Ordinal));
        Assert.Contains(hypotheses, static h => h.Origin.Contains("feedback", StringComparison.Ordinal));
        Assert.All(hypotheses, h => Assert.True(PatchCompatibilityValidator.Instance.Validate(h.Patch, Fm7).IsCompatible, h.Origin));
        Assert.Equal(hypotheses.Count, hypotheses.Select(static h => Canonicalization.PatchCanonicalizer.CanonicalKey(h.Patch).Value).Distinct().Count());

        // Restricted backends get restricted hypotheses.
        SynthesisCapabilities plain = Fm7 with { SupportsSelfFeedback = false, SupportsCircularFeedback = false };
        IReadOnlyList<PatchHypothesis> restricted = provider.Generate(analysis, plain);
        Assert.NotEmpty(restricted);
        Assert.All(restricted, h => Assert.True(PatchCompatibilityValidator.Instance.Validate(h.Patch, plain).IsCompatible, h.Origin));
        Assert.DoesNotContain(restricted, static h => h.Patch.ModulationMatrix.Edges.Any(static e => e.IsSelfFeedback));
    }

    [Fact]
    public void SinePriors_StayOutOfProfilesWithoutSineOperators()
    {
        SynthesisCapabilities sawOnly = Fm7 with { SupportedWaveforms = new HashSet<OperatorWaveform> { OperatorWaveform.Saw } };

        Assert.Empty(new SinePhaseModulationPriorProvider().Generate(StackTarget.Value.Analysis, sawOnly));
    }

    [Fact]
    public void WaveformVariants_AreSeparateStructuralHypotheses()
    {
        var provider = new WaveformVariantPriorProvider(new SinePhaseModulationPriorProvider());
        SynthesisCapabilities experimental = SynthesisProfiles.ExperimentalSineTriangle.Capabilities;

        IReadOnlyList<PatchHypothesis> fm7 = provider.Generate(StackTarget.Value.Analysis, Fm7);
        IReadOnlyList<PatchHypothesis> both = provider.Generate(StackTarget.Value.Analysis, experimental);

        Assert.DoesNotContain(fm7, static h => h.Patch.Operators.Any(static o => o.Enabled && o.Waveform != OperatorWaveform.Sine));
        PatchHypothesis triangle = both.First(static h => h.Origin == "stack 1:2 [Triangle output operators]");
        PatchHypothesis sine = both.First(static h => h.Origin == "stack 1:2");
        Assert.NotEqual(Canonicalization.PatchCanonicalizer.CanonicalKey(sine.Patch), Canonicalization.PatchCanonicalizer.CanonicalKey(triangle.Patch));
    }

    [Fact]
    public void TemporalPriors_FollowTheTarget()
    {
        TargetAnalysis analysis = StackTarget.Value.Analysis;

        OperatorEnvelope output = TemporalPriors.OutputEnvelope(analysis);
        OperatorEnvelope brightness = TemporalPriors.BrightnessEnvelope(analysis);

        Assert.Empty(output.Validate());
        Assert.Empty(brightness.Validate());
        Assert.Equal(5, output.Points.Count);
        Assert.InRange(output.Points[1].TimeSeconds, 0.001, 0.05);
        Assert.True(brightness.Points[2].Level <= output.Points[2].Level + 1e-9, "brightness does not outlast loudness");
        Assert.Same(PitchModel.None, TemporalPriors.Pitch(analysis));
    }

    [Fact]
    public void Mutator_ProposesOnlyWhatTheProfileAllows()
    {
        TargetAnalysis analysis = StackTarget.Value.Analysis;
        SynthesisCapabilities plain = Fm7 with { SupportsSelfFeedback = false, SupportsCircularFeedback = false, SupportsFullModulationMatrix = false };
        var random = new Random(3);

        IReadOnlyList<TopologyMutation> free = new TopologyMutator(Fm7).Propose(ParameterTests.Stack(), analysis, random, 12);
        IReadOnlyList<TopologyMutation> restricted = new TopologyMutator(plain).Propose(ParameterTests.Stack(), analysis, new Random(3), 12);
        IReadOnlyList<TopologyMutation> withWaveforms = new TopologyMutator(SynthesisProfiles.ExperimentalSineTriangle.Capabilities).Propose(ParameterTests.Stack(), analysis, new Random(3), 30);

        Assert.True(free.Count >= 6);
        Assert.All(free, m => Assert.True(PatchCompatibilityValidator.Instance.Validate(m.Patch, Fm7).IsCompatible, m.Description));
        Assert.All(restricted, m => Assert.True(PatchCompatibilityValidator.Instance.Validate(m.Patch, plain).IsCompatible, m.Description));
        Assert.All(free, static m => Assert.All(m.Patch.Operators, static o => Assert.Equal(OperatorWaveform.Sine, o.Waveform)));
        Assert.Contains(withWaveforms, static m => m.Description.Contains("waveform Triangle", StringComparison.Ordinal));
        Assert.Equal(free.Count, free.Select(static m => m.Patch.ComputeHash()).Distinct().Count());
    }

    [Fact]
    public void Evaluator_CountsRendersCachesAndStopsAtTheBudget()
    {
        PatchEvaluator evaluator = Evaluator(budget: 5);
        OperatorMatrixPatch[] patches = [.. Enumerable.Range(0, 8).Select(static i => ParameterTests.Stack(1.0 + (0.3 * i)))];

        IReadOnlyList<PatchEvaluation> first = evaluator.Evaluate([patches[0], patches[1], patches[0]], EvaluationLevel.Normal);
        Assert.Equal(3, first.Count);
        Assert.Equal(2, evaluator.Used);
        Assert.Equal(first[0].Loss, first[2].Loss);

        evaluator.Evaluate([patches[0]], EvaluationLevel.Normal);
        Assert.Equal(2, evaluator.Used);
        Assert.Equal(1, evaluator.CacheHits);

        IReadOnlyList<PatchEvaluation> rest = evaluator.Evaluate([.. patches.Skip(2)], EvaluationLevel.Normal);
        Assert.Equal(3, rest.Count);
        Assert.Equal(5, evaluator.Used);
        Assert.Equal(0, evaluator.Remaining);
        Assert.Null(evaluator.Evaluate(ParameterTests.Stack(9.0), EvaluationLevel.Normal));
        Assert.Null(evaluator.EvaluateWithAudio(ParameterTests.Stack(9.5), EvaluationLevel.Normal));
    }

    [Fact]
    public void Evaluator_ResultsDoNotDependOnParallelism()
    {
        OperatorMatrixPatch[] patches = [.. Enumerable.Range(0, 12).Select(static i => ParameterTests.Stack(0.5 + (0.4 * i)))];

        double[] serial = [.. Evaluator(100, parallel: 1).Evaluate(patches, EvaluationLevel.Normal).Select(static e => e.Loss)];
        double[] parallel = [.. Evaluator(100, parallel: 8).Evaluate(patches, EvaluationLevel.Normal).Select(static e => e.Loss)];

        Assert.Equal(serial, parallel);
        Assert.True(serial[5] < serial[0] && serial[5] < serial[11], "the loss is lowest near the true amount of 2.5");
    }

    [Fact]
    public void TotalLoss_IsAudioLossPlusALightComplexityTerm()
    {
        PatchEvaluation evaluation = Evaluator(10).Evaluate(ParameterTests.Stack(2.5), EvaluationLevel.Normal)!;

        Assert.Equal(evaluation.AudioLoss + (0.02 * evaluation.Complexity), evaluation.Loss, 12);
        Assert.True(0.02 * evaluation.Complexity < 0.01);
    }

    [Fact]
    public void BasinOptimizer_KeepsActiveSubsetsSmallAndImprovesTheBasin()
    {
        PatchEvaluator evaluator = Evaluator(600, parallel: 8);
        var options = new PatchMatchOptions { MaxActiveParameters = 10 };
        var optimizer = new BasinOptimizer(evaluator, Fm7, options);
        OperatorMatrixPatch start = ParameterTests.Stack(0.8);

        foreach (ContinuousStage stage in Enum.GetValues<ContinuousStage>())
        {
            Assert.All(optimizer.SelectSubsets(start, stage, tuningMatters: true), s => Assert.InRange(s.Count, 1, 10));
        }

        // More smooth parameters than may be active at once: no joint stage, the work is split.
        Assert.Empty(optimizer.SelectSubsets(start, ContinuousStage.Joint, tuningMatters: false));
        Assert.True(optimizer.SelectSubsets(start, ContinuousStage.TemporalDetail, false).Count >= 2);
        IReadOnlyList<ActiveParameterSet> macros = optimizer.SelectSubsets(start, ContinuousStage.TemporalMacros, false);
        Assert.All(macros[0].Parameters, static p => Assert.StartsWith("macro.", p.Id));

        ActiveParameterSet spectral = optimizer.SelectSubsets(start, ContinuousStage.Spectral, false)[0];
        Assert.Equal(["op1.output.level", "edge.2>1.amount"], spectral.Parameters.Select(static p => p.Id));
        var basin = new SearchBasin(1, "test", evaluator.Evaluate(start, EvaluationLevel.Normal)!);
        double before = basin.Best.Loss;

        bool improved = optimizer.Optimize(basin, spectral, 300, 0.2, seed: 11);

        Assert.True(improved);
        Assert.True(basin.Best.Loss < 0.5 * before, $"{before} -> {basin.Best.Loss}");
        Assert.InRange(basin.Best.Patch.ModulationMatrix[0, 1], 2.0, 3.0);
        Assert.Equal(basin.Regime, Canonicalization.PatchCanonicalizer.CanonicalKey(basin.Best.Patch));
    }
}
