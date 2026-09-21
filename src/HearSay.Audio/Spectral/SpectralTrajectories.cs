using HearSay.Comparison;
using HearSay.Dsp;
using HearSay.Envelope;

namespace HearSay.Spectral;

/// <summary>How the energy of one frequency band develops over a note.</summary>
/// <param name="Band">Band definition.</param>
/// <param name="LevelsDb">Band energy per frame in dB, floored.</param>
/// <param name="InitialDb">Mean level over the first 30 ms.</param>
/// <param name="PeakDb">Highest level.</param>
/// <param name="PeakSeconds">Time of the highest level.</param>
/// <param name="AttackRateDbPerSecond">Mean rise from the first frame to the peak; zero when the band starts at its peak.</param>
/// <param name="Decay">Decay after the peak: fast and slow rate.</param>
/// <param name="LateDb">Mean level over the last third of the note.</param>
public sealed record BandTrajectory(
    FrequencyBand Band,
    float[] LevelsDb,
    double InitialDb,
    double PeakDb,
    double PeakSeconds,
    double AttackRateDbPerSecond,
    DecayModel Decay,
    double LateDb);

/// <summary>
/// Time-varying timbre of a note: one value per analysis frame for every descriptor. Frames that are too quiet to
/// say anything about are marked in <see cref="Active"/>; their descriptor values are carried over from the last
/// active frame so that the arrays never contain gaps.
/// </summary>
public sealed record SpectralTrajectories
{
    /// <summary>Seconds between two frames.</summary>
    public required double FrameSeconds { get; init; }

    /// <summary>Frame energy in dB.</summary>
    public required float[] EnergyDb { get; init; }

    /// <summary>True for frames loud enough to be measured.</summary>
    public required bool[] Active { get; init; }

    /// <summary>Spectral centroid per frame in hertz.</summary>
    public required float[] CentroidHz { get; init; }

    /// <summary>Spectral bandwidth per frame: the power-weighted standard deviation around the centroid, in hertz.</summary>
    public required float[] BandwidthHz { get; init; }

    /// <summary>Spectral slope per frame in dB per octave, from a line through the third-octave band levels.</summary>
    public required float[] SlopeDbPerOctave { get; init; }

    /// <summary>Spectral flatness per frame, 0 (tonal) to 1 (noise-like), over third-octave bands.</summary>
    public required float[] Flatness { get; init; }

    /// <summary>Spectral flux per frame: distance between the normalized band spectra of this frame and the previous one, 0..1.4.</summary>
    public required float[] Flux { get; init; }

    /// <summary>Energy over time in the configured frequency bands.</summary>
    public required IReadOnlyList<BandTrajectory> Bands { get; init; }

    /// <summary>Number of frames.</summary>
    public int FrameCount => EnergyDb.Length;

    /// <summary>Energy-weighted mean of a descriptor over a time span, ignoring inactive frames; NaN when there are none.</summary>
    /// <param name="values">One of the descriptor arrays.</param>
    /// <param name="fromSeconds">Start of the span.</param>
    /// <param name="toSeconds">End of the span.</param>
    public double Mean(float[] values, double fromSeconds, double toSeconds)
    {
        ArgumentNullException.ThrowIfNull(values);
        int from = Math.Max(0, (int)Math.Floor(fromSeconds / FrameSeconds));
        int to = Math.Min(values.Length, (int)Math.Ceiling(toSeconds / FrameSeconds));
        double sum = 0, weights = 0;
        for (int f = from; f < to; f++)
        {
            if (Active[f])
            {
                double w = MathUtil.DbToPower(EnergyDb[f]);
                sum += w * values[f];
                weights += w;
            }
        }

        return weights > 0 ? sum / weights : double.NaN;
    }
}

/// <summary>Distances between the trajectories of two notes, each scaled to roughly 0..1.</summary>
/// <param name="Centroid">Mean centroid difference in octaves.</param>
/// <param name="Bandwidth">Mean bandwidth difference in octaves.</param>
/// <param name="Slope">Mean slope difference, 12 dB per octave counting as 1.</param>
/// <param name="Flatness">Mean flatness difference.</param>
/// <param name="Flux">Mean flux difference.</param>
/// <param name="BandEnergy">Mean band-level difference over time, 20 dB counting as 1.</param>
public sealed record TrajectoryDistance(double Centroid, double Bandwidth, double Slope, double Flatness, double Flux, double BandEnergy)
{
    /// <summary>Weighted combination used by the objective.</summary>
    public double Combined => (0.3 * Centroid) + (0.15 * Bandwidth) + (0.1 * Slope) + (0.1 * Flatness) + (0.05 * Flux) + (0.3 * BandEnergy);
}

