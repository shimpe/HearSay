using HearSay.Analysis;
using HearSay.OperatorMatrix.Priors;
using HearSay.OperatorMatrix.Search;
using HearSay.Synthesis.Fmx6Compatible.Rendering;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay.OperatorMatrix.Tests;

public sealed class FilterPriorTests
{
    private static readonly SynthesisCapabilities Fmx6 = SynthesisProfiles.SuperColliderFmx6.Capabilities;
    private static readonly Fmx6CompatibleOperatorMatrixRenderer Renderer = new();

    private static TargetAnalysis Analyze(OperatorMatrixPatch patch, double hz)
    {
        RenderedAudio audio = Renderer.Render(patch, RenderRequest.Create(hz, 1.0, 0.3));
        return new TargetAnalyzer().Analyze(new AudioTarget(audio.Samples, audio.SampleRate, hz));
    }

    private static OperatorMatrixPatch Saw(OperatorFilter? filter, OperatorEnvelope? envelope = null) =>
        OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), envelope ?? OperatorEnvelope.Adsr(0.01, 0.1, 0.8, 0.2)) with { Waveform = OperatorWaveform.Saw, HarmonicLimit = 128, Filter = filter })
            .WithOutputLevel(0, 0.8);

    [Theory]
    [InlineData(110.0, 700.0, 1.7)]
    [InlineData(220.0, 400.0, 5.0)]
    public void Formant_IsRecoveredWithinHalfTheFundamental(double f0, double f1, double ratio)
    {
        TargetAnalysis analysis = Analyze(Saw(OperatorFilter.FormantAt(f1, ratio, 0.6, 8)), f0);
        OperatorFilter? found = FilterPriors.Formant(analysis);
        Assert.NotNull(found);
        Assert.InRange(found!.FixedHz, f1 - (f0 / 2), f1 + (f0 / 2));
        Assert.InRange(found.FixedHz * found.FormantRatio, (f1 * ratio) - (f0 / 2), (f1 * ratio) + (f0 / 2));
    }

    [Fact]
    public void LowPass_FindsTheCutoff_AndTheSlope()
    {
        LowPassPrior? two = FilterPriors.LowPass(Analyze(Saw(OperatorFilter.LowPass(6)), 110));
        LowPassPrior? four = FilterPriors.LowPass(Analyze(Saw(OperatorFilter.LowPass(6, poles: 4)), 110));
        Assert.NotNull(two);
        Assert.InRange(two!.CutoffRatio, 3.0, 12.0);
        Assert.Equal(2, two.Poles);
        Assert.Equal(4, four!.Poles);
        Assert.Null(FilterPriors.LowPass(Analyze(Saw(null), 110)));
    }

    [Fact]
    public void Sweep_IsSeenOnlyWhenTheBrightnessMoves()
    {
        var closing = new OperatorEnvelope([new EnvelopePoint(0, 1), new EnvelopePoint(0.3, 0), new EnvelopePoint(0.1, 0)], SustainPoint: 1, ReleasePoint: 2);
        SweepPrior? sweep = FilterPriors.Sweep(Analyze(Saw(OperatorFilter.LowPass(2, poles: 4) with { EnvelopeOctaves = 3, Envelope = closing }), 110));
        Assert.NotNull(sweep);
        Assert.False(sweep!.Rising);
        Assert.InRange(sweep.Octaves, 1.0, 4.0);
        Assert.Null(FilterPriors.Sweep(Analyze(Saw(OperatorFilter.LowPass(4)), 110)));
    }

    [Fact]
    public void Providers_ProposeValidFilteredCarriers_OnlyForProfilesWithFilters()
    {
        TargetAnalysis vowel = Analyze(Saw(OperatorFilter.FormantAt(700, 1.7, 0.6, 8)), 110);
        IReadOnlyList<PatchHypothesis> formants = new FormantPriorProvider().Generate(vowel, Fmx6);
        Assert.NotEmpty(formants);
        Assert.All(formants, h => Assert.True(PatchCompatibilityValidator.Instance.Validate(h.Patch, Fmx6).IsCompatible));
        Assert.Equal(OperatorFilterType.Formant, formants[0].Patch.Operators[0].Filter!.Type);

        TargetAnalysis dark = Analyze(Saw(OperatorFilter.LowPass(5, poles: 4)), 110);
        IReadOnlyList<PatchHypothesis> subtractive = new SubtractivePriorProvider().Generate(dark, Fmx6);
        Assert.NotEmpty(subtractive);
        Assert.All(subtractive, h => Assert.Equal(OperatorFilterType.LowPass, h.Patch.Operators[0].Filter!.Type));

        Assert.Empty(new FormantPriorProvider().Generate(vowel, SynthesisProfiles.SuperColliderFm7.Capabilities));
        Assert.Empty(new SubtractivePriorProvider().Generate(dark, SynthesisProfiles.SuperColliderNome6.Capabilities));
    }

    [Fact]
    public void SweepMutation_ForAnOpeningFilter_LowersTheBaseSoItOpensToTheMeasuredCutoff()
    {
        var opening = new OperatorEnvelope([new EnvelopePoint(0, 0), new EnvelopePoint(0.25, 1), new EnvelopePoint(0.01, 1), new EnvelopePoint(0.1, 1)], SustainPoint: 2, ReleasePoint: 3);
        TargetAnalysis analysis = Analyze(Saw(OperatorFilter.LowPass(1, poles: 4) with { EnvelopeOctaves = 3, Envelope = opening }), 110);
        SweepPrior sweep = FilterPriors.Sweep(analysis)!;
        Assert.True(sweep.Rising);

        OperatorMatrixPatch measured = Saw(OperatorFilter.LowPass(8, poles: 4));
        TopologyMutation variant = Assert.Single(TopologyMutator.FilterVariants(measured, analysis, [0]), static m => m.Description == "OP1 filter sweep");
        OperatorFilter filter = variant.Patch.Operators[0].Filter!;
        Assert.Equal(8.0 / Math.Pow(2.0, sweep.Octaves), filter.CutoffRatio, 10);
        Assert.Equal(sweep.Octaves, filter.EnvelopeOctaves);
    }
}
