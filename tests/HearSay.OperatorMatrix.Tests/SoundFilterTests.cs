using HearSay.OperatorMatrix.Priors;
using HearSay.Synthesis.Patches;

namespace HearSay.OperatorMatrix.Tests;

public sealed class SoundFilterTests
{
    private static SoundTraits T(OperatorWaveform waveform, OperatorFilterType? filter, SoundBrightness brightness, bool noisy = false) =>
        new(1 << (int)waveform, filter is { } f ? 1 << (int)f : SoundTraits.NoFilterBit, brightness, SoundEnvelope.Sustained, noisy, false, 2);

    private static readonly SoundTraits[] Sounds =
    [
        T(OperatorWaveform.Saw, OperatorFilterType.LowPass, SoundBrightness.Dark),
        T(OperatorWaveform.Square, OperatorFilterType.LowPass, SoundBrightness.Bright),
        T(OperatorWaveform.Saw, null, SoundBrightness.Bright, noisy: true),
        T(OperatorWaveform.Sine, OperatorFilterType.HighPass, SoundBrightness.Dark),
        T(OperatorWaveform.Square, null, SoundBrightness.Dark),
    ];

    private static readonly TraitValue Saw = TraitValue.Waveform(OperatorWaveform.Saw);
    private static readonly TraitValue Square = TraitValue.Waveform(OperatorWaveform.Square);
    private static readonly TraitValue LowPass = TraitValue.Filter(OperatorFilterType.LowPass);

    [Fact]
    public void Empty_MatchesEverySound()
    {
        Assert.True(SoundFilter.None.IsEmpty);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, SoundFilter.None.Members(Sounds));
    }

    [Fact]
    public void ValuesOfOneFacet_AreAlternatives()
    {
        SoundFilter filter = SoundFilter.None.With(Saw).With(Square);
        Assert.False(filter.IsEmpty);
        Assert.True(filter.Contains(Saw));
        Assert.Equal(new[] { 0, 1, 2, 4 }, filter.Members(Sounds));
    }

    [Fact]
    public void Facets_AreCombined()
    {
        SoundFilter filter = SoundFilter.None.With(Saw).With(Square).With(LowPass);
        Assert.Equal(new[] { 0, 1 }, filter.Members(Sounds));
        Assert.Equal(new[] { 1 }, filter.With(TraitValue.Of(SoundBrightness.Bright)).Members(Sounds));
        Assert.Equal(new[] { 2 }, SoundFilter.None.With(TraitValue.Noisy).Members(Sounds));
        Assert.Equal(new[] { 0, 2 }, filter.Without(Square).Without(LowPass).Members(Sounds));
    }

    [Fact]
    public void Counts_AreTheSoundsThatMatchTheOtherFacetsAndHaveTheValue()
    {
        SoundFilter filter = SoundFilter.None.With(Saw).With(Square).With(LowPass);
        IReadOnlyList<TraitValue> values = TraitValue.All;
        int[] counts = filter.Counts(Sounds, values);
        Assert.Equal(values.Count, counts.Length);
        for (int k = 0; k < values.Count; k++)
        {
            TraitValue v = values[k];
            SoundFilter others = filter.Chosen.Where(c => c.Facet != v.Facet).Aggregate(SoundFilter.None, static (f, c) => f.With(c));
            Assert.Equal(Sounds.Count(t => t.Has(v) && others.Matches(t)), counts[k]);
        }

        Assert.Equal(1, counts[IndexOf(values, Saw)]);
        Assert.Equal(0, counts[IndexOf(values, TraitValue.Waveform(OperatorWaveform.Sine))]);
        Assert.Equal(2, counts[IndexOf(values, TraitValue.Filter(null))]);
        Assert.Equal(2, counts[IndexOf(values, LowPass)]);
    }

    [Fact]
    public void Describe_ListsFacetsInPanelOrder()
    {
        SoundFilter filter = SoundFilter.None.With(TraitValue.Of(SoundEnvelope.Percussive)).With(LowPass).With(Square).With(Saw);
        Assert.Equal("saw or square · low-pass · percussive", filter.Describe());
        Assert.Equal(string.Empty, SoundFilter.None.Describe());
    }

    private static int IndexOf(IReadOnlyList<TraitValue> values, TraitValue value) => values.ToList().IndexOf(value);
}
