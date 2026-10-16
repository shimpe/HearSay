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
}
