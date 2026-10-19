using System.Globalization;
using HearSay.Synthesis.Patches;

namespace HearSay.Editing;

/// <summary>A vowel of a preset set.</summary>
/// <param name="Key">Its key in the set's data, such as "ie" (Dutch) or "iy" (English); unique within the set.</param>
/// <param name="Word">The word it is named by, such as "piet" or "heed"; for the sung set the vowel itself.</param>
/// <param name="Ipa">Its IPA symbol between slashes, such as "/i/".</param>
public sealed record FormantPresetVowel(string Key, string Word, string Ipa);

/// <summary>A set of vowel presets for the formant filter: one preset per speaker and vowel.</summary>
/// <param name="Name">"Sung", "English" or "Dutch".</param>
/// <param name="Source">Where the data come from, for the user.</param>
/// <param name="Speakers">The voices or groups of speakers, such as "soprano" or "women".</param>
/// <param name="Vowels">The vowels, in the order the editor shows them.</param>
public sealed record FormantPresetSet(string Name, string Source, IReadOnlyList<string> Speakers, IReadOnlyList<FormantPresetVowel> Vowels);

/// <summary>
/// Vowels for the formant filter, in three sets (<see cref="Sets"/>):
/// <list type="bullet">
/// <item>Sung: the five formants of 5 voices × 5 vowels from the formant table of the Csound manual (TABLE III).</item>
/// <item>English: the American English vowels of Hillenbrand et al. (1995), men, women and children.</item>
/// <item>Dutch: the Northern Standard Dutch vowels of Pols et al. and Van Nierop et al. (1973), men and women.</item>
/// </list>
/// A preset sets F1 in hertz with Q1 = F1 / bandwidth 1, and for F2..F5 the ratio Fn / F1, the level relative to F1 and
/// the own Q Fn / bandwidth n, each clamped to the filter's ranges. The sung levels are the table's (10^(dB / 20)). The
/// speech sets measured F1..F3 only (Dutch also the levels of F1..F3); the rest is filled in by the rules of the design
/// spec: bandwidths 80, 100, 150, 250 and 300 Hz; F4 and F5 per group of speakers, at least 300 Hz above the formant
/// below; the levels not measured from a cascade model of the vocal tract (<see cref="CascadeLevels"/>), every level at
/// most F1's.
/// </summary>
public static class FormantPresets
{
    /// <summary>
    /// The Csound table, one row per voice (soprano, alto, countertenor, tenor, bass) and vowel (a e i o u): F1..F5 in
    /// hertz, their levels in dB relative to F1, and their bandwidths in hertz.
    /// </summary>
    private static readonly double[][] SungTable =
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
    /// The means of the speech sets as the design spec has them, computed from the per-speaker data that Praat
    /// distributes: F1..F3 in hertz and, for Dutch, the levels L1..L3 in dB below the overall level.
    /// </summary>
    private const string SpeechMeans = """
        Dutch f a n=25 F=762 1117 2860 L=3.7 8.2 32.0
        Dutch f aa n=25 F=986 1443 2778 L=4.2 10.5 27.4
        Dutch f e n=25 F=669 1905 2788 L=2.2 18.0 23.4
        Dutch f ee n=25 F=472 2352 2896 L=1.9 22.3 24.7
        Dutch f eu n=25 F=476 1690 2512 L=1.8 18.7 26.8
        Dutch f i n=25 F=465 2262 2840 L=1.8 24.2 26.4
        Dutch f ie n=25 F=276 2510 3046 L=-0.2 26.5 29.5
        Dutch f o n=25 F=578 934 2852 L=2.8 7.7 34.6
        Dutch f oe n=25 F=320 842 2746 L=0.2 15.9 44.3
        Dutch f oo n=25 F=506 961 2608 L=2.4 8.9 37.3
        Dutch f u n=25 F=490 1688 2568 L=1.6 20.2 28.2
        Dutch f uu n=25 F=288 1832 2520 L=-0.2 23.8 29.4
        Dutch m a n=50 F=679 1051 2619 L=8.4 12.0 31.2
        Dutch m aa n=50 F=795 1301 2565 L=8.2 13.8 28.7
        Dutch m e n=50 F=583 1725 2471 L=7.2 18.7 25.4
        Dutch m ee n=50 F=407 2017 2554 L=5.0 21.0 23.3
        Dutch m eu n=50 F=443 1497 2260 L=5.1 20.7 27.9
        Dutch m i n=50 F=388 2003 2571 L=5.0 22.3 24.5
        Dutch m ie n=50 F=294 2208 2766 L=5.5 25.0 26.1
        Dutch m o n=50 F=523 866 2692 L=6.1 13.9 34.3
        Dutch m oe n=50 F=339 810 2323 L=5.2 18.2 41.2
        Dutch m oo n=50 F=487 911 2481 L=5.7 13.1 35.6
        Dutch m u n=50 F=438 1498 2354 L=4.8 20.5 28.2
        Dutch m uu n=50 F=305 1730 2208 L=5.1 23.2 28.0
        English children ae n=45 F=720 2499 3292
        English children ah n=45 F=1000 1689 2945
        English children aw n=46 F=836 1303 2951
        English children eh n=46 F=740 2279 3297
        English children ei n=46 F=565 2649 3323
        English children er n=46 F=586 1721 2154
        English children ih n=46 F=513 2556 3409
        English children iy n=46 F=453 3076 3702
        English children oa n=46 F=599 1140 3006
        English children oo n=46 F=571 1506 3076
        English children uh n=46 F=738 1538 3126
        English children uw n=46 F=493 1404 2992
        English men ae n=45 F=591 1930 2595
        English men ah n=45 F=756 1309 2535
        English men aw n=45 F=656 1023 2521
        English men eh n=45 F=588 1803 2604
        English men ei n=45 F=476 2090 2692
        English men er n=45 F=475 1379 1711
        English men ih n=45 F=429 2034 2687
        English men iy n=45 F=343 2323 3001
        English men oa n=45 F=498 910 2459
        English men oo n=45 F=469 1123 2435
        English men uh n=45 F=621 1181 2548
        English men uw n=45 F=380 992 2355
        English women ae n=48 F=676 2335 2973
        English women ah n=48 F=921 1526 2832
        English women aw n=48 F=804 1188 2824
        English women eh n=48 F=727 2063 2952
        English women ei n=48 F=535 2526 3050
        English women er n=48 F=524 1588 1930
        English women ih n=48 F=484 2369 3057
        English women iy n=48 F=437 2761 3373
        English women oa n=48 F=555 1036 2828
        English women oo n=48 F=519 1229 2829
        English women uh n=48 F=760 1416 2901
        English women uw n=48 F=460 1106 2735
        """;

