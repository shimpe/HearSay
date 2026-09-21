namespace HearSay.OperatorMatrix.Optimization;

/// <summary>
/// Covariance Matrix Adaptation Evolution Strategy over the unit cube, in ask-and-tell form so that the caller
/// evaluates a whole population at once, in parallel if it likes. All randomness comes from the seed: the same seed
/// and the same losses give the same run. Not thread-safe; one instance belongs to one search.
/// </summary>
/// <remarks>
/// Only smooth parameters belong here. Structural choices such as edges, waveforms, modes and coarse ratios are
/// fixed by the basin the optimizer works in.
/// </remarks>
public sealed class CmaEs
{
    private readonly Random _random;
    private readonly int _n;
    private readonly int _mu;
    private readonly double[] _weights;
    private readonly double _muEff;
    private readonly double _cSigma;
    private readonly double _dSigma;
    private readonly double _cc;
    private readonly double _c1;
    private readonly double _cMu;
    private readonly double _chiN;
    private readonly double[] _mean;
    private readonly double[] _pSigma;
    private readonly double[] _pc;
    private readonly double[,] _c;
    private double[,] _b;
    private double[] _d;
    private double[][] _z = [];
    private double[][] _y = [];
    private int _eigenAge;

    /// <summary>Creates an optimizer.</summary>
    /// <param name="start">Start point in the unit cube; its length is the number of dimensions.</param>
    /// <param name="sigma">Initial step size as a fraction of the cube, typically 0.1 to 0.3.</param>
    /// <param name="seed">Random seed.</param>
    /// <param name="populationSize">Population size; the usual <c>4 + 3 ln n</c> when null.</param>
    public CmaEs(ReadOnlySpan<double> start, double sigma, int seed, int? populationSize = null)
    {
        _n = start.Length;
        ArgumentOutOfRangeException.ThrowIfLessThan(_n, 1);
        _random = new Random(seed);
        Sigma = sigma;
        PopulationSize = Math.Max(4, populationSize ?? 4 + (int)Math.Floor(3.0 * Math.Log(_n)));
        _mu = PopulationSize / 2;
        _weights = new double[_mu];
        double sum = 0;
        for (int i = 0; i < _mu; i++)
        {
            _weights[i] = Math.Log(_mu + 0.5) - Math.Log(i + 1.0);
            sum += _weights[i];
        }

        double squares = 0;
        for (int i = 0; i < _mu; i++)
        {
            _weights[i] /= sum;
            squares += _weights[i] * _weights[i];
        }

        _muEff = 1.0 / squares;
        _cSigma = (_muEff + 2.0) / (_n + _muEff + 5.0);
        _dSigma = 1.0 + (2.0 * Math.Max(0.0, Math.Sqrt((_muEff - 1.0) / (_n + 1.0)) - 1.0)) + _cSigma;
        _cc = (4.0 + (_muEff / _n)) / (_n + 4.0 + (2.0 * _muEff / _n));
        _c1 = 2.0 / (((_n + 1.3) * (_n + 1.3)) + _muEff);
        _cMu = Math.Min(1.0 - _c1, 2.0 * (_muEff - 1.0) / (((_n + 2.0) * (_n + 2.0)) + _muEff));
        _chiN = Math.Sqrt(_n) * (1.0 - (1.0 / (4.0 * _n)) + (1.0 / (21.0 * _n * _n)));

        _mean = start.ToArray();
        _pSigma = new double[_n];
        _pc = new double[_n];
        _c = new double[_n, _n];
        _b = new double[_n, _n];
        _d = new double[_n];
        for (int i = 0; i < _n; i++)
        {
            _c[i, i] = 1.0;
            _b[i, i] = 1.0;
            _d[i] = 1.0;
        }
    }

    /// <summary>Number of candidates per generation.</summary>
    public int PopulationSize { get; }

    /// <summary>Current step size.</summary>
    public double Sigma { get; private set; }

    /// <summary>Generations completed.</summary>
    public int Generation { get; private set; }

