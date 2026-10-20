using HearSay.Synthesis.Patches;

namespace HearSay.IntegrationTests;

/// <summary>Text descriptions of patches; fast, no search.</summary>
public sealed class PatchDescriptionTests
{
    [Fact]
    public void NoiseUse_NamesTheHeardOperatorsThatUseTheSharedNoise()
    {
        OperatorPatch sine = OperatorPatch.Sine(OperatorFrequency.AtRatio(1));
        OperatorMatrixPatch silent = OperatorMatrixPatch.Silent(6).WithOperator(0, sine).WithOutputLevel(0, 0.8);

        // Nothing uses it: no sentence, and the search agrees.
        Assert.Equal(string.Empty, PatchDescription.NoiseUse(silent));
        Assert.False(HearSay.OperatorMatrix.Parameters.PatchParameterSchema.UsesSharedNoise(silent));

        // A noise operator that is heard, a roughened sine, and an elliptic operator with m roughness.
        OperatorMatrixPatch used = silent
            .WithOperator(0, sine with { NoisePhase = 0.8 })
            .WithOperator(1, sine.WithWaveform(OperatorWaveform.Noise)).WithOutputLevel(1, 0.5)
            .WithOperator(2, sine.WithWaveform(OperatorWaveform.Elliptic) with { NoiseM = 0.1 })
            .WithEdge(new ModulationEdge(2, 0, true, 1.0));
        Assert.Equal("Used by OP1 (roughness φ 0.8), OP2 (plays it), OP3 (roughness m 0.1).", PatchDescription.NoiseUse(used));
        Assert.True(HearSay.OperatorMatrix.Parameters.PatchParameterSchema.UsesSharedNoise(used));

        // An operator that cannot be heard does not count, whatever it would do.
        OperatorMatrixPatch unheard = silent.WithOperator(1, sine.WithWaveform(OperatorWaveform.Noise)).WithOperator(2, sine with { NoisePhase = 0.5 });
        Assert.Equal(string.Empty, PatchDescription.NoiseUse(unheard));
        Assert.False(HearSay.OperatorMatrix.Parameters.PatchParameterSchema.UsesSharedNoise(unheard));
    }

    [Fact]
    public void Filters_AreDescribed()
    {
        Assert.Equal("LP4 x4", PatchDescription.FilterLabel(OperatorFilter.LowPass(4, poles: 4)));
        Assert.Equal("fmt 700/1.8", PatchDescription.FilterLabel(OperatorFilter.FormantAt(700, 1.8, 0.5, 8)));
        Assert.Equal("peak 1500 +12", PatchDescription.FilterLabel(OperatorFilter.PeakAt(1500, 12, 4)));
        Assert.Equal("filter low-pass 4 poles, cutoff 4x, Q 0.71", PatchDescription.FilterDetail(OperatorFilter.LowPass(4, poles: 4)));
        Assert.Equal(
            "filter formant F1 700 Hz, F2 1260 Hz at 0.5, Q 8, sweep 1.5 oct",
            PatchDescription.FilterDetail(OperatorFilter.FormantAt(700, 1.8, 0.5, 8) with { EnvelopeOctaves = 1.5, Envelope = OperatorEnvelope.Adsr(0.01, 0.2, 0.3, 0.1) }));
        OperatorPatch op = OperatorPatch.Sine(OperatorFrequency.AtRatio(1)) with { Waveform = OperatorWaveform.Saw, Filter = OperatorFilter.BandPass(3, 6) };
        Assert.Contains("filter band-pass, centre 3x, Q 6", PatchDescription.WaveformDetail(op), StringComparison.Ordinal);
    }

    [Fact]
    public void Description_ListsTheFormants()
    {
        // The formants that are on, each with its Q when it has its own; F2 is always on.
        OperatorFilter five = OperatorFilter.Formants(800, 10, new FormantPeak(1.4375, 0.5, 12.8), new FormantPeak(3.625, 0.025, 0), new FormantPeak(4.875, 0, 30), new FormantPeak(6.1875, 0.0032, 35.4));
        Assert.Equal(
            "filter formant F1 800 Hz, F2 1150 Hz at 0.5 Q 12.8, F3 2900 Hz at 0.03, F5 4950 Hz at 0.0032 Q 35.4, Q 10",
            PatchDescription.FilterDetail(five));

        // The diagram label stays short: F1, F2 / F1, and how many formants more are on.
        Assert.Equal("fmt 800/1.44 +2", PatchDescription.FilterLabel(five));
        Assert.Equal("fmt 700/1.8", PatchDescription.FilterLabel(OperatorFilter.FormantAt(700, 1.8, 0.5, 41) with { FormantQ2 = 20, Formant4Ratio = 3 }));
        Assert.Equal("filter formant F1 700 Hz, F2 1260 Hz at 0.5 Q 20, Q 41", PatchDescription.FilterDetail(OperatorFilter.FormantAt(700, 1.8, 0.5, 41) with { FormantQ2 = 20, Formant4Ratio = 3 }));
    }

