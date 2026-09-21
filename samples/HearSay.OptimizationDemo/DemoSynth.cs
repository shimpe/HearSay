using HearSay.Audio;
using HearSay.Comparison;
using HearSay.Modeling.Parameters;

namespace HearSay.OptimizationDemo;

/// <summary>
/// A tiny deterministic additive synth with five normalized parameters. Its responses are deliberately non-linear
/// and partly coupled, like a real synth: the library is never told how the parameters work.
/// </summary>
internal static class DemoSynth
{
    public const int SampleRate = 44100;
    public const double FundamentalHz = 220.0;

    public static IReadOnlyList<SynthParameter> Parameters { get; } =
    [
        new("brightness", "Brightness", 0.5, 0, 1, RecommendedMinStep: 0.02, Categories: [SynthParameterCategories.OscillatorBrightness]),
        new("attack", "Attack", 0.1, 0, 1, RecommendedMinStep: 0.02, Categories: [SynthParameterCategories.AmpEnvelopeAttack]),
        new("release", "Release", 0.2, 0, 1, RecommendedMinStep: 0.02, Categories: [SynthParameterCategories.AmpEnvelopeRelease]),
        new("noise", "NoiseAmount", 0.0, 0, 1, RecommendedMinStep: 0.02, Categories: [SynthParameterCategories.NoiseLevel]),
        new("harmonic2", "Harmonic2Amount", 0.0, 0, 1, RecommendedMinStep: 0.02),
    ];

    public static AudioNote Render(SynthParameterState state)
    {
        double brightness = state.Values["brightness"];
        double attackMs = 2.0 + (250.0 * Math.Pow(state.Values["attack"], 1.5));
        double releaseMs = 20.0 + (600.0 * state.Values["release"]);
        double noise = 0.08 * state.Values["noise"] * state.Values["noise"];
        double harmonic2 = 1.2 * state.Values["harmonic2"];

        const double HoldMs = 600.0;
        int length = (int)((HoldMs + releaseMs) * SampleRate / 1000.0);
        var samples = new float[length];

        const int HarmonicCount = 40;
        var amplitudes = new double[HarmonicCount];
        double norm = 0;
        for (int k = 0; k < HarmonicCount; k++)
        {
            amplitudes[k] = Math.Pow(k + 1, -(2.8 - (2.2 * brightness)));
            if (k == 1)
            {
                amplitudes[k] += harmonic2;
            }

            norm += amplitudes[k];
        }

        var random = new Random(12345);
        for (int i = 0; i < length; i++)
        {
            double t = (double)i / SampleRate;
            double ms = t * 1000.0;
            double envelope = Math.Min(1.0, ms / attackMs);
            if (ms > HoldMs)
            {
                envelope *= Math.Max(0.0, 1.0 - ((ms - HoldMs) / releaseMs));
            }

            double value = 0;
            for (int k = 0; k < HarmonicCount; k++)
            {
                double f = (k + 1) * FundamentalHz;
                if (f < SampleRate * 0.45)
                {
                    value += amplitudes[k] * Math.Sin(2.0 * Math.PI * f * t);
                }
            }

            double hiss = noise * ((random.NextDouble() * 2.0) - 1.0);
            samples[i] = (float)(0.4 * envelope * ((value / norm) + hiss));
        }

        return new AudioNote(samples, SampleRate, FundamentalHz);
    }
}
