using System.Collections.Concurrent;

namespace HearSay.OperatorMatrix.Priors;

/// <summary>
/// A family of library sounds: a set of library indices, the centroid of their fingerprints, and the medoid, the member
/// nearest that centroid, whose sound stands for the family.
/// </summary>
public sealed class LibraryFamily
{
    internal LibraryFamily(int[] path, int[] members, double[] centroid, int medoid)
    {
        PathArray = path;
        MemberArray = members;
        CentroidArray = centroid;
        Medoid = medoid;
    }

    /// <summary>Positions among the children from the root down; empty for the root.</summary>
    public IReadOnlyList<int> Path => PathArray;

    /// <summary>Library indices of the members, ascending.</summary>
    public IReadOnlyList<int> Members => MemberArray;

    /// <summary>Centroid of the members' fingerprints, in quarter-decibel steps.</summary>
    public IReadOnlyList<double> Centroid => CentroidArray;

    /// <summary>Library index of the member whose sound stands for the family.</summary>
    public int Medoid { get; }

    /// <summary>Number of members.</summary>
    public int Size => MemberArray.Length;

    /// <summary>True for a single sound.</summary>
    public bool IsLeaf => MemberArray.Length == 1;

    internal int[] PathArray { get; }

    internal int[] MemberArray { get; }

    internal double[] CentroidArray { get; }
}

/// <summary>
/// The sounds of a <see cref="StructureLibrary"/> as a tree of families for browsing: the root holds every sound, a
/// family of more than <see cref="Branching"/> members is split by k-means on the fingerprints into at most that many
/// children, a smaller family has its members as leaves. Children are computed when they are first asked for and kept.
/// Deterministic: k-means is seeded from the family's path. Thread-safe.
/// </summary>
public sealed class LibraryAtlas
{
    /// <summary>Most children of a family.</summary>
    public const int Branching = 40;

    private const int Iterations = 15;

    internal LibraryAtlas(StructureLibrary library, LibraryFamily root)
    {
        Library = library;
        Root = root;
    }

    /// <summary>The library the tree is built on.</summary>
    public StructureLibrary Library { get; }

    /// <summary>The family of all sounds.</summary>
    public LibraryFamily Root { get; }

    internal ConcurrentDictionary<LibraryFamily, Lazy<IReadOnlyList<LibraryFamily>>> ChildrenCache { get; } = new();

    /// <summary>The tree of a library, in memory; nothing is split until it is asked for.</summary>
    /// <param name="library">Library.</param>
    public static LibraryAtlas Build(StructureLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        return new LibraryAtlas(library, RootOf(library));
    }

    /// <summary>The children of a family, largest first; empty for a leaf.</summary>
    /// <param name="family">Family of this atlas.</param>
    public IReadOnlyList<LibraryFamily> Children(LibraryFamily family)
    {
        ArgumentNullException.ThrowIfNull(family);
        return ChildrenCache.GetOrAdd(family, f => new Lazy<IReadOnlyList<LibraryFamily>>(() => Split(Library, f))).Value;
    }

    /// <summary>
    /// The families a sound falls in, from the root down: at every level the child with the nearest centroid, until the
    /// family whose children are single sounds. The sound belongs among the children of the last family.
    /// </summary>
    /// <param name="fingerprint">Fingerprint of the sound, rendered with <see cref="StructureLibrary.ReferenceRequest"/>.</param>
    public IReadOnlyList<LibraryFamily> Locate(ReadOnlySpan<byte> fingerprint)
    {
        if (fingerprint.Length != SpectralFingerprint.Length)
        {
            throw new ArgumentException($"A fingerprint has {SpectralFingerprint.Length} values.", nameof(fingerprint));
        }

        var path = new List<LibraryFamily> { Root };
        LibraryFamily current = Root;
        while (true)
        {
            IReadOnlyList<LibraryFamily> children = Children(current);
            if (children.All(static c => c.IsLeaf))
            {
                return path;
            }

            int best = 0;
            double bestDistance = double.PositiveInfinity;
            for (int k = 0; k < children.Count; k++)
            {
                double d = Distance(fingerprint, children[k].CentroidArray);
                if (d < bestDistance)
                {
                    (best, bestDistance) = (k, d);
                }
            }

            if (children[best].IsLeaf)
            {
                return path;
            }

            current = children[best];
            path.Add(current);
        }
    }

    private const int FileVersion = 1;