    [Fact]
    public void OperatorDetail_OfACarrier_TellsItsTuningWaveformFilterEnvelopeAndPlaying()
    {
        OperatorPatch op = OperatorPatch.Sine(OperatorFrequency.AtRatio(1.41), OperatorEnvelope.Adsr(0.005, 0.2, 0.5, 0.1)) with
        {
            Waveform = OperatorWaveform.Pulse,
            Shape = 0.1,
            FineTuneCents = -3.7,
            DetuneHz = 0.12,
            VelocitySensitivity = 0.42,
            KeyScaling = new KeyScaling(-1.5, 0.2),
            Filter = OperatorFilter.LowPass(4, poles: 4),
        };
        OperatorMatrixPatch patch = OperatorMatrixPatch.Silent(6).WithOperator(2, op).WithOutputLevel(2, 0.81);

        Assert.Equal(
            "OP3 · carrier, output 0.81\n"
            + "ratio ×1.41, fine -3.7 ct, detune 0.12 Hz\n"
            + "pulse, duty 0.1\n"
            + "filter low-pass 4 poles, cutoff 4x, Q 0.71\n"
            + "envelope 0 → 1 in 5 ms → 0.5 in 200 ms, held; release → 0 in 100 ms\n"
            + "velocity 0.42, key scaling -1.5 dB/oct, rate 0.2/oct",
            PatchDescription.OperatorDetail(patch, 2));
    }

    [Fact]
    public void OperatorDetail_OfASweptFilter_TellsTheSweepEnvelope()
    {
        OperatorFilter swept = OperatorFilter.LowPass(4, poles: 4) with { EnvelopeOctaves = -1.5, Envelope = OperatorEnvelope.Adsr(0.01, 0.2, 0.3, 0.1) };
        OperatorMatrixPatch patch = OperatorMatrixPatch.Silent(6).WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1)) with { Waveform = OperatorWaveform.Saw, Filter = swept }).WithOutputLevel(0, 1);

        Assert.Contains(
            "\nfilter low-pass 4 poles, cutoff 4x, Q 0.71, sweep -1.5 oct, envelope 0 → 1 in 10 ms → 0.3 in 200 ms, held; release → 0 in 100 ms\n",
            PatchDescription.OperatorDetail(patch, 0),
            StringComparison.Ordinal);
    }

    [Fact]
    public void EdgeDetail_TellsTheRouteTheAmountAndItsEnvelope()
    {
        var swell = new OperatorEnvelope([new EnvelopePoint(0, 0.2), new EnvelopePoint(0.3, 1)], null, 2);

        Assert.Equal("OP2 → OP1, 2.04 rad", PatchDescription.EdgeDetail(new ModulationEdge(1, 0, true, 2.04)));
        Assert.Equal("OP3 → OP3 (self-feedback), 0.59 rad", PatchDescription.EdgeDetail(new ModulationEdge(2, 2, true, 0.59)));
        Assert.Equal(
            "OP2 → OP1, 2.04 rad\namount × envelope 0.2 → 1 in 300 ms",
            PatchDescription.EdgeDetail(new ModulationEdge(1, 0, true, 2.04, swell)));
    }

    [Fact]
    public void PitchDetail_TellsTheEnvelopeInCentsAndTheVibrato()
    {
        var envelope = new OperatorEnvelope([new EnvelopePoint(0, 37), new EnvelopePoint(0.05, 0)], null, 2);
        var vibrato = new LfoSettings(5.2, 12, DelaySeconds: 0.3, FadeInSeconds: 0.5);

        Assert.Equal(string.Empty, PatchDescription.PitchDetail(PitchModel.None));
        Assert.Equal(string.Empty, PatchDescription.PitchDetail(new PitchModel(null, vibrato with { DepthCents = 0 })));
        Assert.Equal(
            "Pitch of the ratio operators (fixed ones keep their frequency)\n"
            + "envelope in cents: 37 → 0 in 50 ms\n"
            + "vibrato 5.2 Hz, ±12 ct, after 0.3 s, fading in over 0.5 s",
            PatchDescription.PitchDetail(new PitchModel(envelope, vibrato)));
        Assert.Equal(
            "Pitch of the ratio operators (fixed ones keep their frequency)\nvibrato 6 Hz, ±20 ct",
            PatchDescription.PitchDetail(new PitchModel(null, new LfoSettings(6, 20))));
    }

    [Fact]
    public void OperatorDetail_OfAModulator_TellsItsNoiseAndStartPhase_AndLeavesOutWhatItDoesNotUse()
    {
        OperatorPatch op = OperatorPatch.Sine(OperatorFrequency.AtFixed(440)) with
        {
            Waveform = OperatorWaveform.Elliptic,
            Shape = 0.99,
            Morph = 0.3,
            NoisePhase = 0.4,
            NoiseM = -0.2,
            InitialPhase = 0.5,
        };
        OperatorMatrixPatch patch = OperatorMatrixPatch.Silent(6).WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1))).WithOutputLevel(0, 1)
            .WithOperator(1, op).WithEdge(new ModulationEdge(1, 0, true, 2));

        Assert.Equal(
            "OP2 · modulator\n"
            + "fixed 440 Hz\n"
            + "elliptic, m 0.99 (q 0.262), morph 0.3\n"
            + "shared noise: phase 0.4 rad, m -0.2\n"
            + "envelope 0 → 1 in 5 ms, held; release → 0 in 5 ms\n"
            + "start phase 0.5 rad",
            PatchDescription.OperatorDetail(patch, 1));
    }
}
