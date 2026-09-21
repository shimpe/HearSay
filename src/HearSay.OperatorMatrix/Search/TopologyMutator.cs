using System.Globalization;
using HearSay.Analysis;
using HearSay.OperatorMatrix.Parameters;
using HearSay.OperatorMatrix.Priors;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.OperatorMatrix.Search;

/// <summary>A structural variant of a patch.</summary>
/// <param name="Patch">Mutated patch.</param>
/// <param name="Description">What was changed.</param>
public sealed record TopologyMutation(OperatorMatrixPatch Patch, string Description);

/// <summary>
/// Proposes structural changes to a patch: edges, operators, output routing, coarse frequencies and, where the
/// profile offers a choice, waveforms and modes. Everything it proposes fits the capabilities it was given. This is
/// the part of the search that gradients cannot do. Stateless; randomness comes from the caller.
/// </summary>
public sealed class TopologyMutator
{
    private readonly SynthesisCapabilities _capabilities;
    private readonly IReadOnlyList<double> _ratios;

    /// <summary>Creates a mutator.</summary>
    /// <param name="capabilities">Limits of the synthesis profile.</param>
    /// <param name="ratios">Coarse ratios to choose from; the schema defaults when null.</param>
    public TopologyMutator(SynthesisCapabilities capabilities, IReadOnlyList<double>? ratios = null)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        _capabilities = capabilities;
        _ratios = ratios ?? PatchParameterSchema.DefaultRatios;
    }

    /// <summary>Returns up to <paramref name="count"/> different, compatible mutations of a patch.</summary>
    /// <param name="patch">Patch to vary.</param>
    /// <param name="analysis">Target analysis; supplies envelopes and ratios for new operators.</param>
    /// <param name="random">Random source of the search.</param>
    /// <param name="count">Number of mutations wanted.</param>
    public IReadOnlyList<TopologyMutation> Propose(OperatorMatrixPatch patch, TargetAnalysis analysis, Random random, int count)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(random);
        var result = new List<TopologyMutation>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { patch.ComputeHash() };
        for (int attempt = 0; attempt < count * 8 && result.Count < count; attempt++)
        {
            TopologyMutation? mutation = Mutate(patch, analysis, random);
            if (mutation is null
                || !seen.Add(mutation.Patch.ComputeHash())
                || !PatchCompatibilityValidator.Instance.Validate(mutation.Patch, _capabilities).IsCompatible)
            {
                continue;
            }

            result.Add(mutation);
        }

        return result;
    }

    private TopologyMutation? Mutate(OperatorMatrixPatch patch, TargetAnalysis analysis, Random random)
    {
        int n = patch.OperatorCount;
        bool[] audible = patch.FindAudibleOperators();
        var used = new List<int>();
        var free = new List<int>();
        for (int i = 0; i < n; i++)
        {
            (audible[i] ? used : free).Add(i);
        }

        if (used.Count == 0)
        {
            return null;
        }

        int kind = random.Next(9);
        switch (kind)
        {
            case 0:
            {
                // New edge between two existing operators.
                int source = used[random.Next(used.Count)];
                int destination = used[random.Next(used.Count)];
                if (!EdgeAllowed(source, destination) || patch.ModulationMatrix.Find(source, destination) is { Enabled: true })
                {
                    return null;
                }

                double amount = source == destination ? 0.6 : 0.8;
                return new TopologyMutation(patch.WithEdge(new ModulationEdge(source, destination, true, amount)), $"add edge OP{source + 1} -> OP{destination + 1}");
            }

            case 1:
            {
                // Remove the weakest edge.
                ModulationEdge? weakest = null;
                foreach (ModulationEdge edge in patch.ModulationMatrix.Edges)
                {
                    if (edge.Enabled && audible[edge.SourceOperator] && audible[edge.DestinationOperator] && (weakest is null || Math.Abs(edge.Amount) < Math.Abs(weakest.Amount)))
                    {
                        weakest = edge;
                    }
                }

                return weakest is null
                    ? null
                    : new TopologyMutation(patch with { ModulationMatrix = patch.ModulationMatrix.Without(weakest.SourceOperator, weakest.DestinationOperator) }, $"remove edge OP{weakest.SourceOperator + 1} -> OP{weakest.DestinationOperator + 1}");
            }

            case 2:
            {
                // New operator that modulates an existing one.
                if (free.Count == 0)
                {
                    return null;
                }

                int slot = free[0];
                int destination = used[random.Next(used.Count)];
                if (!EdgeAllowed(slot, destination))
                {
                    return null;
                }

                double ratio = _ratios[random.Next(Math.Min(_ratios.Count, 12))];
                OperatorMatrixPatch mutated = patch
                    .WithOperator(slot, NewOperator(ratio, analysis, TemporalPriors.BrightnessEnvelope(analysis), random))
                    .WithOutputLevel(slot, 0.0)
                    .WithEdge(new ModulationEdge(slot, destination, true, 1.0));
                return new TopologyMutation(mutated, $"add OP{slot + 1} at {Text(ratio)} modulating OP{destination + 1}");
            }

            case 3:
            {
                // New operator straight into the mix, at a strong partial that the patch may be missing.
                if (free.Count == 0 || analysis.Partials.Count == 0)
                {
                    return null;
                }

                int slot = free[0];
                var strongest = analysis.Partials.OrderByDescending(static p => p.LevelDb).Take(8).ToList();
                double ratio = Math.Round(strongest[random.Next(strongest.Count)].RelativeFrequency, 2);
                OperatorMatrixPatch mutated = patch
                    .WithOperator(slot, NewOperator(ratio, analysis, TemporalPriors.OutputEnvelope(analysis), random))
                    .WithOutputLevel(slot, 0.3);
                return new TopologyMutation(mutated, $"add OP{slot + 1} at {Text(ratio)} to the output");
            }

            case 4:
            {
                // Remove an operator, together with its edges.
                if (used.Count < 2)
                {
                    return null;
                }

                int victim = used[random.Next(used.Count)];
                OperatorMatrixPatch mutated = patch.WithOperator(victim, patch.Operators[victim] with { Enabled = false }).WithOutputLevel(victim, 0.0);
                return mutated.FindAudibleOperators().Any(static a => a)
                    ? new TopologyMutation(mutated, $"remove OP{victim + 1}")
                    : null;
            }

            case 5:
            {
                // Move an operator to a neighbouring coarse ratio.
                int op = used[random.Next(used.Count)];
                if (patch.Operators[op].Frequency.Mode != OperatorFrequencyMode.Ratio)
                {
                    return null;
                }

                double current = patch.Operators[op].Frequency.Ratio;
                int nearest = 0;
                for (int i = 1; i < _ratios.Count; i++)
                {
                    if (Math.Abs(_ratios[i] - current) < Math.Abs(_ratios[nearest] - current))
                    {
                        nearest = i;
                    }
                }

                int next = Math.Clamp(nearest + (random.Next(2) == 0 ? -1 : 1), 0, _ratios.Count - 1);
                if (_ratios[next] == current)
                {
                    return null;
                }

                OperatorPatch changed = patch.Operators[op] with { Frequency = patch.Operators[op].Frequency with { Ratio = _ratios[next] } };
                return new TopologyMutation(patch.WithOperator(op, changed), $"OP{op + 1} ratio {Text(current)} -> {Text(_ratios[next])}");
            }

            case 6:
            {
                // Let a modulator also be heard, or take an operator out of the mix.
                int op = used[random.Next(used.Count)];
                if (patch.OutputLevels[op] == 0.0)
                {
                    return new TopologyMutation(patch.WithOutputLevel(op, 0.3), $"OP{op + 1} to the output");
                }

                OperatorMatrixPatch mutated = patch.WithOutputLevel(op, 0.0);
                return mutated.FindAudibleOperators().Any(static a => a) ? new TopologyMutation(mutated, $"OP{op + 1} out of the output") : null;
            }

            case 7:
            {
                // Waveform and mode are structural; they only vary when the profile offers a choice.
                int op = used[random.Next(used.Count)];
                if (_capabilities.SupportedWaveforms.Count > 1)
                {
                    OperatorWaveform[] others = [.. _capabilities.SupportedWaveforms.Where(w => w != patch.Operators[op].Waveform).Order()];
                    OperatorWaveform waveform = others[random.Next(others.Length)];
                    return new TopologyMutation(patch.WithOperator(op, patch.Operators[op] with { Waveform = waveform }), $"OP{op + 1} waveform {waveform}");
                }

                if (_capabilities.SupportedModes.Count > 1)
                {
                    OperatorMode[] others = [.. _capabilities.SupportedModes.Where(m => m != patch.Operators[op].Mode).Order()];
                    OperatorMode mode = others[random.Next(others.Length)];
                    return new TopologyMutation(patch.WithOperator(op, patch.Operators[op] with { Mode = mode }), $"OP{op + 1} mode {mode}");
                }

                return null;
            }

            default:
            {
                // Ratio to fixed frequency at the same pitch, and back. Indistinguishable on one note, different on a keyboard.
                int op = used[random.Next(used.Count)];
                OperatorFrequency frequency = patch.Operators[op].Frequency;
                if (frequency.Mode == OperatorFrequencyMode.Ratio && _capabilities.SupportsFixedFrequency && frequency.Ratio * analysis.FundamentalHz < 30.0)
                {
                    // Only very low operators are plausible fixed-frequency candidates without a second note to tell.
                    OperatorPatch changed = patch.Operators[op] with { Frequency = OperatorFrequency.AtFixed(Math.Round(frequency.Ratio * analysis.FundamentalHz, 2)) };
                    return new TopologyMutation(patch.WithOperator(op, changed), $"OP{op + 1} to fixed frequency");
                }

                return null;
            }
        }
    }

    private bool EdgeAllowed(int source, int destination) =>
        source == destination
            ? _capabilities.SupportsSelfFeedback
            : source > destination || (_capabilities.SupportsFullModulationMatrix && _capabilities.SupportsCircularFeedback);

    private OperatorPatch NewOperator(double ratio, TargetAnalysis analysis, OperatorEnvelope envelope, Random random)
    {
        OperatorWaveform[] waveforms = [.. _capabilities.SupportedWaveforms.Order()];
        OperatorMode[] modes = [.. _capabilities.SupportedModes.Order()];
        OperatorFrequency frequency = _capabilities.SupportsRatioFrequency
            ? OperatorFrequency.AtRatio(ratio)
            : OperatorFrequency.AtFixed(ratio * analysis.FundamentalHz);
        return OperatorPatch.Disabled with
        {
            Enabled = true,
            Mode = modes[0],
            Waveform = waveforms.Contains(OperatorWaveform.Sine) ? OperatorWaveform.Sine : waveforms[random.Next(waveforms.Length)],
            Frequency = frequency,
            AmplitudeEnvelope = envelope,
        };
    }

    private static string Text(double ratio) => ratio.ToString("0.##", CultureInfo.InvariantCulture);
}
