using HearSay.Synthesis.Patches;

namespace HearSay.Synthesis.Filters;

/// <summary>The two integrator states of one TPT state-variable filter.</summary>
public struct SvfState
{
    /// <summary>First integrator.</summary>
    public double Ic1;

    /// <summary>Second integrator.</summary>
    public double Ic2;
}

/// <summary>
/// The operator filter of the FMX6 UGen, operation for operation, so that a renderer built on it is sample-exact:
/// TPT state-variable filters in double precision with <c>k = 1 / Q</c>, the frequency <c>baseHz x 2^octaves</c>
/// clamped to 10 Hz .. 0.49 x the sample rate. Stateless; the caller keeps two <see cref="SvfState"/> per operator.
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

    /// <summary>Two band-pass peaks in parallel.</summary>
    public const int Formant = 8;

    private const double ButterworthK = 1.4142135623730951;

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

    /// <summary>Filters one sample.</summary>
    /// <param name="type">Type code, <see cref="Off"/> .. <see cref="Formant"/>.</param>
    /// <param name="x">Input sample.</param>
    /// <param name="baseHz">Cutoff, centre or F1 before the envelope.</param>
    /// <param name="octaves">Envelope offset in octaves.</param>
    /// <param name="q">Resonance; clamped to 0.5..20.</param>
    /// <param name="formantRatio">F2 / F1.</param>
    /// <param name="formantLevel">Level of F2.</param>
    /// <param name="gainDb">Peak gain.</param>
    /// <param name="sampleRate">Sample rate the filter runs at.</param>
    /// <param name="first">State of the first filter.</param>
    /// <param name="second">State of the second filter (four poles, or F2).</param>
    public static double Process(int type, double x, double baseHz, double octaves, double q, double formantRatio, double formantLevel, double gainDb, double sampleRate, ref SvfState first, ref SvfState second)
    {
        double fc = baseHz * Math.Pow(2.0, octaves);
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
                Tick(lp1, fc, k, sampleRate, ref second, out _, out double lp2);
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
                Tick(hp1, fc, k, sampleRate, ref second, out double w1, out double w2);
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
                double g = Math.Pow(10.0, gainDb / 20.0);
                return x + ((g - 1.0) * (k * v1));
            }

            case Formant:
            {
                Tick(x, fc, k, sampleRate, ref first, out double v1, out _);
                Tick(x, fc * formantRatio, k, sampleRate, ref second, out double w1, out _);
                return (k * v1) + (formantLevel * (k * w1));
            }

            default:
                return x;
        }
    }

    private static void Tick(double x, double fc, double k, double sampleRate, ref SvfState s, out double v1, out double v2)
    {
        double f = Math.Max(10.0, Math.Min(fc, 0.49 * sampleRate));
        double g = Math.Tan(Math.PI * f / sampleRate);
        double a1 = 1.0 / (1.0 + (g * (g + k)));
        double a2 = g * a1;
        double a3 = g * a2;
        double v3 = x - s.Ic2;
        v1 = (a1 * s.Ic1) + (a2 * v3);
        v2 = s.Ic2 + (a2 * s.Ic1) + (a3 * v3);
        s.Ic1 = (2.0 * v1) - s.Ic1;
        s.Ic2 = (2.0 * v2) - s.Ic2;
    }
}