    /// <summary>The file the top level of a library's tree is kept in, next to the library file.</summary>
    /// <param name="libraryFile">Path of the library file, see <see cref="StructureLibrary.FileOf"/>.</param>
    public static string CacheFileOf(string libraryFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryFile);
        return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(libraryFile) ?? string.Empty, System.IO.Path.GetFileNameWithoutExtension(libraryFile) + "-atlas-v1.bin");
    }

    /// <summary>
    /// The tree of a library with its top level loaded from a file, or split and saved there; a missing, truncated or
    /// mismatching file is rebuilt, and one that cannot be written still serves this process.
    /// </summary>
    /// <param name="library">Library.</param>
    /// <param name="cacheFile">File for the top level, see <see cref="CacheFileOf"/>.</param>
    public static LibraryAtlas Open(StructureLibrary library, string cacheFile)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheFile);
        LibraryFamily root = RootOf(library);
        var atlas = new LibraryAtlas(library, root);
        IReadOnlyList<LibraryFamily>? children = TryLoad(library, cacheFile);
        if (children is null)
        {
            children = Split(library, root);
            TrySave(library, children, cacheFile);
        }

        atlas.ChildrenCache[root] = new Lazy<IReadOnlyList<LibraryFamily>>(children);
        return atlas;
    }

    private static IReadOnlyList<LibraryFamily>? TryLoad(StructureLibrary library, string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            using var reader = new BinaryReader(File.OpenRead(file));
            int length = SpectralFingerprint.Length;
            if (reader.ReadInt32() != FileVersion || reader.ReadInt32() != library.Count || reader.ReadInt32() != length)
            {
                return null;
            }

            int count = reader.ReadInt32();
            if (count is < 0 or > Branching)
            {
                return null;
            }

            var children = new List<LibraryFamily>(count);
            int total = 0;
            for (int k = 0; k < count; k++)
            {
                int size = reader.ReadInt32();
                if (size <= 0 || size > library.Count - total)
                {
                    return null;
                }

                var members = new int[size];
                for (int i = 0; i < size; i++)
                {
                    members[i] = reader.ReadInt32();
                    if (members[i] < 0 || members[i] >= library.Count)
                    {
                        return null;
                    }
                }

                var centroid = new double[length];
                for (int b = 0; b < length; b++)
                {
                    centroid[b] = reader.ReadDouble();
                }

                int medoid = reader.ReadInt32();
                children.Add(new LibraryFamily([k], members, centroid, medoid));
                total += size;
            }

            return total == library.Count || count == Branching ? children : null;
        }
        catch (IOException)
        {
            // Also a truncated file (EndOfStreamException): it is rebuilt.
            return null;
        }
    }

    private static void TrySave(StructureLibrary library, IReadOnlyList<LibraryFamily> children, string file)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(file))!);
            string temporary = file + ".tmp";
            using (var writer = new BinaryWriter(File.Create(temporary)))
            {
                writer.Write(FileVersion);
                writer.Write(library.Count);
                writer.Write(SpectralFingerprint.Length);
                writer.Write(children.Count);
                foreach (LibraryFamily child in children)
                {
                    writer.Write(child.Size);
                    foreach (int m in child.MemberArray)
                    {
                        writer.Write(m);
                    }

                    foreach (double value in child.CentroidArray)
                    {
                        writer.Write(value);
                    }

                    writer.Write(child.Medoid);
                }
            }

            File.Move(temporary, file, overwrite: true);
        }
        catch (IOException)
        {
            // An atlas that cannot be saved still serves this process.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal static LibraryFamily RootOf(StructureLibrary library)
    {
        int[] members = [.. Enumerable.Range(0, library.Count)];
        double[] centroid = Mean(library, members);
        return new LibraryFamily([], members, centroid, MedoidOf(library, members, centroid));
    }

    internal static IReadOnlyList<LibraryFamily> Split(StructureLibrary library, LibraryFamily family)
    {
        int[] members = family.MemberArray;
        if (members.Length <= 1)
        {
            return [];
        }

        if (members.Length <= Branching)
        {
            return Leaves(library, family, members);
        }

        (int[][] clusters, double[][] centroids) = KMeans(library, members, SeedOf(family.PathArray));
        if (clusters.Length <= 1)
        {
            // Nothing to separate (identical fingerprints): the first sounds stand for the rest.
            return Leaves(library, family, members[..Branching]);
        }

        var children = clusters
            .Select((c, j) => (Members: c, Centroid: centroids[j], Medoid: MedoidOf(library, c, centroids[j])))
            .OrderByDescending(static x => x.Members.Length)
            .ThenBy(static x => x.Medoid)
            .ToList();
        return [.. children.Select((x, k) => new LibraryFamily([.. family.PathArray, k], x.Members, x.Centroid, x.Medoid))];
    }

    private static IReadOnlyList<LibraryFamily> Leaves(StructureLibrary library, LibraryFamily parent, int[] members) =>
        [.. members.Select((m, k) => new LibraryFamily([.. parent.PathArray, k], [m], ToDoubles(library.Fingerprint(m)), m))];

    /// <summary>
    /// k-means with k-means++ seeding; the last step assigns every member to its nearest final centroid, so that the
    /// returned centroids and clusters agree. Empty clusters are dropped; members stay in ascending order.
    /// </summary>
    private static (int[][] Clusters, double[][] Centroids) KMeans(StructureLibrary library, int[] members, int seed)
    {
        int n = members.Length;
        int length = SpectralFingerprint.Length;
        var random = new Random(seed);
        var centroids = new List<double[]> { ToDoubles(library.Fingerprint(members[random.Next(n)])) };
        var nearest = new double[n];
        Array.Fill(nearest, double.PositiveInfinity);
        while (centroids.Count < Branching)
        {
            double[] latest = centroids[^1];
            Parallel.For(0, n, i => nearest[i] = Math.Min(nearest[i], Distance(library.Fingerprint(members[i]), latest)));
            double total = 0;
            foreach (double d in nearest)
            {
                total += d;
            }

            if (total <= 0)
            {
                break;
            }

            double r = random.NextDouble() * total;
            int pick = n - 1;
            for (int i = 0; i < n; i++)
            {
                r -= nearest[i];
                if (r < 0)
                {
                    pick = i;
                    break;
                }
            }

            centroids.Add(ToDoubles(library.Fingerprint(members[pick])));
        }

        double[][] c = [.. centroids];
        int k = c.Length;
        var assignment = new int[n];
        for (int iteration = 0; ; iteration++)
        {
            Parallel.For(0, n, i => assignment[i] = NearestIndex(library.Fingerprint(members[i]), c));
            if (iteration == Iterations)
            {
                break;
            }

            var sums = new double[k][];
            var counts = new int[k];
            for (int j = 0; j < k; j++)
            {
                sums[j] = new double[length];
            }

            for (int i = 0; i < n; i++)
            {
                int j = assignment[i];
                counts[j]++;
                ReadOnlySpan<byte> print = library.Fingerprint(members[i]);
                for (int b = 0; b < length; b++)
                {
                    sums[j][b] += print[b];
                }
            }

            bool moved = false;
            for (int j = 0; j < k; j++)
            {
                if (counts[j] == 0)
                {
                    continue;
                }

                for (int b = 0; b < length; b++)
                {
                    double value = sums[j][b] / counts[j];
                    moved |= value != c[j][b];
                    c[j][b] = value;
                }
            }

            if (!moved)
            {
                break;
            }
        }

        var groups = new List<int>[k];
        for (int j = 0; j < k; j++)
        {
            groups[j] = [];
        }

        for (int i = 0; i < n; i++)
        {
            groups[assignment[i]].Add(members[i]);
        }

        int[] used = [.. Enumerable.Range(0, k).Where(j => groups[j].Count > 0)];
        return ([.. used.Select(j => groups[j].ToArray())], [.. used.Select(j => c[j])]);
    }

    private static int NearestIndex(ReadOnlySpan<byte> print, double[][] centroids)
    {
        int best = 0;
        double bestDistance = double.PositiveInfinity;
        for (int j = 0; j < centroids.Length; j++)
        {
            double d = Distance(print, centroids[j]);
            if (d < bestDistance)
            {
                (best, bestDistance) = (j, d);
            }
        }

        return best;
    }

    private static double[] Mean(StructureLibrary library, int[] members)
    {
        var mean = new double[SpectralFingerprint.Length];
        foreach (int m in members)
        {
            ReadOnlySpan<byte> print = library.Fingerprint(m);
            for (int b = 0; b < mean.Length; b++)
            {
                mean[b] += print[b];
            }
        }

        for (int b = 0; b < mean.Length; b++)
        {
            mean[b] /= Math.Max(1, members.Length);
        }

        return mean;
    }

    private static int MedoidOf(StructureLibrary library, int[] members, double[] centroid)
    {
        int best = members[0];
        double bestDistance = double.PositiveInfinity;
        foreach (int m in members)
        {
            double d = Distance(library.Fingerprint(m), centroid);
            if (d < bestDistance)
            {
                (best, bestDistance) = (m, d);
            }
        }

        return best;
    }

    private static double Distance(ReadOnlySpan<byte> print, double[] centroid)
    {
        double sum = 0;
        for (int b = 0; b < print.Length; b++)
        {
            double d = print[b] - centroid[b];
            sum += d * d;
        }

        return sum;
    }

    private static double[] ToDoubles(ReadOnlySpan<byte> print)
    {
        var values = new double[print.Length];
        for (int b = 0; b < print.Length; b++)
        {
            values[b] = print[b];
        }

        return values;
    }

    private static int SeedOf(int[] path)
    {
        int seed = 17;
        foreach (int p in path)
        {
            seed = unchecked((seed * 31) + p + 1);
        }

        return seed;
    }
}
