using System.Text.Json.Serialization;

namespace HearSay.Synthesis.Patches;

/// <summary>One directed connection of the modulation matrix: the output of a source operator modulates a destination.</summary>
/// <remarks>
/// Whether an edge exists is a structural decision; its amount is a continuous parameter that only matters while the
/// edge is enabled. A self-edge (<c>Source == Destination</c>) is operator feedback.
/// </remarks>
/// <param name="SourceOperator">Zero-based index of the modulating operator.</param>
/// <param name="DestinationOperator">Zero-based index of the modulated operator.</param>
/// <param name="Enabled">A disabled edge contributes nothing, whatever its amount.</param>
/// <param name="Amount">
/// Phase deviation in radians per unit of source output. The source output includes the amplitude envelope of the
/// source operator, so the modulation index over time is <c>Amount * AmountEnvelope(t) * sourceAmplitude(t)</c>.
/// </param>
/// <param name="AmountEnvelope">Optional control-rate trajectory that multiplies <paramref name="Amount"/>.</param>
public sealed record ModulationEdge(
    int SourceOperator,
    int DestinationOperator,
    bool Enabled,
    double Amount,
    OperatorEnvelope? AmountEnvelope = null)
{
    /// <summary>True for operator self-feedback.</summary>
    [JsonIgnore]
    public bool IsSelfFeedback => SourceOperator == DestinationOperator;
}

/// <summary>
/// Full directed modulation matrix, stored sparsely. <c>this[destination, source]</c> is the static amount of the
/// edge from <c>source</c> to <c>destination</c>, zero when the edge is absent or disabled. Feed-forward stacks,
/// branches, self-feedback and feedback loops through several operators are all ordinary entries. Immutable.
/// </summary>
public sealed class PhaseModulationMatrix : IEquatable<PhaseModulationMatrix>
{
    private readonly double[] _dense;

    /// <summary>Creates a matrix from its edges.</summary>
    /// <param name="operatorCount">Number of operators.</param>
    /// <param name="edges">Edges; at most one per source and destination pair.</param>
    /// <exception cref="ArgumentException">An edge is out of range or a pair occurs twice.</exception>
    [JsonConstructor]
    public PhaseModulationMatrix(int operatorCount, IReadOnlyList<ModulationEdge> edges)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(operatorCount, 1);
        ArgumentNullException.ThrowIfNull(edges);
        OperatorCount = operatorCount;
        _dense = new double[operatorCount * operatorCount];
        var seen = new HashSet<int>();
        var sorted = new List<ModulationEdge>(edges.Count);
        foreach (ModulationEdge edge in edges)
        {
            if ((uint)edge.SourceOperator >= (uint)operatorCount || (uint)edge.DestinationOperator >= (uint)operatorCount)
            {
                throw new ArgumentException($"Edge {edge.SourceOperator} -> {edge.DestinationOperator} is outside a {operatorCount}-operator matrix.", nameof(edges));
            }

            int cell = (edge.DestinationOperator * operatorCount) + edge.SourceOperator;
            if (!seen.Add(cell))
            {
                throw new ArgumentException($"Edge {edge.SourceOperator} -> {edge.DestinationOperator} occurs more than once.", nameof(edges));
            }

            sorted.Add(edge);
            if (edge.Enabled)
            {
                _dense[cell] = edge.Amount;
            }
        }

