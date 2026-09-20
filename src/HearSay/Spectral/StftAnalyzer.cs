using HearSay.Dsp;

namespace HearSay.Spectral;

/// <summary>Configuration of the multi-resolution STFT comparison.</summary>
public sealed record StftOptions
{
    /// <summary>FFT sizes (powers of two) of the resolutions that are compared.</summary>
    public IReadOnlyList<int> FftSizes { get; init; } = [256, 1024, 4096];

    /// <summary>Hop size as a fraction of the FFT size.</summary>
    public double HopFraction { get; init; } = 0.25;

    /// <summary>
    /// Dynamic range, in dB below the loudest bin of the target, that is compared. Quieter content is treated as
    /// silence so that inaudible differences in the noise floor do not dominate the log-magnitude distance.
    /// </summary>
    public double DynamicRangeDb { get; init; } = 80.0;

    /// <summary>FFT size of the resolution used for per-band, per-segment and over-time diagnostics.</summary>
    public int DiagnosticFftSize { get; init; } = 1024;

    /// <summary>Maximum number of points in the distance-over-time profile.</summary>
    public int TimeProfilePoints { get; init; } = 48;
}

/// <summary>Computes magnitude spectrograms.</summary>
public interface IStftAnalyzer
{
    /// <summary>Computes a Hann-windowed magnitude spectrogram.</summary>
    /// <param name="samples">Input signal.</param>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="fftSize">FFT size; a power of two.</param>
    /// <param name="hopSize">Hop between frames in samples.</param>
    Spectrogram Analyze(ReadOnlySpan<float> samples, int sampleRate, int fftSize, int hopSize);
}

/// <summary>Default STFT implementation. Windows and FFT plans are cached; the analyzer itself is stateless and thread-safe.</summary>
public sealed class StftAnalyzer : IStftAnalyzer
{
    /// <inheritdoc />
    public Spectrogram Analyze(ReadOnlySpan<float> samples, int sampleRate, int fftSize, int hopSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hopSize);
        FftPlan plan = FftPlan.Get(fftSize);
        HannWindow window = HannWindow.Get(fftSize);
        ReadOnlySpan<float> w = window.Coefficients;

        int bins = (fftSize / 2) + 1;
        int frames = (samples.Length / hopSize) + 1;
        var magnitudes = new float[frames * bins];
        var real = new float[fftSize];
        var imaginary = new float[fftSize];
        float scale = (float)(2.0 / window.Sum);
        int half = fftSize / 2;

        for (int frame = 0; frame < frames; frame++)
        {
            int start = (frame * hopSize) - half;
            int first = Math.Max(0, -start);
            int last = Math.Min(fftSize, samples.Length - start);
            Array.Clear(real);
            Array.Clear(imaginary);
            for (int i = first; i < last; i++)
            {
                real[i] = samples[start + i] * w[i];
            }

            plan.Forward(real, imaginary);
            Span<float> row = magnitudes.AsSpan(frame * bins, bins);
            for (int k = 0; k < bins; k++)
            {
                row[k] = MathF.Sqrt((real[k] * real[k]) + (imaginary[k] * imaginary[k])) * scale;
            }
        }

        return new Spectrogram(magnitudes, frames, fftSize, hopSize, sampleRate);
    }
}
