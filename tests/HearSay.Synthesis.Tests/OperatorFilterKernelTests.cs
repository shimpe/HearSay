using HearSay.Synthesis.Filters;
using HearSay.Synthesis.Patches;

namespace HearSay.Synthesis.Tests;

public sealed class OperatorFilterKernelTests
{
    private const double Rate = 48000.0;

    /// <summary>Steady-state gain of a sine through a filter: RMS of the second half over RMS of the input.</summary>
    private static double Gain(int type, double sineHz, double baseHz, double q = 0.7071, double octaves = 0, double ratio = 2, double level = 0.5, double gainDb = 0)
    {
        var a = default(SvfState);
        var b = default(SvfState);
        int n = 48000;
        double sumIn = 0, sumOut = 0;
        for (int i = 0; i < n; i++)
        {
            double x = Math.Sin(2.0 * Math.PI * sineHz * i / Rate);
            double y = OperatorFilterKernel.Process(type, x, baseHz, octaves, q, ratio, level, gainDb, Rate, ref a, ref b);
            if (i >= n / 2)
            {
                sumIn += x * x;
                sumOut += y * y;
            }
        }

        return Math.Sqrt(sumOut / sumIn);
    }

    private static double Db(double gain) => 20.0 * Math.Log10(gain);

    [Fact]
    public void LowPass_FallsTwelveDbPerOctave_AndFourPolesTwiceAsFast()
    {
        Assert.InRange(Db(Gain(OperatorFilterKernel.LowPass2, 100, 1000)), -0.5, 0.5);
        Assert.InRange(Db(Gain(OperatorFilterKernel.LowPass2, 1000, 1000)), -3.6, -2.4);
        Assert.InRange(Db(Gain(OperatorFilterKernel.LowPass2, 4000, 1000)), -26, -22);
        Assert.InRange(Db(Gain(OperatorFilterKernel.LowPass4, 4000, 1000)), -52, -44);
    }

    [Fact]
    public void HighPass_MirrorsTheLowPass()
    {
        Assert.InRange(Db(Gain(OperatorFilterKernel.HighPass2, 10000, 1000)), -0.5, 0.5);
        Assert.InRange(Db(Gain(OperatorFilterKernel.HighPass2, 250, 1000)), -26, -22);
        Assert.InRange(Db(Gain(OperatorFilterKernel.HighPass4, 250, 1000)), -52, -44);
    }

    [Fact]
    public void BandPassNotchAndPeak_ActAtTheirCentre()
    {
        Assert.InRange(Gain(OperatorFilterKernel.BandPass, 1000, 1000, q: 4), 0.98, 1.02);
        Assert.True(Gain(OperatorFilterKernel.BandPass, 4000, 1000, q: 4) < 0.1);
        Assert.True(Gain(OperatorFilterKernel.Notch, 1000, 1000, q: 4) < 0.02);
        Assert.InRange(Gain(OperatorFilterKernel.Notch, 4000, 1000, q: 4), 0.95, 1.01);
        Assert.InRange(Db(Gain(OperatorFilterKernel.Peak, 1000, 1000, q: 2, gainDb: 12)), 11.8, 12.2);
        Assert.InRange(Db(Gain(OperatorFilterKernel.Peak, 1000, 1000, q: 2, gainDb: -12)), -12.2, -11.8);
        Assert.InRange(Gain(OperatorFilterKernel.Peak, 8000, 1000, q: 2, gainDb: 12), 0.98, 1.1);
    }

    [Fact]
    public void Formant_HasPeaksAtF1AndF2_AndTheEnvelopeShiftsBoth()
    {
        // F1 700 Hz, F2 = 1.8 x 700 = 1260 Hz at level 0.5, Q 10.
        double atF1 = Gain(OperatorFilterKernel.Formant, 700, 700, q: 10, ratio: 1.8, level: 0.5);
        double atF2 = Gain(OperatorFilterKernel.Formant, 1260, 700, q: 10, ratio: 1.8, level: 0.5);
        double between = Gain(OperatorFilterKernel.Formant, 940, 700, q: 10, ratio: 1.8, level: 0.5);
        Assert.InRange(atF1, 0.95, 1.1);
        Assert.InRange(atF2, 0.45, 0.6);
        Assert.True(between < 0.3, $"between the peaks {between:F3}");

        // One octave up by the envelope: F1 at 1400 Hz.
        Assert.InRange(Gain(OperatorFilterKernel.Formant, 1400, 700, q: 10, octaves: 1.0, ratio: 1.8, level: 0.5), 0.95, 1.1);
    }

    [Fact]
    public void Frequency_IsClampedAtBothEnds_AndStaysFinite()
    {
        var a = default(SvfState);
        var b = default(SvfState);
        for (int i = 0; i < 4800; i++)
        {
            double x = i % 2 == 0 ? 1.0 : -1.0;
            Assert.True(double.IsFinite(OperatorFilterKernel.Process(OperatorFilterKernel.LowPass4, x, 1e6, 0, 20, 2, 0.5, 0, Rate, ref a, ref b)));
            Assert.True(double.IsFinite(OperatorFilterKernel.Process(OperatorFilterKernel.Formant, x, 20, -4, 20, 16, 1, 0, Rate, ref a, ref b)));
        }
    }

