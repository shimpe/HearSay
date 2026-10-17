using System.Globalization;
using HearSay.Synthesis.Patches;

namespace HearSay.OperatorMatrix.Priors;

/// <summary>
/// A few words for a sound, from its <see cref="SpectralFingerprint"/> and its patch: brightness, envelope, the waveform
/// of the loudest carrier when it is not a sine, and whether a filter or noise can be heard. For labels in the library
/// browser.
/// </summary>
public static class SoundDescriptors
{
    private const int Bands = SpectralFingerprint.Bands;

    /// <summary>Power-weighted mean frequency of the body slice, as a multiple of the note.</summary>
    /// <param name="print">Fingerprint.</param>
    public static double BrightnessRatio(ReadOnlySpan<byte> print)
    {
        CheckLength(print);
        double sum = 0, weighted = 0;
        for (int b = 0; b < Bands; b++)
        {
            double p = Power(print[Bands + b]);
            sum += p;
            weighted += p * b;
        }

        return SpectralFingerprint.LowestRatio * Math.Pow(2, weighted / sum / 12.0);
    }

    /// <summary>Total power of the decay slice against the attack slice, in decibels; negative when the sound dies away.</summary>
    /// <param name="print">Fingerprint.</param>
    public static double DecayDb(ReadOnlySpan<byte> print)
    {
        CheckLength(print);
        double attack = 0, decay = 0;
        for (int b = 0; b < Bands; b++)
        {
            attack += Power(print[b]);
            decay += Power(print[(2 * Bands) + b]);
        }

        return 10.0 * Math.Log10(decay / attack);
    }

    /// <summary>Brightness class of a sound: the power-weighted mean of the body slice, as a multiple of the note.</summary>
    /// <param name="print">Fingerprint.</param>
    public static SoundBrightness BrightnessOf(ReadOnlySpan<byte> print)
    {
        double ratio = BrightnessRatio(print);
        return ratio < 2 ? SoundBrightness.Dark : ratio < 4 ? SoundBrightness.Mellow : ratio < 8 ? SoundBrightness.Bright : SoundBrightness.VeryBright;
    }

    /// <summary>Envelope class of a sound: the decay slice against the attack slice.</summary>
    /// <param name="print">Fingerprint.</param>
    public static SoundEnvelope EnvelopeOf(ReadOnlySpan<byte> print)
    {
        double decay = DecayDb(print);
        return decay <= -18 ? SoundEnvelope.Percussive : decay <= -6 ? SoundEnvelope.Decaying : decay <= 6 ? SoundEnvelope.Sustained : SoundEnvelope.Swelling;
    }

    /// <summary>The word for a brightness class.</summary>
    /// <param name="brightness">Class.</param>
    public static string Word(SoundBrightness brightness) => brightness switch
    {
        SoundBrightness.Dark => "dark",
        SoundBrightness.Mellow => "mellow",
        SoundBrightness.Bright => "bright",
        _ => "very bright",
    };

    /// <summary>The word for an envelope class.</summary>
    /// <param name="envelope">Class.</param>
    public static string Word(SoundEnvelope envelope) => envelope switch
    {
        SoundEnvelope.Percussive => "percussive",
        SoundEnvelope.Decaying => "decaying",
        SoundEnvelope.Sustained => "sustained",
        _ => "swelling",
    };

    /// <summary>True when an audible operator is a noise waveform or roughened by the shared noise.</summary>
    /// <param name="patch">Patch.</param>
    /// <param name="audible">Its audible operators, see <see cref="OperatorMatrixPatch.FindAudibleOperators"/>.</param>
    internal static bool IsNoisy(OperatorMatrixPatch patch, bool[] audible)
    {
        for (int i = 0; i < patch.OperatorCount; i++)
        {
            OperatorPatch o = patch.Operators[i];
            if (audible[i] && (o.Waveform.IsNoise() || o.NoisePhase != 0.0 || o.NoiseM != 0.0))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The words for a sound, in a fixed order.</summary>
    /// <param name="print">Fingerprint of the sound.</param>
    /// <param name="patch">Patch that plays it.</param>
    public static IReadOnlyList<string> Of(ReadOnlySpan<byte> print, OperatorMatrixPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var words = new List<string>(5) { Word(BrightnessOf(print)), Word(EnvelopeOf(print)) };

        bool[] audible = patch.FindAudibleOperators();
        int loudest = -1;
        for (int i = 0; i < patch.OperatorCount; i++)
        {
            if (audible[i] && patch.OutputLevels[i] != 0.0 && (loudest < 0 || Math.Abs(patch.OutputLevels[i]) > Math.Abs(patch.OutputLevels[loudest])))
            {
                loudest = i;
            }
        }

        if (loudest >= 0 && patch.Operators[loudest].Waveform != OperatorWaveform.Sine)
        {
            words.Add(WaveformWord(patch.Operators[loudest].Waveform));
        }

        if (Enumerable.Range(0, patch.OperatorCount).Any(i => audible[i] && patch.Operators[i].Filter is not null))
        {
            words.Add("filtered");
        }

        if (IsNoisy(patch, audible))
        {
            words.Add("noisy");
        }

        return words;
    }

    /// <summary>The words joined with a middle dot, and the number of sounds.</summary>
    /// <param name="print">Fingerprint of the sound.</param>
    /// <param name="patch">Patch that plays it.</param>
    /// <param name="size">Number of sounds it stands for.</param>
    public static string Label(ReadOnlySpan<byte> print, OperatorMatrixPatch patch, int size) =>
        string.Join(" · ", Of(print, patch)) + " · " + (size == 1 ? "1 sound" : size.ToString("N0", CultureInfo.InvariantCulture) + " sounds");

    private static double Power(byte value) => Math.Pow(10.0, -value / 40.0);

    private static string WaveformWord(OperatorWaveform waveform) => waveform switch
    {
        OperatorWaveform.SampleHoldNoise => "sample-and-hold noise",
        OperatorWaveform.FilteredNoise => "filtered noise",
        _ => waveform.ToString().ToLowerInvariant(),
    };

    private static void CheckLength(ReadOnlySpan<byte> print)
    {
        if (print.Length != SpectralFingerprint.Length)
        {
            throw new ArgumentException($"A fingerprint has {SpectralFingerprint.Length} values.", nameof(print));
        }
    }
}
