using System.Text.Json.Serialization;

namespace HearSay.Synthesis.Patches;

/// <summary>Response of an operator filter. A structural parameter: the search moves between types by mutation only.</summary>
public enum OperatorFilterType
{
    /// <summary>Low-pass; cutoff follows the note.</summary>
    LowPass,

    /// <summary>High-pass; cutoff follows the note.</summary>
    HighPass,

    /// <summary>Band-pass with unity gain at the centre; centre follows the note.</summary>
    BandPass,

    /// <summary>Band-reject; centre follows the note.</summary>
    Notch,

    /// <summary>Boost or cut around a centre fixed in hertz; the rest passes unchanged.</summary>
    Peak,

    /// <summary>
    /// Up to five band-pass peaks fixed in hertz: F1, and F2..F5 = F1 x their ratio, each with its own level and,
    /// when it has one, its own Q.
    /// </summary>
    Formant,
}

/// <summary>One of the formants F2..F5 of a formant filter.</summary>
/// <param name="Ratio">Frequency as a multiple of F1, 1..32.</param>
/// <param name="Level">Level relative to F1, 0..1; F3..F5 at 0 are off.</param>
/// <param name="Q">Its own resonance, 0.5..50; 0 for the Q of the filter.</param>
public readonly record struct FormantPeak(double Ratio, double Level, double Q);

