using HearSay.Dsp;

namespace HearSay.Spectral;

/// <summary>
/// Time-averaged (Welch) power spectrum. Bins are scaled so that summing them over a frequency range gives the
/// mean-square power of the signal in that range. Immutable after construction.
/// </summary>
public sealed class AveragePowerSpectrum
{
    private AveragePowerSpectrum(double[] power, int fftSize, int sampleRate)
    {
        Power = power;
        FftSize = fftSize;
        SampleRate = sampleRate;
        double total = 0;
        int firstBin = Math.Max(1, (int)Math.Ceiling(20.0 / BinHz));
        for (int k = firstBin; k < power.Length; k++)
        {
            total += power[k];
        }

        TotalPower = total;
    }

    /// <summary>Power per bin.</summary>
    public double[] Power { get; }

    /// <summary>FFT size the spectrum was computed with.</summary>
    public int FftSize { get; }

    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate { get; }

    /// <summary>Width of one bin in Hz.</summary>
    public double BinHz => (double)SampleRate / FftSize;

    /// <summary>Total power from 20 Hz to the Nyquist frequency.</summary>
    public double TotalPower { get; }

    /// <summary>Averages the frames of a spectrogram into a power spectrum.</summary>
    /// <param name="spectrogram">Source spectrogram (Hann windowed, as produced by <see cref="StftAnalyzer"/>).</param>
    /// <param name="firstFrame">First frame to include.</param>
    /// <param name="frameCount">Number of frames to include; clamped to the available frames.</param>
    public static AveragePowerSpectrum FromSpectrogram(Spectrogram spectrogram, int firstFrame = 0, int frameCount = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(spectrogram);
        int bins = spectrogram.BinCount;
        var power = new double[bins];
        firstFrame = Math.Clamp(firstFrame, 0, Math.Max(0, spectrogram.FrameCount - 1));
        int lastFrame = (int)Math.Min(spectrogram.FrameCount, (long)firstFrame + Math.Max(1, frameCount));
        for (int f = firstFrame; f < lastFrame; f++)
        {
            ReadOnlySpan<float> row = spectrogram.Frame(f);
            for (int k = 0; k < bins; k++)
            {
                power[k] += (double)row[k] * row[k];
            }
        }

        // Spectrogram magnitudes are |X| * 2 / sum(w); convert to a Parseval-consistent one-sided power estimate.
        HannWindow window = HannWindow.Get(spectrogram.FftSize);
        double scale = window.Sum * window.Sum / (2.0 * spectrogram.FftSize * window.SumOfSquares) / Math.Max(1, lastFrame - firstFrame);
        for (int k = 0; k < bins; k++)
        {
            power[k] *= scale;
        }

        return new AveragePowerSpectrum(power, spectrogram.FftSize, spectrogram.SampleRate);
    }

    /// <summary>Power between two frequencies.</summary>
    /// <param name="startHz">Lower edge in Hz (inclusive).</param>
    /// <param name="endHz">Upper edge in Hz (exclusive).</param>
    public double BandPower(double startHz, double endHz)
    {
        int a = Math.Max(0, (int)Math.Ceiling(startHz / BinHz));
        int b = Math.Min(Power.Length, (int)Math.Ceiling(endHz / BinHz));
        double sum = 0;
        for (int k = a; k < b; k++)
        {
            sum += Power[k];
        }

        return sum;
    }
}
