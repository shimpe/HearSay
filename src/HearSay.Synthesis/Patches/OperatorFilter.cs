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

    /// <summary>Two band-pass peaks fixed in hertz, F1 and F2 = F1 x ratio, with a shared Q.</summary>
    Formant,
}

/// <summary>
/// Filter between the waveform and the amplitude of an operator: what the operator sends, to the output and to the
/// operators it modulates, is the filtered signal. A TPT state-variable filter, see the FMX6 UGen.
/// </summary>
/// <param name="Type">Response; structural.</param>
/// <param name="Poles">2 or 4; 4 only for low-pass and high-pass. Structural.</param>
/// <param name="CutoffRatio">Low-pass, high-pass, band-pass and notch: frequency as a multiple of the note, 0.25..64.</param>
/// <param name="FixedHz">Peak: centre; formant: F1. In hertz, 10..20000.</param>
/// <param name="Q">Resonance, 0.5..20; shared by both formant peaks.</param>
/// <param name="FormantRatio">Formant: F2 / F1, 1..16.</param>
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

    /// <summary>The Q of a Butterworth response.</summary>
    public const double ButterworthQ = 0.7071;

    /// <summary>True for the types whose frequency follows the note; peak and formant are fixed in hertz.</summary>
    public bool IsKeyTracked => Type is OperatorFilterType.LowPass or OperatorFilterType.HighPass or OperatorFilterType.BandPass or OperatorFilterType.Notch;

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

    /// <summary>What is wrong with the filter, without operator prefix; empty when it is well formed.</summary>
    public IEnumerable<string> Validate()
    {
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

        if (!(Q is >= MinQ and <= MaxQ))
        {
            yield return "Q must be between 0.5 and 20.";
        }

        if (!(FormantRatio is >= 1.0 and <= 16.0))
        {
            yield return "formant ratio must be between 1 and 16.";
        }

        if (!(FormantLevel is >= 0.0 and <= 1.0))
        {
            yield return "formant level must be between 0 and 1.";
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
}