/// <summary>Measures spectral descriptors per frame instead of reducing a note to one average. Stateless and thread-safe.</summary>
public static class SpectralTrajectoryAnalyzer
{
    private const double ActiveRangeDb = 50.0;
    private const double BandFloorDb = -80.0;

    /// <summary>Analyses a spectrogram.</summary>
    /// <param name="spectrogram">Magnitude spectrogram.</param>
    /// <param name="bands">Bands for the band-energy trajectories.</param>
    public static SpectralTrajectories Analyze(Spectrogram spectrogram, BandOptions? bands = null)
    {
        ArgumentNullException.ThrowIfNull(spectrogram);
        bands ??= new BandOptions();
        int frames = spectrogram.FrameCount;
        int bins = spectrogram.BinCount;
        double binHz = spectrogram.BinHz;
        double frameSeconds = (double)spectrogram.HopSize / spectrogram.SampleRate;
        LogBandLayout layout = LogBandLayout.Get(spectrogram.SampleRate, spectrogram.FftSize);
        int logBands = layout.BandCount;

        var energy = new float[frames];
        var active = new bool[frames];
        var centroid = new float[frames];
        var bandwidth = new float[frames];
        var slope = new float[frames];
        var flatness = new float[frames];
        var flux = new float[frames];
        var bandLevels = new float[bands.Bands.Count][];
        var bandFirst = new int[bands.Bands.Count];
        var bandLast = new int[bands.Bands.Count];
        for (int b = 0; b < bandLevels.Length; b++)
        {
            bandLevels[b] = new float[frames];
            bandFirst[b] = Math.Clamp((int)Math.Ceiling(bands.Bands[b].StartHz / binHz), 1, bins - 1);
            bandLast[b] = Math.Clamp((int)Math.Ceiling(bands.Bands[b].EndHz / binHz) - 1, 0, bins - 1);
        }

        var bandPower = new double[logBands];
        var shape = new double[logBands];
        var previousShape = new double[logBands];
        bool hasPrevious = false;
        double maxEnergy = double.NegativeInfinity;

        for (int f = 0; f < frames; f++)
        {
            ReadOnlySpan<float> row = spectrogram.Frame(f);
            double total = 0, weighted = 0;
            for (int k = 1; k < bins; k++)
            {
                double p = (double)row[k] * row[k];
                total += p;
                weighted += p * k;
            }

            energy[f] = (float)MathUtil.PowerToDb(total);
            maxEnergy = Math.Max(maxEnergy, energy[f]);
            for (int b = 0; b < bandLevels.Length; b++)
            {
                double sum = 0;
                for (int k = bandFirst[b]; k <= bandLast[b]; k++)
                {
                    sum += (double)row[k] * row[k];
                }

                bandLevels[b][f] = (float)Math.Max(BandFloorDb, MathUtil.PowerToDb(sum));
            }

            if (total <= 1e-14)
            {
                continue;
            }

            double centre = weighted / total;
            double spread = 0;
            for (int k = 1; k < bins; k++)
            {
                double d = k - centre;
                spread += (double)row[k] * row[k] * d * d;
            }

            centroid[f] = (float)(centre * binHz);
            bandwidth[f] = (float)(Math.Sqrt(spread / total) * binHz);

            layout.Apply(row, bandPower);
            double peak = 0;
            for (int b = 0; b < logBands; b++)
            {
                peak = Math.Max(peak, bandPower[b]);
            }

            double floor = peak * 1e-7;
            double sx = 0, sy = 0, sxx = 0, sxy = 0, logSum = 0, linearSum = 0, norm = 0;
            for (int b = 0; b < logBands; b++)
            {
                double p = Math.Max(bandPower[b], floor);
                double y = 10.0 * Math.Log10(p);
                double x = layout.LogCenter[b];
                sx += x;
                sy += y;
                sxx += x * x;
                sxy += x * y;
                logSum += Math.Log(p);
                linearSum += p;
                shape[b] = Math.Sqrt(bandPower[b]);
                norm += bandPower[b];
            }

            double variance = (logBands * sxx) - (sx * sx);
            slope[f] = variance > 0 ? (float)(((logBands * sxy) - (sx * sy)) / variance) : 0f;
            flatness[f] = (float)(Math.Exp(logSum / logBands) / (linearSum / logBands));

            norm = Math.Sqrt(norm);
            double difference = 0;
            for (int b = 0; b < logBands; b++)
            {
                shape[b] = norm > 0 ? shape[b] / norm : 0;
                double d = shape[b] - previousShape[b];
                difference += d * d;
            }

            flux[f] = hasPrevious ? (float)Math.Sqrt(difference) : 0f;
            (shape, previousShape) = (previousShape, shape);
            hasPrevious = true;
        }

        // Quiet frames carry the last measured value so that consumers never see gaps.
        double threshold = maxEnergy - ActiveRangeDb;
        int lastActive = -1;
        for (int f = 0; f < frames; f++)
        {
            active[f] = energy[f] >= threshold && centroid[f] > 0;
            if (active[f])
            {
                lastActive = f;
            }
            else if (lastActive >= 0)
            {
                centroid[f] = centroid[lastActive];
                bandwidth[f] = bandwidth[lastActive];
                slope[f] = slope[lastActive];
                flatness[f] = flatness[lastActive];
                flux[f] = 0;
            }
        }

        var trajectories = new List<BandTrajectory>(bandLevels.Length);
        for (int b = 0; b < bandLevels.Length; b++)
        {
            trajectories.Add(Summarize(bands.Bands[b], bandLevels[b], frameSeconds));
        }

        return new SpectralTrajectories
        {
            FrameSeconds = frameSeconds,
            EnergyDb = energy,
            Active = active,
            CentroidHz = centroid,
            BandwidthHz = bandwidth,
            SlopeDbPerOctave = slope,
            Flatness = flatness,
            Flux = flux,
            Bands = trajectories,
        };
    }