    /// <summary>Current distribution mean, inside the unit cube.</summary>
    public ReadOnlySpan<double> Mean => _mean;

    /// <summary>True when the distribution has collapsed and further generations cannot find anything new.</summary>
    public bool HasConverged => Sigma * MaxAxis() < 1e-4;

    /// <summary>Samples a population. Every candidate lies inside the unit cube.</summary>
    public double[][] Ask()
    {
        _z = new double[PopulationSize][];
        _y = new double[PopulationSize][];
        var population = new double[PopulationSize][];
        for (int k = 0; k < PopulationSize; k++)
        {
            var z = new double[_n];
            for (int i = 0; i < _n; i++)
            {
                z[i] = Gaussian();
            }

            var y = new double[_n];
            for (int i = 0; i < _n; i++)
            {
                double value = 0;
                for (int j = 0; j < _n; j++)
                {
                    value += _b[i, j] * _d[j] * z[j];
                }

                y[i] = value;
            }

            var x = new double[_n];
            for (int i = 0; i < _n; i++)
            {
                // Candidates are reflected into the cube; the update below uses the reflected step, which keeps the
                // distribution from drifting outside the bounds.
                double value = Reflect(_mean[i] + (Sigma * y[i]));
                x[i] = value;
                y[i] = (value - _mean[i]) / Sigma;
            }

            _z[k] = z;
            _y[k] = y;
            population[k] = x;
        }

        return population;
    }

    /// <summary>Reports the losses of the population returned by the last <see cref="Ask"/>.</summary>
    /// <param name="losses">One loss per candidate, in the order of the population; lower is better.</param>
    public void Tell(ReadOnlySpan<double> losses)
    {
        if (losses.Length != PopulationSize || _y.Length != PopulationSize)
        {
            throw new InvalidOperationException("Tell must follow Ask with one loss per candidate.");
        }

        var order = new int[PopulationSize];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        double[] keys = losses.ToArray();
        for (int i = 0; i < keys.Length; i++)
        {
            if (double.IsNaN(keys[i]))
            {
                keys[i] = double.PositiveInfinity;
            }
        }

        // Stable ordering keeps runs reproducible when losses tie.
        Array.Sort(order, (a, b) =>
        {
            int result = keys[a].CompareTo(keys[b]);
            return result != 0 ? result : a.CompareTo(b);
        });

        var step = new double[_n];
        for (int r = 0; r < _mu; r++)
        {
            double[] y = _y[order[r]];
            for (int i = 0; i < _n; i++)
            {
                step[i] += _weights[r] * y[i];
            }
        }

        for (int i = 0; i < _n; i++)
        {
            _mean[i] = Math.Clamp(_mean[i] + (Sigma * step[i]), 0.0, 1.0);
        }

        // C^(-1/2) * step = B * D^-1 * B^T * step
        var temp = new double[_n];
        for (int j = 0; j < _n; j++)
        {
            double value = 0;
            for (int i = 0; i < _n; i++)
            {
                value += _b[i, j] * step[i];
            }

            temp[j] = value / _d[j];
        }

        double pSigmaNorm = 0;
        double factorSigma = Math.Sqrt(_cSigma * (2.0 - _cSigma) * _muEff);
        for (int i = 0; i < _n; i++)
        {
            double value = 0;
            for (int j = 0; j < _n; j++)
            {
                value += _b[i, j] * temp[j];
            }

            _pSigma[i] = ((1.0 - _cSigma) * _pSigma[i]) + (factorSigma * value);
            pSigmaNorm += _pSigma[i] * _pSigma[i];
        }

        pSigmaNorm = Math.Sqrt(pSigmaNorm);
        Generation++;
        double expected = Math.Sqrt(1.0 - Math.Pow(1.0 - _cSigma, 2.0 * Generation));
        bool stall = pSigmaNorm / expected / _chiN >= 1.4 + (2.0 / (_n + 1.0));
        double factorC = stall ? 0.0 : Math.Sqrt(_cc * (2.0 - _cc) * _muEff);
        for (int i = 0; i < _n; i++)
        {
            _pc[i] = ((1.0 - _cc) * _pc[i]) + (factorC * step[i]);
        }

        double correction = stall ? _c1 * _cc * (2.0 - _cc) : 0.0;
        for (int i = 0; i < _n; i++)
        {
            for (int j = 0; j <= i; j++)
            {
                double rankMu = 0;
                for (int r = 0; r < _mu; r++)
                {
                    double[] y = _y[order[r]];
                    rankMu += _weights[r] * y[i] * y[j];
                }

                double value = ((1.0 - _c1 - _cMu + correction) * _c[i, j]) + (_c1 * _pc[i] * _pc[j]) + (_cMu * rankMu);
                _c[i, j] = value;
                _c[j, i] = value;
            }
        }

        Sigma *= Math.Exp(Math.Min(1.0, (_cSigma / _dSigma) * ((pSigmaNorm / _chiN) - 1.0)));
        Sigma = Math.Min(Sigma, 0.5);

        _eigenAge++;
        if (_eigenAge >= Math.Max(1, (int)(1.0 / (10.0 * _n * (_c1 + _cMu)))))
        {
            _eigenAge = 0;
            (double[] values, double[,] vectors) = SymmetricEigen.Decompose(_c);
            for (int i = 0; i < _n; i++)
            {
                _d[i] = Math.Sqrt(Math.Max(values[i], 1e-20));
            }

            _b = vectors;
        }
    }

