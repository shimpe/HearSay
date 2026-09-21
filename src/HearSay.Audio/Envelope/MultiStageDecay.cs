namespace HearSay.Envelope;

/// <summary>
/// Piecewise description of a decaying level curve: a fast first stage followed by a slow second stage, as in a
/// struck or plucked sound where the brightness dies quickly and the body rings on. A single ADSR decay cannot
/// represent that.
/// </summary>
/// <param name="FastRateDbPerSecond">Slope of the first stage; negative for a decay.</param>
/// <param name="SlowRateDbPerSecond">Slope of the second stage. Equal to the first when the decay has one stage.</param>
/// <param name="BreakSeconds">Time, from the start of the fitted span, at which the second stage takes over.</param>
/// <param name="BreakLevelDb">Level at the break, relative to the level at the start of the fitted span.</param>
/// <param name="IsTwoStage">True when two stages describe the curve clearly better than one.</param>
/// <param name="FitErrorDb">Root-mean-square error of the fit in dB.</param>
/// <param name="Confidence">0..1, from the number of points and the fit error.</param>
public sealed record DecayModel(
    double FastRateDbPerSecond,
    double SlowRateDbPerSecond,
    double BreakSeconds,
    double BreakLevelDb,
    bool IsTwoStage,
    double FitErrorDb,
    double Confidence)
{
    /// <summary>Returned when there is nothing to fit.</summary>
    public static DecayModel None { get; } = new(0, 0, 0, 0, false, 0, 0);
}

/// <summary>Fits one or two straight lines, in decibels over time, to a level curve. Stateless and thread-safe.</summary>
public static class MultiStageDecay
{
    /// <summary>Fits a level curve sampled at a constant interval.</summary>
    /// <param name="levelsDb">Levels in dB.</param>
    /// <param name="stepSeconds">Time between two levels.</param>
    /// <param name="floorDb">Points below this level are ignored; the fit stops at the first long run below it.</param>
    public static DecayModel Fit(ReadOnlySpan<float> levelsDb, double stepSeconds, double floorDb = -70.0)
    {
        int n = levelsDb.Length;
        while (n > 0 && levelsDb[n - 1] < floorDb)
        {
            n--;
        }

        if (n < 6 || !(stepSeconds > 0))
        {
            return DecayModel.None;
        }

        // Prefix sums for least-squares lines over any index range.
        var sx = new double[n + 1];
        var sy = new double[n + 1];
        var sxx = new double[n + 1];
        var sxy = new double[n + 1];
        var syy = new double[n + 1];
        for (int i = 0; i < n; i++)
        {
            double x = i * stepSeconds;
            double y = Math.Max(levelsDb[i], floorDb);
            sx[i + 1] = sx[i] + x;
            sy[i + 1] = sy[i] + y;
            sxx[i + 1] = sxx[i] + (x * x);
            sxy[i + 1] = sxy[i] + (x * y);
            syy[i + 1] = syy[i] + (y * y);
        }

        (double slope, double intercept, double sse) whole = Line(0, n);
        int minimum = Math.Max(3, n / 20);
        double bestSse = double.PositiveInfinity;
        int bestBreak = -1;
        (double slope, double intercept, double sse) bestFirst = default, bestSecond = default;
        for (int k = minimum; k <= n - minimum; k++)
        {
            (double slope, double intercept, double sse) first = Line(0, k);
            (double slope, double intercept, double sse) second = Line(k, n);
            double sse = first.sse + second.sse;
            if (sse < bestSse)
            {
                bestSse = sse;
                bestBreak = k;
                bestFirst = first;
                bestSecond = second;
            }
        }

        double start = Math.Max(levelsDb[0], floorDb);
        double confidence = Math.Clamp((n - 5) / 20.0, 0.0, 1.0);
        bool twoStage = bestBreak > 0
            && bestSse < 0.5 * whole.sse
            && whole.sse / n > 0.04
            && bestFirst.slope < bestSecond.slope
            && Math.Abs(bestFirst.slope) > 1.8 * Math.Abs(bestSecond.slope);
        if (!twoStage)
        {
            double rms = Math.Sqrt(whole.sse / n);
            return new DecayModel(whole.slope, whole.slope, 0, 0, false, rms, confidence * Math.Clamp(1.0 - (rms / 12.0), 0.0, 1.0));
        }

        // The break is where the two lines meet; fall back to the split index when they are nearly parallel.
        double breakSeconds = bestBreak * stepSeconds;
        double denominator = bestFirst.slope - bestSecond.slope;
        if (Math.Abs(denominator) > 1e-9)
        {
            double crossing = (bestSecond.intercept - bestFirst.intercept) / denominator;
            if (crossing > 0 && crossing < (n - 1) * stepSeconds)
            {
                breakSeconds = crossing;
            }
        }

        double breakLevel = bestFirst.intercept + (bestFirst.slope * breakSeconds) - start;
        double error = Math.Sqrt(bestSse / n);
        return new DecayModel(bestFirst.slope, bestSecond.slope, breakSeconds, breakLevel, true, error, confidence * Math.Clamp(1.0 - (error / 12.0), 0.0, 1.0));

        (double Slope, double Intercept, double Sse) Line(int from, int to)
        {
            int count = to - from;
            double x = sx[to] - sx[from], y = sy[to] - sy[from];
            double xx = sxx[to] - sxx[from], xy = sxy[to] - sxy[from], yy = syy[to] - syy[from];
            double variance = (count * xx) - (x * x);
            if (count < 2 || variance <= 1e-12)
            {
                return (0, count > 0 ? y / count : 0, 0);
            }

            double slope = ((count * xy) - (x * y)) / variance;
            double intercept = (y - (slope * x)) / count;
            double sse = yy - (2 * slope * xy) - (2 * intercept * y) + (slope * slope * xx) + (2 * slope * intercept * x) + (count * intercept * intercept);
            return (slope, intercept, Math.Max(0, sse));
        }
    }
}
