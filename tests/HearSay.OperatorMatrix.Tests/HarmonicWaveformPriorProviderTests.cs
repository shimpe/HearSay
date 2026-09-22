using HearSay.Analysis;
using HearSay.OperatorMatrix.Priors;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.OperatorMatrix.Tests;

public sealed class HarmonicWaveformPriorProviderTests
{
    private static readonly SynthesisCapabilities Fmx6 = SynthesisProfiles.SuperColliderFmx6.Capabilities;

    /// <summary>A note built from harmonic amplitudes, with a short attack and a decaying tail.</summary>
    private static TargetAnalysis Analyze(Func<int, double> amplitude, double hz = 220, int sampleRate = 48000)
    {
        int n = sampleRate;
        var samples = new float[n];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / sampleRate;
            double env = Math.Min(1.0, t / 0.01) * Math.Exp(-1.5 * t);
            double sum = 0;
            for (int h = 1; h <= 40; h++)
            {
                sum += amplitude(h) * Math.Sin(2 * Math.PI * hz * h * t);
            }

            samples[i] = (float)(0.3 * env * sum);
        }

        return new TargetAnalyzer().Analyze(new AudioTarget(samples, sampleRate, hz));
    }

    [Theory]
    [InlineData("saw", OperatorWaveform.Saw)]
    [InlineData("square", OperatorWaveform.Square)]
    [InlineData("triangle", OperatorWaveform.Triangle)]
    public void BestHypothesis_HasTheWaveformOfTheTarget(string kind, OperatorWaveform expected)
    {
        Func<int, double> amplitude = kind switch
        {
            "saw" => static h => 1.0 / h,
            "square" => static h => (h & 1) != 0 ? 1.0 / h : 0.0,
            _ => static h => (h & 1) != 0 ? ((h - 1) / 2 % 2 == 0 ? 1.0 : -1.0) / (h * h) : 0.0,
        };

        IReadOnlyList<PatchHypothesis> hypotheses = new HarmonicWaveformPriorProvider().Generate(Analyze(amplitude), Fmx6);

        Assert.NotEmpty(hypotheses);
        PatchHypothesis best = hypotheses.MaxBy(static h => h.PriorScore)!;
        Assert.Equal(expected, best.Patch.Operators[0].Waveform);
        Assert.Equal(1.0, best.Patch.Operators[0].Frequency.Ratio);
        Assert.NotEqual(0.0, best.Patch.OutputLevels[0]);

        // Several harmonic limits of the winning waveform, a few modulated variants, and the noise hypotheses.
        Assert.True(hypotheses.Count(h => h.Patch.Operators[0].Waveform == expected) >= 4);
        Assert.Contains(hypotheses, static h => h.Origin.Contains("noise", StringComparison.Ordinal));
        Assert.All(hypotheses, static h => Assert.True(PatchCompatibilityValidator.Instance.Validate(h.Patch, Fmx6).IsCompatible));
    }

    [Fact]
    public void SineTarget_GetsNoBandLimitedHypothesis_AndFm7GetsNothing()
    {
        TargetAnalysis sine = Analyze(static h => h == 1 ? 1.0 : 0.0);
        IReadOnlyList<PatchHypothesis> hypotheses = new HarmonicWaveformPriorProvider().Generate(sine, Fmx6);
        Assert.DoesNotContain(hypotheses, static h => h.Patch.Operators[0].Waveform is OperatorWaveform.Saw or OperatorWaveform.Square or OperatorWaveform.Triangle or OperatorWaveform.Pulse);
        Assert.DoesNotContain(new HarmonicWaveformPriorProvider().Generate(sine, SynthesisProfiles.SuperColliderFm7.Capabilities), static h => h.Patch.Operators.Any(static o => o.Enabled && o.Waveform != OperatorWaveform.Sine));
    }

    [Fact]
    public void Match_PrefersTheRightPattern()
    {
        double[] saw = HarmonicWaveformPriorProvider.Ideal(OperatorWaveform.Saw, 0.5);
        double[] square = HarmonicWaveformPriorProvider.Ideal(OperatorWaveform.Square, 0.5);
        double[] triangle = HarmonicWaveformPriorProvider.Ideal(OperatorWaveform.Triangle, 0.5);
        Assert.Equal(1.0, HarmonicWaveformPriorProvider.Match(saw, saw), 6);
        Assert.True(HarmonicWaveformPriorProvider.Match(square, saw) < HarmonicWaveformPriorProvider.Match(square, square));
        Assert.True(HarmonicWaveformPriorProvider.Match(triangle, square) > HarmonicWaveformPriorProvider.Match(triangle, saw));
        Assert.True(HarmonicWaveformPriorProvider.Match(triangle, square) < HarmonicWaveformPriorProvider.Match(triangle, triangle));
    }
}