    private double MaxAxis()
    {
        double max = 0;
        foreach (double d in _d)
        {
            max = Math.Max(max, d);
        }

        return max;
    }

    private static double Reflect(double value)
    {
        // Mirror at both faces of the cube until the value lies inside.
        value = Math.Abs(value) % 2.0;
        return value > 1.0 ? 2.0 - value : value;
    }

    private double Gaussian()
    {
        double u1 = 1.0 - _random.NextDouble();
        double u2 = _random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}

/// <summary>Eigen decomposition of small symmetric matrices with the cyclic Jacobi method.</summary>
internal static class SymmetricEigen
{
    /// <summary>Returns eigenvalues and eigenvectors (as columns) of a symmetric matrix.</summary>
    public static (double[] Values, double[,] Vectors) Decompose(double[,] matrix)
    {
        int n = matrix.GetLength(0);
        var a = (double[,])matrix.Clone();
        var v = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            v[i, i] = 1.0;
        }

        for (int sweep = 0; sweep < 60; sweep++)
        {
            double off = 0;
            for (int p = 0; p < n; p++)
            {
                for (int q = p + 1; q < n; q++)
                {
                    off += a[p, q] * a[p, q];
                }
            }

            if (off < 1e-22)
            {
                break;
            }

            for (int p = 0; p < n; p++)
            {
                for (int q = p + 1; q < n; q++)
                {
                    if (Math.Abs(a[p, q]) < 1e-300)
                    {
                        continue;
                    }

                    double theta = (a[q, q] - a[p, p]) / (2.0 * a[p, q]);
                    double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt((theta * theta) + 1.0));
                    if (theta == 0.0)
                    {
                        t = 1.0;
                    }

                    double c = 1.0 / Math.Sqrt((t * t) + 1.0);
                    double s = t * c;
                    for (int k = 0; k < n; k++)
                    {
                        double akp = a[k, p], akq = a[k, q];
                        a[k, p] = (c * akp) - (s * akq);
                        a[k, q] = (s * akp) + (c * akq);
                    }

                    for (int k = 0; k < n; k++)
                    {
                        double apk = a[p, k], aqk = a[q, k];
                        a[p, k] = (c * apk) - (s * aqk);
                        a[q, k] = (s * apk) + (c * aqk);
                    }

                    for (int k = 0; k < n; k++)
                    {
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = (c * vkp) - (s * vkq);
                        v[k, q] = (s * vkp) + (c * vkq);
                    }
                }
            }
        }

        var values = new double[n];
        for (int i = 0; i < n; i++)
        {
            values[i] = a[i, i];
        }

        return (values, v);
    }
}