    /// <summary>The bandwidths of the speech sets, B1..B5 in hertz.</summary>
    private static readonly double[] SpeechBandwidths = [80, 100, 150, 250, 300];

    /// <summary>The sung vowels of the Csound table.</summary>
    private static readonly FormantPresetSet Sung = new(
        "Sung",
        "Csound manual, appendix \"Formant values\" (TABLE III): sung vowels of five voices.",
        ["soprano", "alto", "countertenor", "tenor", "bass"],
        [new("a", "a", "/a/"), new("e", "e", "/e/"), new("i", "i", "/i/"), new("o", "o", "/o/"), new("u", "u", "/u/")]);

    /// <summary>American English vowels in /hVd/ words.</summary>
    private static readonly FormantPresetSet English = new(
        "English",
        "Hillenbrand, Getty, Clark & Wheeler (1995), \"Acoustic characteristics of American English vowels\", JASA 97: means of 45 men, 48 women and 46 children; F4, F5, bandwidths and levels filled in by rule.",
        ["men", "women", "children"],
        [
            new("iy", "heed", "/i/"), new("ih", "hid", "/ɪ/"), new("ei", "hayed", "/e/"), new("eh", "head", "/ɛ/"),
            new("ae", "had", "/æ/"), new("ah", "hod", "/ɑ/"), new("aw", "hawed", "/ɔ/"), new("oa", "hoed", "/o/"),
            new("oo", "hood", "/ʊ/"), new("uw", "who'd", "/u/"), new("uh", "hud", "/ʌ/"), new("er", "heard", "/ɝ/"),
        ]);

    /// <summary>Northern (Netherlands) Standard Dutch monophthongs, named by the keywords of the original lists.</summary>
    private static readonly FormantPresetSet Dutch = new(
        "Dutch",
        "Pols, Tromp & Plomp (1973), JASA 53 (50 men) and Van Nierop, Pols & Plomp (1973), Acta Psychologica 37 (25 women): means of Northern (Netherlands) Standard Dutch speakers, F1-F3 and their levels; F4, F5, bandwidths and the other levels filled in by rule.",
        ["men", "women"],
        [
            new("oe", "poet", "/u/"), new("aa", "paat", "/a/"), new("oo", "poot", "/o/"), new("a", "pat", "/ɑ/"),
            new("eu", "peut", "/ø/"), new("ie", "piet", "/i/"), new("uu", "puut", "/y/"), new("ee", "peet", "/e/"),
            new("u", "put", "/ʏ/"), new("e", "pet", "/ɛ/"), new("o", "pot", "/ɔ/"), new("i", "pit", "/ɪ/"),
        ]);

