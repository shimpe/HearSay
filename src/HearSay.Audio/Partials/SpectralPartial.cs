using HearSay.Dsp;

namespace HearSay.Partials;

/// <summary>One spectral peak of a note, harmonic or not.</summary>
/// <param name="FrequencyHz">Interpolated peak frequency.</param>
/// <param name="RelativeFrequency">Frequency divided by the fundamental, for example 2.73 for an inharmonic bell partial.</param>
/// <param name="LevelDb">Peak amplitude in dBFS of the analysed (loudness-matched) signal.</param>
/// <param name="Confidence">0..1, how clearly the peak stands out of its surroundings.</param>
public sealed record SpectralPartial(double FrequencyHz, double RelativeFrequency, double LevelDb, double Confidence);

/// <summary>Configuration of <see cref="PartialDetector"/>.</summary>
public sealed record PartialOptions
{
    /// <summary>FFT size of the long-term spectrum the partial cloud is read from.</summary>
    public int FftSize { get; init; } = 8192;

    /// <summary>Largest number of partials kept, strongest first.</summary>
    public int MaxPartials { get; init; } = 40;

    /// <summary>Partials weaker than this, relative to the strongest one, are ignored.</summary>
    public double RangeDb { get; init; } = 60.0;

    /// <summary>A peak has to rise this far above the median of its neighbourhood.</summary>
    public double MinimumProminenceDb { get; init; } = 10.0;

    /// <summary>Lowest frequency considered.</summary>
    public double MinimumHz { get; init; } = 20.0;
}

/// <summary>
/// Finds arbitrary spectral peaks. Nothing here assumes partials at whole multiples of the fundamental, so a partial
/// structure such as 1.00, 2.73, 4.41, 6.82 is preserved as it is. Stateless and thread-safe.
/// </summary>
public static class PartialDetector
{
    /// <summary>Detects the peaks of a power spectrum.</summary>
    /// <param name="power">Power per bin, amplitude-scaled: a sine of amplitude A has a peak of A squared.</param>
    /// <param name="binHz">Width of a bin in hertz.</param>
    /// <param name="fundamentalHz">Fundamental used for <see cref="SpectralPartial.RelativeFrequency"/>.</param>
    /// <param name="options">Detection options.</param>
    /// <returns>Partials ordered by frequency.</returns>
    public static IReadOnlyList<SpectralPartial> Detect(ReadOnlySpan<double> power, double binHz, double fundamentalHz, PartialOptions? options = null)
    {
        options ??= new PartialOptions();
        int bins = power.Length;
        if (bins < 8 || !(fundamentalHz > 0))
        {
            return [];
        }

        var db = new double[bins];
        double max = double.NegativeInfinity;
        for (int k = 0; k < bins; k++)
        {
            db[k] = MathUtil.PowerToDb(power[k]);
            max = Math.Max(max, db[k]);
        }

        if (double.IsNegativeInfinity(max))
        {
            return [];
        }

        double threshold = max - options.RangeDb;
        int first = Math.Max(2, (int)Math.Ceiling(options.MinimumHz / binHz));
        int neighbourhood = Math.Max(8, (int)Math.Round(0.35 * fundamentalHz / binHz));
        var scratch = new double[(2 * neighbourhood) + 1];
        var found = new List<SpectralPartial>();
        for (int k = first; k < bins - 2; k++)
        {
            double level = db[k];
            if (level < threshold || level <= db[k - 1] || level < db[k + 1] || level <= db[k - 2] || level < db[k + 2])
            {
                continue;
            }

            int from = Math.Max(0, k - neighbourhood);
            int to = Math.Min(bins - 1, k + neighbourhood);
            int count = 0;
            for (int j = from; j <= to; j++)
            {
                scratch[count++] = db[j];
            }

            double median = MathUtil.MedianInPlace(scratch.AsSpan(0, count));
            double prominence = level - median;
            if (prominence < options.MinimumProminenceDb)
            {
                continue;
            }

            double offset = MathUtil.ParabolicOffset(db[k - 1], level, db[k + 1]);
            double peakDb = level - (0.25 * (db[k - 1] - db[k + 1]) * offset);
            double frequency = (k + offset) * binHz;
            found.Add(new SpectralPartial(frequency, frequency / fundamentalHz, peakDb, MathUtil.Clamp01((prominence - options.MinimumProminenceDb) / 30.0)));
        }

        if (found.Count > options.MaxPartials)
        {
            found.Sort(static (a, b) => b.LevelDb.CompareTo(a.LevelDb));
            found.RemoveRange(options.MaxPartials, found.Count - options.MaxPartials);
        }

        found.Sort(static (a, b) => a.FrequencyHz.CompareTo(b.FrequencyHz));
        return found;
    }

