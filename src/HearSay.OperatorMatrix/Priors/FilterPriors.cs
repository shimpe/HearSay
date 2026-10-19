using HearSay.Analysis;
using HearSay.OperatorMatrix.Parameters;
using HearSay.Partials;
using HearSay.Synthesis.Patches;

namespace HearSay.OperatorMatrix.Priors;

/// <summary>A maximum of the spectral envelope of a target, relative to a sawtooth source.</summary>
/// <param name="Hz">Frequency.</param>
/// <param name="LevelDb">Level of the envelope there: smoothed for <see cref="FilterPriors.Peaks"/>, the fitted resonance for <see cref="FilterPriors.FormantPeaks"/>.</param>
/// <param name="Q">Centre over the width at 3 dB below the peak: 0.5..20 for <see cref="FilterPriors.Peaks"/>, 0.5..50 for <see cref="FilterPriors.FormantPeaks"/>.</param>
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

    // How far below the strongest formant a weaker one may be.
    private const double MaxFormantFallDb = 36.0;

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
    /// The formants of a target: the lowest <paramref name="max"/> maxima of its harmonic envelope that are at most
    /// 36 dB below the strongest, strongest first, each with its own width. Unlike <see cref="Peaks"/> the envelope is
    /// not smoothed, so formants a few harmonics apart stay apart: a peak is a harmonic stronger than the one below it
    /// and at least as strong as the one above, that stands 3 dB or more above the higher of the two valleys around it.
    /// The lowest harmonic can be one when the next is 3 dB lower, for a high voice whose F1 is at its fundamental.
    /// <para>
    /// A formant is often narrower than the spacing of the harmonics, so its width cannot be read off the harmonics
    /// directly. A resonance |H(f)|² = G² / (1 + ((f² - F²) / (f·B))²), the shape of a two-pole band-pass and of each
    /// peak of the formant filter, is fitted through the peak harmonic and its two neighbours; 1 / |H|² is linear in 1,
    /// f² and 1 / f², so three harmonics give the centre F, the bandwidth B and the height G between the harmonics at
    /// once. Where that fit has no such resonance (a neighbour as strong, another formant close by), the centre is the
    /// vertex of a parabola through the three levels and the width is that of a resonance at it through the
    /// neighbours. Q = F / B, 0.5..50; the level is G.
    /// </para>
    /// </summary>
    /// <param name="analysis">Target analysis.</param>
    /// <param name="max">Largest number of peaks.</param>
    public static IReadOnlyList<SpectralPeak> FormantPeaks(TargetAnalysis analysis, int max = 3)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        double f0 = analysis.FundamentalHz;
        (double Hz, double Db)[] h = [.. Harmonics(analysis).Select(x => (x.N * f0, x.Db))];
        var peaks = new List<SpectralPeak>();
        if (h.Length < 3 || !(f0 > 0))
        {
            return peaks;
        }

        for (int i = 0; i < h.Length - 1; i++)
        {
            double db = h[i].Db;
            bool edge = i == 0;
            if (edge ? h[i].Hz > 1.5 * f0 || db < h[1].Db + 3.0 : db <= h[i - 1].Db || db < h[i + 1].Db)
            {
                continue;
            }

            // Prominence: how far the harmonics fall on both sides before one is stronger than this peak again.
            double leftMin = db, rightMin = db;
            for (int k = i - 1; k >= 0 && h[k].Db <= db; k--)
            {
                leftMin = Math.Min(leftMin, h[k].Db);
            }

            for (int k = i + 1; k < h.Length && h[k].Db <= db; k++)
            {
                rightMin = Math.Min(rightMin, h[k].Db);
            }

            if (db - (edge ? rightMin : Math.Max(leftMin, rightMin)) < 3.0)
            {
                continue;
            }

            peaks.Add(edge ? EdgeResonance(h[0], h[1]) : Resonance(h[i - 1], h[i], h[i + 1]));
        }

        // The lowest ones are the first formants: F4 is often as strong as F2 or F3. Ripples far below the strongest
        // peak are not formants (the weakest third formant of the formant table is 35 dB below its first).
        double strongest = peaks.Count > 0 ? peaks.Max(static p => p.LevelDb) : 0.0;
        return [.. peaks.Where(p => p.LevelDb >= strongest - MaxFormantFallDb).OrderBy(static p => p.Hz).Take(max).OrderByDescending(static p => p.LevelDb)];
    }

    /// <summary>The resonance through a peak harmonic and its two neighbours (see <see cref="FormantPeaks"/>).</summary>
    private static SpectralPeak Resonance((double Hz, double Db) below, (double Hz, double Db) peak, (double Hz, double Db) above)
    {
        // In kilohertz, with the levels relative to the peak harmonic: y = 1 / |H|² = a + b·u + c / u with u = f².
        (double Hz, double Db)[] points = [below, peak, above];
        var m = new double[3, 3];
        var y = new double[3];
        for (int k = 0; k < 3; k++)
        {
            double u = Math.Pow(points[k].Hz / 1000.0, 2);
            m[k, 0] = 1.0;
            m[k, 1] = u;
            m[k, 2] = 1.0 / u;
            y[k] = Math.Pow(10.0, (peak.Db - points[k].Db) / 10.0);
        }

        if (Solve(m, y) is { } abc && abc[1] > 0.0 && abc[2] > 0.0)
        {
            double centre2 = Math.Sqrt(abc[2] / abc[1]);
            double width2 = (abc[0] / abc[1]) + (2.0 * centre2);
            double centre = 1000.0 * Math.Sqrt(centre2);
            if (width2 > 0.0 && centre > below.Hz && centre < above.Hz)
            {
                // G² = 1 / (b·B²), relative to the peak harmonic: the resonance is at least as high, and at most 20 dB
                // higher, between the harmonics.
                double width = 1000.0 * Math.Sqrt(width2);
                double gainDb = Math.Clamp(-10.0 * Math.Log10(abc[1] * width2), 0.0, 20.0);
                return new SpectralPeak(centre, peak.Db + gainDb, Math.Clamp(centre / width, OperatorFilter.MinQ, OperatorFilter.MaxFormantQ));
            }
        }

        // The vertex of the parabola through the three levels, and the width of a resonance there through the
        // neighbours, from how far they fall below the peak harmonic.
        double d1 = peak.Hz - below.Hz, d2 = peak.Hz - above.Hz;
        double denominator = (d1 * (peak.Db - above.Db)) - (d2 * (peak.Db - below.Db));
        double hz = denominator != 0.0
            ? peak.Hz - (0.5 * ((d1 * d1 * (peak.Db - above.Db)) - (d2 * d2 * (peak.Db - below.Db))) / denominator)
            : peak.Hz;
        hz = Math.Clamp(hz, (below.Hz + peak.Hz) / 2.0, (peak.Hz + above.Hz) / 2.0);
        return new SpectralPeak(hz, peak.Db, WidthQ(hz, peak.Db, [below, above]));
    }

    /// <summary>A peak at the lowest harmonic: there, with the width of a resonance through the harmonic above.</summary>
    private static SpectralPeak EdgeResonance((double Hz, double Db) peak, (double Hz, double Db) above) =>
        new(peak.Hz, peak.Db, WidthQ(peak.Hz, peak.Db, [above]));

    /// <summary>
    /// The Q of a resonance at a centre whose neighbouring harmonics fall as far below its level as they do: for a fall
    /// D, x² = 10^(D/10) - 1 and B = |f² - F²| / (f·x); the geometric mean over the neighbours, 2 when none falls.
    /// </summary>
    private static double WidthQ(double centre, double db, (double Hz, double Db)[] neighbours)
    {
        double logSum = 0.0;
        int count = 0;
        foreach ((double hz, double level) in neighbours)
        {
            double x = Math.Sqrt(Math.Max(0.0, Math.Pow(10.0, (db - level) / 10.0) - 1.0));
            double width = x > 1e-6 ? Math.Abs((hz * hz) - (centre * centre)) / (hz * x) : 0.0;
            if (width > 0.0)
            {
                logSum += Math.Log(centre / width);
                count++;
            }
        }

        return count > 0 ? Math.Clamp(Math.Exp(logSum / count), OperatorFilter.MinQ, OperatorFilter.MaxFormantQ) : 2.0;
    }

    /// <summary>Solves a 3 x 3 linear system by Cramer's rule; null when it is singular.</summary>
    private static double[]? Solve(double[,] m, double[] y)
    {
        static double Det(double[,] a) =>
            (a[0, 0] * ((a[1, 1] * a[2, 2]) - (a[1, 2] * a[2, 1])))
            - (a[0, 1] * ((a[1, 0] * a[2, 2]) - (a[1, 2] * a[2, 0])))
            + (a[0, 2] * ((a[1, 0] * a[2, 1]) - (a[1, 1] * a[2, 0])));
        double det = Det(m);
        if (!(Math.Abs(det) > 1e-300))
        {
            return null;
        }

        var solution = new double[3];
        for (int c = 0; c < 3; c++)
        {
            var replaced = (double[,])m.Clone();
            for (int r = 0; r < 3; r++)
            {
                replaced[r, c] = y[r];
            }

            solution[c] = Det(replaced) / det;
        }

        return solution;
    }

    /// <summary>
    /// A formant filter from the target's formants (<see cref="FormantPeaks"/>): F1, F2 and F3 from three, F1 and F2
    /// with the default F3 from two, F1 alone with the default F3 from one; null without peaks.
    /// </summary>
    /// <param name="analysis">Target analysis.</param>
    public static OperatorFilter? Formant(TargetAnalysis analysis) => Formant(FormantPeaks(analysis, 3));

    /// <summary>
    /// A formant filter on given peaks: F1 on the lowest, the others in rising frequency as ratios to it (1.05..16) with
    /// levels relative to it (0..1); every peak has the Q of its width (2..50), and a higher peak whose Q is that of F1
    /// shares it. Every formant filter has F3, as the search tunes F1 to F3 on all of them: with fewer than three peaks
    /// it is the search's default (<see cref="PatchParameterSchema.DefaultFormant3Ratio"/> at the level floor, with the
    /// shared Q), and a single peak leaves F2 off. F4 and F5 stay off. Null without peaks.
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
        FormantPeak Higher(SpectralPeak peak, double minimumLevel) => new(
            Math.Clamp(peak.Hz / f1.Hz, 1.05, 16.0),
            Math.Clamp(Math.Pow(10.0, (peak.LevelDb - f1.LevelDb) / 20.0), minimumLevel, 1.0),
            FormantQ(peak) is var own && own != q ? own : 0.0);
        OperatorFilter filter = rising.Length switch
        {
            1 => OperatorFilter.FormantAt(hz, 2.0, 0.0, q),
            2 => OperatorFilter.Formants(hz, q, Higher(rising[1], 0.0)),
            _ => OperatorFilter.Formants(hz, q, Higher(rising[1], 0.0), Higher(rising[2], PatchParameterSchema.MinFormant3Level)),
        };
        return rising.Length == 3
            ? filter
            : filter with { Formant3Ratio = PatchParameterSchema.DefaultFormant3Ratio(filter), Formant3Level = PatchParameterSchema.MinFormant3Level, Formant3Q = 0.0 };
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
