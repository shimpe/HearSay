using HearSay.Partials;

namespace HearSay.OperatorMatrix.Priors;

/// <summary>
/// Predicts the line strengths of small sine phase-modulation structures from Bessel functions and fits their
/// modulation indices to measured partials. Which lines a structure produces does not tell different structures on
/// the same grid of lines apart; how strong they are does. In a three-stack whose second modulator drives the first
/// at index 2.5, the first modulator's own frequency nearly vanishes (J0(2.5) is close to zero), so the carrier's
/// strongest sidebands sit at the combination frequencies, and only a model of the strengths recognizes the stack.
/// </summary>
/// <remarks>
/// The model is static: a fixed index per modulation, fitted to the average spectrum of the note, whose indices in
/// truth follow envelopes. It ranks hypotheses and gives them starting amounts; the search does the rest.
/// </remarks>
public static class FmSpectrumModel
{
    private const int Quadrature = 64;
    private const int MaxOrder = 4;
    private const double MatchCents = 35.0;
    private const double RangeDb = 40.0;
    private static readonly double[] IndexGrid = [0.3, 0.6, 1.0, 1.5, 2.0, 2.5, 3.0, 4.0, 5.0];

    /// <summary>Bessel function of the first kind, J_n(x), for integer n, from its integral representation.</summary>
    /// <param name="n">Order.</param>
    /// <param name="x">Argument.</param>
    public static double BesselJ(int n, double x)
    {
        // (1/pi) * integral over [0, pi] of cos(n t - x sin t) dt; the integrand is smooth and periodic, so the
        // trapezoid rule converges geometrically.
        double sum = 0;
        for (int i = 0; i <= Quadrature; i++)
        {
            double t = Math.PI * i / Quadrature;
            double w = i == 0 || i == Quadrature ? 0.5 : 1.0;
            sum += w * Math.Cos((n * t) - (x * Math.Sin(t)));
        }

        return sum / Quadrature;
    }

    /// <summary>
    /// Lines of a carrier at <paramref name="carrier"/> phase-modulated by sinusoidal components, each given as a
    /// frequency and an index. Frequencies are ratios; lines below zero fold over with the opposite sign.
    /// </summary>
    /// <param name="carrier">Carrier ratio.</param>
    /// <param name="components">Modulating components: frequency ratio and index.</param>
    /// <returns>Line frequencies and signed amplitudes; coinciding lines are summed.</returns>
    public static Dictionary<double, double> CarrierLines(double carrier, IReadOnlyList<(double Frequency, double Index)> components)
    {
        var lines = new Dictionary<double, double>();
        var bessel = new double[components.Count][];
        for (int c = 0; c < components.Count; c++)
        {
            bessel[c] = new double[(2 * MaxOrder) + 1];
            for (int n = -MaxOrder; n <= MaxOrder; n++)
            {
                bessel[c][n + MaxOrder] = BesselJ(n, components[c].Index);
            }
        }

        void Walk(int component, double frequency, double amplitude, int budget)
        {
            if (component == components.Count)
            {
                double sign = frequency < 0 ? -1.0 : 1.0;
                double key = Math.Round(Math.Abs(frequency), 3);
                lines[key] = lines.GetValueOrDefault(key) + (sign * amplitude);
                return;
            }

            for (int n = -budget; n <= budget; n++)
            {
                double a = amplitude * bessel[component][n + MaxOrder];
                if (Math.Abs(a) > 1e-4)
                {
                    Walk(component + 1, frequency + (n * components[component].Frequency), a, budget - Math.Abs(n));
                }
            }
        }

        Walk(0, carrier, 1.0, MaxOrder);
        return lines;
    }

    /// <summary>Components of a modulator at <paramref name="modulator"/>, itself modulated by <paramref name="second"/> at <paramref name="secondIndex"/> when given.</summary>
    public static List<(double Frequency, double Amplitude)> Modulator(double modulator, double? second = null, double secondIndex = 0.0)
    {
        if (second is not double m2 || secondIndex <= 0)
        {
            return [(modulator, 1.0)];
        }

        var result = new List<(double Frequency, double Amplitude)>();
        for (int j = -2; j <= 2; j++)
        {
            double a = BesselJ(j, secondIndex);
            if (Math.Abs(a) > 0.02)
            {
                // A component below zero is the same sine at the positive frequency with the opposite sign.
                double f = modulator + (j * m2);
                result.Add(f < 0 ? (-f, -a) : (f, a));
            }
        }

        return result;
    }

    /// <summary>
    /// Fit error of predicted lines against measured partials: the mean level difference in dB (20 dB counting as 1)
    /// over the measured partials within <see cref="RangeDb"/> of the strongest, plus predicted lines within that range
    /// that the target lacks. Both sides are normalized to their strongest line.
    /// </summary>
    public static double Error(Dictionary<double, double> lines, IReadOnlyList<SpectralPartial> partials, double fundamentalHz)
    {
        if (partials.Count == 0 || lines.Count == 0)
        {
            return 1.0;
        }

        double maxLine = lines.Values.Max(static a => Math.Abs(a));
        double maxPartial = partials.Max(static p => p.LevelDb);
        if (maxLine <= 0)
        {
            return 1.0;
        }

        var predicted = lines.Where(l => Math.Abs(l.Value) > 0).Select(l => (Ratio: l.Key, Db: 20.0 * Math.Log10(Math.Abs(l.Value) / maxLine))).Where(static l => l.Ratio > 0.05 && l.Db > -60).ToList();
        double cost = 0;
        int count = 0;
        foreach (SpectralPartial partial in partials)
        {
            double measured = partial.LevelDb - maxPartial;
            if (measured < -RangeDb)
            {
                continue;
            }

            double best = -60;
            foreach ((double ratio, double db) in predicted)
            {
                if (Math.Abs(1200.0 * Math.Log2(partial.RelativeFrequency / ratio)) < MatchCents)
                {
                    best = Math.Max(best, db);
                }
            }

            cost += Math.Min(30.0, Math.Abs(measured - Math.Max(best, -RangeDb))) / 20.0;
            count++;
        }

        foreach ((double ratio, double db) in predicted)
        {
            if (db > -RangeDb + 10 && !partials.Any(p => Math.Abs(1200.0 * Math.Log2(p.RelativeFrequency / ratio)) < MatchCents))
            {
                cost += Math.Min(30.0, db + RangeDb) / 20.0;
                count++;
            }
        }

        return count > 0 ? cost / count : 1.0;
    }

    /// <summary>
    /// Best indices of a three-stack <c>carrier &lt;- modulator &lt;- second</c> on the index grid, and their error;
    /// with <paramref name="parallel"/> the second modulator acts on the carrier instead.
    /// </summary>
    public static (double Index, double SecondIndex, double Error) FitStack(double carrier, double modulator, double second, bool parallel, IReadOnlyList<SpectralPartial> partials, double fundamentalHz)
    {
        (double, double, double) best = (1.0, 1.0, double.MaxValue);
        foreach (double i1 in IndexGrid)
        {
            foreach (double i2 in IndexGrid)
            {
                List<(double Frequency, double Index)> components = parallel
                    ? [(modulator, i1), (second, i2)]
                    : [.. Modulator(modulator, second, i2).Select(c => (c.Frequency, i1 * c.Amplitude))];
                double error = Error(CarrierLines(carrier, components), partials, fundamentalHz);
                if (error < best.Item3)
                {
                    best = (i1, i2, error);
                }
            }
        }

        return best;
    }
}
