using HearSay.Spectral;

namespace HearSay.OperatorMatrix.Priors;

/// <summary>
/// A pitch-normalised spectral fingerprint of a note, for finding patches that sound alike: band powers on relative
/// frequency, from a quarter of the note to 64 times it, a semitone apart, averaged over three time slices (attack up to
/// 0.1 s, body up to 0.4 s, decay up to 1 s), in dB under the loudest band and floored at -60 dB. Sideband spacings
/// become fixed patterns whatever the note, and the slices keep how the brightness moves. Each value is stored as one
/// byte, a quarter of a decibel per step below the loudest band.
/// </summary>
public static class SpectralFingerprint
{
    /// <summary>Semitone bands per slice.</summary>
    public const int Bands = 96;

    /// <summary>Lowest band centre as a multiple of the note frequency.</summary>
    public const double LowestRatio = 0.25;

    private const double FloorDb = -60.0;
    private const double StepsPerDb = 4.0;
    private static readonly double[] SliceEnds = [0.1, 0.4, 1.0];

    /// <summary>Number of values in a fingerprint.</summary>
    public static int Length => Bands * SliceEnds.Length;

    /// <summary>Fingerprint of a note.</summary>
    /// <param name="samples">Audio, starting at the onset.</param>
    /// <param name="sampleRate">Sample rate in hertz.</param>
    /// <param name="fundamentalHz">Frequency of the note.</param>
    public static byte[] Of(ReadOnlySpan<float> samples, int sampleRate, double fundamentalHz)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fundamentalHz);
        var result = new byte[Length];
        Array.Fill(result, (byte)(-FloorDb * StepsPerDb));
        if (samples.Length < 4096)
        {
            return result;
        }

        Spectrogram spectrogram = new StftAnalyzer().Analyze(samples, sampleRate, 4096, 1024);
        var power = new double[Length];
        var frames = new int[SliceEnds.Length];
        double binHz = spectrogram.BinHz;
        var bandOfBin = new int[spectrogram.BinCount];
        for (int k = 0; k < bandOfBin.Length; k++)
        {
            bandOfBin[k] = k == 0 ? -1 : (int)Math.Round(12.0 * Math.Log2(k * binHz / fundamentalHz / LowestRatio));
        }

        for (int f = 0; f < spectrogram.FrameCount; f++)
        {
            double time = spectrogram.FrameTimeMs(f) / 1000.0;
            int slice = Array.FindIndex(SliceEnds, e => time < e);
            if (slice < 0)
            {
                break;
            }

            frames[slice]++;
            ReadOnlySpan<float> frame = spectrogram.Frame(f);
            for (int k = 1; k < frame.Length; k++)
            {
                int band = bandOfBin[k];
                if (band >= 0 && band < Bands)
                {
                    power[(slice * Bands) + band] += (double)frame[k] * frame[k];
                }
            }
        }

        double max = 1e-30;
        for (int i = 0; i < power.Length; i++)
        {
            power[i] /= Math.Max(1, frames[i / Bands]);
            max = Math.Max(max, power[i]);
        }

        for (int i = 0; i < power.Length; i++)
        {
            double db = Math.Max(FloorDb, 10.0 * Math.Log10(Math.Max(power[i], 1e-30) / max));
            result[i] = (byte)Math.Round(-db * StepsPerDb);
        }

        return result;
    }

    /// <summary>Squared distance between two fingerprints, in quarter-decibel steps.</summary>
    /// <param name="a">First fingerprint.</param>
    /// <param name="b">Second fingerprint.</param>
    public static long Distance(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int d = a[i] - b[i];
            sum += d * d;
        }

        return sum;
    }
}
