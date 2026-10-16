using HearSay.OperatorMatrix.Priors;
using HearSay.Synthesis.Patches;

namespace HearSay.OperatorMatrix.Tests;

public sealed class SoundDescriptorsTests
{
    private const byte Floor = 240;

    /// <summary>A fingerprint with one loud band per slice; the attack and decay slices get their own level.</summary>
    private static byte[] Print(int band, byte attack = 0, byte body = 0, byte decay = 0)
    {
        var print = new byte[SpectralFingerprint.Length];
        Array.Fill(print, Floor);
        print[band] = attack;
        print[SpectralFingerprint.Bands + band] = body;
        print[(2 * SpectralFingerprint.Bands) + band] = decay;
        return print;
    }

    private static OperatorMatrixPatch Carrier(OperatorWaveform waveform = OperatorWaveform.Sine, OperatorFilter? filter = null) =>
        OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1)) with { Waveform = waveform, Filter = filter })
            .WithOutputLevel(0, 1.0);

    [Theory]
    [InlineData(30, "dark")]
    [InlineData(42, "mellow")]
    [InlineData(54, "bright")]
    [InlineData(66, "very bright")]
    public void Brightness_FollowsTheBodySlice(int band, string word)
    {
        Assert.Equal(word, SoundDescriptors.Of(Print(band), Carrier())[0]);
        Assert.Equal(0.25 * Math.Pow(2, band / 12.0), SoundDescriptors.BrightnessRatio(Print(band)), 0.02);
    }

    [Theory]
    [InlineData(0, 100, "percussive")]
    [InlineData(0, 40, "decaying")]
    [InlineData(0, 0, "sustained")]
    [InlineData(40, 0, "swelling")]
    public void Envelope_ComparesTheDecayWithTheAttack(byte attack, byte decay, string word) =>
        Assert.Equal(word, SoundDescriptors.Of(Print(30, attack, 0, decay), Carrier())[1]);

    [Fact]
    public void Patch_AddsWaveformFilterAndNoise_OfWhatCanBeHeard()
    {
        byte[] print = Print(30);
        Assert.Equal(new[] { "dark", "sustained" }, SoundDescriptors.Of(print, Carrier()));
        Assert.Equal(new[] { "dark", "sustained", "saw", "filtered" }, SoundDescriptors.Of(print, Carrier(OperatorWaveform.Saw, OperatorFilter.LowPass(4))));
        Assert.Contains("noisy", SoundDescriptors.Of(print, Carrier(OperatorWaveform.Noise)));

        // OP2 cannot be heard: its waveform and filter do not count.
        OperatorMatrixPatch silentSaw = Carrier().WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(2)) with { Waveform = OperatorWaveform.Saw, Filter = OperatorFilter.LowPass(4) });
        Assert.Equal(new[] { "dark", "sustained" }, SoundDescriptors.Of(print, silentSaw));
    }

    [Fact]
    public void Label_JoinsTheWordsAndTheSize()
    {
        Assert.Equal("dark · sustained · 1 sound", SoundDescriptors.Label(Print(30), Carrier(), 1));
        Assert.Equal("dark · sustained · 7,412 sounds", SoundDescriptors.Label(Print(30), Carrier(), 7412));
    }
}
