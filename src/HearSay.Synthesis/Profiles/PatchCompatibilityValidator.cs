using HearSay.Synthesis.Patches;

namespace HearSay.Synthesis.Profiles;

/// <summary>Machine-readable reasons why a patch does not fit a backend.</summary>
public static class PatchCompatibilityCodes
{
    /// <summary>The patch is internally inconsistent.</summary>
    public const string InvalidPatch = "invalid-patch";

    /// <summary>Wrong number of operators.</summary>
    public const string OperatorCount = "operator-count";

    /// <summary>An operator uses a mode the backend does not implement.</summary>
    public const string UnsupportedMode = "unsupported-mode";

    /// <summary>An operator uses a waveform the backend does not implement.</summary>
    public const string UnsupportedWaveform = "unsupported-waveform";

    /// <summary>An operator uses a frequency mode the backend does not implement.</summary>
    public const string UnsupportedFrequencyMode = "unsupported-frequency-mode";

    /// <summary>An operator modulates itself and the backend has no self-feedback.</summary>
    public const string SelfFeedback = "self-feedback";

    /// <summary>The routing contains a feedback loop through several operators.</summary>
    public const string CircularFeedback = "circular-feedback";

    /// <summary>The routing needs a full matrix.</summary>
    public const string Routing = "routing";

    /// <summary>A modulation amount follows an envelope and the backend has a static matrix.</summary>
    public const string DynamicModulation = "dynamic-modulation";

    /// <summary>The backend cannot mix operators individually.</summary>
    public const string OutputMix = "output-mix";

    /// <summary>The total modulation that arrives at an operator exceeds the backend limit.</summary>
    public const string ModulationRange = "modulation-range";
}

/// <summary>One reason why a patch does not fit a backend.</summary>
/// <param name="Code">One of <see cref="PatchCompatibilityCodes"/>.</param>
/// <param name="Message">Diagnostic text.</param>
/// <param name="OperatorIndex">Zero-based operator the issue is about, when there is one.</param>
public sealed record PatchCompatibilityIssue(string Code, string Message, int? OperatorIndex = null);

/// <summary>Outcome of a compatibility check.</summary>
/// <param name="Issues">Everything that prevents the backend from playing the patch; empty when compatible.</param>
public sealed record PatchCompatibilityResult(IReadOnlyList<PatchCompatibilityIssue> Issues)
{
    /// <summary>True when the backend can represent the patch exactly.</summary>
    public bool IsCompatible => Issues.Count == 0;

    /// <summary>All issue messages on one line each.</summary>
    public override string ToString() => IsCompatible ? "compatible" : string.Join(Environment.NewLine, Issues.Select(static i => i.Message));
}

/// <summary>Checks whether a backend can represent a patch.</summary>
public interface IPatchCompatibilityValidator
{
    /// <summary>Compares a patch with the capabilities of a backend.</summary>
    /// <param name="patch">Patch to check.</param>
    /// <param name="capabilities">Capabilities of the backend.</param>
    PatchCompatibilityResult Validate(OperatorMatrixPatch patch, SynthesisCapabilities capabilities);
}

/// <summary>
/// Default compatibility check. Disabled operators and disabled edges are ignored: only what can be heard has to be
/// representable. Stateless and thread-safe.
/// </summary>
public sealed class PatchCompatibilityValidator : IPatchCompatibilityValidator
{
    /// <summary>Shared instance.</summary>
    public static PatchCompatibilityValidator Instance { get; } = new();

