using System.Globalization;
using System.Text;
using HearSay.Synthesis.Filters;
using HearSay.Synthesis.Patches;

namespace HearSay.OperatorMatrix.Canonicalization;

/// <summary>
/// Identifies the structural regime of a patch: which operators are audible, their modes, waveforms and coarse
/// frequencies, which modulation edges exist and which operators feed the output. Continuous values are not part of
/// the key. Local sensitivities and Jacobians are only valid inside one regime: what was learned with a sine in OP3
/// says nothing about a saw in OP3, and a Jacobian learned with an edge present says nothing once it is gone.
/// </summary>
/// <param name="Value">Canonical text form; equal structures give equal values.</param>
public sealed record OperatorMatrixRegimeKey(string Value)
{
    private static readonly string[] FilterToken = ["", "LP2", "LP4", "HP2", "HP4", "BP", "N", "PK", "FMT"];

    /// <summary>Derives the key of a patch. Operators and edges that cannot be heard are ignored.</summary>
    /// <param name="patch">Patch to describe.</param>
    public static OperatorMatrixRegimeKey FromPatch(OperatorMatrixPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        bool[] audible = patch.FindAudibleOperators();
        var text = new StringBuilder();
        for (int i = 0; i < patch.OperatorCount; i++)
        {
            if (!audible[i])
            {
                continue;
            }

            OperatorPatch op = patch.Operators[i];
            text.Append("op").Append(i + 1).Append(':').Append(op.Mode).Append('/').Append(op.Waveform).Append('/');
            if (op.Frequency.Mode == OperatorFrequencyMode.Ratio)
            {
                text.Append('r').Append(op.Frequency.Ratio.ToString("0.###", CultureInfo.InvariantCulture));
            }
            else
            {
                // Fixed frequencies within a semitone are the same regime.
                double semitone = Math.Round(12.0 * Math.Log2(Math.Max(1.0, op.Frequency.FixedFrequencyHz) / 440.0));
                text.Append('f').Append(semitone.ToString("0", CultureInfo.InvariantCulture));
            }

            if (patch.OutputLevels[i] != 0.0)
            {
                text.Append("/out");
            }

            // The filter type and its slope are structural; a sweep changes how the cutoff responds over time.
            if (op.Filter is { } filter)
            {
                text.Append('/').Append(FilterToken[OperatorFilterKernel.Code(filter)]);
                if (filter.Envelope is not null && filter.EnvelopeOctaves != 0.0)
                {
                    text.Append('~');
                }
            }

            text.Append(';');
        }

        text.Append('|');
        foreach (ModulationEdge edge in patch.ModulationMatrix.Edges)
        {
            if (edge.Enabled && edge.Amount != 0.0 && audible[edge.SourceOperator] && audible[edge.DestinationOperator])
            {
                text.Append(edge.SourceOperator + 1).Append('>').Append(edge.DestinationOperator + 1);
                if (edge.AmountEnvelope is not null)
                {
                    text.Append('~');
                }

                text.Append(';');
            }
        }

        if (!patch.Pitch.IsStatic)
        {
            text.Append("|pitch");
        }

        return new OperatorMatrixRegimeKey(text.ToString());
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Brings equivalent operator arrangements into one form. The operators of a matrix synth are interchangeable, so
/// every audible patch has many relabelled twins; canonical ordering makes them comparable. Stateless and thread-safe.
/// </summary>
public static class PatchCanonicalizer
{
    /// <summary>
    /// Returns an equivalent patch in canonical form: operators that feed the output first, then by frequency, then by
    /// their place in the routing; inaudible operators are reset and edges that cannot be heard are removed. The
    /// canonical patch renders the same audio.
    /// </summary>
    /// <param name="patch">Patch to canonicalize.</param>
    public static OperatorMatrixPatch Canonicalize(OperatorMatrixPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        int n = patch.OperatorCount;
        bool[] audible = patch.FindAudibleOperators();
        int[] depth = RoutingDepth(patch, audible);
        var order = new List<int>();
        for (int i = 0; i < n; i++)
        {
            order.Add(i);
        }

        order.Sort((a, b) =>
        {
            int result = audible[b].CompareTo(audible[a]);
            if (result == 0 && audible[a])
            {
                result = depth[a].CompareTo(depth[b]);
                if (result == 0)
                {
                    result = SortFrequency(patch.Operators[a]).CompareTo(SortFrequency(patch.Operators[b]));
                }

                if (result == 0)
                {
                    result = patch.OutputLevels[b].CompareTo(patch.OutputLevels[a]);
                }
            }

            return result != 0 ? result : a.CompareTo(b);
        });

        var newIndex = new int[n];
        for (int position = 0; position < n; position++)
        {
            newIndex[order[position]] = position;
        }

        var operators = new OperatorPatch[n];
        var levels = new double[n];
        for (int position = 0; position < n; position++)
        {
            int old = order[position];
            operators[position] = audible[old] ? patch.Operators[old] : OperatorPatch.Disabled;
            levels[position] = audible[old] ? patch.OutputLevels[old] : 0.0;
        }

        var edges = new List<ModulationEdge>();
        foreach (ModulationEdge edge in patch.ModulationMatrix.Edges)
        {
            if (edge.Enabled && edge.Amount != 0.0 && audible[edge.SourceOperator] && audible[edge.DestinationOperator])
            {
                edges.Add(edge with { SourceOperator = newIndex[edge.SourceOperator], DestinationOperator = newIndex[edge.DestinationOperator] });
            }
        }

        return new OperatorMatrixPatch(operators, new PhaseModulationMatrix(n, edges), levels, patch.Pitch);
    }

    /// <summary>Regime key of the canonical form: equal for patches that differ only in operator numbering.</summary>
    /// <param name="patch">Patch to describe.</param>
    public static OperatorMatrixRegimeKey CanonicalKey(OperatorMatrixPatch patch) =>
        OperatorMatrixRegimeKey.FromPatch(Canonicalize(patch));

    private static double SortFrequency(OperatorPatch op) =>
        op.Frequency.Mode == OperatorFrequencyMode.Ratio ? op.Frequency.Ratio : 1000.0 + op.Frequency.FixedFrequencyHz;

    /// <summary>Distance of every operator from the output: 0 for operators in the mix, 1 for their modulators, and so on.</summary>
    private static int[] RoutingDepth(OperatorMatrixPatch patch, bool[] audible)
    {
        int n = patch.OperatorCount;
        var depth = new int[n];
        Array.Fill(depth, int.MaxValue);
        var queue = new Queue<int>();
        for (int i = 0; i < n; i++)
        {
            if (audible[i] && patch.OutputLevels[i] != 0.0)
            {
                depth[i] = 0;
                queue.Enqueue(i);
            }
        }

        while (queue.Count > 0)
        {
            int destination = queue.Dequeue();
            foreach (ModulationEdge edge in patch.ModulationMatrix.Edges)
            {
                if (edge.Enabled && edge.Amount != 0.0 && edge.DestinationOperator == destination && audible[edge.SourceOperator] && depth[edge.SourceOperator] == int.MaxValue)
                {
                    depth[edge.SourceOperator] = depth[destination] + 1;
                    queue.Enqueue(edge.SourceOperator);
                }
            }
        }

        return depth;
    }
}

/// <summary>Weights of <see cref="PatchComplexity"/>.</summary>
public sealed record ComplexityOptions
{
    /// <summary>Cost of every audible operator.</summary>
    public double PerOperator { get; init; } = 0.10;

    /// <summary>Cost of every live modulation edge.</summary>
    public double PerEdge { get; init; } = 0.06;

    /// <summary>Extra cost of an edge or output contribution too small to matter, which should rather be absent.</summary>
    public double PerTinyContribution { get; init; } = 0.10;

    /// <summary>Cost of every envelope point beyond five, and of every edge envelope.</summary>
    public double PerExtraEnvelopePoint { get; init; } = 0.03;

    /// <summary>Cost of extreme detuning, per operator, growing beyond 3 Hz or 25 cents.</summary>
    public double ExtremeDetune { get; init; } = 0.10;

    /// <summary>Cost of an operator filter.</summary>
    public double PerFilter { get; init; } = 0.05;

    /// <summary>Extra cost of a filter envelope.</summary>
    public double PerFilterEnvelope { get; init; } = 0.03;
}

/// <summary>
/// Simplicity measure of a patch, 0 for a single plain operator and around 1..2 for a busy six-operator network. It is
/// a light tie-breaker between patches that sound alike and is never allowed to dominate the audio loss.
/// </summary>
public static class PatchComplexity
{
    /// <summary>Measures a patch.</summary>
    /// <param name="patch">Patch to measure.</param>
    /// <param name="options">Weights.</param>
    public static double Measure(OperatorMatrixPatch patch, ComplexityOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(patch);
        options ??= new ComplexityOptions();
        bool[] audible = patch.FindAudibleOperators();
        double total = 0;
        int operators = 0;
        for (int i = 0; i < patch.OperatorCount; i++)
        {
            if (!audible[i])
            {
                continue;
            }

            operators++;
            OperatorPatch op = patch.Operators[i];
            total += options.PerExtraEnvelopePoint * Math.Max(0, op.AmplitudeEnvelope.Points.Count - 5);
            total += options.ExtremeDetune * (Math.Max(0.0, (Math.Abs(op.DetuneHz) - 3.0) / 5.0) + Math.Max(0.0, (Math.Abs(op.FineTuneCents) - 25.0) / 25.0));
            if (op.Filter is { } filter)
            {
                total += options.PerFilter + (filter.Envelope is null ? 0.0 : options.PerFilterEnvelope);
            }

            double level = patch.OutputLevels[i];
            if (level != 0.0 && Math.Abs(level) < 0.01)
            {
                total += options.PerTinyContribution;
            }
        }

        total += options.PerOperator * Math.Max(0, operators - 1);
        foreach (ModulationEdge edge in patch.ModulationMatrix.Edges)
        {
            if (!edge.Enabled || edge.Amount == 0.0 || !audible[edge.SourceOperator] || !audible[edge.DestinationOperator])
            {
                continue;
            }

            total += options.PerEdge;
            if (Math.Abs(edge.Amount) < 0.05)
            {
                total += options.PerTinyContribution;
            }

            if (edge.AmountEnvelope is not null)
            {
                total += options.PerExtraEnvelopePoint * edge.AmountEnvelope.Points.Count;
            }
        }

        return total;
    }
}
