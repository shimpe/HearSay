using HearSay.OperatorMatrix.Priors;
using HearSay.Synthesis.Patches;

namespace HearSay.OperatorMatrix.Tests;

public sealed class SoundTraitsTests
{
    private const byte Floor = 240;

    private static byte[] Print(int band, byte attack = 0, byte decay = 0)
    {
        var print = new byte[SpectralFingerprint.Length];
        Array.Fill(print, Floor);
        print[band] = attack;
        print[SpectralFingerprint.Bands + band] = 0;
        print[(2 * SpectralFingerprint.Bands) + band] = decay;
        return print;
    }

    private static OperatorPatch Op(double ratio, OperatorWaveform waveform = OperatorWaveform.Sine, OperatorFilter? filter = null) =>
        OperatorPatch.Sine(OperatorFrequency.AtRatio(ratio)) with { Waveform = waveform, Filter = filter };

    private static OperatorMatrixPatch Carrier(OperatorWaveform waveform = OperatorWaveform.Sine, OperatorFilter? filter = null) =>
        OperatorMatrixPatch.Silent(6).WithOperator(0, Op(1, waveform, filter)).WithOutputLevel(0, 1.0);

    [Fact]
    public void AudibleOperators_GiveTheirWaveformsAndFilters()
    {
        // OP2 modulates OP1 and so is heard; OP3 is connected to nothing.
        OperatorMatrixPatch patch = Carrier(OperatorWaveform.Saw, OperatorFilter.LowPass(4))
            .WithOperator(1, Op(2, OperatorWaveform.Square, OperatorFilter.Notch(2, 2)))
            .WithEdge(new ModulationEdge(1, 0, true, 1.0))
            .WithOperator(2, Op(3, OperatorWaveform.Pulse, OperatorFilter.FormantAt(500, 2, 0.5, 5)));
        SoundTraits traits = SoundTraits.Of(Print(30), patch);

        Assert.True(traits.Has(TraitValue.Waveform(OperatorWaveform.Saw)));
        Assert.True(traits.Has(TraitValue.Waveform(OperatorWaveform.Square)));
        Assert.False(traits.Has(TraitValue.Waveform(OperatorWaveform.Pulse)));
        Assert.False(traits.Has(TraitValue.Waveform(OperatorWaveform.Sine)));
        Assert.True(traits.Has(TraitValue.Filter(OperatorFilterType.LowPass)));
        Assert.True(traits.Has(TraitValue.Filter(OperatorFilterType.Notch)));
        Assert.False(traits.Has(TraitValue.Filter(OperatorFilterType.Formant)));
        Assert.False(traits.Has(TraitValue.Filter(null)));
        Assert.Equal(2, traits.AudibleOperators);
        Assert.True(traits.Has(TraitValue.Operators(2)));
        Assert.False(traits.Has(TraitValue.Operators(3)));
    }

    [Fact]
    public void NoFilter_IsAValueOfItsOwn()
    {
        SoundTraits traits = SoundTraits.Of(Print(30), Carrier());
        Assert.True(traits.Has(TraitValue.Filter(null)));
        Assert.False(traits.Has(TraitValue.Filter(OperatorFilterType.LowPass)));
        Assert.Equal(SoundTraits.NoFilterBit, traits.Filters);
    }

    [Fact]
    public void Noise_FollowsTheDescriptorRule()
    {
        Assert.True(SoundTraits.Of(Print(30), Carrier(OperatorWaveform.Noise)).Has(TraitValue.Noisy));
        Assert.False(SoundTraits.Of(Print(30), Carrier()).Has(TraitValue.Noisy));
    }

    [Fact]
    public void Feedback_CountsOnlyOnAudibleOperators()
    {
        Assert.True(SoundTraits.Of(Print(30), Carrier().WithEdge(new ModulationEdge(0, 0, true, 0.5))).Has(TraitValue.Feedback(true)));
        Assert.True(SoundTraits.Of(Print(30), Carrier()).Has(TraitValue.Feedback(false)));

        OperatorMatrixPatch silent = Carrier().WithOperator(1, Op(2)).WithEdge(new ModulationEdge(1, 1, true, 0.5));
        Assert.True(SoundTraits.Of(Print(30), silent).Has(TraitValue.Feedback(false)));
    }

    [Theory]
    [InlineData(30, 0, 0)]
    [InlineData(42, 0, 100)]
    [InlineData(54, 0, 40)]
    [InlineData(66, 40, 0)]
    public void Words_AgreeWithTheLabels(int band, byte attack, byte decay)
    {
        byte[] print = Print(band, attack, decay);
        SoundTraits traits = SoundTraits.Of(print, Carrier());
        IReadOnlyList<string> words = SoundDescriptors.Of(print, Carrier());
        Assert.Equal(words[0], SoundDescriptors.Word(traits.Brightness));
        Assert.Equal(words[1], SoundDescriptors.Word(traits.Envelope));
        Assert.True(traits.Has(TraitValue.Of(traits.Brightness)));
        Assert.True(traits.Has(TraitValue.Of(traits.Envelope)));
    }

    [Fact]
    public void Library_GivesTheTraitsOfEverySound()
    {
        StructureLibrary library = LibraryAtlasTests.Library.Value;
        SoundTraits[] traits = SoundTraits.Of(library);
        Assert.Equal(library.Count, traits.Length);
        foreach (int i in new[] { 0, 1234, library.Count - 1 })
        {
            Assert.Equal(SoundTraits.Of(library.Fingerprint(i), library.Patch(library.Seed(i))), traits[i]);
        }
    }

    [Fact]
    public void Values_HaveLabelsAndAFixedOrder()
    {
        Assert.Equal("S&H noise", TraitValue.Waveform(OperatorWaveform.SampleHoldNoise).Label);
        Assert.Equal("saw", TraitValue.Waveform(OperatorWaveform.Saw).Label);
        Assert.Equal("none", TraitValue.Filter(null).Label);
        Assert.Equal("low-pass", TraitValue.Filter(OperatorFilterType.LowPass).Label);
        Assert.Equal("very bright", TraitValue.Of(SoundBrightness.VeryBright).Label);
        Assert.Equal("percussive", TraitValue.Of(SoundEnvelope.Percussive).Label);
        Assert.Equal("noisy", TraitValue.Noisy.Label);
        Assert.Equal("3 ops", TraitValue.Operators(3).Label);
        Assert.Equal("no feedback", TraitValue.Feedback(false).Label);
        Assert.Equal("Audible operators", TraitValue.TitleOf(TraitFacet.Operators));

        IReadOnlyList<TraitValue> all = TraitValue.All;
        Assert.Equal(11 + 7 + 4 + 4 + 1 + 6 + 2, all.Count);
        Assert.Equal(all.OrderBy(static v => v.Facet).ThenBy(static v => v.Value), all);
        Assert.Equal(all.Count, all.Distinct().Count());
    }
}
