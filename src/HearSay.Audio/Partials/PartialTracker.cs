using HearSay.Dsp;
using HearSay.Envelope;
using HearSay.Spectral;

namespace HearSay.Partials;

/// <summary>One observation of a tracked partial.</summary>
/// <param name="TimeSeconds">Time from the start of the analysed note.</param>
/// <param name="FrequencyHz">Interpolated frequency.</param>
/// <param name="LevelDb">Amplitude in dBFS.</param>
public readonly record struct PartialTrackPoint(double TimeSeconds, double FrequencyHz, double LevelDb);

/// <summary>A partial followed through time, with the features a synthesis search needs from it.</summary>
/// <param name="Points">Observations in chronological order.</param>
/// <param name="Confidence">0..1, from the length and the prominence of the track.</param>
public sealed record PartialTrack(IReadOnlyList<PartialTrackPoint> Points, double Confidence)
{
    /// <summary>Level-weighted mean frequency.</summary>
    public double FrequencyHz { get; init; }

    /// <summary>Mean frequency divided by the fundamental.</summary>
    public double RelativeFrequency { get; init; }

    /// <summary>Highest level of the track.</summary>
    public double PeakLevelDb { get; init; }

    /// <summary>Level at the start of the track.</summary>
    public double InitialLevelDb { get; init; }

    /// <summary>Time of the first observation.</summary>
    public double BirthSeconds { get; init; }

    /// <summary>Time of the last observation.</summary>
    public double DeathSeconds { get; init; }

    /// <summary>Decay after the peak of the track: fast and slow rate in dB per second.</summary>
    public DecayModel Decay { get; init; } = DecayModel.None;

    /// <summary>Frequency change from the start to the end of the track in cents, from a straight-line fit.</summary>
    public double FrequencyDriftCents { get; init; }
}

/// <summary>Configuration of <see cref="PartialTracker"/>.</summary>
public sealed record PartialTrackingOptions
{
    /// <summary>FFT size of the tracking spectrogram.</summary>
    public int FftSize { get; init; } = 4096;

    /// <summary>Hop of the tracking spectrogram in samples.</summary>
    public int HopSize { get; init; } = 1024;

    /// <summary>Largest number of peaks followed per frame.</summary>
    public int MaxPeaksPerFrame { get; init; } = 24;

    /// <summary>Peaks weaker than this, relative to the strongest peak of the whole note, are ignored.</summary>
    public double RangeDb { get; init; } = 60.0;

    /// <summary>Largest frequency step between two frames of one track, in cents.</summary>
    public double MaxStepCents { get; init; } = 50.0;

    /// <summary>Frames a track may be missing before it ends.</summary>
    public int MaxGapFrames { get; init; } = 2;

    /// <summary>Shortest track that is reported, in frames.</summary>
    public int MinimumFrames { get; init; } = 4;

    /// <summary>Largest number of tracks reported, most salient first.</summary>
    public int MaxTracks { get; init; } = 24;
}

/// <summary>
/// Follows spectral peaks from frame to frame by nearest frequency. Tracks are born and die freely, so partials that
/// appear late, vanish early or drift are all represented. Stateless and thread-safe.
/// </summary>
public static class PartialTracker
{
    private sealed class Builder
    {
        public List<PartialTrackPoint> Points { get; } = [];

        public double LastFrequency { get; set; }

        public int Missing { get; set; }

        public double Prominence { get; set; }
    }

