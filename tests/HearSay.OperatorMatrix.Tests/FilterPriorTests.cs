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
    public void FormantPrior_ThreePeaks_GiveF3()
    {
        const double f0 = 110.0, f1 = 400.0, ratio2 = 3.0, ratio3 = 7.0;
        TargetAnalysis analysis = Analyze(Saw(OperatorFilter.Formants(f1, 8, new(ratio2, 0.6, 0), new(ratio3, 0.5, 0))), f0);
        Assert.Equal(3, FilterPriors.Peaks(analysis, 3).Count);
        OperatorFilter found = FilterPriors.Formant(analysis)!;

        // F1 is the lowest peak; F2 and F3 are ratios to it, with levels relative to it. F1 and F2 are within half the
        // fundamental; F3 within the fundamental, as the envelope's grid of 1/24 octave is 80 Hz wide up there.
        Assert.InRange(found.FixedHz, f1 - (f0 / 2), f1 + (f0 / 2));
        Assert.InRange(found.FixedHz * found.FormantRatio, (f1 * ratio2) - (f0 / 2), (f1 * ratio2) + (f0 / 2));
        Assert.InRange(found.FixedHz * found.Formant3Ratio, (f1 * ratio3) - f0, (f1 * ratio3) + f0);
        Assert.InRange(found.FormantLevel, 0.2, 1.0);
        Assert.InRange(found.Formant3Level, 0.1, 1.0);

        // Own Qs from the peak widths, within the formant range; F4 and F5 are never set.
        Assert.InRange(found.Q, 2.0, OperatorFilter.MaxFormantQ);
        Assert.True(found.FormantQ2 == 0.0 || found.FormantQ2 is >= 2.0 and <= OperatorFilter.MaxFormantQ, $"Q2 {found.FormantQ2}");
        Assert.True(found.Formant3Q == 0.0 || found.Formant3Q is >= 2.0 and <= OperatorFilter.MaxFormantQ, $"Q3 {found.Formant3Q}");
        Assert.Equal((0.0, 0.0), (found.Formant4Level, found.Formant5Level));
        Assert.Empty(Saw(found).Validate());

        // The prior, the provider and the mutation all offer it.
        IReadOnlyList<PatchHypothesis> hypotheses = new FormantPriorProvider().Generate(analysis, Fmx6);
        Assert.Equal(found, hypotheses[0].Patch.Operators[0].Filter);
        Assert.Contains(hypotheses, static h => h.Patch.Operators[0].Filter!.Formant3Level == 0.0 && h.Patch.Operators[0].Filter!.FormantLevel > 0.0);
        Assert.All(hypotheses, h => Assert.True(PatchCompatibilityValidator.Instance.Validate(h.Patch, Fmx6).IsCompatible));
        OperatorMatrixPatch plain = Saw(null);
        TopologyMutation formant = Assert.Single(TopologyMutator.FilterVariants(plain, analysis, [0]), static m => m.Description == "OP1 filter formant");
        Assert.Equal(found, formant.Patch.Operators[0].Filter);

        // A two-formant filter can be given the target's third formant, as a ratio to its own F1.
        OperatorMatrixPatch two = Saw(OperatorFilter.FormantAt(380, 3.1, 0.6, 8));
        TopologyMutation third = Assert.Single(TopologyMutator.FilterVariants(two, analysis, [0]), static m => m.Description == "OP1 filter third formant");
        OperatorFilter added = third.Patch.Operators[0].Filter!;
        Assert.Equal((380.0, 3.1, 0.6, 8.0), (added.FixedHz, added.FormantRatio, added.FormantLevel, added.Q));
        Assert.Equal(found.FixedHz * found.Formant3Ratio, 380.0 * added.Formant3Ratio, 6);
        Assert.Equal(found.Formant3Level, added.Formant3Level);
        Assert.Equal(found.Formant3Q > 0 ? found.Formant3Q : found.Q, added.Formant3Q > 0 ? added.Formant3Q : added.Q);
        Assert.DoesNotContain(TopologyMutator.FilterVariants(third.Patch, analysis, [0]), static m => m.Description == "OP1 filter third formant");
    }

    [Fact]
    public void ThirdFormantVariant_IsNotOffered_WhenTheFiltersF2IsAboveTheTargetsF3()
    {
        TargetAnalysis analysis = Analyze(Saw(OperatorFilter.Formants(400, 8, new(3.0, 0.6, 0), new(7.0, 0.5, 0))), 110);
        OperatorFilter heard = FilterPriors.Formant(analysis)!;
        double f3 = heard.FixedHz * heard.Formant3Ratio;

        // F1 above the heard F3, and F1 below it with F2 above it: a third peak there would not be the third.
        foreach (OperatorFilter filter in new[] { OperatorFilter.FormantAt(f3 * 1.2, 1.5, 0.5, 8), OperatorFilter.FormantAt(f3 / 2, 2.5, 0.5, 8), OperatorFilter.FormantAt(f3 / 2, 2.0, 0.5, 8) })
        {
            Assert.DoesNotContain(TopologyMutator.FilterVariants(Saw(filter), analysis, [0]), static m => m.Description == "OP1 filter third formant");
        }

        // Just below it, it is offered.
        Assert.Contains(TopologyMutator.FilterVariants(Saw(OperatorFilter.FormantAt(f3 / 2, 1.9, 0.5, 8)), analysis, [0]), static m => m.Description == "OP1 filter third formant");
    }

    [Fact]
    public void FormantPrior_TwoPeaks_LeaveF3Off()
    {
        TargetAnalysis analysis = Analyze(Saw(OperatorFilter.FormantAt(700, 1.7, 0.6, 8)), 110);
        Assert.Equal(2, FilterPriors.Peaks(analysis, 3).Count);
        OperatorFilter found = FilterPriors.Formant(analysis)!;
        Assert.True(found.FormantLevel > 0.0);
        Assert.Equal((1.0, 0.0, 0.0), (found.Formant3Ratio, found.Formant3Level, found.Formant3Q));
        Assert.Equal((0.0, 0.0), (found.Formant4Level, found.Formant5Level));
        Assert.DoesNotContain(TopologyMutator.FilterVariants(Saw(OperatorFilter.FormantAt(700, 1.7, 0.6, 8)), analysis, [0]), static m => m.Description == "OP1 filter third formant");
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
