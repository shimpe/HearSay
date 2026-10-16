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
}