        sorted.Sort(static (a, b) =>
        {
            int order = a.DestinationOperator.CompareTo(b.DestinationOperator);
            return order != 0 ? order : a.SourceOperator.CompareTo(b.SourceOperator);
        });
        Edges = sorted;
    }

    /// <summary>Number of operators; the matrix is <c>OperatorCount x OperatorCount</c>.</summary>
    public int OperatorCount { get; }

    /// <summary>All stored edges, enabled or not, ordered by destination and then source.</summary>
    public IReadOnlyList<ModulationEdge> Edges { get; }

    /// <summary>Static amount in radians of the edge from <paramref name="source"/> to <paramref name="destination"/>.</summary>
    /// <param name="destination">Zero-based modulated operator.</param>
    /// <param name="source">Zero-based modulating operator.</param>
    public double this[int destination, int source] => _dense[(destination * OperatorCount) + source];

    /// <summary>A matrix without edges.</summary>
    /// <param name="operatorCount">Number of operators.</param>
    public static PhaseModulationMatrix Empty(int operatorCount) => new(operatorCount, []);

    /// <summary>Creates a matrix of enabled static edges.</summary>
    /// <param name="operatorCount">Number of operators.</param>
    /// <param name="edges">Tuples of source, destination and amount in radians.</param>
    public static PhaseModulationMatrix FromEdges(int operatorCount, params (int Source, int Destination, double Amount)[] edges)
    {
        var list = new List<ModulationEdge>(edges.Length);
        foreach ((int source, int destination, double amount) in edges)
        {
            list.Add(new ModulationEdge(source, destination, true, amount));
        }

        return new PhaseModulationMatrix(operatorCount, list);
    }

    /// <summary>The stored edge from <paramref name="source"/> to <paramref name="destination"/>, or null.</summary>
    /// <param name="source">Zero-based modulating operator.</param>
    /// <param name="destination">Zero-based modulated operator.</param>
    public ModulationEdge? Find(int source, int destination)
    {
        foreach (ModulationEdge edge in Edges)
        {
            if (edge.SourceOperator == source && edge.DestinationOperator == destination)
            {
                return edge;
            }
        }

        return null;
    }

    /// <summary>Returns a matrix in which the given edge replaces the edge between the same two operators.</summary>
    /// <param name="edge">Edge to add or replace.</param>
    public PhaseModulationMatrix With(ModulationEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        var list = new List<ModulationEdge>(Edges.Count + 1);
        foreach (ModulationEdge existing in Edges)
        {
            if (existing.SourceOperator != edge.SourceOperator || existing.DestinationOperator != edge.DestinationOperator)
            {
                list.Add(existing);
            }
        }

        list.Add(edge);
        return new PhaseModulationMatrix(OperatorCount, list);
    }

    /// <summary>Returns a matrix without the edge between the two operators.</summary>
    /// <param name="source">Zero-based modulating operator.</param>
    /// <param name="destination">Zero-based modulated operator.</param>
    public PhaseModulationMatrix Without(int source, int destination)
    {
        var list = new List<ModulationEdge>(Edges.Count);
        foreach (ModulationEdge existing in Edges)
        {
            if (existing.SourceOperator != source || existing.DestinationOperator != destination)
            {
                list.Add(existing);
            }
        }

        return new PhaseModulationMatrix(OperatorCount, list);
    }

    /// <summary>The dense static matrix as rows of destinations and columns of sources.</summary>
    public double[,] ToDense()
    {
        var result = new double[OperatorCount, OperatorCount];
        for (int d = 0; d < OperatorCount; d++)
        {
            for (int s = 0; s < OperatorCount; s++)
            {
                result[d, s] = this[d, s];
            }
        }

        return result;
    }

    /// <inheritdoc />
    public bool Equals(PhaseModulationMatrix? other)
    {
        if (other is null || other.OperatorCount != OperatorCount || other.Edges.Count != Edges.Count)
        {
            return false;
        }

        for (int i = 0; i < Edges.Count; i++)
        {
            ModulationEdge a = Edges[i];
            ModulationEdge b = other.Edges[i];
            if (a.SourceOperator != b.SourceOperator
                || a.DestinationOperator != b.DestinationOperator
                || a.Enabled != b.Enabled
                || a.Amount != b.Amount
                || !PatchEquality.EnvelopesEqual(a.AmountEnvelope, b.AmountEnvelope))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as PhaseModulationMatrix);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(OperatorCount);
        foreach (ModulationEdge edge in Edges)
        {
            hash.Add(edge.SourceOperator);
            hash.Add(edge.DestinationOperator);
            hash.Add(edge.Enabled);
            hash.Add(edge.Amount);
        }

        return hash.ToHashCode();
    }
}

/// <summary>Value comparison helpers for patch parts that contain lists.</summary>
internal static class PatchEquality
{
    public static bool EnvelopesEqual(OperatorEnvelope? a, OperatorEnvelope? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        if (a.SustainPoint != b.SustainPoint || a.ReleasePoint != b.ReleasePoint || a.Points.Count != b.Points.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Points.Count; i++)
        {
            if (a.Points[i] != b.Points[i])
            {
                return false;
            }
        }

        return true;
    }
}