    /// <summary>Compares the trajectories of two notes frame by frame, weighted by the energy of the target.</summary>
    /// <param name="target">Trajectories of the target.</param>
    /// <param name="candidate">Trajectories of the candidate, with the same frame interval.</param>
    public static TrajectoryDistance Compare(SpectralTrajectories target, SpectralTrajectories candidate)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(candidate);
        int frames = Math.Min(target.FrameCount, candidate.FrameCount);
        double centroid = 0, bandwidth = 0, slope = 0, flatness = 0, flux = 0, weights = 0;
        for (int f = 0; f < frames; f++)
        {
            if (!target.Active[f] || !candidate.Active[f])
            {
                continue;
            }

            // Amplitude weighting: loud frames matter more, but a long quiet tail still counts.
            double w = Math.Pow(10.0, target.EnergyDb[f] / 40.0);
            centroid += w * Math.Min(3.0, Math.Abs(Math.Log2(candidate.CentroidHz[f] / target.CentroidHz[f])));
            bandwidth += w * Math.Min(3.0, Math.Abs(Math.Log2(Math.Max(1f, candidate.BandwidthHz[f]) / Math.Max(1f, target.BandwidthHz[f]))));
            slope += w * Math.Min(1.0, Math.Abs(candidate.SlopeDbPerOctave[f] - target.SlopeDbPerOctave[f]) / 12.0);
            flatness += w * Math.Abs(candidate.Flatness[f] - target.Flatness[f]);
            flux += w * Math.Abs(candidate.Flux[f] - target.Flux[f]);
            weights += w;
        }

        if (weights > 0)
        {
            centroid /= weights;
            bandwidth /= weights;
            slope /= weights;
            flatness /= weights;
            flux /= weights;
        }
        else
        {
            centroid = bandwidth = slope = flatness = flux = 1.0;
        }

        // Band energies are compared over the longer of the two notes; a missing frame is silence.
        int longest = Math.Max(target.FrameCount, candidate.FrameCount);
        int bandCount = Math.Min(target.Bands.Count, candidate.Bands.Count);
        double bandSum = 0;
        long bandPoints = 0;
        for (int b = 0; b < bandCount; b++)
        {
            float[] t = target.Bands[b].LevelsDb;
            float[] c = candidate.Bands[b].LevelsDb;
            for (int f = 0; f < longest; f++)
            {
                double lt = f < t.Length ? Math.Max(t[f], -70f) : -70.0;
                double lc = f < c.Length ? Math.Max(c[f], -70f) : -70.0;
                if (lt <= -70.0 && lc <= -70.0)
                {
                    continue;
                }

                bandSum += Math.Min(1.0, Math.Abs(lt - lc) / 20.0);
                bandPoints++;
            }
        }

