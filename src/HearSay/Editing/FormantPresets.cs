using HearSay.Synthesis.Patches;

namespace HearSay.Editing;

/// <summary>A voice of the formant table.</summary>
public enum FormantVoice
{
    /// <summary>Soprano.</summary>
    Soprano,

    /// <summary>Alto.</summary>
    Alto,

    /// <summary>Countertenor.</summary>
    Countertenor,

    /// <summary>Tenor.</summary>
    Tenor,

    /// <summary>Bass.</summary>
    Bass,
}

/// <summary>A vowel of the formant table.</summary>
public enum FormantVowel
{
    /// <summary>"a".</summary>
    A,

    /// <summary>"e".</summary>
    E,

    /// <summary>"i".</summary>
    I,

    /// <summary>"o".</summary>
    O,

    /// <summary>"u".</summary>
    U,
}

/// <summary>
/// Vowels for the formant filter: the five formants of 5 voices × 5 vowels, from the formant table of the Csound manual
/// (TABLE III). A preset sets F1 in hertz with Q1 = F1 / bandwidth 1, and for F2..F5 the ratio Fn / F1, the level
/// 10^(dBn / 20) relative to F1 and the own Q Fn / bandwidth n, each clamped to the filter's ranges.
/// </summary>
public static class FormantPresets
{
    /// <summary>
    /// The table, one row per voice and vowel in the order of the enums: F1..F5 in hertz, their levels in dB relative
    /// to F1, and their bandwidths in hertz.
    /// </summary>
    private static readonly double[][] Table =
    [
        // soprano a, e, i, o, u
        [800, 1150, 2900, 3900, 4950, 0, -6, -32, -20, -50, 80, 90, 120, 130, 140],
        [350, 2000, 2800, 3600, 4950, 0, -20, -15, -40, -56, 60, 100, 120, 150, 200],
        [270, 2140, 2950, 3900, 4950, 0, -12, -26, -26, -44, 60, 90, 100, 120, 120],
        [450, 800, 2830, 3800, 4950, 0, -11, -22, -22, -50, 70, 80, 100, 130, 135],
        [325, 700, 2700, 3800, 4950, 0, -16, -35, -40, -60, 50, 60, 170, 180, 200],

        // alto a, e, i, o, u
        [800, 1150, 2800, 3500, 4950, 0, -4, -20, -36, -60, 80, 90, 120, 130, 140],
        [400, 1600, 2700, 3300, 4950, 0, -24, -30, -35, -60, 60, 80, 120, 150, 200],
        [350, 1700, 2700, 3700, 4950, 0, -20, -30, -36, -60, 50, 100, 120, 150, 200],
        [450, 800, 2830, 3500, 4950, 0, -9, -16, -28, -55, 70, 80, 100, 130, 135],
        [325, 700, 2530, 3500, 4950, 0, -12, -30, -40, -64, 50, 60, 170, 180, 200],

        // countertenor a, e, i, o, u
        [660, 1120, 2750, 3000, 3350, 0, -6, -23, -24, -38, 80, 90, 120, 130, 140],
        [440, 1800, 2700, 3000, 3300, 0, -14, -18, -20, -20, 70, 80, 100, 120, 120],
        [270, 1850, 2900, 3350, 3590, 0, -24, -24, -36, -36, 40, 90, 100, 120, 120],
        [430, 820, 2700, 3000, 3300, 0, -10, -26, -22, -34, 40, 80, 100, 120, 120],
        [370, 630, 2750, 3000, 3400, 0, -20, -23, -30, -34, 40, 60, 100, 120, 120],

        // tenor a, e, i, o, u
        [650, 1080, 2650, 2900, 3250, 0, -6, -7, -8, -22, 80, 90, 120, 130, 140],
        [400, 1700, 2600, 3200, 3580, 0, -14, -12, -14, -20, 70, 80, 100, 120, 120],
        [290, 1870, 2800, 3250, 3540, 0, -15, -18, -20, -30, 40, 90, 100, 120, 120],
        [400, 800, 2600, 2800, 3000, 0, -10, -12, -12, -26, 40, 80, 100, 120, 120],
        [350, 600, 2700, 2900, 3300, 0, -20, -17, -14, -26, 40, 60, 100, 120, 120],

        // bass a, e, i, o, u
        [600, 1040, 2250, 2450, 2750, 0, -7, -9, -9, -20, 60, 70, 110, 120, 130],
        [400, 1620, 2400, 2800, 3100, 0, -12, -9, -12, -18, 40, 80, 100, 120, 120],
        [250, 1750, 2600, 3050, 3340, 0, -30, -16, -22, -28, 60, 90, 100, 120, 120],
        [400, 750, 2400, 2600, 2900, 0, -11, -21, -20, -40, 40, 80, 100, 120, 120],
        [350, 600, 2400, 2675, 2950, 0, -20, -32, -28, -36, 40, 80, 100, 120, 120],
    ];

    /// <summary>
    /// The formant filter of a voice and vowel. It keeps the sweep (<see cref="OperatorFilter.EnvelopeOctaves"/> and
    /// <see cref="OperatorFilter.Envelope"/>) of the filter it replaces, of whatever type.
    /// </summary>
    /// <param name="voice">The voice.</param>
    /// <param name="vowel">The vowel.</param>
    /// <param name="keepSweepFrom">The filter whose sweep to keep; null for none.</param>
    public static OperatorFilter Filter(FormantVoice voice, FormantVowel vowel, OperatorFilter? keepSweepFrom)
    {
        if (!Enum.IsDefined(voice))
        {
            throw new ArgumentOutOfRangeException(nameof(voice));
        }

        if (!Enum.IsDefined(vowel))
        {
            throw new ArgumentOutOfRangeException(nameof(vowel));
        }

        double[] row = Table[((int)voice * 5) + (int)vowel];
        double f1 = Math.Clamp(row[0], 10.0, 20000.0);
        var higher = new FormantPeak[4];
        for (int n = 1; n < 5; n++)
        {
            higher[n - 1] = new FormantPeak(
                Math.Clamp(row[n] / f1, 1.0, OperatorFilter.MaxFormantRatio),
                Math.Clamp(Math.Pow(10.0, row[5 + n] / 20.0), 0.0, 1.0),
                OwnQ(row[n] / row[10 + n]));
        }

        OperatorFilter filter = OperatorFilter.Formants(f1, OwnQ(f1 / row[10]), higher);
        return keepSweepFrom is null ? filter : filter with { EnvelopeOctaves = keepSweepFrom.EnvelopeOctaves, Envelope = keepSweepFrom.Envelope };
    }

    /// <summary>The name of a voice for the user, such as "soprano".</summary>
    /// <param name="voice">The voice.</param>
    public static string Name(FormantVoice voice) => voice.ToString().ToLowerInvariant();

    /// <summary>The name of a vowel for the user, such as "a".</summary>
    /// <param name="vowel">The vowel.</param>
    public static string Name(FormantVowel vowel) => vowel.ToString().ToLowerInvariant();

    private static double OwnQ(double q) => Math.Clamp(q, OperatorFilter.MinQ, OperatorFilter.MaxFormantQ);
}