/// <summary>
/// Filter between the waveform and the amplitude of an operator: what the operator sends, to the output and to the
/// operators it modulates, is the filtered signal. A TPT state-variable filter, see the FMX6 UGen.
/// </summary>
/// <param name="Type">Response; structural.</param>
/// <param name="Poles">2 or 4; 4 only for low-pass and high-pass. Structural.</param>
/// <param name="CutoffRatio">Low-pass, high-pass, band-pass and notch: frequency as a multiple of the note, 0.25..64.</param>
/// <param name="FixedHz">Peak: centre; formant: F1. In hertz, 10..20000.</param>
/// <param name="Q">
/// Resonance, 0.5..20; for a formant filter 0.5..50, the Q of F1 and of every formant without a Q of its own.
/// </param>
/// <param name="FormantRatio">Formant: F2 / F1, 1..32.</param>
/// <param name="FormantLevel">Formant: level of F2 relative to F1, 0..1.</param>
/// <param name="PeakGainDb">Peak: gain at the centre, -24..24 dB.</param>
/// <param name="EnvelopeOctaves">How far the envelope moves the frequency, -4..4 octaves at envelope level 1.</param>
/// <param name="Envelope">Levels 0..1; null for a static frequency.</param>
public sealed record OperatorFilter(
    OperatorFilterType Type,
    int Poles = 2,
    double CutoffRatio = 4.0,
    double FixedHz = 1000.0,
    double Q = 0.7071,
    double FormantRatio = 2.0,
    double FormantLevel = 0.5,
    double PeakGainDb = 6.0,
    double EnvelopeOctaves = 0.0,
    OperatorEnvelope? Envelope = null)
{
    /// <summary>Smallest Q.</summary>
    public const double MinQ = 0.5;

    /// <summary>Largest Q, below self-oscillation.</summary>
    public const double MaxQ = 20.0;

    /// <summary>Largest Q of a formant filter: its <see cref="Q"/>, <see cref="FormantQ2"/> and <c>Formant{n}Q</c>.</summary>
    public const double MaxFormantQ = 50.0;

    /// <summary>Largest ratio of a formant to F1.</summary>
    public const double MaxFormantRatio = 32.0;

    /// <summary>The Q of a Butterworth response.</summary>
    public const double ButterworthQ = 0.7071;

    /// <summary>Formant: the Q of F2, 0.5..50; 0 (the default) for <see cref="Q"/>. Left out of the JSON when 0.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double FormantQ2 { get; init; }

    /// <summary>Formant: F3 / F1, 1..32. Left out of the JSON when 1 (the default).</summary>
    [JsonIgnore]
    public double Formant3Ratio { get; init; } = 1.0;

    /// <summary>Formant: level of F3 relative to F1, 0..1; 0 (the default) is off. Left out of the JSON when 0.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Formant3Level { get; init; }

    /// <summary>Formant: the Q of F3, 0.5..50; 0 (the default) for <see cref="Q"/>. Left out of the JSON when 0.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Formant3Q { get; init; }

    /// <summary>Formant: F4 / F1, 1..32. Left out of the JSON when 1 (the default).</summary>
    [JsonIgnore]
    public double Formant4Ratio { get; init; } = 1.0;

    /// <summary>Formant: level of F4 relative to F1, 0..1; 0 (the default) is off. Left out of the JSON when 0.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Formant4Level { get; init; }

    /// <summary>Formant: the Q of F4, 0.5..50; 0 (the default) for <see cref="Q"/>. Left out of the JSON when 0.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Formant4Q { get; init; }

    /// <summary>Formant: F5 / F1, 1..32. Left out of the JSON when 1 (the default).</summary>
    [JsonIgnore]
    public double Formant5Ratio { get; init; } = 1.0;

    /// <summary>Formant: level of F5 relative to F1, 0..1; 0 (the default) is off. Left out of the JSON when 0.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Formant5Level { get; init; }

    /// <summary>Formant: the Q of F5, 0.5..50; 0 (the default) for <see cref="Q"/>. Left out of the JSON when 0.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Formant5Q { get; init; }

    // The ratios in the JSON: null, and so left out, at their default of 1, so that patches of before keep their
    // serialized form and their hash.
    [JsonInclude]
    [JsonPropertyName("formant3Ratio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    private double? Formant3RatioJson
    {
        get => Formant3Ratio == 1.0 ? null : Formant3Ratio;
        init => Formant3Ratio = value ?? 1.0;
    }

    [JsonInclude]
    [JsonPropertyName("formant4Ratio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    private double? Formant4RatioJson
    {
        get => Formant4Ratio == 1.0 ? null : Formant4Ratio;
        init => Formant4Ratio = value ?? 1.0;
    }

    [JsonInclude]
    [JsonPropertyName("formant5Ratio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    private double? Formant5RatioJson
    {
        get => Formant5Ratio == 1.0 ? null : Formant5Ratio;
        init => Formant5Ratio = value ?? 1.0;
    }

    /// <summary>True for the types whose frequency follows the note; peak and formant are fixed in hertz.</summary>
    [JsonIgnore]
    public bool IsKeyTracked => Type is OperatorFilterType.LowPass or OperatorFilterType.HighPass or OperatorFilterType.BandPass or OperatorFilterType.Notch;

    /// <summary>Formant: F2..F5 as stored, in order; meaningful only for <see cref="OperatorFilterType.Formant"/>.</summary>
    [JsonIgnore]
    public IReadOnlyList<FormantPeak> FormantPeaks =>
    [
        new(FormantRatio, FormantLevel, FormantQ2),
        new(Formant3Ratio, Formant3Level, Formant3Q),
        new(Formant4Ratio, Formant4Level, Formant4Q),
        new(Formant5Ratio, Formant5Level, Formant5Q),
    ];

    /// <summary>
    /// True for a formant filter whose F2 has a Q of its own or whose F3, F4 or F5 is on: more than the two formants
    /// with one Q that the filter rows of 7 fields of the FMX6 UGen can carry.
    /// </summary>
    [JsonIgnore]
    public bool UsesExtendedFormants =>
        Type == OperatorFilterType.Formant && (FormantQ2 != 0.0 || Formant3Level != 0.0 || Formant4Level != 0.0 || Formant5Level != 0.0);

    /// <summary>A low-pass filter.</summary>
    /// <param name="cutoffRatio">Cutoff as a multiple of the note.</param>
    /// <param name="q">Resonance.</param>
    /// <param name="poles">2 or 4.</param>
    public static OperatorFilter LowPass(double cutoffRatio, double q = ButterworthQ, int poles = 2) =>
        new(OperatorFilterType.LowPass, poles, CutoffRatio: cutoffRatio, Q: q);

    /// <summary>A high-pass filter.</summary>
    /// <param name="cutoffRatio">Cutoff as a multiple of the note.</param>
    /// <param name="q">Resonance.</param>
    /// <param name="poles">2 or 4.</param>
    public static OperatorFilter HighPass(double cutoffRatio, double q = ButterworthQ, int poles = 2) =>
        new(OperatorFilterType.HighPass, poles, CutoffRatio: cutoffRatio, Q: q);

    /// <summary>A band-pass filter.</summary>
    /// <param name="centreRatio">Centre as a multiple of the note.</param>
    /// <param name="q">Resonance.</param>
    public static OperatorFilter BandPass(double centreRatio, double q) => new(OperatorFilterType.BandPass, CutoffRatio: centreRatio, Q: q);

    /// <summary>A notch filter.</summary>
    /// <param name="centreRatio">Centre as a multiple of the note.</param>
    /// <param name="q">Resonance.</param>
    public static OperatorFilter Notch(double centreRatio, double q) => new(OperatorFilterType.Notch, CutoffRatio: centreRatio, Q: q);

    /// <summary>A peak filter.</summary>
    /// <param name="hz">Centre in hertz.</param>
    /// <param name="gainDb">Gain at the centre.</param>
    /// <param name="q">Resonance.</param>
    public static OperatorFilter PeakAt(double hz, double gainDb, double q) => new(OperatorFilterType.Peak, FixedHz: hz, Q: q, PeakGainDb: gainDb);

    /// <summary>A formant filter.</summary>
    /// <param name="f1Hz">First peak in hertz.</param>
    /// <param name="f2Ratio">Second peak as a multiple of the first.</param>
    /// <param name="f2Level">Level of the second peak relative to the first.</param>
    /// <param name="q">Resonance of both peaks.</param>
    public static OperatorFilter FormantAt(double f1Hz, double f2Ratio, double f2Level, double q) =>
        new(OperatorFilterType.Formant, FixedHz: f1Hz, Q: q, FormantRatio: f2Ratio, FormantLevel: f2Level);

    /// <summary>A formant filter with up to five formants.</summary>
    /// <param name="f1Hz">F1 in hertz.</param>
    /// <param name="q">Resonance of F1 and of every formant without a Q of its own.</param>
    /// <param name="higher">F2, F3, F4 and F5 in this order, at most four; the ones not given are off.</param>
    /// <exception cref="ArgumentException">More than four formants above F1.</exception>
    public static OperatorFilter Formants(double f1Hz, double q, params FormantPeak[] higher)
    {
        ArgumentNullException.ThrowIfNull(higher);
        if (higher.Length > 4)
        {
            throw new ArgumentException("A formant filter has at most four formants above F1.", nameof(higher));
        }

        var off = new FormantPeak(1.0, 0.0, 0.0);
        FormantPeak Peak(int i) => i < higher.Length ? higher[i] : off;
        return new OperatorFilter(OperatorFilterType.Formant, FixedHz: f1Hz, Q: q, FormantRatio: Peak(0).Ratio, FormantLevel: Peak(0).Level)
        {
            FormantQ2 = Peak(0).Q,
            Formant3Ratio = Peak(1).Ratio,
            Formant3Level = Peak(1).Level,
            Formant3Q = Peak(1).Q,
            Formant4Ratio = Peak(2).Ratio,
            Formant4Level = Peak(2).Level,
            Formant4Q = Peak(2).Q,
            Formant5Ratio = Peak(3).Ratio,
            Formant5Level = Peak(3).Level,
            Formant5Q = Peak(3).Q,
        };
    }

    /// <summary>What is wrong with the filter, without operator prefix; empty when it is well formed.</summary>
    public IEnumerable<string> Validate()
    {
        bool formant = Type == OperatorFilterType.Formant;
        if (Poles is not (2 or 4) || (Poles == 4 && Type is not (OperatorFilterType.LowPass or OperatorFilterType.HighPass)))
        {
            yield return "poles must be 2, or 4 for a low-pass or high-pass.";
        }

        if (!(CutoffRatio is >= 0.25 and <= 64.0))
        {
            yield return "cutoff ratio must be between 0.25 and 64.";
        }

        if (!(FixedHz is >= 10.0 and <= 20000.0))
        {
            yield return "frequency must be between 10 and 20000 Hz.";
        }

        if (formant ? !(Q is >= MinQ and <= MaxFormantQ) : !(Q is >= MinQ and <= MaxQ))
        {
            yield return formant ? "Q must be between 0.5 and 50." : "Q must be between 0.5 and 20.";
        }

        if (!(FormantRatio is >= 1.0 and <= MaxFormantRatio))
        {
            yield return "formant ratio must be between 1 and 32.";
        }

        if (!(FormantLevel is >= 0.0 and <= 1.0))
        {
            yield return "formant level must be between 0 and 1.";
        }

        if (!IsOwnQ(FormantQ2))
        {
            yield return "F2 Q must be 0 (the filter's Q) or between 0.5 and 50.";
        }

        (int N, double Ratio, double Level, double Q)[] higher = [(3, Formant3Ratio, Formant3Level, Formant3Q), (4, Formant4Ratio, Formant4Level, Formant4Q), (5, Formant5Ratio, Formant5Level, Formant5Q)];
        foreach ((int n, double ratio, double level, double q) in higher)
        {
            if (!(ratio is >= 1.0 and <= MaxFormantRatio))
            {
                yield return $"F{n} ratio must be between 1 and 32.";
            }

            if (!(level is >= 0.0 and <= 1.0))
            {
                yield return $"F{n} level must be between 0 and 1.";
            }

            if (!IsOwnQ(q))
            {
                yield return $"F{n} Q must be 0 (the filter's Q) or between 0.5 and 50.";
            }
        }

        if (!(PeakGainDb is >= -24.0 and <= 24.0))
        {
            yield return "peak gain must be between -24 and 24 dB.";
        }

        if (!(EnvelopeOctaves is >= -4.0 and <= 4.0))
        {
            yield return "envelope depth must be between -4 and 4 octaves.";
        }

        if (Envelope is not null)
        {
            foreach (string problem in Envelope.Validate())
            {
                yield return "envelope: " + problem;
            }
        }
    }

    /// <summary>A Q of a formant peak: 0 for the filter's Q, or 0.5..50.</summary>
    private static bool IsOwnQ(double q) => q == 0.0 || q is >= MinQ and <= MaxFormantQ;
}