    /// <summary>
    /// Level-weighted mean deviation of the partials from the nearest whole multiple of the fundamental, in cents,
    /// scaled so that 0 is perfectly harmonic and 1 is as inharmonic as possible (a quarter tone off on average).
    /// </summary>
    /// <param name="partials">Detected partials.</param>
    public static double Inharmonicity(IReadOnlyList<SpectralPartial> partials)
    {
        ArgumentNullException.ThrowIfNull(partials);
        double weighted = 0, weights = 0;
        foreach (SpectralPartial partial in partials)
        {
            double nearest = Math.Max(1.0, Math.Round(partial.RelativeFrequency));
            double cents = Math.Abs(1200.0 * Math.Log2(partial.RelativeFrequency / nearest));
            double weight = MathUtil.DbToAmplitude(partial.LevelDb);
            weighted += weight * Math.Min(cents, 50.0);
            weights += weight;
        }

        return weights > 0 ? weighted / weights / 50.0 : 0.0;
    }
}

/// <summary>Result of comparing two partial sets.</summary>
/// <param name="Distance">0 when the sets are identical, 1 when nothing matches.</param>
/// <param name="FrequencyDistance">Share of the distance caused by matched partials being out of tune.</param>
/// <param name="LevelDistance">Share of the distance caused by matched partials having the wrong level.</param>
/// <param name="MissingShare">Salience-weighted share of target partials without a partner in the candidate.</param>
/// <param name="ExtraShare">Salience-weighted share of candidate partials without a partner in the target.</param>
/// <param name="MatchedCount">Number of matched pairs.</param>
public sealed record PartialSetComparison(
    double Distance,
    double FrequencyDistance,
    double LevelDistance,
    double MissingShare,
    double ExtraShare,
    int MatchedCount)
{
    /// <summary>
    /// True when the spectral structure is right and mostly levels differ: the partials line up but their amplitudes
    /// do not. The opposite case, missing or extra partials, points at a wrong frequency structure.
    /// </summary>
    public bool StructureMatches => MissingShare + ExtraShare < 0.2;
}

/// <summary>
/// Distance between two partial sets by assignment of partials rather than by FFT bin. It separates "right
/// frequencies, wrong amplitudes" from "wrong spectral structure". Stateless and thread-safe.
/// </summary>
public static class PartialSetDistance
{
    /// <summary>Compares two partial sets that are ordered by frequency.</summary>
    /// <param name="target">Partials of the target.</param>
    /// <param name="candidate">Partials of the candidate.</param>
    /// <param name="maxCents">Largest frequency difference of a matched pair.</param>
    /// <param name="levelScaleDb">Level difference that counts as a complete mismatch.</param>
    /// <param name="rangeDb">Partials this far below the strongest one have no salience.</param>
    public static PartialSetComparison Compare(
        IReadOnlyList<SpectralPartial> target,
        IReadOnlyList<SpectralPartial> candidate,
        double maxCents = 40.0,
        double levelScaleDb = 30.0,
        double rangeDb = 60.0)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(candidate);
        int n = target.Count, m = candidate.Count;
        if (n == 0 && m == 0)
        {
            return new PartialSetComparison(0, 0, 0, 0, 0, 0);
        }