    /// <summary>Tracks the partials of a spectrogram.</summary>
    /// <param name="spectrogram">Magnitude spectrogram, amplitude-scaled.</param>
    /// <param name="fundamentalHz">Fundamental used for relative frequencies.</param>
    /// <param name="options">Tracking options.</param>
    /// <returns>Tracks ordered by mean frequency.</returns>
    public static IReadOnlyList<PartialTrack> Track(Spectrogram spectrogram, double fundamentalHz, PartialTrackingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(spectrogram);
        options ??= new PartialTrackingOptions();
        int frames = spectrogram.FrameCount;
        int bins = spectrogram.BinCount;
        if (frames == 0 || !(fundamentalHz > 0))
        {
            return [];
        }

        float globalMax = 0;
        foreach (float magnitude in spectrogram.Magnitudes)
        {
            globalMax = Math.Max(globalMax, magnitude);
        }

        if (globalMax <= 0)
        {
            return [];
        }

        double floorDb = MathUtil.AmplitudeToDb(globalMax) - options.RangeDb;
        double frameSeconds = (double)spectrogram.HopSize / spectrogram.SampleRate;
        var power = new double[bins];
        var detection = new PartialOptions { MaxPartials = options.MaxPeaksPerFrame, RangeDb = options.RangeDb, MinimumProminenceDb = 8.0 };
        var active = new List<Builder>();
        var finished = new List<Builder>();
        var claimed = new List<bool>();

        for (int frame = 0; frame < frames; frame++)
        {
            ReadOnlySpan<float> row = spectrogram.Frame(frame);
            for (int k = 0; k < bins; k++)
            {
                power[k] = (double)row[k] * row[k];
            }

            IReadOnlyList<SpectralPartial> peaks = PartialDetector.Detect(power, spectrogram.BinHz, fundamentalHz, detection);
            claimed.Clear();
            for (int p = 0; p < peaks.Count; p++)
            {
                claimed.Add(peaks[p].LevelDb < floorDb);
            }

            // Strong tracks choose first, so a weak neighbour cannot steal their peak.
            active.Sort(static (a, b) => b.Points[^1].LevelDb.CompareTo(a.Points[^1].LevelDb));
            foreach (Builder track in active)
            {
                int best = -1;
                double bestCents = options.MaxStepCents;
                for (int p = 0; p < peaks.Count; p++)
                {
                    if (claimed[p])
                    {
                        continue;
                    }

                    double cents = Math.Abs(1200.0 * Math.Log2(peaks[p].FrequencyHz / track.LastFrequency));
                    if (cents < bestCents)
                    {
                        bestCents = cents;
                        best = p;
                    }
                }

                if (best >= 0)
                {
                    claimed[best] = true;
                    track.Points.Add(new PartialTrackPoint(frame * frameSeconds, peaks[best].FrequencyHz, peaks[best].LevelDb));
                    track.LastFrequency = peaks[best].FrequencyHz;
                    track.Prominence = Math.Max(track.Prominence, peaks[best].Confidence);
                    track.Missing = 0;
                }
                else
                {
                    track.Missing++;
                }
            }

            for (int i = active.Count - 1; i >= 0; i--)
            {
                if (active[i].Missing > options.MaxGapFrames)
                {
                    finished.Add(active[i]);
                    active.RemoveAt(i);
                }
            }

            for (int p = 0; p < peaks.Count; p++)
            {
                if (!claimed[p])
                {
                    var track = new Builder { LastFrequency = peaks[p].FrequencyHz, Prominence = peaks[p].Confidence };
                    track.Points.Add(new PartialTrackPoint(frame * frameSeconds, peaks[p].FrequencyHz, peaks[p].LevelDb));
                    active.Add(track);
                }
            }
        }

        finished.AddRange(active);
        var tracks = new List<PartialTrack>();
        foreach (Builder builder in finished)
        {
            if (builder.Points.Count >= options.MinimumFrames)
            {
                tracks.Add(Summarize(builder, fundamentalHz, frameSeconds, frames));
            }
        }

        if (tracks.Count > options.MaxTracks)
        {
            tracks.Sort(static (a, b) => (b.PeakLevelDb + (20 * b.Confidence)).CompareTo(a.PeakLevelDb + (20 * a.Confidence)));
            tracks.RemoveRange(options.MaxTracks, tracks.Count - options.MaxTracks);
        }

        tracks.Sort(static (a, b) => a.FrequencyHz.CompareTo(b.FrequencyHz));
        return tracks;
    }

    private static PartialTrack Summarize(Builder builder, double fundamentalHz, double frameSeconds, int totalFrames)
    {
        List<PartialTrackPoint> points = builder.Points;
        double weightSum = 0, frequencySum = 0, peak = double.NegativeInfinity;
        int peakIndex = 0;
        for (int i = 0; i < points.Count; i++)
        {
            double weight = MathUtil.DbToAmplitude(points[i].LevelDb);
            weightSum += weight;
            frequencySum += weight * points[i].FrequencyHz;
            if (points[i].LevelDb > peak)
            {
                peak = points[i].LevelDb;
                peakIndex = i;
            }
        }

        double mean = frequencySum / weightSum;

        // Straight-line fit of the frequency in cents over time, weighted by amplitude.
        double st = 0, sc = 0, stt = 0, stc = 0;
        foreach (PartialTrackPoint point in points)
        {
            double w = MathUtil.DbToAmplitude(point.LevelDb);
            double cents = 1200.0 * Math.Log2(point.FrequencyHz / mean);
            st += w * point.TimeSeconds;
            sc += w * cents;
            stt += w * point.TimeSeconds * point.TimeSeconds;
            stc += w * point.TimeSeconds * cents;
        }

        double variance = (weightSum * stt) - (st * st);
        double slope = variance > 1e-12 ? ((weightSum * stc) - (st * sc)) / variance : 0.0;
        double drift = slope * (points[^1].TimeSeconds - points[0].TimeSeconds);

        var tail = new float[points.Count - peakIndex];
        for (int i = peakIndex; i < points.Count; i++)
        {
            tail[i - peakIndex] = (float)points[i].LevelDb;
        }

        int head = Math.Min(2, points.Count);
        double initial = 0;
        for (int i = 0; i < head; i++)
        {
            initial += points[i].LevelDb;
        }

        double coverage = Math.Min(1.0, points.Count / Math.Max(1.0, 0.25 * totalFrames));
        return new PartialTrack(points, MathUtil.Clamp01(0.5 * (coverage + builder.Prominence)))
        {
            FrequencyHz = mean,
            RelativeFrequency = mean / fundamentalHz,
            PeakLevelDb = peak,
            InitialLevelDb = initial / head,
            BirthSeconds = points[0].TimeSeconds,
            DeathSeconds = points[^1].TimeSeconds,
            Decay = MultiStageDecay.Fit(tail, frameSeconds, peak - 70.0),
            FrequencyDriftCents = drift,
        };
    }
}
