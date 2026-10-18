using HearSay.Synthesis.Patches;

namespace HearSay.IntegrationTests;

/// <summary>Text descriptions of patches; fast, no search.</summary>
public sealed class PatchDescriptionTests
{
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
