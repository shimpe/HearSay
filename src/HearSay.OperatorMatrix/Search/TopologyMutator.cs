using System.Globalization;
using HearSay.Analysis;
using HearSay.OperatorMatrix.Parameters;
using HearSay.OperatorMatrix.Priors;
using HearSay.Synthesis.Patches;
using HearSay.Partials;
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
    private const double NeutralAmount = 0.12;
    private const double NeutralLevel = 0.03;

    private readonly SynthesisCapabilities _capabilities;
    private readonly IReadOnlyList<double> _ratios;

    /// <summary>
    /// Allow any operator to switch between ratio and fixed frequency. On a single note the two cannot be told apart, so
    /// by default only very low operators are tried as fixed; a target set with several pitches makes the choice audible.
    /// </summary>
    public bool FixedFrequencyIdentifiable { get; init; }

    /// <summary>Frequencies in hertz at which the target set shows a partial that does not transpose; each becomes a candidate fixed-frequency operator.</summary>
    public IReadOnlyList<double> FixedFrequencyCandidatesHz { get; init; } = [];

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

    /// <summary>
    /// Proposes structural additions by trying them: every edge that could be added between audible operators and every
    /// new modulator at a simple ratio is rendered at two amounts with everything else left alone, and the variants
    /// that help most are returned, already at the better of the two amounts. Random mutations of the other kinds
    /// (removals, ratio moves, output routing) fill up the list. A blind random pick would rarely hit the one edge
    /// that matters; this costs about a hundred renders and finds it.
    /// </summary>
    /// <param name="patch">Patch to vary.</param>
    /// <param name="analysis">Target analysis.</param>
    /// <param name="evaluator">Evaluator of the search; screening renders count against its budget.</param>
    /// <param name="random">Random source of the search.</param>
    /// <param name="count">Number of mutations wanted.</param>
    /// <param name="maxScreened">Largest number of variants rendered for screening.</param>
    public IReadOnlyList<TopologyMutation> ProposeScreened(OperatorMatrixPatch patch, TargetAnalysis analysis, PatchEvaluator evaluator, Random random, int count, int maxScreened = 160)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(random);
        int n = patch.OperatorCount;
        bool[] audible = patch.FindAudibleOperators();
        var used = new List<int>();
        int free = -1;
        for (int i = 0; i < n; i++)
        {
            if (audible[i])
            {
                used.Add(i);
            }
            else if (free < 0)
            {
                free = i;
            }
        }

        var candidates = new List<TopologyMutation>();

        // A few noise washes first, so that the cap on screened variants cannot cut them off.
        if (free >= 0)
        {
            candidates.AddRange(NoiseWashes(patch, analysis, audible, free));
        }

        foreach (int destination in used)
        {
            foreach (int source in used)
            {
                if (EdgeAllowed(source, destination) && patch.ModulationMatrix.Find(source, destination) is not { Enabled: true })
                {
                    foreach (double amount in new[] { 0.3, 0.6, 1.2 })
                    {
                        candidates.Add(new TopologyMutation(patch.WithEdge(new ModulationEdge(source, destination, true, amount)), $"add edge OP{source + 1} -> OP{destination + 1}"));
                    }
                }
            }
        }

        if (free >= 0)
        {
            OperatorEnvelope envelope = TemporalPriors.BrightnessEnvelope(analysis);
            foreach (int destination in used)
            {
                if (!EdgeAllowed(free, destination))
                {
                    continue;
                }

                for (int r = 0; r < Math.Min(_ratios.Count, 10); r++)
                {
                    foreach (double amount in new[] { 0.5, 1.5 })
                    {
                        OperatorMatrixPatch variant = patch
                            .WithOperator(free, NewOperator(_ratios[r], analysis, envelope, random))
                            .WithOutputLevel(free, 0.0)
                            .WithEdge(new ModulationEdge(free, destination, true, amount));
                        candidates.Add(new TopologyMutation(variant, $"add OP{free + 1} at {Text(_ratios[r])} modulating OP{destination + 1}"));
                    }
                }
            }
        }


        // Partials that do not transpose: a fixed-frequency operator straight into the mix, screened at two levels.
        if (free >= 0 && _capabilities.SupportsFixedFrequency)
        {
            OperatorEnvelope envelope = TemporalPriors.OutputEnvelope(analysis);
            foreach (double hz in FixedFrequencyCandidatesHz.Take(4))
            {
                foreach (double level in new[] { 0.1, 0.3 })
                {
                    OperatorMatrixPatch variant = patch
                        .WithOperator(free, OperatorPatch.Sine(OperatorFrequency.AtFixed(hz), envelope))
                        .WithOutputLevel(free, level);
                    candidates.Add(new TopologyMutation(variant, FormattableString.Invariant($"add OP{free + 1} fixed at {hz:0.#} Hz to the output")));
                }
            }
        }
        // Ratio operators pinned to their current frequency: on a target set with several pitches this tells a
        // fixed-frequency operator from a transposing one.
        if (FixedFrequencyIdentifiable && _capabilities.SupportsFixedFrequency)
        {
            foreach (int op in used)
            {
                OperatorPatch current = patch.Operators[op];
                if (current.Frequency.Mode == OperatorFrequencyMode.Ratio)
                {
                    double hz = Math.Round(current.FrequencyHz(analysis.FundamentalHz), 1);
                    candidates.Add(new TopologyMutation(patch.WithOperator(op, current with { Frequency = OperatorFrequency.AtFixed(hz), FineTuneCents = 0 }), FormattableString.Invariant($"OP{op + 1} fixed at {hz:0.#} Hz")));
                }
            }
        }

        candidates.RemoveAll(c => !PatchCompatibilityValidator.Instance.Validate(c.Patch, _capabilities).IsCompatible);
        if (candidates.Count > maxScreened)
        {
            candidates.RemoveRange(maxScreened, candidates.Count - maxScreened);
        }

        var result = new List<TopologyMutation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (candidates.Count > 0 && evaluator.Remaining > candidates.Count)
        {
            IReadOnlyList<PatchEvaluation> screened = evaluator.Evaluate([.. candidates.Select(static c => c.Patch)], HearSay.Objective.EvaluationLevel.Normal);
            var ranked = new List<(double Loss, int Index)>();
            for (int i = 0; i < screened.Count; i++)
            {
                ranked.Add((screened[i].Loss, i));
            }

            ranked.Sort(static (a, b) =>
            {
                int order = a.Loss.CompareTo(b.Loss);
                return order != 0 ? order : a.Index.CompareTo(b.Index);
            });

            // A non-transposing partial is rare and decisive: its best variant always gets a slot.
            foreach ((double _, int index) in ranked)
            {
                if (candidates[index].Description.Contains(" fixed at ", StringComparison.Ordinal) && seen.Add(candidates[index].Description))
                {
                    result.Add(candidates[index]);
                    break;
                }
            }

            // Half of the list comes from screening; one description is one structure, whatever the amount. Edges, new
            // operators and noise washes take turns, best first: there are many more operator variants than edges, and
            // they would otherwise crowd out the single edge that is missing. After the edges, the kind whose best
            // variant helps more goes first.
            var edgesFirst = new List<int>();
            var operatorsFirst = new List<int>();
            var noiseFirst = new List<int>();
            foreach ((double _, int index) in ranked)
            {
                string description = candidates[index].Description;
                (description.StartsWith("add edge", StringComparison.Ordinal) || description.Contains(" fixed at ", StringComparison.Ordinal) ? edgesFirst
                    : description.Contains(" noise", StringComparison.Ordinal) ? noiseFirst
                    : operatorsFirst).Add(index);
            }

            List<int>[] turns = noiseFirst.Count > 0 && (operatorsFirst.Count == 0 || screened[noiseFirst[0]].Loss < screened[operatorsFirst[0]].Loss)
                ? [edgesFirst, noiseFirst, operatorsFirst]
                : [edgesFirst, operatorsFirst, noiseFirst];
            for (int i = 0; i < turns.Max(static t => t.Count) && result.Count < (count + 1) / 2; i++)
            {
                foreach (List<int> list in turns)
                {
                    if (i < list.Count && result.Count < (count + 1) / 2 && seen.Add(candidates[list[i]].Description))
                    {
                        result.Add(candidates[list[i]]);
                    }
                }
            }
        }

        foreach (TopologyMutation mutation in Propose(patch, analysis, random, count * 2))
        {
            if (result.Count >= count)
            {
                break;
            }

            if (seen.Add(mutation.Description))
            {
                result.Add(mutation);
            }
        }

        return result;
    }

    /// <summary>
    /// Partials that stay at the same frequency in hertz across notes of different pitch: the signature of a
    /// fixed-frequency operator. Only a target set can reveal them; on a single note every partial transposes.
    /// </summary>
    /// <param name="analyses">Analyses of notes at several pitches.</param>
    /// <param name="toleranceCents">Largest frequency difference of a partial between notes.</param>
    /// <returns>Frequencies in hertz, strongest first; empty when the notes share a pitch.</returns>
    public static IReadOnlyList<double> FindFixedPartials(IReadOnlyList<TargetAnalysis> analyses, double toleranceCents = 30.0)
    {
        ArgumentNullException.ThrowIfNull(analyses);
        if (analyses.Count < 2 || analyses.Select(static a => Math.Round(a.FundamentalHz)).Distinct().Count() < 2)
        {
            return [];
        }

        var result = new List<(double Hz, double LevelDb)>();
        foreach (SpectralPartial candidate in analyses[0].Partials)
        {
            // A partial at n × f0 of the reference note is not fixed just because another note happens to have a
            // partial there too; it has to be absent from the harmonic grid of at least one other note.
            bool everywhere = true;
            bool offGrid = false;
            double level = candidate.LevelDb;
            for (int i = 1; i < analyses.Count && everywhere; i++)
            {
                TargetAnalysis other = analyses[i];
                SpectralPartial? match = null;
                foreach (SpectralPartial p in other.Partials)
                {
                    if (Math.Abs(1200.0 * Math.Log2(p.FrequencyHz / candidate.FrequencyHz)) <= toleranceCents)
                    {
                        match = p;
                        break;
                    }
                }

                if (match is null)
                {
                    everywhere = false;
                    break;
                }

                level = Math.Min(level, match.LevelDb);
                double relative = match.FrequencyHz / other.FundamentalHz;
                offGrid |= Math.Abs(1200.0 * Math.Log2(relative / Math.Max(1.0, Math.Round(relative)))) > toleranceCents;
            }

            if (everywhere && offGrid)
            {
                result.Add((candidate.FrequencyHz, level));
            }
        }

        return [.. result.OrderByDescending(static r => r.LevelDb).Select(static r => Math.Round(r.Hz, 1))];
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

        int kind = random.Next(10);
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

                // New elements start almost silent: the child then sounds like its parent, and optimization can only make
                // it better. A disruptive start would need most of a short budget just to get back to where it began.
                double amount = NeutralAmount;
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
                    .WithEdge(new ModulationEdge(slot, destination, true, NeutralAmount));
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
                    .WithOutputLevel(slot, NeutralLevel);
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
                    return new TopologyMutation(patch.WithOutputLevel(op, NeutralLevel), $"OP{op + 1} to the output");
                }

                OperatorMatrixPatch mutated = patch.WithOutputLevel(op, 0.0);
                return mutated.FindAudibleOperators().Any(static a => a) ? new TopologyMutation(mutated, $"OP{op + 1} out of the output") : null;
            }

            case 7:
            {
                // Waveform and mode are structural; they only vary when the profile offers a choice.
                int op = used[random.Next(used.Count)];
                OperatorWaveform present = patch.Operators[op].Waveform;
                if (present is OperatorWaveform.Triangle or OperatorWaveform.Saw or OperatorWaveform.Square or OperatorWaveform.Pulse && random.Next(2) == 0)
                {
                    // The harmonic limit of a band-limited operator is its brightness: halve or double it.
                    int limit = patch.Operators[op].HarmonicLimit;
                    int changed = Math.Clamp(random.Next(2) == 0 ? limit / 2 : limit * 2, 4, 128);
                    if (changed != limit)
                    {
                        return new TopologyMutation(patch.WithOperator(op, patch.Operators[op] with { HarmonicLimit = changed }), $"OP{op + 1} harmonics {limit} -> {changed}");
                    }
                }

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

            case 8:
            {
                // An operator that is heard and also modulates cannot give both roles their own shape with one amplitude
                // envelope; an envelope on the edge lets the modulation amount move on its own.
                if (!_capabilities.SupportsDynamicModulationMatrix)
                {
                    return null;
                }

                var candidates = new List<ModulationEdge>();
                foreach (ModulationEdge edge in patch.ModulationMatrix.Edges)
                {
                    if (edge.Enabled && edge.AmountEnvelope is null && audible[edge.SourceOperator] && audible[edge.DestinationOperator]
                        && (edge.IsSelfFeedback || patch.OutputLevels[edge.SourceOperator] != 0.0))
                    {
                        candidates.Add(edge);
                    }
                }

                if (candidates.Count == 0)
                {
                    return null;
                }

                ModulationEdge chosen = candidates[random.Next(candidates.Count)];
                return new TopologyMutation(
                    patch.WithEdge(chosen with { AmountEnvelope = TemporalPriors.BrightnessEnvelope(analysis) }),
                    $"amount envelope on edge OP{chosen.SourceOperator + 1} -> OP{chosen.DestinationOperator + 1}");
            }

            default:
            {
                // Ratio to fixed frequency at the same pitch, and back. Indistinguishable on one note, different on a keyboard.
                int op = used[random.Next(used.Count)];
                OperatorFrequency frequency = patch.Operators[op].Frequency;
                if (frequency.Mode == OperatorFrequencyMode.Ratio && _capabilities.SupportsFixedFrequency && (FixedFrequencyIdentifiable || frequency.Ratio * analysis.FundamentalHz < 30.0))
                {
                    // Only very low operators are plausible fixed-frequency candidates without a second note to tell.
                    OperatorPatch changed = patch.Operators[op] with { Frequency = OperatorFrequency.AtFixed(Math.Round(frequency.Ratio * analysis.FundamentalHz, 2)) };
                    return new TopologyMutation(patch.WithOperator(op, changed), $"OP{op + 1} to fixed frequency");
                }

                return null;
            }
        }
    }

    /// <summary>
    /// Noise next to the structure, in one step: white noise straight into the mix, and a sine at a high fixed
    /// frequency that noise modulates deeply, which spreads into a band of noise around that frequency and is how a
    /// coloured wash, such as the high-passed noise of a cymbal, is made from these operators. Through "add a sine to
    /// the output" and "make it noise" a wash would need an intermediate that sounds worse. Nothing for profiles
    /// without noise.
    /// </summary>
    private List<TopologyMutation> NoiseWashes(OperatorMatrixPatch patch, TargetAnalysis analysis, bool[] audible, int free)
    {
        var result = new List<TopologyMutation>();
        if (!_capabilities.SupportedWaveforms.Contains(OperatorWaveform.Noise))
        {
            return result;
        }

        double loudest = Math.Max(0.1, patch.OutputLevels.Max());
        OperatorEnvelope decaying = TemporalPriors.OutputEnvelope(analysis);
        OperatorEnvelope burst = OperatorEnvelope.Adsr(0.001, 0.03, 0.0, 0.03);
        OperatorPatch Noise(OperatorEnvelope envelope) => OperatorPatch.Sine(OperatorFrequency.AtRatio(1), envelope) with { Waveform = OperatorWaveform.Noise };

        foreach (double level in new[] { 0.15, 0.4 })
        {
            result.Add(new TopologyMutation(patch.WithOperator(free, Noise(decaying)).WithOutputLevel(free, level * loudest), $"add OP{free + 1} white noise to the output"));
        }

        result.Add(new TopologyMutation(patch.WithOperator(free, Noise(burst)).WithOutputLevel(free, 0.3 * loudest), $"add OP{free + 1} noise burst to the output"));

        int source = Array.FindIndex(audible, free + 1, static a => !a);
        if (source < 0 || !EdgeAllowed(source, free) || !_capabilities.SupportedWaveforms.Contains(OperatorWaveform.Sine))
        {
            return result;
        }

        double depth = Math.Min(4.0, _capabilities.MaxModulationRadiansPerDestination);
        foreach (double hz in new[] { 3000.0, 7000.0, 12000.0 })
        {
            OperatorFrequency frequency = _capabilities.SupportsFixedFrequency ? OperatorFrequency.AtFixed(hz) : OperatorFrequency.AtRatio(Math.Round(hz / analysis.FundamentalHz, 2));
            foreach (double level in new[] { 0.15, 0.4 })
            {
                OperatorMatrixPatch variant = patch
                    .WithOperator(free, OperatorPatch.Sine(frequency, decaying))
                    .WithOutputLevel(free, level * loudest)
                    .WithOperator(source, Noise(OperatorEnvelope.Constant()))
                    .WithOutputLevel(source, 0.0)
                    .WithEdge(new ModulationEdge(source, free, true, depth));
                result.Add(new TopologyMutation(variant, FormattableString.Invariant($"add OP{free + 1} with a noise band at {hz:0} Hz from OP{source + 1} to the output")));
            }
        }

        return result;
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