    [Fact]
    public void Code_FollowsTheTypeAndThePoles()
    {
        Assert.Equal(OperatorFilterKernel.Off, OperatorFilterKernel.Code(null));
        Assert.Equal(1, OperatorFilterKernel.Code(OperatorFilter.LowPass(4)));
        Assert.Equal(2, OperatorFilterKernel.Code(OperatorFilter.LowPass(4, poles: 4)));
        Assert.Equal(3, OperatorFilterKernel.Code(OperatorFilter.HighPass(4)));
        Assert.Equal(4, OperatorFilterKernel.Code(OperatorFilter.HighPass(4, poles: 4)));
        Assert.Equal(5, OperatorFilterKernel.Code(OperatorFilter.BandPass(2, 2)));
        Assert.Equal(6, OperatorFilterKernel.Code(OperatorFilter.Notch(2, 2)));
        Assert.Equal(7, OperatorFilterKernel.Code(OperatorFilter.PeakAt(1000, 6, 2)));
        Assert.Equal(8, OperatorFilterKernel.Code(OperatorFilter.FormantAt(700, 2, 0.5, 8)));
    }

    /// <summary>The formulas of the UGen, without any caching: the kernel must give exactly these samples.</summary>
    private static double Reference(int type, double x, double baseHz, double octaves, double q, double ratio, double level, double gainDb, double rate, double[] a, double[] b)
    {
        static void Tick(double x, double fc, double k, double rate, double[] s, out double v1, out double v2)
        {
            double f = Math.Max(10.0, Math.Min(fc, 0.49 * rate));
            double g = Math.Tan(Math.PI * f / rate);
            double a1 = 1.0 / (1.0 + (g * (g + k)));
            double a2 = g * a1;
            double a3 = g * a2;
            double v3 = x - s[1];
            v1 = (a1 * s[0]) + (a2 * v3);
            v2 = s[1] + (a2 * s[0]) + (a3 * v3);
            s[0] = (2.0 * v1) - s[0];
            s[1] = (2.0 * v2) - s[1];
        }

        const double bw = 1.4142135623730951;
        double fc = baseHz * Math.Pow(2.0, octaves);
        double k = 1.0 / Math.Max(0.5, Math.Min(q, 20.0));
        double v1, v2, w1, w2;
        switch (type)
        {
            case 1: Tick(x, fc, k, rate, a, out _, out v2); return v2;
            case 2: Tick(x, fc, bw, rate, a, out _, out v2); Tick(v2, fc, k, rate, b, out _, out w2); return w2;
            case 3: Tick(x, fc, k, rate, a, out v1, out v2); return x - (k * v1) - v2;
            case 4:
                Tick(x, fc, bw, rate, a, out v1, out v2);
                double hp1 = x - (bw * v1) - v2;
                Tick(hp1, fc, k, rate, b, out w1, out w2);
                return hp1 - (k * w1) - w2;
            case 5: Tick(x, fc, k, rate, a, out v1, out _); return k * v1;
            case 6: Tick(x, fc, k, rate, a, out v1, out _); return x - (k * v1);
            case 7: Tick(x, fc, k, rate, a, out v1, out _); return x + ((Math.Pow(10.0, gainDb / 20.0) - 1.0) * (k * v1));
            case 8:
                Tick(x, fc, k, rate, a, out v1, out _);
                Tick(x, fc * ratio, k, rate, b, out w1, out _);
                return (k * v1) + (level * (k * w1));
            default: return x;
        }
    }

    [Fact]
    public void Kernel_IsBitIdenticalToTheUncachedFormulas_WhileItsInputsMoveAndHold()
    {
        var random = new Random(7);
        for (int type = 0; type <= 8; type++)
        {
            var a = default(SvfState);
            var b = default(SvfState);
            double[] ra = new double[2], rb = new double[2];
            double baseHz = 300, octaves = 0, gain = 6, q = 3;
            for (int i = 0; i < 6000; i++)
            {
                // Inputs hold for a while, then jump or ramp, as control inputs of the UGen do.
                if (i % 64 == 0)
                {
                    baseHz = random.Next(3) == 0 ? baseHz : 20 + (random.NextDouble() * 9000);
                    octaves = i % 128 == 0 ? 0.0 : (random.NextDouble() * 4) - 2;
                    gain = random.Next(2) == 0 ? gain : (random.NextDouble() * 48) - 24;
                }
                else if (i % 64 == 32)
                {
                    // The damping alone, and the gain alone, change while the frequency holds.
                    q = 0.5 + (random.NextDouble() * 19.5);
                    gain = random.Next(2) == 0 ? gain : (random.NextDouble() * 48) - 24;
                }
                else if (i % 512 > 256)
                {
                    octaves += 0.001;
                }

                double x = Math.Sin(i * 0.37) + (0.3 * Math.Sin(i * 2.1));
                double expected = Reference(type, x, baseHz, octaves, q, 2.3, 0.6, gain, Rate, ra, rb);
                double actual = OperatorFilterKernel.Process(type, x, baseHz, octaves, q, 2.3, 0.6, gain, Rate, ref a, ref b);
                Assert.True(expected.Equals(actual), FormattableString.Invariant($"type {type}, sample {i}: {expected:R} vs {actual:R}"));
            }
        }
    }
}
