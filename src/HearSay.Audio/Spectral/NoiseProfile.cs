namespace HearSay.Spectral;

/// <summary>The noise of a note: what the spectrum holds between its partials.</summary>
/// <param name="Fraction">Share of the energy of the note that is noise, from 0 (only partials) to 1 (only noise).</param>
/// <param name="CentroidHz">Spectral centroid of the noise alone, its colour; 0 when there is no noise.</param>
public sealed record NoiseProfile(double Fraction, double CentroidHz)
{
    /// <summary>No noise at all.</summary>
    public static NoiseProfile None { get; } = new(0.0, 0.0);
}

/// <summary>
/// Separates noise from partials. In every frame the noise floor is the running median of the power spectrum across
/// frequency: partials are narrow peaks that a median ignores, noise fills every bin. The median of noise power is
/// ln 2 times its mean, so the floor is scaled back to a mean. Stateless.
/// </summary>
public static class NoiseAnalyzer
{
    /// <summary>Half the width of the median window. Wide enough to span the gaps between partials, narrow enough to follow the colour of the noise.</summary>
    private const double HalfWindowHz = 400.0;

    private const double LowestHz = 20.0;

    /// <summary>
    /// A bin counts as part of a partial when its power exceeds the mean noise floor this many times. Noise power is
    /// exponentially distributed, so a bin of pure noise gets there with a probability of e^-8, about 0.03 %.
    /// </summary>
    private const double PartialRatio = 8.0;

    /// <summary>Frames on either side whose floors are averaged when the noise is smoothed.</summary>
    private const int TimeSpanFrames = 2;

    /// <summary>Bins and frames on either side over which a spectrogram too coarse to resolve the partials is averaged.</summary>
    private const int AverageSpan = 4;

    /// <summary>Partials have to lie at least this many bins apart for the noise between them to be measured.</summary>
    private const double MinimumBinsBetweenPartials = 6.0;

    /// <summary>Measures the noise of a note.</summary>
    /// <param name="spectrogram">
    /// Spectrogram with enough frequency resolution to resolve the partials; a main lobe must be much narrower than
    /// the spacing of the partials.
    /// </param>
    public static NoiseProfile Analyze(Spectrogram spectrogram)
    {
        ArgumentNullException.ThrowIfNull(spectrogram);
        var floors = new FloorEstimator(spectrogram);
        double total = 0, noise = 0, weightedHz = 0;
        for (int f = 0; f < spectrogram.FrameCount; f++)
        {
            floors.Estimate(spectrogram.Frame(f));
            double frameTotal = 0, frameNoise = 0, frameWeightedHz = 0;
            for (int k = floors.First; k < floors.Power.Length; k++)
            {
                frameTotal += floors.Power[k];
                frameNoise += floors.Floor[k];
                frameWeightedHz += floors.Floor[k] * k * spectrogram.BinHz;
            }

            if (frameTotal <= 0)
            {
                continue;
            }

            // The floor of a frame that is all noise can come out a little above its power; it is never more.
            double scale = frameNoise > frameTotal ? frameTotal / frameNoise : 1.0;
            total += frameTotal;
            noise += frameNoise * scale;
            weightedHz += frameWeightedHz * scale;
        }

        return total > 0 && noise > 0 ? new NoiseProfile(noise / total, weightedHz / noise) : NoiseProfile.None;
    }

    /// <summary>
    /// The spectrogram with its noise smoothed: bins of partials keep their magnitude, every other bin gets the mean
    /// noise floor around it. Two notes that differ only in the random fine structure of their noise then look the
    /// same, while the level and colour of the noise, and every partial, still show.
    /// </summary>
    /// <remarks>
    /// Only a spectrogram that resolves the partials can be separated: their spacing has to be several bins, or the
    /// median sees the partials themselves and the result depends on their phases. A coarser spectrogram serves time
    /// resolution, not frequency detail: its power is averaged over neighbouring bins and frames instead, which takes
    /// out the fine structure of noise and the phase-dependent interference of unresolved partials alike.
    /// </remarks>
    /// <param name="spectrogram">Spectrogram to smooth.</param>
    /// <param name="fundamentalHz">Fundamental of the note, the spacing of its partials; for an inharmonic note its lowest strong partial.</param>
    public static Spectrogram SmoothNoise(Spectrogram spectrogram, double fundamentalHz)
    {
        ArgumentNullException.ThrowIfNull(spectrogram);
        if (!(spectrogram.BinHz * MinimumBinsBetweenPartials <= fundamentalHz))
        {
            return AveragePower(spectrogram);
        }

        var floors = new FloorEstimator(spectrogram, minimumHalfBins: 8);
        int bins = spectrogram.BinCount;
        int frames = spectrogram.FrameCount;
        var floor = new double[frames * bins];
        for (int f = 0; f < frames; f++)
        {
            floors.Estimate(spectrogram.Frame(f));
            Array.Copy(floors.Floor, 0, floor, f * bins, bins);
        }

        // A median of a few dozen bins still wanders; the floors of the neighbouring frames average that out. Two
        // frames either side blur the onset of the noise by no more than two hops.
        float[] magnitudes = (float[])spectrogram.Magnitudes.Clone();
        for (int f = 0; f < frames; f++)
        {
            int from = Math.Max(0, f - TimeSpanFrames), to = Math.Min(frames - 1, f + TimeSpanFrames);
            ReadOnlySpan<float> frame = spectrogram.Frame(f);
            Span<float> row = magnitudes.AsSpan(f * bins, bins);
            for (int k = floors.First; k < bins; k++)
            {
                double mean = 0;
                for (int g = from; g <= to; g++)
                {
                    mean += floor[(g * bins) + k];
                }

                mean /= to - from + 1;
                if ((double)frame[k] * frame[k] <= PartialRatio * mean)
                {
                    row[k] = (float)Math.Sqrt(mean);
                }
            }
        }

        return new Spectrogram(magnitudes, spectrogram.FrameCount, spectrogram.FftSize, spectrogram.HopSize, spectrogram.SampleRate);
    }

