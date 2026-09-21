using HearSay.Audio;

namespace HearSay.Tests;

/// <summary>Deterministic synthetic notes for the tests.</summary>
internal static class TestSignals
{
    public const int SampleRate = 44100;

    /// <summary>Additive tone with a linear ADSR envelope. <paramref name="harmonics"/>[0] is the fundamental amplitude.</summary>
    public static float[] Tone(
        double f0,
        double[] harmonics,
        double seconds = 1.0,
        double attackMs = 10,
        double decayMs = 0,
        double sustain = 1.0,
        double releaseMs = 50,
        double phase = 0,
        double gain = 0.3,
        int sampleRate = SampleRate)
    {
        int n = (int)(seconds * sampleRate);
        var samples = new float[n];
        double norm = 0;
        foreach (double a in harmonics)
        {
            norm += Math.Abs(a);
        }

        for (int i = 0; i < n; i++)
        {
            double t = (double)i / sampleRate;
            double value = 0;
            for (int k = 0; k < harmonics.Length; k++)
            {
                double f = (k + 1) * f0;
                if (f < sampleRate * 0.45)
                {
                    value += harmonics[k] * Math.Sin((2 * Math.PI * f * t) + (phase * (k + 1)));
                }
            }

            samples[i] = (float)(gain * value / norm * Adsr(t * 1000.0, seconds * 1000.0, attackMs, decayMs, sustain, releaseMs));
        }

        return samples;
    }

    public static double Adsr(double tMs, double totalMs, double attackMs, double decayMs, double sustain, double releaseMs)
    {
        double releaseStart = totalMs - releaseMs;
        double level;
        if (tMs < attackMs)
        {
            level = tMs / attackMs;
        }
        else if (tMs < attackMs + decayMs)
        {
            level = 1.0 - ((1.0 - sustain) * (tMs - attackMs) / decayMs);
        }
        else
        {
            level = sustain;
        }

        if (tMs > releaseStart && releaseMs > 0)
        {
            level *= Math.Max(0.0, 1.0 - ((tMs - releaseStart) / releaseMs));
        }

        return level;
    }

    /// <summary>Amplitudes k^-power for the first <paramref name="count"/> harmonics.</summary>
    public static double[] Rolloff(int count, double power)
    {
        var amplitudes = new double[count];
        for (int k = 0; k < count; k++)
        {
            amplitudes[k] = Math.Pow(k + 1, -power);
        }

        return amplitudes;
    }

    public static float[] AddNoise(float[] samples, double noiseRms, int seed = 1234)
    {
        var random = new Random(seed);
        var result = new float[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            // Sum of uniforms approximates a Gaussian; variance of each uniform(-1,1) is 1/3.
            double noise = (random.NextDouble() + random.NextDouble() + random.NextDouble() + random.NextDouble() - 2.0) * Math.Sqrt(3.0 / 4.0) * 2.0;
            result[i] = samples[i] + (float)(noise * noiseRms);
        }

        return result;
    }

    public static float[] Delay(float[] samples, double ms, int sampleRate = SampleRate)
    {
        int pad = (int)(ms * sampleRate / 1000.0);
        var result = new float[samples.Length + pad];
        samples.CopyTo(result, pad);
        return result;
    }

    public static float[] Scale(float[] samples, double db)
    {
        float gain = (float)Math.Pow(10.0, db / 20.0);
        var result = new float[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            result[i] = samples[i] * gain;
        }

        return result;
    }

    public static AudioNote Note(float[] samples, double? f0 = 440.0, int sampleRate = SampleRate) => new(samples, sampleRate, f0);
}
