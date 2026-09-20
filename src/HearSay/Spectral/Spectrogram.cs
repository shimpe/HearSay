namespace HearSay.Spectral;

/// <summary>
/// Magnitude spectrogram. Frame <c>i</c> is centred on sample <c>i * HopSize</c>; samples outside the signal are
/// treated as zero. Magnitudes are scaled so that a stationary sinusoid of amplitude A peaks at about A.
/// Immutable after construction; the backing array must not be modified.
/// </summary>
public sealed class Spectrogram
{
    /// <summary>Creates a spectrogram from frame-major magnitude data.</summary>
    /// <param name="magnitudes">Magnitudes, <paramref name="frameCount"/> rows of <c>fftSize / 2 + 1</c> bins.</param>
    /// <param name="frameCount">Number of frames.</param>
    /// <param name="fftSize">FFT size.</param>
    /// <param name="hopSize">Hop between frames in samples.</param>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    public Spectrogram(float[] magnitudes, int frameCount, int fftSize, int hopSize, int sampleRate)
    {
        Magnitudes = magnitudes;
        FrameCount = frameCount;
        FftSize = fftSize;
        HopSize = hopSize;
        SampleRate = sampleRate;
        float max = 0f;
        foreach (float m in magnitudes)
        {
            max = Math.Max(max, m);
        }

        MaxMagnitude = max;
    }

    /// <summary>Frame-major magnitude data.</summary>
    public float[] Magnitudes { get; }

    /// <summary>Number of frames.</summary>
    public int FrameCount { get; }

    /// <summary>FFT size.</summary>
    public int FftSize { get; }

    /// <summary>Hop between frames in samples.</summary>
    public int HopSize { get; }

    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate { get; }

    /// <summary>Number of frequency bins per frame.</summary>
    public int BinCount => (FftSize / 2) + 1;

    /// <summary>Width of one frequency bin in Hz.</summary>
    public double BinHz => (double)SampleRate / FftSize;

    /// <summary>Largest magnitude in the spectrogram.</summary>
    public float MaxMagnitude { get; }

    /// <summary>Magnitudes of one frame.</summary>
    /// <param name="index">Frame index.</param>
    public ReadOnlySpan<float> Frame(int index) => Magnitudes.AsSpan(index * BinCount, BinCount);

    /// <summary>Centre time of a frame in milliseconds.</summary>
    /// <param name="index">Frame index.</param>
    public double FrameTimeMs(int index) => 1000.0 * index * HopSize / SampleRate;
}