        double[] wt = Salience(target, rangeDb);
        double[] wc = Salience(candidate, rangeDb);

        // Both lists are ordered by frequency, so the best assignment is monotone: a sequence alignment.
        var cost = new double[n + 1, m + 1];
        var move = new byte[n + 1, m + 1];
        for (int i = 1; i <= n; i++)
        {
            cost[i, 0] = cost[i - 1, 0] + wt[i - 1];
            move[i, 0] = 1;
        }

        for (int j = 1; j <= m; j++)
        {
            cost[0, j] = cost[0, j - 1] + wc[j - 1];
            move[0, j] = 2;
        }

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                double skipTarget = cost[i - 1, j] + wt[i - 1];
                double skipCandidate = cost[i, j - 1] + wc[j - 1];
                double best = skipTarget;
                byte step = 1;
                if (skipCandidate < best)
                {
                    best = skipCandidate;
                    step = 2;
                }

                double cents = Math.Abs(1200.0 * Math.Log2(candidate[j - 1].FrequencyHz / target[i - 1].FrequencyHz));
                if (cents <= maxCents)
                {
                    double pair = PairCost(cents, target[i - 1].LevelDb - candidate[j - 1].LevelDb, maxCents, levelScaleDb, out _, out _);
                    double matched = cost[i - 1, j - 1] + (pair * (wt[i - 1] + wc[j - 1]));
                    if (matched < best)
                    {
                        best = matched;
                        step = 0;
                    }
                }

                cost[i, j] = best;
                move[i, j] = step;
            }
        }

        double total = 0;
        foreach (double w in wt)
        {
            total += w;
        }

        foreach (double w in wc)
        {
            total += w;
        }

        if (total <= 0)
        {
            return new PartialSetComparison(0, 0, 0, 0, 0, 0);
        }

        double frequency = 0, level = 0, missing = 0, extra = 0;
        int matches = 0;
        for (int i = n, j = m; i > 0 || j > 0;)
        {
            switch (move[i, j])
            {
                case 0:
                    double cents = Math.Abs(1200.0 * Math.Log2(candidate[j - 1].FrequencyHz / target[i - 1].FrequencyHz));
                    PairCost(cents, target[i - 1].LevelDb - candidate[j - 1].LevelDb, maxCents, levelScaleDb, out double f, out double l);
                    frequency += f * (wt[i - 1] + wc[j - 1]);
                    level += l * (wt[i - 1] + wc[j - 1]);
                    matches++;
                    i--;
                    j--;
                    break;
                case 1:
                    missing += wt[i - 1];
                    i--;
                    break;
                default:
                    extra += wc[j - 1];
                    j--;
                    break;
            }
        }

        return new PartialSetComparison(cost[n, m] / total, frequency / total, level / total, missing / total, extra / total, matches);
    }

    private static double PairCost(double cents, double levelDifferenceDb, double maxCents, double levelScaleDb, out double frequencyPart, out double levelPart)
    {
        frequencyPart = 0.4 * (cents / maxCents);
        levelPart = 0.6 * Math.Min(1.0, Math.Abs(levelDifferenceDb) / levelScaleDb);
        return frequencyPart + levelPart;
    }

    private static double[] Salience(IReadOnlyList<SpectralPartial> partials, double rangeDb)
    {
        double max = double.NegativeInfinity;
        foreach (SpectralPartial partial in partials)
        {
            max = Math.Max(max, partial.LevelDb);
        }

        var weights = new double[partials.Count];
        for (int i = 0; i < weights.Length; i++)
        {
            weights[i] = Math.Max(0.0, 1.0 - ((max - partials[i].LevelDb) / rangeDb));
        }

        return weights;
    }
}