    /// <inheritdoc />
    public PatchCompatibilityResult Validate(OperatorMatrixPatch patch, SynthesisCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(capabilities);
        var issues = new List<PatchCompatibilityIssue>();
        foreach (string problem in patch.Validate())
        {
            issues.Add(new PatchCompatibilityIssue(PatchCompatibilityCodes.InvalidPatch, problem));
        }

        if (issues.Count > 0)
        {
            return new PatchCompatibilityResult(issues);
        }

        int n = patch.OperatorCount;
        if (n != capabilities.OperatorCount)
        {
            issues.Add(new PatchCompatibilityIssue(
                PatchCompatibilityCodes.OperatorCount,
                $"The patch has {n} operators; the backend needs exactly {capabilities.OperatorCount}."));
        }

        for (int i = 0; i < n; i++)
        {
            OperatorPatch op = patch.Operators[i];
            if (!op.Enabled)
            {
                continue;
            }

            if (!capabilities.SupportedModes.Contains(op.Mode))
            {
                issues.Add(new PatchCompatibilityIssue(PatchCompatibilityCodes.UnsupportedMode, $"OP{i + 1} uses mode {op.Mode}, which the backend does not implement.", i));
            }

            if (!capabilities.SupportedWaveforms.Contains(op.Waveform))
            {
                issues.Add(new PatchCompatibilityIssue(PatchCompatibilityCodes.UnsupportedWaveform, $"OP{i + 1} uses waveform {op.Waveform}, which the backend does not implement.", i));
            }

            bool ratio = op.Frequency.Mode == OperatorFrequencyMode.Ratio;
            if ((ratio && !capabilities.SupportsRatioFrequency) || (!ratio && !capabilities.SupportsFixedFrequency))
            {
                issues.Add(new PatchCompatibilityIssue(PatchCompatibilityCodes.UnsupportedFrequencyMode, $"OP{i + 1} uses {op.Frequency.Mode} frequency, which the backend does not implement.", i));
            }
        }

        var incoming = new double[n];
        var adjacency = new List<int>[n];
        for (int i = 0; i < n; i++)
        {
            adjacency[i] = [];
        }

        foreach (ModulationEdge edge in patch.ModulationMatrix.Edges)
        {
            if (!edge.Enabled || edge.Amount == 0.0 || !patch.Operators[edge.SourceOperator].Enabled || !patch.Operators[edge.DestinationOperator].Enabled)
            {
                continue;
            }

            string name = $"OP{edge.SourceOperator + 1} -> OP{edge.DestinationOperator + 1}";
            if (edge.IsSelfFeedback)
            {
                if (!capabilities.SupportsSelfFeedback)
                {
                    issues.Add(new PatchCompatibilityIssue(PatchCompatibilityCodes.SelfFeedback, $"Edge {name} is self-feedback, which the backend does not implement.", edge.SourceOperator));
                }
            }
            else
            {
                adjacency[edge.SourceOperator].Add(edge.DestinationOperator);
                if (!capabilities.SupportsFullModulationMatrix && edge.SourceOperator < edge.DestinationOperator)
                {
                    issues.Add(new PatchCompatibilityIssue(PatchCompatibilityCodes.Routing, $"Edge {name} needs a full modulation matrix.", edge.DestinationOperator));
                }
            }

            if (edge.AmountEnvelope is not null && !capabilities.SupportsDynamicModulationMatrix)
            {
                issues.Add(new PatchCompatibilityIssue(PatchCompatibilityCodes.DynamicModulation, $"Edge {name} has an amount envelope; the backend has a static matrix.", edge.DestinationOperator));
            }

            double peak = Math.Abs(edge.Amount);
            if (edge.AmountEnvelope is not null)
            {
                double max = 0;
                for (int p = 0; p < edge.AmountEnvelope.Points.Count; p++)
                {
                    max = Math.Max(max, Math.Abs(edge.AmountEnvelope.EffectiveLevel(p)));
                }

                peak *= max;
            }

            incoming[edge.DestinationOperator] += peak;
        }

        for (int i = 0; i < n; i++)
        {
            if (incoming[i] > capabilities.MaxModulationRadiansPerDestination)
            {
                issues.Add(new PatchCompatibilityIssue(
                    PatchCompatibilityCodes.ModulationRange,
                    FormattableString.Invariant($"OP{i + 1} receives up to {incoming[i]:F1} rad of modulation; the backend limit is {capabilities.MaxModulationRadiansPerDestination:F1} rad."),
                    i));
            }
        }

        if (!capabilities.SupportsCircularFeedback && HasCycle(adjacency))
        {
            issues.Add(new PatchCompatibilityIssue(PatchCompatibilityCodes.CircularFeedback, "The routing contains a feedback loop through several operators, which the backend does not implement."));
        }

        if (!capabilities.SupportsIndependentOperatorOutputs)
        {
            double? level = null;
            for (int i = 0; i < n; i++)
            {
                double value = patch.OutputLevels[i];
                if (!patch.Operators[i].Enabled || value == 0.0)
                {
                    continue;
                }

                if (level is double first && first != value)
                {
                    issues.Add(new PatchCompatibilityIssue(PatchCompatibilityCodes.OutputMix, "Operators have different output levels; the backend mixes all carriers equally."));
                    break;
                }

                level = value;
            }
        }

        return new PatchCompatibilityResult(issues);
    }

    private static bool HasCycle(List<int>[] adjacency)
    {
        // 0 = unvisited, 1 = on the current path, 2 = finished.
        var state = new int[adjacency.Length];
        for (int start = 0; start < adjacency.Length; start++)
        {
            if (state[start] == 0 && Visit(start, adjacency, state))
            {
                return true;
            }
        }

        return false;

        static bool Visit(int node, List<int>[] adjacency, int[] state)
        {
            state[node] = 1;
            foreach (int next in adjacency[node])
            {
                if (state[next] == 1 || (state[next] == 0 && Visit(next, adjacency, state)))
                {
                    return true;
                }
            }

            state[node] = 2;
            return false;
        }
    }
}
