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
    public void WaveformVariants_LeaveNoiseToItsOwnHypotheses()
    {
        // A noise output operator ignores its frequency and everything that modulates it: every structure would turn
        // into the same noise, many times over.
        IReadOnlyList<PatchHypothesis> variants = new WaveformVariantPriorProvider(new SinePhaseModulationPriorProvider(), includeInner: false)
            .Generate(StackTarget.Value.Analysis, SynthesisProfiles.SuperColliderFmx6.Capabilities);

        Assert.Contains(variants, static h => h.Origin.EndsWith("[Saw output operators]", StringComparison.Ordinal));
        Assert.DoesNotContain(variants, static h => h.Patch.Operators.Any(static o => o.Enabled && o.Waveform.IsNoise()));
    }

    [Fact]
    public void NoisePriors_AddANoiseBandOfTheRightColour_OnlyWhenTheTargetIsNoisy()
    {
        var provider = new NoisePriorProvider(new SinePhaseModulationPriorProvider());
        SynthesisCapabilities fmx6 = SynthesisProfiles.SuperColliderFmx6.Capabilities;
        TargetAnalysis noisy = StackTarget.Value.Analysis with { Noise = new HearSay.Spectral.NoiseProfile(0.3, 9000) };
        TargetAnalysis clean = StackTarget.Value.Analysis with { Noise = new HearSay.Spectral.NoiseProfile(0.02, 9000) };

        SynthesisCapabilities whiteOnly = fmx6 with { SupportedWaveforms = new HashSet<OperatorWaveform>(fmx6.SupportedWaveforms.Where(static w => w != OperatorWaveform.FilteredNoise)) };

        IReadOnlyList<PatchHypothesis> filtered = provider.Generate(noisy, fmx6);
        IReadOnlyList<PatchHypothesis> white = provider.Generate(noisy, whiteOnly);

        // With filtered noise, one operator carries the wash: a band at the colour of the noise, and a high-pass below it.
        Assert.NotEmpty(filtered);
        Assert.All(filtered, h => Assert.True(PatchCompatibilityValidator.Instance.Validate(h.Patch, fmx6).IsCompatible, h.Origin));
        Assert.All(filtered, static h => Assert.Single(h.Patch.Operators, static o => o.Enabled && o.Waveform == OperatorWaveform.FilteredNoise));
        Assert.Contains(filtered, static h => h.Patch.Operators.Any(static o => o.Enabled && o.Waveform == OperatorWaveform.FilteredNoise && o.Shape == 0.5 && o.Frequency.FixedFrequencyHz == 9000));
        Assert.Contains(filtered, static h => h.Patch.Operators.Any(static o => o.Enabled && o.Waveform == OperatorWaveform.FilteredNoise && o.Shape == 1.0 && o.Frequency.FixedFrequencyHz < 9000));

        // Without it, noise modulating a sine at that colour, or white noise.
        Assert.All(white, h => Assert.True(PatchCompatibilityValidator.Instance.Validate(h.Patch, whiteOnly).IsCompatible, h.Origin));
        Assert.All(white, static h => Assert.Contains(h.Patch.Operators, static o => o.Enabled && o.Waveform == OperatorWaveform.Noise));
        Assert.Contains(white, static h => h.Patch.Operators.Any(static o => o.Enabled && o.Frequency.Mode == OperatorFrequencyMode.Fixed && o.Frequency.FixedFrequencyHz == 9000));

        // Every hypothesis keeps the sine structure it started from.
        Assert.All(filtered.Concat(white), static h => Assert.Contains(h.Patch.Operators, static o => o.Enabled && o.Waveform == OperatorWaveform.Sine));
        Assert.Empty(provider.Generate(clean, fmx6));
        Assert.Empty(provider.Generate(noisy, Fm7));
    }

    [Fact]
    public void SinePriors_FindThreeInharmonicPairs_IncludingAModulatorAboveItsCarrier()
    {
        // Three parallel pairs at inharmonic frequencies, as in a six-operator FM cymbal. The third modulator lies
        // above its carrier, so its lower sideband folds over: 1321 - 2089 is heard at 768 Hz.
        (double Carrier, double Modulator)[] pairs = [(613, 941), (887, 1423), (1321, 2089)];
        OperatorMatrixPatch cymbal = OperatorMatrixPatch.Silent(6);
        for (int i = 0; i < 3; i++)
        {
            cymbal = cymbal
                .WithOperator(i, OperatorPatch.Sine(OperatorFrequency.AtFixed(pairs[i].Carrier), OperatorEnvelope.Adsr(0.002, 1.5, 0.0, 0.1)))
                .WithOutputLevel(i, 0.5)
                .WithOperator(i + 3, OperatorPatch.Sine(OperatorFrequency.AtFixed(pairs[i].Modulator), OperatorEnvelope.Constant()))
                .WithEdge(new ModulationEdge(i + 3, i, true, 2.5, OperatorEnvelope.Adsr(0.001, 0.6, 0.0, 0.1)));
        }

        RenderedAudio audio = Renderer.Render(cymbal, RenderRequest.Create(305, 1.2, 0.3));
        TargetAnalysis analysis = new AudioObjective().Prepare(new AudioTarget(audio.Samples, audio.SampleRate, 305.0)).Analysis;

        PatchHypothesis best = new SinePhaseModulationPriorProvider().Generate(analysis, Fm7).First(static h => h.Origin.StartsWith("3 stacks", StringComparison.Ordinal));

        // Every pair is found within 10 cents: the carriers as measured, not snapped to simple ratios, and the modulators
        // themselves rather than half of them.
        OperatorMatrixPatch patch = best.Patch;
        foreach ((double carrier, double modulator) in pairs)
        {
            ModulationEdge? edge = patch.ModulationMatrix.Edges.FirstOrDefault(e =>
                Cents(patch.Operators[e.DestinationOperator].FrequencyHz(305.0), carrier) < 10 && Cents(patch.Operators[e.SourceOperator].FrequencyHz(305.0), modulator) < 10);
            Assert.True(edge is not null, $"{carrier}:{modulator} Hz not found in {best.Origin}");
        }

        static double Cents(double a, double b) => Math.Abs(1200.0 * Math.Log2(a / b));
    }

    [Fact]
    public void SinePriors_FindTheSecondModulatorOfAThreeStack()
    {
        // A DX7-style three-stack: 1 <- 5 <- 2.73 with feedback on the top operator. The second modulator makes the
        // first one rich, so the carrier gets lines at |1 + a*5 + b*2.73|.
        OperatorMatrixPatch stack = OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.005, 0.8, 0.5, 0.2))).WithOutputLevel(0, 0.7)
            .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(5), OperatorEnvelope.Adsr(0.002, 0.4, 0.4, 0.2)))
            .WithOperator(2, OperatorPatch.Sine(OperatorFrequency.AtRatio(2.73), OperatorEnvelope.Adsr(0.002, 0.3, 0.3, 0.2)))
            .WithEdge(new ModulationEdge(1, 0, true, 1.6))
            .WithEdge(new ModulationEdge(2, 1, true, 2.5))
            .WithEdge(new ModulationEdge(2, 2, true, 0.7));
        RenderedAudio audio = Renderer.Render(stack, RenderRequest.Create(220, 1.0, 0.3));
        TargetAnalysis analysis = new AudioObjective().Prepare(new AudioTarget(audio.Samples, audio.SampleRate, 220.0)).Analysis;

        IReadOnlyList<PatchHypothesis> hypotheses = new SinePhaseModulationPriorProvider().Generate(analysis, Fm7);

        Assert.Contains(hypotheses, static h => h.Origin.StartsWith("three-stack 1:5:2.73", StringComparison.Ordinal));
    }

    [Fact]
    public void SinePriors_PutACarrierOnAWeakLowThump_RatherThanExplainingItAsASideband()
    {
        // The keyed electric piano has a fixed 60 Hz thump under a 262 Hz note. As a carrier of its own it can become a
        // fixed-frequency operator; as a sideband of the note (262 - 0.77 * 262) it would transpose with it.
        ReferencePatch piano = ReferencePatches.Find("electric-piano-keyed")!;
        RenderRequest request = RenderRequest.Create(piano.FrequencyHz, piano.NoteSeconds, piano.ReleaseSeconds);
        RenderedAudio audio = Renderer.Render(piano.Patch, request);
        TargetAnalysis analysis = new AudioObjective().Prepare(new AudioTarget(audio.Samples, audio.SampleRate, piano.FrequencyHz)).Analysis;

        PatchHypothesis best = new SinePhaseModulationPriorProvider().Generate(analysis, Fm7).First(static h => h.Origin.StartsWith("3 stacks", StringComparison.Ordinal));

        bool[] audible = best.Patch.FindAudibleOperators();
        Assert.Contains(Enumerable.Range(0, 6), i => audible[i] && best.Patch.OutputLevels[i] > 0 && Math.Abs(best.Patch.Operators[i].FrequencyHz(piano.FrequencyHz) - 60.0) < 3.0);
    }

    [Fact]
    public void PartialTrackPriors_PutOperatorsOnTheMeasuredPartials_WithTheirOwnDecays()
    {
        // A small bell: three inharmonic partials that decay at different rates, and a bright burst in the attack from
        // a modulator whose modulation dies within a tenth of a second.
        OperatorPatch Ring(double hz, double seconds) => OperatorPatch.Sine(OperatorFrequency.AtFixed(hz), OperatorEnvelope.Adsr(0.002, seconds, 0.0, 0.1));
        OperatorMatrixPatch bell = OperatorMatrixPatch.Silent(6)
            .WithOperator(0, Ring(520, 1.8)).WithOutputLevel(0, 0.5)
            .WithOperator(1, Ring(941, 1.2)).WithOutputLevel(1, 0.5)
            .WithOperator(2, Ring(1733, 0.7)).WithOutputLevel(2, 0.5)
            .WithOperator(3, OperatorPatch.Sine(OperatorFrequency.AtFixed(731), OperatorEnvelope.Constant()))
            .WithEdge(new ModulationEdge(3, 0, true, 4.0, OperatorEnvelope.Adsr(0.001, 0.08, 0.0, 0.05)));
        RenderRequest request = RenderRequest.Create(210, 0.9, 0.3);
        RenderedAudio audio = Renderer.Render(bell, request);
        TargetAnalysis analysis = new AudioObjective().Prepare(new AudioTarget(audio.Samples, audio.SampleRate, 210.0)).Analysis;

        IReadOnlyList<PatchHypothesis> hypotheses = new PartialTrackPriorProvider().Generate(analysis, Fm7);

        Assert.NotEmpty(hypotheses);
        Assert.All(hypotheses, h => Assert.True(PatchCompatibilityValidator.Instance.Validate(h.Patch, Fm7).IsCompatible, h.Origin));
        PatchHypothesis additive = hypotheses.First(static h => h.Patch.ModulationMatrix.Edges.Count == 0 && h.Patch.FindAudibleOperators().Count(static a => a) == 3);
        double[] hz = [.. additive.Patch.Operators.Where(static o => o.Enabled).Select(o => o.FrequencyHz(analysis.FundamentalHz)).Order()];
        Assert.Equal(3, hz.Length);
        Assert.InRange(hz[0], 520 * 0.99, 520 * 1.01);
        Assert.InRange(hz[1], 941 * 0.99, 941 * 1.01);
        Assert.InRange(hz[2], 1733 * 0.99, 1733 * 1.01);

        // Every partial keeps its own decay: the lowest rings longest.
        double Remaining(OperatorPatch o) => o.AmplitudeEnvelope.EffectiveLevel(o.AmplitudeEnvelope.Points.Count - 2);
        OperatorPatch[] byFrequency = [.. additive.Patch.Operators.Where(static o => o.Enabled).OrderBy(o => o.FrequencyHz(analysis.FundamentalHz))];
        Assert.True(Remaining(byFrequency[0]) > Remaining(byFrequency[2]), "the lowest partial should decay slowest");

        // And a variant with a modulator whose amount dies quickly, for the bright attack.
        Assert.Contains(hypotheses, static h => h.Patch.ModulationMatrix.Edges.Any(static e => e.Enabled && e.AmountEnvelope is not null));
    }

    [Fact]
    public void PartialTrackPriors_StayOut_WhenSidebandArithmeticExplainsTheSpectrum()
    {
        // A plain FM stack: its sidebands ring as long as the note, and read as partial tracks they would give an
        // additive guess at what one modulator explains.
        TargetAnalysis stack = StackTarget.Value.Analysis;
        var gated = new PartialTrackPriorProvider(new SinePhaseModulationPriorProvider());

        Assert.NotEmpty(new PartialTrackPriorProvider().Generate(stack, Fm7));
        Assert.Empty(gated.Generate(stack, Fm7));
    }

    [Fact]
    public void ModulatorWaveforms_AreProposedAndScreened_WhereTheProfileHasThem()
    {
        // A sine carrier with a square modulator: its harmonics put sidebands on the same grid as a sine modulator, so
        // the sine priors find the ratios and only the waveform of the modulator is left to find.
        SynthesisCapabilities fmx6 = SynthesisProfiles.SuperColliderFmx6.Capabilities;
        var renderer = new HearSay.Synthesis.Fmx6Compatible.Rendering.Fmx6CompatibleOperatorMatrixRenderer();
        OperatorMatrixPatch sineStack = OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.005, 0.3, 0.6, 0.2)))
            .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(3.5), OperatorEnvelope.Adsr(0.005, 0.3, 0.6, 0.2)))
            .WithOutputLevel(0, 0.8)
            .WithEdge(new ModulationEdge(1, 0, true, 1.5));
        OperatorMatrixPatch squareStack = sineStack.WithOperator(1, sineStack.Operators[1] with { Waveform = OperatorWaveform.Square });
        RenderRequest request = RenderRequest.Create(220, 0.8, 0.3);
        RenderedAudio audio = renderer.Render(squareStack, request);
        PreparedObjectiveTarget target = new AudioObjective().Prepare(new AudioTarget(audio.Samples, audio.SampleRate, 220.0));

        IReadOnlyList<PatchHypothesis> variants = new ModulatorWaveformPriorProvider(new SinePhaseModulationPriorProvider()).Generate(target.Analysis, fmx6);

        Assert.NotEmpty(variants);
        Assert.All(variants, h => Assert.True(PatchCompatibilityValidator.Instance.Validate(h.Patch, fmx6).IsCompatible, h.Origin));
        Assert.Contains(variants, static h => h.Patch.Operators.Where((o, i) => h.Patch.FindAudibleOperators()[i] && h.Patch.OutputLevels[i] == 0.0)
            .Any(static o => o.Waveform == OperatorWaveform.Square && Math.Abs(o.Frequency.Ratio - 3.5) < 0.01));
        Assert.All(variants, static h => Assert.All(h.Patch.Operators.Where((o, i) => h.Patch.OutputLevels[i] != 0.0), static o => Assert.Equal(OperatorWaveform.Sine, o.Waveform)));
        Assert.Empty(new ModulatorWaveformPriorProvider(new SinePhaseModulationPriorProvider()).Generate(target.Analysis, Fm7));

        // Screened mutations of the sine stack find the square modulator in one step.
        var evaluator = new PatchEvaluator(renderer, new AudioObjective(), target, request, 1000, 0.02, null, 8);
        IReadOnlyList<TopologyMutation> proposals = new TopologyMutator(fmx6).ProposeScreened(sineStack, target.Analysis, evaluator, new Random(1), 6);
        Assert.Contains(proposals.Take(3), static p => p.Description == "OP2 waveform Square");
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
    public void ScreenedMutations_FindTheEdgeThatIsMissing()
    {
        // Target: the bass reference, a 1:1 stack whose modulator feeds back into itself. Candidate: the same patch
        // without that feedback edge.
        ReferencePatch bass = ReferencePatches.Find("bass")!;
        RenderRequest request = RenderRequest.Create(bass.FrequencyHz, 0.6, 0.2);
        RenderedAudio audio = Renderer.Render(bass.Patch, request);
        PreparedObjectiveTarget target = new AudioObjective().Prepare(new AudioTarget(audio.Samples, audio.SampleRate, bass.FrequencyHz));
        var evaluator = new PatchEvaluator(Renderer, new AudioObjective(), target, request, 1000, 0.02, null, 8);
        OperatorMatrixPatch incomplete = bass.Patch with { ModulationMatrix = bass.Patch.ModulationMatrix.Without(1, 1) };

        IReadOnlyList<TopologyMutation> proposals = new TopologyMutator(Fm7).ProposeScreened(incomplete, target.Analysis, evaluator, new Random(1), 4);

        // The first half of the list comes from screening. Feedback on the modulator and a second modulator at the same
        // ratio brighten the modulator in much the same way, so either may come first; the missing edge must be there.
        Assert.Contains("add edge OP2 -> OP2", proposals.Take(2).Select(static p => p.Description));
        Assert.InRange(evaluator.Used, 20, 200);
        Assert.Equal(4, proposals.Count);
        Assert.Equal(4, proposals.Select(static p => p.Description).Distinct().Count());
        Assert.All(proposals, p => Assert.True(PatchCompatibilityValidator.Instance.Validate(p.Patch, Fm7).IsCompatible));
    }

    [Fact]
    public void ScreenedMutations_AddANoiseWashInOneStep_WhereTheProfileHasNoise()
    {
        // Target: a sine stack with a noise wash next to it. Candidate: the stack alone. Reaching the wash through
        // "add a sine to the output" and then "make it noise" would need an intermediate that sounds worse.
        SynthesisCapabilities fmx6 = SynthesisProfiles.SuperColliderFmx6.Capabilities;
        var renderer = new HearSay.Synthesis.Fmx6Compatible.Rendering.Fmx6CompatibleOperatorMatrixRenderer();
        RenderRequest request = RenderRequest.Create(220, 0.8, 0.3);
        OperatorPatch wash = OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.002, 0.3, 0.0, 0.1)) with { Waveform = OperatorWaveform.Noise };
        OperatorMatrixPatch stack = ParameterTests.Stack(2.5);
        RenderedAudio audio = renderer.Render(stack.WithOperator(2, wash).WithOutputLevel(2, 0.3), request);
        PreparedObjectiveTarget target = new AudioObjective().Prepare(new AudioTarget(audio.Samples, audio.SampleRate, 220.0));
        var evaluator = new PatchEvaluator(renderer, new AudioObjective(), target, request, 1000, 0.02, null, 8);

        IReadOnlyList<TopologyMutation> proposals = new TopologyMutator(fmx6).ProposeScreened(stack, target.Analysis, evaluator, new Random(1), 6);
        IReadOnlyList<TopologyMutation> fm7 = new TopologyMutator(Fm7).ProposeScreened(stack, StackTarget.Value.Analysis, Evaluator(1000), new Random(1), 6);

        // The first half of the list comes from screening.
        Assert.Contains(proposals.Take(3), static p => p.Description.Contains("noise", StringComparison.Ordinal));
        TopologyMutation noise = proposals.First(static p => p.Description.Contains("noise", StringComparison.Ordinal));
        Assert.Contains(noise.Patch.Operators, static o => o.Enabled && o.Waveform == OperatorWaveform.Noise);
        Assert.All(proposals, p => Assert.True(PatchCompatibilityValidator.Instance.Validate(p.Patch, fmx6).IsCompatible, p.Description));
        Assert.DoesNotContain(fm7, static p => p.Description.Contains("noise", StringComparison.Ordinal));
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
    public void Runs_AreResumedAcrossCallsAndRestartOnlyAfterStagnation()
    {
        PatchEvaluator evaluator = Evaluator(4000, parallel: 8);
        var optimizer = new BasinOptimizer(evaluator, Fm7, new PatchMatchOptions { StagnationGenerations = 6 });
        var basin = new SearchBasin(1, "test", evaluator.Evaluate(ParameterTests.Stack(0.8), EvaluationLevel.Normal)!);
        int seed = 0;
        ActiveParameterSet? Select(OperatorMatrixPatch patch) => optimizer.SelectSubsets(patch, ContinuousStage.Joint, false) is [var set] ? set : null;

        optimizer.OptimizeWithRestarts(basin, Select, 60, 0.2, () => ++seed);
        ContinuousRun first = Assert.Single(basin.Runs.Values);
        int generations = first.Cma.Generation;
        optimizer.OptimizeWithRestarts(basin, Select, 60, 0.2, () => ++seed);

        // Same run, further along: the covariance it has learned is kept.
        Assert.Same(first, Assert.Single(basin.Runs.Values));
        Assert.True(first.Cma.Generation > generations);
        Assert.Equal(1, seed);

        // Enough budget to stagnate: the next run starts with a larger population.
        optimizer.OptimizeWithRestarts(basin, Select, 3000, 0.2, () => ++seed);
        ContinuousRun later = Assert.Single(basin.Runs.Values);
        Assert.True(later.Index >= 1);
        Assert.True(later.Cma.PopulationSize > first.Cma.PopulationSize);
        Assert.True(basin.Best.AudioLoss < 0.05, $"loss {basin.Best.AudioLoss}");

        // The joint stage leaves out what cannot matter: the only output level under timbre-normalized comparison,
        // and the two segment times that the break position stands in for.
        string[] ids = [.. Select(basin.Best.Patch)!.Parameters.Select(static p => p.Id)];
        Assert.DoesNotContain("op1.output.level", ids);
        Assert.DoesNotContain("op1.env.p2.time", ids);
        Assert.Contains("op1.env.breakPosition", ids);
        Assert.Contains("edge.2>1.amount", ids);
    }

    [Fact]
    public void BreakPosition_KeepsTheEnvelopeExactlyAsLongAsTheHeldNote()
    {
        OperatorMatrixPatch patch = ParameterTests.Stack();
        PatchParameterSchema schema = PatchParameterSchema.Build(patch, Fm7, null, new EnvelopeTimeLimits(0.8, 0.3));
        PatchParameter position = schema.Find("op1.env.breakPosition")!;

        OperatorMatrixPatch moved = position.Set(patch, 0.25);
        OperatorEnvelope envelope = moved.Operators[0].AmplitudeEnvelope;

        Assert.Equal(0.8, envelope.Points[1].TimeSeconds + envelope.Points[2].TimeSeconds + envelope.Points[3].TimeSeconds, 9);
        Assert.Equal(0.25, position.Get(moved), 9);
        Assert.Equal(patch.Operators[0].AmplitudeEnvelope.Points[4], envelope.Points[4]);
        Assert.Null(PatchParameterSchema.Build(patch, Fm7).Find("op1.env.breakPosition"));
    }

    [Fact]
    public void TotalLoss_IsAudioLossPlusALightComplexityTerm()
    {
        PatchEvaluation evaluation = Evaluator(10).Evaluate(ParameterTests.Stack(2.5), EvaluationLevel.Normal)!;

        Assert.Equal(evaluation.AudioLoss + (0.02 * evaluation.Complexity), evaluation.Loss, 12);
        Assert.True(0.02 * evaluation.Complexity < 0.01);
    }

    [Fact]
    public void BasinOptimizer_TunesTheDutyCycleOfAPulseAndTheResponseOfFilteredNoise()
    {
        SynthesisCapabilities fmx6 = SynthesisProfiles.SuperColliderFmx6.Capabilities;
        var optimizer = new BasinOptimizer(Evaluator(100), fmx6, new PatchMatchOptions { SynthesisProfile = SynthesisProfiles.SuperColliderFmx6 });
        OperatorMatrixPatch patch = ParameterTests.Stack(0.8)
            .WithOperator(0, ParameterTests.Stack(0.8).Operators[0] with { Waveform = OperatorWaveform.Pulse, Shape = 0.3 })
            .WithOperator(2, OperatorPatch.Sine(OperatorFrequency.AtFixed(8000), OperatorEnvelope.Constant()) with { Waveform = OperatorWaveform.FilteredNoise, Shape = 1.0 })
            .WithOutputLevel(2, 0.3);

        string[] spectral = [.. optimizer.SelectSubsets(patch, ContinuousStage.Spectral, tuningMatters: false).SelectMany(static s => s.Parameters).Select(static p => p.Id)];

        Assert.Contains(spectral, static id => id.EndsWith(".shape", StringComparison.Ordinal));
        Assert.Contains(spectral, static id => id.EndsWith(".filter", StringComparison.Ordinal));
    }

    [Fact]
    public void BasinOptimizer_KeepsActiveSubsetsSmallAndImprovesTheBasin()
    {
        PatchEvaluator evaluator = Evaluator(600, parallel: 8);
        var options = new PatchMatchOptions { MaxActiveParameters = 6 };
        var optimizer = new BasinOptimizer(evaluator, Fm7, options);
        OperatorMatrixPatch start = ParameterTests.Stack(0.8);

        foreach (ContinuousStage stage in Enum.GetValues<ContinuousStage>())
        {
            Assert.All(optimizer.SelectSubsets(start, stage, tuningMatters: true), s => Assert.InRange(s.Count, 1, 6));
        }

        // More smooth parameters than may be active at once: no joint stage, the work is split.
        Assert.Empty(optimizer.SelectSubsets(start, ContinuousStage.Joint, tuningMatters: false));
        Assert.True(optimizer.SelectSubsets(start, ContinuousStage.TemporalDetail, false).Count >= 2);
        IReadOnlyList<ActiveParameterSet> macros = optimizer.SelectSubsets(start, ContinuousStage.TemporalMacros, false);
        Assert.All(macros[0].Parameters, static p => Assert.StartsWith("macro.", p.Id));

        ActiveParameterSet spectral = optimizer.SelectSubsets(start, ContinuousStage.Spectral, false)[0];
        // The level of the only output operator is frozen: loudness is normalized away, so it cannot matter.
        Assert.Equal(["edge.2>1.amount"], spectral.Parameters.Select(static p => p.Id));
        var basin = new SearchBasin(1, "test", evaluator.Evaluate(start, EvaluationLevel.Normal)!);
        double before = basin.Best.Loss;

        bool improved = optimizer.Optimize(basin, spectral, 300, 0.2, seed: 11);

        Assert.True(improved);
        Assert.True(basin.Best.Loss < 0.5 * before, $"{before} -> {basin.Best.Loss}");
        Assert.InRange(basin.Best.Patch.ModulationMatrix[0, 1], 2.0, 3.0);
        Assert.Equal(basin.Regime, Canonicalization.PatchCanonicalizer.CanonicalKey(basin.Best.Patch));
    }
}
