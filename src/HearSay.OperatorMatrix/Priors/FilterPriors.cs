using HearSay.Analysis;
using HearSay.Partials;
using HearSay.Synthesis.Patches;

namespace HearSay.OperatorMatrix.Priors;

/// <summary>A maximum of the spectral envelope of a target, relative to a sawtooth source.</summary>
/// <param name="Hz">Frequency.</param>
/// <param name="LevelDb">Level of the smoothed envelope there.</param>
/// <param name="Q">Centre over the width at 3 dB below the peak, 0.5..20.</param>
public sealed record SpectralPeak(double Hz, double LevelDb, double Q);

/// <summary>A low-pass reading of a target.</summary>
/// <param name="CutoffRatio">Cutoff as a multiple of the fundamental.</param>
/// <param name="Poles">2, or 4 when the spectrum falls 18 dB per octave or more above the cutoff.</param>
public sealed record LowPassPrior(double CutoffRatio, int Poles);

/// <summary>A filter sweep read from the brightness of a target.</summary>
/// <param name="Octaves">Depth.</param>
/// <param name="Envelope">Levels 0..1.</param>
/// <param name="Rising">True when the brightness rises and stays (an opening filter), false when it starts bright and closes.</param>
public sealed record SweepPrior(double Octaves, OperatorEnvelope Envelope, bool Rising);

/// <summary>
/// Starting values for operator filters, read from a target analysis. The harmonic partials are compared with a
/// sawtooth (amplitudes falling as 1/n): what is left is the filter a rich carrier would need. Formants are the
/// maxima of a smooth envelope through the harmonic levels (not LPC, which pulls its poles onto the harmonics when the
/// fundamental is high), so their accuracy is about half the fundamental; the search refines them.
/// </summary>
public static class FilterPriors
{
    private const double SmoothingOctaves = 1.0 / 6.0;
    private const double GridOctaves = 1.0 / 24.0;

    // The power-weighted centroid of a 1/n source moves about a third as many octaves as a low-pass cutoff does
    // (0.92 octaves for a 4-pole cutoff moving from 16 to 2 times the fundamental).
    private const double CutoffOctavesPerCentroidOctave = 3.0;

    /// <summary>The strongest maxima of the spectral envelope, strongest first.</summary>
    /// <param name="analysis">Target analysis.</param>
    /// <param name="max">Largest number of peaks.</param>
    public static IReadOnlyList<SpectralPeak> Peaks(TargetAnalysis analysis, int max)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        List<(double Octave, double Db)> residual = Residual(analysis);
        var peaks = new List<SpectralPeak>();
        if (residual.Count < 4)
        {
            return peaks;
        }

        double from = residual[0].Octave, to = residual[^1].Octave;
        int count = (int)((to - from) / GridOctaves) + 1;
        var curve = new double[count];
        for (int g = 0; g < count; g++)
        {
            double x = from + (g * GridOctaves), weighted = 0, total = 0;
            foreach ((double octave, double db) in residual)
            {
                double w = Math.Exp(-0.5 * Math.Pow((x - octave) / SmoothingOctaves, 2));
                weighted += w * db;
                total += w;
            }

            curve[g] = weighted / total;
        }

        for (int g = 1; g < count - 1; g++)
        {
            if (curve[g] < curve[g - 1] || curve[g] < curve[g + 1])
            {
                continue;
            }

            // Prominence: how far the curve falls on both sides before it rises above this peak again.
            int left = g, right = g;
            double leftMin = curve[g], rightMin = curve[g];
            while (left > 0 && curve[left - 1] <= curve[g])
            {
                left--;
                leftMin = Math.Min(leftMin, curve[left]);
            }

            while (right < count - 1 && curve[right + 1] <= curve[g])
            {
                right++;
                rightMin = Math.Min(rightMin, curve[right]);
            }

            if (curve[g] - Math.Max(leftMin, rightMin) < 3.0)
            {
                continue;
            }

            int lo = g, hi = g;
            while (lo > 0 && curve[lo] > curve[g] - 3.0)
            {
                lo--;
            }

            while (hi < count - 1 && curve[hi] > curve[g] - 3.0)
            {
                hi++;
            }

            double hz = Math.Pow(2.0, from + (g * GridOctaves));
            double width = Math.Pow(2.0, from + (hi * GridOctaves)) - Math.Pow(2.0, from + (lo * GridOctaves));
            peaks.Add(new SpectralPeak(hz, curve[g], Math.Clamp(hz / Math.Max(width, 1.0), OperatorFilter.MinQ, OperatorFilter.MaxQ)));
        }