    /// <summary>Power averaged over ±<see cref="AverageSpan"/> frames and as many bins, back as magnitudes.</summary>
    private static Spectrogram AveragePower(Spectrogram spectrogram)
    {
        int bins = spectrogram.BinCount;
        int frames = spectrogram.FrameCount;
        float[] source = spectrogram.Magnitudes;

        // Along frequency first, into power; then along time, back to magnitude.
        var power = new double[source.Length];
        for (int f = 0; f < frames; f++)
        {
            int row = f * bins;
            for (int k = 0; k < bins; k++)
            {
                int from = Math.Max(0, k - AverageSpan), to = Math.Min(bins - 1, k + AverageSpan);
                double sum = 0;
                for (int j = from; j <= to; j++)
                {
                    sum += (double)source[row + j] * source[row + j];
                }

                power[row + k] = sum / (to - from + 1);
            }
        }

        var magnitudes = new float[source.Length];
        for (int f = 0; f < frames; f++)
        {
            int from = Math.Max(0, f - AverageSpan), to = Math.Min(frames - 1, f + AverageSpan);
            for (int k = 0; k < bins; k++)
            {
                double sum = 0;
                for (int g = from; g <= to; g++)
                {
                    sum += power[(g * bins) + k];
                }

                magnitudes[(f * bins) + k] = (float)Math.Sqrt(sum / (to - from + 1));
            }
        }

        return new Spectrogram(magnitudes, frames, spectrogram.FftSize, spectrogram.HopSize, spectrogram.SampleRate);
    }

    /// <summary>Power and mean noise floor of one frame at a time; reuses its buffers.</summary>
    private sealed class FloorEstimator
    {
        private readonly int _half;
        private readonly int _stride;
        private readonly double[] _window;

        public FloorEstimator(Spectrogram spectrogram, int minimumHalfBins = 4)
        {
            int bins = spectrogram.BinCount;
            _half = Math.Max(minimumHalfBins, (int)Math.Round(HalfWindowHz / spectrogram.BinHz));

            // The median changes slowly across frequency: computing it every quarter window and interpolating is enough.
            _stride = Math.Max(1, _half / 2);
            _window = new double[(2 * _half) + 1];
            First = Math.Min(bins, Math.Max(1, (int)Math.Ceiling(LowestHz / spectrogram.BinHz)));
            Power = new double[bins];
            Floor = new double[bins];
        }

        /// <summary>First bin above the lowest frequency that is considered.</summary>
        public int First { get; }

        /// <summary>Power per bin of the last frame.</summary>
        public double[] Power { get; }

        /// <summary>Mean noise floor per bin of the last frame.</summary>
        public double[] Floor { get; }

        public void Estimate(ReadOnlySpan<float> frame)
        {
            int bins = Power.Length;
            for (int k = First; k < bins; k++)
            {
                Power[k] = (double)frame[k] * frame[k];
            }

            // Anchors every stride bins and at the last bin; linear in between.
            int previous = -1;
            double previousFloor = 0;
            int anchor = First;
            while (anchor < bins)
            {
                double floor = MedianAround(anchor, bins) / Math.Log(2.0);
                if (previous < 0)
                {
                    Floor[anchor] = floor;
                }
                else
                {
                    for (int j = previous + 1; j <= anchor; j++)
                    {
                        Floor[j] = previousFloor + ((floor - previousFloor) * (j - previous) / (anchor - previous));
                    }
                }

                previous = anchor;
                previousFloor = floor;
                anchor = anchor == bins - 1 ? bins : Math.Min(anchor + _stride, bins - 1);
            }
        }

        private double MedianAround(int k, int bins)
        {
            int from = Math.Max(First, k - _half), to = Math.Min(bins - 1, k + _half);
            int count = to - from + 1;
            Array.Copy(Power, from, _window, 0, count);
            return Select(_window, count, count / 2);
        }

        /// <summary>The element that would sit at <paramref name="rank"/> after sorting the first <paramref name="count"/> values; reorders them.</summary>
        private static double Select(double[] values, int count, int rank)
        {
            int left = 0, right = count - 1;
            while (left < right)
            {
                double pivot = values[(left + right) / 2];
                int i = left, j = right;
                while (i <= j)
                {
                    while (values[i] < pivot)
                    {
                        i++;
                    }

                    while (values[j] > pivot)
                    {
                        j--;
                    }

                    if (i <= j)
                    {
                        (values[i], values[j]) = (values[j], values[i]);
                        i++;
                        j--;
                    }
                }

                if (rank <= j)
                {
                    right = j;
                }
                else if (rank >= i)
                {
                    left = i;
                }
                else
                {
                    return values[rank];
                }
            }

            return values[rank];
        }
    }
}
