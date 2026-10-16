namespace HearSay.OperatorMatrix.Priors;

/// <summary>
/// Where to draw a set of sounds in the unit square: classical multidimensional scaling of their fingerprint
/// distances, nodes sized by how many sounds they stand for and pushed apart until none overlap, each joined to its two
/// nearest neighbours. Deterministic.
/// </summary>
public sealed class AtlasLayout
{
    private const double Gap = 0.006;
    private const int OverlapPasses = 200;

    private AtlasLayout(IReadOnlyList<(double X, double Y)> positions, IReadOnlyList<double> radii, IReadOnlyList<(int From, int To)> edges)
    {
        Positions = positions;
        Radii = radii;
        Edges = edges;
    }

    /// <summary>Centre of every node, in the unit square.</summary>
    public IReadOnlyList<(double X, double Y)> Positions { get; }

    /// <summary>Radius of every node, in the units of <see cref="Positions"/>.</summary>
    public IReadOnlyList<double> Radii { get; }

    /// <summary>Pairs of nodes to join, lower index first, each pair once.</summary>
    public IReadOnlyList<(int From, int To)> Edges { get; }

    /// <summary>The layout of a set of sounds.</summary>
    /// <param name="prints">Fingerprints, one per node.</param>
    /// <param name="sizes">Number of sounds each node stands for, at least 1.</param>
    public static AtlasLayout Compute(IReadOnlyList<byte[]> prints, IReadOnlyList<int> sizes)
    {
        ArgumentNullException.ThrowIfNull(prints);
        ArgumentNullException.ThrowIfNull(sizes);
        if (prints.Count != sizes.Count)
        {
            throw new ArgumentException("One size per fingerprint.", nameof(sizes));
        }

        int n = prints.Count;
        if (n == 0)
        {
            return new AtlasLayout([], [], []);
        }

        var d2 = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                d2[i, j] = d2[j, i] = SpectralFingerprint.Distance(prints[i], prints[j]);
            }
        }

        int largest = Math.Max(1, sizes.Max());
        double[] radii = [.. sizes.Select(s => 0.018 + (0.022 * Math.Log(Math.Max(1, s) + 1) / Math.Log(largest + 1)))];
        (double[] x, double[] y) = Scale(Mds(d2), radii);
        RemoveOverlaps(x, y, radii);
        return new AtlasLayout([.. Enumerable.Range(0, n).Select(i => (x[i], y[i]))], radii, Edges2(d2));
    }

    /// <summary>
    /// The same layout with one node in the middle. Each side of it, left, right, above and below, is scaled on its own
    /// to fill its half of the square, so the graph keeps its size wherever the node was; radii shrink with the
    /// smallest of these scales, so no overlap comes back.
    /// </summary>
    /// <param name="index">Node to centre.</param>
    public AtlasLayout CenterOn(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Positions.Count);
        (double cx, double cy) = Positions[index];
        double room = 0.5 - (Radii.Max() + Gap);
        double Scale(Func<(double X, double Y), double> axis, double centre, int side)
        {
            double far = Positions.Max(p => side * (axis(p) - centre));
            return far > 1e-12 ? room / far : double.PositiveInfinity;
        }

        double left = Scale(static p => p.X, cx, -1), right = Scale(static p => p.X, cx, 1);
        double up = Scale(static p => p.Y, cy, -1), down = Scale(static p => p.Y, cy, 1);
        double radii = Math.Min(1.0, Math.Min(Math.Min(left, right), Math.Min(up, down)));

        // A side without nodes has an infinite scale; the centred node itself stays at 0.5 rather than 0 times that.
        static double Map(double value, double centre, double below, double above) =>
            value == centre ? 0.5 : 0.5 + ((value - centre) * (value < centre ? below : above));
        return new AtlasLayout(
            [.. Positions.Select(p => (Map(p.X, cx, left, right), Map(p.Y, cy, up, down)))],
            [.. Radii.Select(r => r * radii)],
            Edges);
    }

    /// <summary>Two coordinates per point from squared distances: the two largest eigenvectors of the double-centred matrix.</summary>
    private static (double[] X, double[] Y) Mds(double[,] d2)
    {
        int n = d2.GetLength(0);
        var rowMean = new double[n];
        double grand = 0;
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                rowMean[i] += d2[i, j] / n;
            }

            grand += rowMean[i] / n;
        }

        var b = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                b[i, j] = -0.5 * (d2[i, j] - rowMean[i] - rowMean[j] + grand);
            }
        }

        (double l1, double[] v1) = PowerIteration(b, i => 1.0 + ((double)i / n));
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                b[i, j] -= l1 * v1[i] * v1[j];
            }
        }

        (double l2, double[] v2) = PowerIteration(b, i => (i % 2 == 0 ? 1.0 : -1.0) + ((double)i / n));
        double s1 = Math.Sqrt(Math.Max(0, l1)), s2 = Math.Sqrt(Math.Max(0, l2));
        return ([.. v1.Select(v => v * s1)], [.. v2.Select(v => v * s2)]);
    }

    private static (double Value, double[] Vector) PowerIteration(double[,] m, Func<int, double> start)
    {
        int n = m.GetLength(0);
        double[] v = [.. Enumerable.Range(0, n).Select(start)];
        double value = 0;
        for (int iteration = 0; iteration < 300; iteration++)
        {
            var w = new double[n];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    w[i] += m[i, j] * v[j];
                }
            }

            double norm = Math.Sqrt(w.Sum(static x => x * x));
            if (norm < 1e-12)
            {
                return (0, new double[n]);
            }

            for (int i = 0; i < n; i++)
            {
                v[i] = w[i] / norm;
            }

            value = norm;
        }

        // The sign of the eigenvalue: Rayleigh quotient.
        double rayleigh = 0;
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                rayleigh += v[i] * m[i, j] * v[j];
            }
        }

        return (Math.Sign(rayleigh) * value, v);
    }

    /// <summary>
    /// Into the unit square with room for the largest node, each axis filling it: the second axis of the scaling is
    /// usually the narrower, and is stretched up to 2.5 times the first. Points that coincide go on a circle.
    /// </summary>
    private static (double[] X, double[] Y) Scale((double[] X, double[] Y) points, double[] radii)
    {
        int n = radii.Length;
        double margin = radii.Max() + Gap;
        double minX = points.X.Min(), maxX = points.X.Max(), minY = points.Y.Min(), maxY = points.Y.Max();
        double span = Math.Max(maxX - minX, maxY - minY);
        var x = new double[n];
        var y = new double[n];
        if (n == 1)
        {
            x[0] = y[0] = 0.5;
            return (x, y);
        }

        if (span < 1e-9)
        {
            for (int i = 0; i < n; i++)
            {
                double angle = 2 * Math.PI * i / n;
                x[i] = 0.5 + (0.3 * Math.Cos(angle));
                y[i] = 0.5 + (0.3 * Math.Sin(angle));
            }

            return (x, y);
        }

        double room = 1 - (2 * margin);
        double scaleX = room / Math.Max(maxX - minX, span / 2.5);
        double scaleY = room / Math.Max(maxY - minY, span / 2.5);
        double ox = 0.5 - ((minX + maxX) / 2 * scaleX), oy = 0.5 - ((minY + maxY) / 2 * scaleY);
        for (int i = 0; i < n; i++)
        {
            x[i] = ox + (points.X[i] * scaleX);
            y[i] = oy + (points.Y[i] * scaleY);
        }

        return (x, y);
    }

    private static void RemoveOverlaps(double[] x, double[] y, double[] radii)
    {
        int n = x.Length;
        for (int pass = 0; pass < OverlapPasses; pass++)
        {
            bool any = false;
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    double dx = x[j] - x[i], dy = y[j] - y[i];
                    double distance = Math.Sqrt((dx * dx) + (dy * dy));
                    double need = radii[i] + radii[j] + Gap;
                    if (distance >= need)
                    {
                        continue;
                    }

                    any = true;
                    double push = (need - distance) / 2;
                    double ux = dx / distance, uy = dy / distance;
                    if (distance < 1e-9)
                    {
                        // Coincident: apart along a direction fixed by the pair.
                        double angle = 2.399963 * (i + (n * j));
                        (ux, uy) = (Math.Cos(angle), Math.Sin(angle));
                    }

                    x[i] -= push * ux;
                    y[i] -= push * uy;
                    x[j] += push * ux;
                    y[j] += push * uy;
                }
            }

            for (int i = 0; i < n; i++)
            {
                x[i] = Math.Clamp(x[i], radii[i], 1 - radii[i]);
                y[i] = Math.Clamp(y[i], radii[i], 1 - radii[i]);
            }

            if (!any)
            {
                return;
            }
        }
    }

    private static IReadOnlyList<(int From, int To)> Edges2(double[,] d2)
    {
        int n = d2.GetLength(0);
        var edges = new SortedSet<(int, int)>();
        for (int i = 0; i < n; i++)
        {
            foreach (int j in Enumerable.Range(0, n).Where(j => j != i).OrderBy(j => d2[i, j]).ThenBy(static j => j).Take(2))
            {
                edges.Add((Math.Min(i, j), Math.Max(i, j)));
            }
        }

        return [.. edges];
    }
}