        return [.. peaks.OrderByDescending(static p => p.LevelDb).Take(max)];
    }

    /// <summary>
    /// A formant filter from the three strongest peaks (F1, F2 and F3), two when there are only two, or one; null
    /// without peaks.
    /// </summary>
    /// <param name="analysis">Target analysis.</param>
    public static OperatorFilter? Formant(TargetAnalysis analysis) => Formant(Peaks(analysis, 3));

    /// <summary>
    /// A formant filter on given peaks: F1 on the lowest, the others in rising frequency as ratios to it (1.05..16) with
    /// levels relative to it (0..1); every peak has the Q of its width (2..50), and a higher peak whose Q is that of F1
    /// shares it. A single peak gives F1 alone; F4 and F5 stay off. Null without peaks.
    /// </summary>
    /// <param name="peaks">One to three peaks, in any order.</param>
    public static OperatorFilter? Formant(IReadOnlyList<SpectralPeak> peaks)
    {
        ArgumentNullException.ThrowIfNull(peaks);
        if (peaks.Count == 0)
        {
            return null;
        }

        SpectralPeak[] rising = [.. peaks.OrderBy(static p => p.Hz).Take(3)];
        SpectralPeak f1 = rising[0];
        double q = FormantQ(f1);
        double hz = Math.Clamp(f1.Hz, 80.0, 8000.0);
        if (rising.Length == 1)
        {
            return OperatorFilter.FormantAt(hz, 2.0, 0.0, q);
        }

        FormantPeak Higher(SpectralPeak peak) => new(
            Math.Clamp(peak.Hz / f1.Hz, 1.05, 16.0),
            Math.Clamp(Math.Pow(10.0, (peak.LevelDb - f1.LevelDb) / 20.0), 0.0, 1.0),
            FormantQ(peak) is var own && own != q ? own : 0.0);
        return OperatorFilter.Formants(hz, q, [.. rising.Skip(1).Select(Higher)]);
    }

    private static double FormantQ(SpectralPeak peak) => Math.Clamp(peak.Q, 2.0, OperatorFilter.MaxFormantQ);

    /// <summary>
    /// Where the harmonics fall 6 dB below their level at the bottom of the spectrum, relative to a sawtooth; null when
    /// they do not.
    /// </summary>
    /// <param name="analysis">Target analysis.</param>
    public static LowPassPrior? LowPass(TargetAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        List<(int N, double Db)> harmonics = Harmonics(analysis);
        if (harmonics.Count < 6)
        {
            return null;
        }

        double reference = harmonics.Where(static h => h.N <= 3).Select(static h => h.Db).DefaultIfEmpty(harmonics[0].Db).Average();
        for (int i = 0; i < harmonics.Count - 1; i++)
        {
            if (harmonics[i].N <= 3 || harmonics[i].Db >= reference - 6.0 || harmonics[i + 1].Db >= reference - 6.0)
            {
                continue;
            }

            int cutoff = harmonics[i].N;
            (int N, double Db)? octaveUp = harmonics.Where(h => h.N >= 2 * cutoff).Select(static h => ((int, double)?)h).FirstOrDefault();
            double slope = octaveUp is { } up ? (harmonics[i].Db - up.Db) / Math.Log2((double)up.N / cutoff) : 12.0;
            return new LowPassPrior(Math.Clamp(cutoff, 0.25, 64.0), slope >= 18.0 ? 4 : 2);
        }

        return null;
    }

    /// <summary>A high-pass cutoff ratio when the fundamental is 9 dB or more below the strongest of harmonics 2..4; null otherwise.</summary>
    /// <param name="analysis">Target analysis.</param>
    public static double? HighPassRatio(TargetAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        List<(int N, double Db)> harmonics = Harmonics(analysis);
        (int N, double Db)? first = harmonics.Where(static h => h.N == 1).Select(static h => ((int, double)?)h).FirstOrDefault();
        (int N, double Db)? strongest = harmonics.Where(static h => h.N is >= 2 and <= 4).OrderByDescending(static h => h.Db).Select(static h => ((int, double)?)h).FirstOrDefault();
        if (strongest is not { } s || (first is { } f && f.Db > s.Db - 9.0))
        {
            return null;
        }

        return Math.Clamp(0.7 * s.N, 0.25, 64.0);
    }

    /// <summary>
    /// A filter sweep when the spectral centroid moves by half an octave or more during the note; null otherwise. The
    /// depth is that of the cutoff, about three times the movement of the centroid.
    /// </summary>
    /// <param name="analysis">Target analysis.</param>
    public static SweepPrior? Sweep(TargetAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        var trajectories = analysis.Trajectories;
        double frame = trajectories.FrameSeconds;
        int end = Math.Min(trajectories.CentroidHz.Length, (int)(analysis.NoteOffSeconds / frame));
        var frames = Enumerable.Range(0, Math.Max(0, end)).Where(i => trajectories.Active[i] && trajectories.CentroidHz[i] > 0).ToList();
        if (frames.Count < 8)
        {
            return null;
        }

        double Median(IEnumerable<int> indices)
        {
            double[] values = [.. indices.Select(i => (double)trajectories.CentroidHz[i]).Order()];
            return values[values.Length / 2];
        }

        int early = Math.Max(1, frames.Count * 15 / 100);
        double start = Median(frames.Take(early));
        double late = Median(frames.Skip(frames.Count / 2));
        int peakFrame = frames.Take(Math.Max(1, frames.Count * 40 / 100)).MaxBy(i => trajectories.CentroidHz[i]);
        double peak = trajectories.CentroidHz[peakFrame];

        if (peak / late >= Math.Sqrt(2.0))
        {
            // Bright first, then closing to the sustained brightness.
            double octaves = Math.Clamp(CutoffOctavesPerCentroidOctave * Math.Log2(peak / late), 0.25, 4.0);
            double attack = Math.Max(0.002, peakFrame * frame);
            double decay = Math.Max(0.05, ((frames[frames.Count / 2] - peakFrame) * frame) / 2.0);
            return new SweepPrior(octaves, Envelope(attack, decay, 0.0), Rising: false);
        }

        if (late / start >= Math.Sqrt(2.0))
        {
            // Dark first, opening to the sustained brightness.
            double octaves = Math.Clamp(CutoffOctavesPerCentroidOctave * Math.Log2(late / start), 0.25, 4.0);
            int open = frames.First(i => trajectories.CentroidHz[i] >= 0.9 * late);
            return new SweepPrior(octaves, Envelope(Math.Max(0.002, open * frame), 0.01, 1.0), Rising: true);
        }

        return null;
    }

    private static OperatorEnvelope Envelope(double attack, double decay, double sustain) =>
        new([new EnvelopePoint(0, 0), new EnvelopePoint(attack, 1), new EnvelopePoint(decay, sustain), new EnvelopePoint(0.1, sustain)], SustainPoint: 2, ReleasePoint: 3);

    /// <summary>Harmonic number and level in dB relative to a sawtooth, lowest first.</summary>
    private static List<(int N, double Db)> Harmonics(TargetAnalysis analysis)
    {
        var harmonics = new Dictionary<int, double>();
        foreach (SpectralPartial partial in analysis.Partials)
        {
            int n = (int)Math.Round(partial.RelativeFrequency);
            if (n >= 1 && Math.Abs(partial.RelativeFrequency - n) <= 0.06)
            {
                double db = partial.LevelDb + (20.0 * Math.Log10(n));
                harmonics[n] = harmonics.TryGetValue(n, out double old) ? Math.Max(old, db) : db;
            }
        }

        return [.. harmonics.OrderBy(static h => h.Key).Select(static h => (h.Key, h.Value))];
    }

    private static List<(double Octave, double Db)> Residual(TargetAnalysis analysis) =>
        [.. Harmonics(analysis).Select(h => (Math.Log2(h.N * analysis.FundamentalHz), h.Db))];
}