    /// <summary>The presets of each set, by speaker and then vowel key; the sets are told apart by reference.</summary>
    private static readonly Dictionary<FormantPresetSet, Dictionary<(string Speaker, string Vowel), Preset>> Data = Build();

    /// <summary>The preset sets: Sung, English and Dutch.</summary>
    public static IReadOnlyList<FormantPresetSet> Sets { get; } = [Sung, English, Dutch];

    /// <summary>
    /// The formant filter of a speaker and vowel of a set. It keeps the sweep (<see cref="OperatorFilter.EnvelopeOctaves"/>
    /// and <see cref="OperatorFilter.Envelope"/>) of the filter it replaces, of whatever type.
    /// </summary>
    /// <param name="set">One of <see cref="Sets"/>.</param>
    /// <param name="speaker">One of the set's <see cref="FormantPresetSet.Speakers"/>.</param>
    /// <param name="vowelKey">The <see cref="FormantPresetVowel.Key"/> of one of the set's vowels.</param>
    /// <param name="keepSweepFrom">The filter whose sweep to keep; null for none.</param>
    /// <exception cref="ArgumentException">The set is not one of <see cref="Sets"/>, or has no such speaker or vowel.</exception>
    public static OperatorFilter Filter(FormantPresetSet set, string speaker, string vowelKey, OperatorFilter? keepSweepFrom)
    {
        if (Problem(set, speaker, vowelKey) is { } problem)
        {
            throw new ArgumentException(problem);
        }

        Preset preset = Data[set][(speaker, vowelKey)];
        double f1 = Math.Clamp(preset.Hz[0], 10.0, 20000.0);
        var higher = new FormantPeak[4];
        for (int n = 1; n < 5; n++)
        {
            higher[n - 1] = new FormantPeak(
                Math.Clamp(preset.Hz[n] / f1, 1.0, OperatorFilter.MaxFormantRatio),
                Math.Clamp(preset.Levels[n - 1], 0.0, 1.0),
                OwnQ(preset.Hz[n] / preset.Bandwidths[n]));
        }

        OperatorFilter filter = OperatorFilter.Formants(f1, OwnQ(f1 / preset.Bandwidths[0]), higher);
        return keepSweepFrom is null ? filter : filter with { EnvelopeOctaves = keepSweepFrom.EnvelopeOctaves, Envelope = keepSweepFrom.Envelope };
    }

    /// <summary>Why there is no such preset, in words for the user; null when there is.</summary>
    internal static string? Problem(FormantPresetSet set, string speaker, string vowelKey)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (!Data.TryGetValue(set, out Dictionary<(string, string), Preset>? presets))
        {
            return $"\"{set.Name}\" is not one of the formant preset sets.";
        }

        if (!set.Speakers.Contains(speaker))
        {
            return $"The {set.Name} presets have no speaker \"{speaker}\"; they have {string.Join(", ", set.Speakers)}.";
        }

