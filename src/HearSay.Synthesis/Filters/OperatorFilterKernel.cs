using HearSay.Synthesis.Patches;

namespace HearSay.Synthesis.Filters;

/// <summary>The two integrator states of one TPT state-variable filter.</summary>
public struct SvfState
{
    /// <summary>First integrator.</summary>
    public double Ic1;

    /// <summary>Second integrator.</summary>
    public double Ic2;

    // The coefficients of the last frequency and damping, recomputed only when either changes: the same inputs give
    // the same coefficients, so this is bit-identical to computing them every sample. Zero frequency never matches.
    internal double CachedF;
    internal double CachedK;
    internal double A1;
    internal double A2;
    internal double A3;

    // Kept in the first state of an operator: 2^octaves of the envelope offset and the gain of a peak.
    internal bool HasOctaves;
    internal double CachedOctaves;
    internal double OctaveFactor;
    internal bool HasGain;
    internal double CachedGainDb;
    internal double Gain;
}

/// <summary>
/// The operator filter of the FMX6 UGen, operation for operation, so that a renderer built on it is sample-exact:
/// TPT state-variable filters in double precision with <c>k = 1 / Q</c>, the frequency <c>baseHz x 2^octaves</c>
/// clamped to 10 Hz .. 0.49 x the sample rate. Stateless; the caller keeps the <see cref="SvfState"/> of an operator:
/// two for the two stages of a four-pole filter, five for a formant filter, one per formant.
/// </summary>
public static class OperatorFilterKernel
{
    /// <summary>No filter.</summary>
    public const int Off = 0;

    /// <summary>Two-pole low-pass.</summary>
    public const int LowPass2 = 1;

    /// <summary>Four-pole low-pass: a Butterworth stage, then a stage with the Q.</summary>
    public const int LowPass4 = 2;

    /// <summary>Two-pole high-pass.</summary>
    public const int HighPass2 = 3;

    /// <summary>Four-pole high-pass: a Butterworth stage, then a stage with the Q.</summary>
    public const int HighPass4 = 4;

    /// <summary>Band-pass, unity gain at the centre.</summary>
    public const int BandPass = 5;

    /// <summary>Notch.</summary>
    public const int Notch = 6;

    /// <summary>Peak with a gain in dB.</summary>
    public const int Peak = 7;

    /// <summary>Up to five unity-gain band-pass peaks in parallel, F1 .. F5.</summary>
    public const int Formant = 8;

    /// <summary>States per operator for every type: one per formant.</summary>
    public const int StateCount = 5;

    /// <summary>Formants above F1, each a <see cref="FormantPeak"/>.</summary>
    public const int HigherFormantCount = 4;

    /// <summary>
    /// Extra formant inputs per operator of the 175-input layout of the UGen, in this order:
    /// <c>[q2, ratio3, level3, q3, ratio4, level4, q4, ratio5, level5, q5]</c>.
    /// </summary>
    public const int FormantFieldCount = 10;

    private const double ButterworthK = 1.4142135623730951;

    // The clamp of the Q of every formant peak; the other types clamp at 20.
    private const double MaxFormantQ = 50.0;

    /// <summary>The extra formant inputs of a filter in the layout of <see cref="FormantFieldCount"/>.</summary>
    /// <param name="filter">Filter, or null for none, which has the defaults: the filter's Q, F3..F5 off.</param>
    /// <param name="fields">Receives the <see cref="FormantFieldCount"/> values, in single precision as the UGen receives them.</param>
    public static void FormantFields(OperatorFilter? filter, Span<float> fields)
    {
        if (filter is null)
        {
            fields[..FormantFieldCount].Clear();
            fields[1] = fields[4] = fields[7] = 1f;
            return;
        }

        fields[0] = (float)filter.FormantQ2;
        fields[1] = (float)filter.Formant3Ratio;
        fields[2] = (float)filter.Formant3Level;
        fields[3] = (float)filter.Formant3Q;
        fields[4] = (float)filter.Formant4Ratio;
        fields[5] = (float)filter.Formant4Level;
        fields[6] = (float)filter.Formant4Q;
        fields[7] = (float)filter.Formant5Ratio;
        fields[8] = (float)filter.Formant5Level;
        fields[9] = (float)filter.Formant5Q;
    }