        return new TrajectoryDistance(centroid, bandwidth, slope, flatness, flux, bandPoints > 0 ? bandSum / bandPoints : 0.0);
    }

    private static BandTrajectory Summarize(FrequencyBand band, float[] levels, double frameSeconds)
    {
        int frames = levels.Length;
        if (frames == 0)
        {
            return new BandTrajectory(band, levels, BandFloorDb, BandFloorDb, 0, 0, DecayModel.None, BandFloorDb);
        }

        int head = Math.Clamp((int)Math.Round(0.03 / frameSeconds), 1, frames);
        double initial = 0;
        for (int f = 0; f < head; f++)
        {
            initial += levels[f];
        }

        initial /= head;
        int peakIndex = 0;
        for (int f = 1; f < frames; f++)
        {
            if (levels[f] > levels[peakIndex])
            {
                peakIndex = f;
            }
        }

        double peak = levels[peakIndex];
        double attackRate = peakIndex > 0 ? (peak - levels[0]) / (peakIndex * frameSeconds) : 0.0;
        int lateFrom = frames - Math.Max(1, frames / 3);
        double late = 0;
        for (int f = lateFrom; f < frames; f++)
        {
            late += levels[f];
        }

        late /= frames - lateFrom;
        DecayModel decay = MultiStageDecay.Fit(levels.AsSpan(peakIndex), frameSeconds, Math.Max(BandFloorDb + 1, peak - 60.0));
        return new BandTrajectory(band, levels, initial, peak, peakIndex * frameSeconds, attackRate, decay, late);
    }
}

/// <summary>
/// Long-term average spectrum in third-octave bands. Averaging over the whole note makes it robust against the
/// starting phase of oscillators, the phase of detune beating and the phase of a vibrato.
/// </summary>
/// <param name="CenterHz">Band centres in hertz.</param>
/// <param name="LevelsDb">Band levels in dB relative to the total power of the note.</param>
public sealed record LongTermAverageSpectrum(double[] CenterHz, double[] LevelsDb)
{
    private const double FloorDb = -70.0;

    /// <summary>Computes the long-term average spectrum of an averaged power spectrum.</summary>
    /// <param name="spectrum">Time-averaged power spectrum.</param>
    public static LongTermAverageSpectrum From(AveragePowerSpectrum spectrum)
    {
        ArgumentNullException.ThrowIfNull(spectrum);
        LogBandLayout layout = LogBandLayout.Get(spectrum.SampleRate, spectrum.FftSize);
        var power = new double[layout.BandCount];
        layout.Apply(spectrum.Power, power);
        double total = 0;
        foreach (double p in power)
        {
            total += p;
        }

        var levels = new double[power.Length];
        for (int b = 0; b < levels.Length; b++)
        {
            levels[b] = total > 0 ? Math.Max(FloorDb, MathUtil.PowerToDb(power[b] / total)) : FloorDb;
        }

        return new LongTermAverageSpectrum(layout.CenterHz, levels);
    }

    /// <summary>Mean absolute level difference over the bands that matter in either spectrum, 20 dB counting as 1.</summary>
    /// <param name="other">Spectrum to compare with; must use the same band layout.</param>
    public double DistanceTo(LongTermAverageSpectrum other)
    {
        ArgumentNullException.ThrowIfNull(other);
        int bands = Math.Min(LevelsDb.Length, other.LevelsDb.Length);
        double sum = 0, weights = 0;
        for (int b = 0; b < bands; b++)
        {
            // A band counts by how loud it is in either spectrum: residue near the floor of an otherwise empty band
            // is inaudible next to the rest of the note and must not dominate the distance.
            double louder = Math.Max(LevelsDb[b], other.LevelsDb[b]);
            double weight = Math.Pow(10.0, louder / 20.0);
            sum += weight * Math.Min(1.5, Math.Abs(LevelsDb[b] - other.LevelsDb[b]) / 20.0);
            weights += weight;
        }

        return weights > 0 ? sum / weights : 0.0;
    }
}