        return presets.ContainsKey((speaker, vowelKey)) ? null : $"The {set.Name} presets have no vowel \"{vowelKey}\".";
    }

    /// <summary>
    /// F4 and F5 of a speech preset: the defaults of the group of speakers, raised where needed so that each is at
    /// least 300 Hz above the formant below.
    /// </summary>
    /// <param name="f3">The measured F3 in hertz.</param>
    /// <param name="defaultF4">F4 of the group: men 3500, women 4100, children 4600 Hz.</param>
    /// <param name="defaultF5">F5 of the group: men 4500, women 5000, children 5600 Hz.</param>
    internal static (double F4, double F5) HigherFormants(double f3, double defaultF4, double defaultF5)
    {
        double f4 = Math.Max(defaultF4, f3 + 300.0);
        return (f4, Math.Max(defaultF5, f4 + 300.0));
    }

    /// <summary>
    /// The levels of F2..F5 relative to F1 in a cascade model of the vocal tract: each formant a two-pole resonance of
    /// unity gain at 0 Hz, <c>R_k(f) = (F_k² + (B_k/2)²) / sqrt((F_k² + (B_k/2)² − f²)² + (B_k·f)²)</c>, with a −6 dB per
    /// octave source-plus-radiation slope <c>S(f) = 1/f</c>; the level of Fn is <c>H(F_n) / H(F_1)</c> with
    /// <c>H(f) = S(f) · Π_k R_k(f)</c> over all five formants. Not capped.
    /// </summary>
    /// <param name="hz">F1..F5 in hertz.</param>
    /// <param name="bandwidths">B1..B5 in hertz.</param>
    internal static double[] CascadeLevels(IReadOnlyList<double> hz, IReadOnlyList<double> bandwidths)
    {
        double H(double f)
        {
            double h = 1.0 / f;
            for (int k = 0; k < 5; k++)
            {
                double half = bandwidths[k] / 2.0;
                double pole = (hz[k] * hz[k]) + (half * half);
                double away = pole - (f * f);
                double damping = bandwidths[k] * f;
                h *= pole / Math.Sqrt((away * away) + (damping * damping));
            }

            return h;
        }

        double first = H(hz[0]);
        return [H(hz[1]) / first, H(hz[2]) / first, H(hz[3]) / first, H(hz[4]) / first];
    }

    /// <summary>A level relative to F1, at most F1's own.</summary>
    internal static double CappedLevel(double level) => Math.Min(level, 1.0);

    private static double OwnQ(double q) => Math.Clamp(q, OperatorFilter.MinQ, OperatorFilter.MaxFormantQ);

    private static Dictionary<FormantPresetSet, Dictionary<(string Speaker, string Vowel), Preset>> Build()
    {
        var data = new Dictionary<FormantPresetSet, Dictionary<(string, string), Preset>>(ReferenceEqualityComparer.Instance);

        var sung = new Dictionary<(string, string), Preset>();
        for (int s = 0; s < Sung.Speakers.Count; s++)
        {
            for (int v = 0; v < Sung.Vowels.Count; v++)
            {
                double[] row = SungTable[(s * Sung.Vowels.Count) + v];
                double[] levels = [.. Enumerable.Range(1, 4).Select(n => Math.Pow(10.0, row[5 + n] / 20.0))];
                sung[(Sung.Speakers[s], Sung.Vowels[v].Key)] = new Preset(row[..5], levels, row[10..15]);
            }
        }

        data[Sung] = sung;
        data[English] = [];
        data[Dutch] = [];
        foreach (string line in SpeechMeans.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // "Dutch m ie n=50 F=294 2208 2766 L=5.5 25.0 26.1" or "English men iy n=45 F=343 2323 3001".
            string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            static double Number(string word) => double.Parse(word[(word.IndexOf('=', StringComparison.Ordinal) + 1)..], CultureInfo.InvariantCulture);
            bool dutch = words[0] == Dutch.Name;
            string speaker = words[1] switch { "m" => "men", "f" => "women", var group => group };
            (double defaultF4, double defaultF5) = speaker switch
            {
                "men" => (3500.0, 4500.0),
                "women" => (4100.0, 5000.0),
                _ => (4600.0, 5600.0),
            };
            double f1 = Number(words[4]), f2 = Number(words[5]), f3 = Number(words[6]);
            (double f4, double f5) = HigherFormants(f3, defaultF4, defaultF5);
            double[] hz = [f1, f2, f3, f4, f5];
            double[] model = CascadeLevels(hz, SpeechBandwidths);
            double[] levels;
            if (dutch)
            {
                // The measured levels of F2 and F3 relative to F1, (L1 − Ln) dB; F4 and F5 from the model, relative to
                // the measured F3.
                double l1 = Number(words[7]), l2 = Number(words[8]), l3 = Number(words[9]);
                double level3 = Math.Pow(10.0, (l1 - l3) / 20.0);
                levels = [Math.Pow(10.0, (l1 - l2) / 20.0), level3, model[2] / model[1] * level3, model[3] / model[1] * level3];
            }
            else
            {
                levels = model;
            }

            data[dutch ? Dutch : English][(speaker, words[2])] = new Preset(hz, [.. levels.Select(CappedLevel)], SpeechBandwidths);
        }

        return data;
    }

    /// <summary>The five formants of one preset.</summary>
    /// <param name="Hz">F1..F5 in hertz.</param>
    /// <param name="Levels">The levels of F2..F5 relative to F1, at most 1.</param>
    /// <param name="Bandwidths">B1..B5 in hertz.</param>
    private sealed record Preset(double[] Hz, double[] Levels, double[] Bandwidths);
}