    /// <summary>F2..F5 of an operator as the UGen reads them from its single-precision inputs.</summary>
    /// <param name="formantRatio">F2 / F1.</param>
    /// <param name="formantLevel">Level of F2.</param>
    /// <param name="fields">The <see cref="FormantFieldCount"/> extra formant inputs.</param>
    /// <param name="peaks">Receives the <see cref="HigherFormantCount"/> peaks.</param>
    public static void Peaks(float formantRatio, float formantLevel, ReadOnlySpan<float> fields, Span<FormantPeak> peaks)
    {
        peaks[0] = new FormantPeak(formantRatio, formantLevel, fields[0]);
        peaks[1] = new FormantPeak(fields[1], fields[2], fields[3]);
        peaks[2] = new FormantPeak(fields[4], fields[5], fields[6]);
        peaks[3] = new FormantPeak(fields[7], fields[8], fields[9]);
    }

    /// <summary>The type code of the UGen for a filter.</summary>
    /// <param name="filter">Filter, or null for none.</param>
    public static int Code(OperatorFilter? filter) =>
        filter is null
            ? Off
            : filter.Type switch
            {
                OperatorFilterType.LowPass => filter.Poles == 4 ? LowPass4 : LowPass2,
                OperatorFilterType.HighPass => filter.Poles == 4 ? HighPass4 : HighPass2,
                OperatorFilterType.BandPass => BandPass,
                OperatorFilterType.Notch => Notch,
                OperatorFilterType.Peak => Peak,
                _ => Formant,
            };

    /// <summary>Filters one sample; a formant filter has two formants with one Q, as before F2 had its own Q.</summary>
    /// <param name="type">Type code, <see cref="Off"/> .. <see cref="Formant"/>.</param>
    /// <param name="x">Input sample.</param>
    /// <param name="baseHz">Cutoff, centre or F1 before the envelope.</param>
    /// <param name="octaves">Envelope offset in octaves.</param>
    /// <param name="q">Resonance; clamped to 0.5..20, for a formant filter to 0.5..50.</param>
    /// <param name="formantRatio">F2 / F1.</param>
    /// <param name="formantLevel">Level of F2.</param>
    /// <param name="gainDb">Peak gain.</param>
    /// <param name="sampleRate">Sample rate the filter runs at.</param>
    /// <param name="first">State of the first filter.</param>
    /// <param name="second">State of the second filter (four poles, or F2).</param>
    public static double Process(int type, double x, double baseHz, double octaves, double q, double formantRatio, double formantLevel, double gainDb, double sampleRate, ref SvfState first, ref SvfState second)
    {
        Span<SvfState> states = [first, second];
        ReadOnlySpan<FormantPeak> peaks = [new FormantPeak(formantRatio, formantLevel, 0.0)];
        double y = Process(type, x, baseHz, octaves, q, gainDb, peaks, sampleRate, states);
        first = states[0];
        second = states[1];
        return y;
    }

    /// <summary>Filters one sample.</summary>
    /// <param name="type">Type code, <see cref="Off"/> .. <see cref="Formant"/>.</param>
    /// <param name="x">Input sample.</param>
    /// <param name="baseHz">Cutoff, centre or F1 before the envelope.</param>
    /// <param name="octaves">Envelope offset in octaves.</param>
    /// <param name="q">Resonance; clamped to 0.5..20, for a formant filter to 0.5..50.</param>
    /// <param name="gainDb">Peak gain.</param>
    /// <param name="peaks">
    /// Formant: F2..F5 in order, at most <see cref="HigherFormantCount"/>; a peak Q of 0 or less is <paramref name="q"/>,
    /// and F3..F5 at level 0 are off: not computed, their state untouched. F2 is always computed.
    /// </param>
    /// <param name="sampleRate">Sample rate the filter runs at.</param>
    /// <param name="states">
    /// The states of the operator: at least two, and for a formant filter one more than <paramref name="peaks"/>. State
    /// 0 is the first filter (and F1), state 1 the second stage of four poles, state n - 1 formant Fn.
    /// </param>
    public static double Process(int type, double x, double baseHz, double octaves, double q, double gainDb, ReadOnlySpan<FormantPeak> peaks, double sampleRate, Span<SvfState> states)
    {
        ref SvfState first = ref states[0];
        if (!first.HasOctaves || !octaves.Equals(first.CachedOctaves))
        {
            first.OctaveFactor = Math.Pow(2.0, octaves);
            first.CachedOctaves = octaves;
            first.HasOctaves = true;
        }

        double fc = baseHz * first.OctaveFactor;
        double k = 1.0 / Math.Max(0.5, Math.Min(q, 20.0));
        switch (type)
        {
            case LowPass2:
            {
                Tick(x, fc, k, sampleRate, ref first, out _, out double lp);
                return lp;
            }

            case LowPass4:
            {
                Tick(x, fc, ButterworthK, sampleRate, ref first, out _, out double lp1);
                Tick(lp1, fc, k, sampleRate, ref states[1], out _, out double lp2);
                return lp2;
            }

            case HighPass2:
            {
                Tick(x, fc, k, sampleRate, ref first, out double v1, out double v2);
                return x - (k * v1) - v2;
            }

            case HighPass4:
            {
                Tick(x, fc, ButterworthK, sampleRate, ref first, out double v1, out double v2);
                double hp1 = x - (ButterworthK * v1) - v2;
                Tick(hp1, fc, k, sampleRate, ref states[1], out double w1, out double w2);
                return hp1 - (k * w1) - w2;
            }

            case BandPass:
            {
                Tick(x, fc, k, sampleRate, ref first, out double v1, out _);
                return k * v1;
            }

            case Notch:
            {
                Tick(x, fc, k, sampleRate, ref first, out double v1, out _);
                return x - (k * v1);
            }

            case Peak:
            {
                Tick(x, fc, k, sampleRate, ref first, out double v1, out _);
                if (!first.HasGain || !gainDb.Equals(first.CachedGainDb))
                {
                    first.Gain = Math.Pow(10.0, gainDb / 20.0);
                    first.CachedGainDb = gainDb;
                    first.HasGain = true;
                }

                return x + ((first.Gain - 1.0) * (k * v1));
            }

            case Formant:
            {
                // A sum of unity-gain band-passes on the same input, added left to right in the order of the UGen:
                // y = k1 * v1, then y = y + Ln * (kn * w1) for F2..F5. With Q up to 20, F2 at the shared Q and
                // F3..F5 off this is the two-peak (k * v1) + (level * (k * w1)) of before, bit for bit.
                double k1 = 1.0 / Math.Max(0.5, Math.Min(q, MaxFormantQ));
                Tick(x, fc, k1, sampleRate, ref first, out double v1, out _);
                double y = k1 * v1;
                for (int n = 0; n < peaks.Length; n++)
                {
                    FormantPeak p = peaks[n];
                    if (n >= 1 && p.Level == 0.0)
                    {
                        continue;
                    }

                    double kn = 1.0 / Math.Max(0.5, Math.Min(p.Q > 0.0 ? p.Q : q, MaxFormantQ));
                    Tick(x, fc * p.Ratio, kn, sampleRate, ref states[n + 1], out double w1, out _);
                    y = y + (p.Level * (kn * w1));
                }

                return y;
            }

            default:
                return x;
        }
    }

    private static void Tick(double x, double fc, double k, double sampleRate, ref SvfState s, out double v1, out double v2)
    {
        double f = Math.Max(10.0, Math.Min(fc, 0.49 * sampleRate));
        if (f != s.CachedF || k != s.CachedK)
        {
            double g = Math.Tan(Math.PI * f / sampleRate);
            s.A1 = 1.0 / (1.0 + (g * (g + k)));
            s.A2 = g * s.A1;
            s.A3 = g * s.A2;
            s.CachedF = f;
            s.CachedK = k;
        }

        double v3 = x - s.Ic2;
        v1 = (s.A1 * s.Ic1) + (s.A2 * v3);
        v2 = s.Ic2 + (s.A2 * s.Ic1) + (s.A3 * v3);
        s.Ic1 = (2.0 * v1) - s.Ic1;
        s.Ic2 = (2.0 * v2) - s.Ic2;
    }
}
