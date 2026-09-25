using System.Globalization;
using HearSay.Analysis;
using HearSay.OperatorMatrix.Canonicalization;
using HearSay.OperatorMatrix.Parameters;
using HearSay.Partials;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.OperatorMatrix.Priors;

/// <summary>
/// Hypothesis generator for sine phase-modulation networks. It reads the partial cloud of the target with the
/// arithmetic of simple FM: a carrier at ratio <c>c</c> with a modulator at ratio <c>m</c> produces sidebands at
/// <c>|c ± k·m|</c>, and Carson's rule <c>BW ≈ 2(I + 1)·fm</c> gives a rough modulation index from the bandwidth.
/// </summary>
/// <remarks>
/// These formulas only rank guesses. They describe one carrier and one sine modulator; a six-operator network with
/// feedback does not follow them, and nothing downstream treats them as an inverse of the synthesis. All sine-FM
/// knowledge of HearSay is in this class; the search coordinator works with whatever hypotheses it is given.
/// </remarks>
public sealed class SinePhaseModulationPriorProvider : ISynthesisPriorProvider
{
    private const double MatchCents = 35.0;

    /// <summary>How many times longer a sideband has to ring than the carrier to take its place.</summary>
    private const double PersistenceMargin = 1.5;

    /// <inheritdoc />
    public string Name => StackedModulatorsOnly ? "sine-phase-modulation stacks" : "sine-phase-modulation";

    /// <summary>Largest number of hypotheses returned.</summary>
    public int MaxHypotheses { get; init; } = 128;

    /// <summary>
    /// Only the structures with a second modulator read from the lines it adds: registered next to the full provider,
    /// they form a group of their own, so that a DX7 stack, which sounds right only once its amounts are found, keeps
    /// a basin next to readings that sound closer before any tuning.
    /// </summary>
    public bool StackedModulatorsOnly { get; init; }

    /// <summary>
    /// With <see cref="StackedModulatorsOnly"/>: the prior score of the best full hypothesis above which the spectrum
    /// counts as explained and nothing is proposed.
    /// </summary>
    public double ExplainedAbove { get; init; } = 0.9;

    /// <inheritdoc />
    public IReadOnlyList<PatchHypothesis> Generate(TargetAnalysis analysis, SynthesisCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (!capabilities.SupportedWaveforms.Contains(OperatorWaveform.Sine)
            || !capabilities.SupportedModes.Contains(OperatorMode.PhaseModulation)
            || !(capabilities.SupportsRatioFrequency || capabilities.SupportsFixedFrequency))
        {
            return [];
        }

        var context = new Context(analysis, capabilities);
        var hypotheses = new List<PatchHypothesis>();
        int n = capabilities.OperatorCount;

        // A single operator at the strongest partial: the baseline every other hypothesis has to beat.
        hypotheses.Add(context.Build("single sine", 0.0, b => b.Carrier(context.StrongestRatio, 0.8)));

        List<Pair> pairs = context.RankPairs();

        // A dense harmonic spectrum is covered equally well from a carrier in its middle as from its fundamental, and
        // sideband counting alone cannot tell them apart. Pairs whose carrier sits on the fundamental or on the
        // strongest partial are therefore interleaved with the best-scoring ones, so that both kinds reach screening.
        List<Pair> anchored = [.. pairs.Where(p => p.Carrier == context.StrongestRatio || p.Carrier == context.StrongestHarmonicBase).Take(8)];
        var interleaved = new List<Pair>(pairs.Count + anchored.Count);
        for (int i = 0; i < Math.Max(pairs.Count, anchored.Count); i++)
        {
            if (i < anchored.Count && !interleaved.Contains(anchored[i]))
            {
                interleaved.Add(anchored[i]);
            }

            if (i < pairs.Count && !interleaved.Contains(pairs[i]))
            {
                interleaved.Add(pairs[i]);
            }
        }

        pairs = interleaved;
        foreach (Pair pair in pairs.Take(16))
        {
            if (n >= 2)
            {
                hypotheses.Add(context.Build($"stack {Text(pair.Carrier)}:{Text(pair.Modulator)}", pair.Score, b =>
                {
                    int c = b.Carrier(pair.Carrier, 0.8);
                    b.Modulator(pair.Modulator, c, pair.Index);
                }));
            }
        }

        foreach (Pair pair in pairs.Take(6))
        {
            if (n >= 2 && capabilities.SupportsSelfFeedback)
            {
                hypotheses.Add(context.Build($"stack {Text(pair.Carrier)}:{Text(pair.Modulator)} with modulator feedback", pair.Score - 0.03, b =>
                {
                    int c = b.Carrier(pair.Carrier, 0.8);
                    int m = b.Modulator(pair.Modulator, c, pair.Index * 0.7);
                    b.Edge(m, m, 0.7);
                }));
            }

            if (n >= 3)
            {
                hypotheses.Add(context.Build($"three-stack {Text(pair.Carrier)}:{Text(pair.Modulator)}:{Text(pair.Modulator)}", pair.Score - 0.04, b =>
                {
                    int c = b.Carrier(pair.Carrier, 0.8);
                    int m = b.Modulator(pair.Modulator, c, pair.Index * 0.8);
                    b.Modulator(pair.Modulator, m, 1.0);
                }));
                hypotheses.Add(context.Build($"three-stack {Text(pair.Carrier)}:{Text(pair.Modulator)}:{Text(pair.Carrier)}", pair.Score - 0.05, b =>
                {
                    int c = b.Carrier(pair.Carrier, 0.8);
                    int m = b.Modulator(pair.Modulator, c, pair.Index * 0.8);
                    b.Modulator(pair.Carrier, m, 1.0);
                }));
            }
        }

        // Second modulators read from the lines they add, as a modulator of the modulator (a DX7 stack, also with
        // feedback on its top operator) and next to it on the carrier.
        var stacked = new List<PatchHypothesis>();
        if (n >= 3)
        {
            foreach ((Pair pair, double second, double score) in context.SecondModulators(pairs))
            {
                // The carrier and its first sidebands predict the same lines; when a sideband line is the strongest
                // partial, such as the fundamental of a stack with a high first modulator, it is offered as carrier too.
                foreach (double carrier in new[] { pair.Carrier, context.StrongestLine(pair.Carrier, pair.Modulator) }.Distinct())
                {
                    foreach (double feedback in capabilities.SupportsSelfFeedback ? new[] { 0.0, 0.7 } : [0.0])
                    {
                        string name = $"three-stack {Text(carrier)}:{Text(pair.Modulator)}:{Text(second)}" + (feedback > 0 ? " with top feedback" : string.Empty);
                        stacked.Add(context.Build(name, score - (carrier == pair.Carrier ? 0.02 : 0.015) - (feedback > 0 ? 0.005 : 0.0), b =>
                        {
                            int c = b.Carrier(carrier, 0.8);
                            int m = b.Modulator(pair.Modulator, c, Math.Max(1.0, pair.Index * 0.8));
                            int top = b.Modulator(second, m, 2.0);
                            if (feedback > 0)
                            {
                                b.Edge(top, top, feedback);
                            }
                        }));
                    }
                }

                stacked.Add(context.Build($"carrier {Text(pair.Carrier)} with parallel modulators {Text(pair.Modulator)} and {Text(second)}", score - 0.03, b =>
                {
                    int c = b.Carrier(pair.Carrier, 0.8);
                    b.Modulator(pair.Modulator, c, pair.Index * 0.8);
                    b.Modulator(second, c, 1.0);
                }));
            }
        }

        if (StackedModulatorsOnly)
        {
            // Only where the simpler structures leave much of the spectrum unexplained: otherwise these would take
            // survivor slots from a reading that is already right.
            double explained = new SinePhaseModulationPriorProvider { MaxHypotheses = MaxHypotheses }.Generate(analysis, capabilities).Select(static h => h.PriorScore).DefaultIfEmpty(0.0).Max();
            if (explained >= ExplainedAbove || n < 3)
            {
                return [];
            }

            // Ranked by how well the strengths of their lines fit, with the fitted indices as starting amounts: lines
            // alone cannot tell apart the readings of one grid of lines that a stack produces.
            var modelled = new List<PatchHypothesis>();
            foreach (var s in context.ModelledStacks(pairs))
            {
                string shape = s.Parallel
                    ? $"carrier {Text(s.Carrier)} with parallel modulators {Text(s.First)} and {Text(s.Second)}"
                    : $"three-stack {Text(s.Carrier)}:{Text(s.First)}:{Text(s.Second)}";
                foreach (double feedback in !s.Parallel && capabilities.SupportsSelfFeedback ? new[] { 0.0, 0.7 } : [0.0])
                {
                    modelled.Add(context.Build($"{shape} (modelled indices {s.Index:0.#} and {s.SecondIndex:0.#}){(feedback > 0 ? " with top feedback" : string.Empty)}", 1.0 - s.Error - (feedback > 0 ? 0.005 : 0.0), b =>
                    {
                        int c = b.Carrier(s.Carrier, 0.8);
                        int m = b.Modulator(s.First, c, s.Index);
                        int top = b.Modulator(s.Second, s.Parallel ? c : m, s.SecondIndex);
                        if (feedback > 0)
                        {
                            b.Edge(top, top, feedback);
                        }
                    }));
                }
            }

            return Distinct(modelled, capabilities);
        }

        hypotheses.AddRange(stacked);

        // Deeper stacks with a second modulator at a small ratio: the modulator itself becomes bright, which fills the
        // spectrum much more densely than a single sine modulator can.
        if (n >= 3)
        {
            foreach (Pair pair in pairs.Take(6))
            {
                foreach (double second in new[] { 1.0, 2.0, 3.0 })
                {
                    if (second == pair.Modulator || second == pair.Carrier)
                    {
                        continue;
                    }

                    hypotheses.Add(context.Build($"three-stack {Text(pair.Carrier)}:{Text(pair.Modulator)}:{Text(second)}", pair.Score - 0.06, b =>
                    {
                        int c = b.Carrier(pair.Carrier, 0.8);
                        int m = b.Modulator(pair.Modulator, c, pair.Index * 0.8);
                        b.Modulator(second, m, 1.0);
                    }));
                }
            }
        }

        // Two modulators in parallel on one carrier: different sideband spacings at once.
        if (n >= 3)
        {
            foreach (IGrouping<double, Pair> byCarrier in pairs.Take(40).GroupBy(static p => p.Carrier).Take(4))
            {
                Pair[] best = [.. byCarrier.Take(3)];
                for (int a = 0; a < best.Length; a++)
                {
                    for (int b2 = a + 1; b2 < best.Length; b2++)
                    {
                        Pair first = best[a], second = best[b2];
                        hypotheses.Add(context.Build($"carrier {Text(first.Carrier)} with parallel modulators {Text(first.Modulator)} and {Text(second.Modulator)}", Math.Max(first.Score, second.Score) - 0.03, b =>
                        {
                            int c = b.Carrier(first.Carrier, 0.8);
                            b.Modulator(first.Modulator, c, first.Index * 0.7);
                            b.Modulator(second.Modulator, c, second.Index * 0.7);
                        }));
                    }
                }
            }
        }

        // Several independent stacks: each one explains what the previous ones left over.
        for (int stacks = 2; stacks <= n / 2; stacks++)
        {
            foreach (List<Pair> cover in context.GreedyCovers(pairs, stacks))
            {
                double score = context.CoverScore(cover) - (0.02 * cover.Count);
                string name = string.Join(" + ", cover.Select(p => $"{Text(p.Carrier)}:{Text(p.Modulator)}"));
                hypotheses.Add(context.Build($"{cover.Count} stacks {name}", score, b =>
                {
                    foreach (Pair pair in cover)
                    {
                        int c = b.Carrier(pair.Carrier, context.LevelAt(pair.Carrier));
                        b.Modulator(pair.Modulator, c, pair.Index);
                    }
                }));
            }
        }

        // Additive: operators straight at the strongest partials. The natural choice for inharmonic spectra.
        IReadOnlyList<SpectralPartial> strongest = context.StrongestPartials(n);
        for (int count = 2; count <= strongest.Count; count++)
        {
            int take = count;
            double covered = context.Coverage(strongest.Take(take).Select(static p => p.RelativeFrequency));
            hypotheses.Add(context.Build($"{take} additive operators", covered - (0.03 * take), b =>
            {
                foreach (SpectralPartial partial in strongest.Take(take))
                {
                    b.Carrier(Math.Round(partial.RelativeFrequency, 2), context.LevelAt(partial.RelativeFrequency));
                }
            }));
        }

        // Additive body with one modulated operator for the bright part.
        if (strongest.Count >= 2 && pairs.Count > 0 && n >= 4)
        {
            Pair bright = pairs[0];
            hypotheses.Add(context.Build($"additive body with stack {Text(bright.Carrier)}:{Text(bright.Modulator)}", bright.Score - 0.02, b =>
            {
                int c = b.Carrier(bright.Carrier, 0.8);
                b.Modulator(bright.Modulator, c, bright.Index);
                foreach (SpectralPartial partial in strongest.Take(Math.Min(n - 2, 3)))
                {
                    if (Math.Abs(partial.RelativeFrequency - bright.Carrier) > 0.05)
                    {
                        b.Carrier(Math.Round(partial.RelativeFrequency, 2), context.LevelAt(partial.RelativeFrequency) * 0.7);
                    }
                }
            }));
        }

        // Self-feedback turns a sine into a sawtooth-like spectrum: dense harmonics that fall off evenly.
        if (capabilities.SupportsSelfFeedback)
        {
            foreach (double feedback in new[] { 0.8, 1.4 })
            {
                hypotheses.Add(context.Build(FormattableString.Invariant($"feedback operator ({feedback:0.0} rad)"), context.Coverage(Enumerable.Range(1, 12).Select(static h => (double)h)) - 0.1, b =>
                {
                    int c = b.Carrier(context.StrongestHarmonicBase, 0.8);
                    b.Edge(c, c, feedback);
                }));
            }
        }

        // Slow beating suggests two nearly identical voices that are slightly detuned against each other.
        if (analysis.Beating.IsPresent && pairs.Count > 0)
        {
            Pair pair = pairs[0];
            double detune = analysis.Beating.RateHz;
            if (n >= 4)
            {
                hypotheses.Add(context.Build($"detuned double stack {Text(pair.Carrier)}:{Text(pair.Modulator)}", pair.Score + 0.05, b =>
                {
                    int c1 = b.Carrier(pair.Carrier, 0.6);
                    b.Modulator(pair.Modulator, c1, pair.Index);
                    int c2 = b.Carrier(pair.Carrier, 0.6, detune);
                    b.Modulator(pair.Modulator, c2, pair.Index, detune);
                }));
            }

            hypotheses.Add(context.Build("detuned sine pair", 0.05, b =>
            {
                b.Carrier(context.StrongestRatio, 0.6);
                b.Carrier(context.StrongestRatio, 0.6, detune);
            }));
        }

        return Distinct(hypotheses, capabilities);
    }

    /// <summary>Relabelled twins and anything the profile cannot represent are dropped; the best-scored twin stays.</summary>
    private List<PatchHypothesis> Distinct(List<PatchHypothesis> hypotheses, SynthesisCapabilities capabilities)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<PatchHypothesis>();
        foreach (PatchHypothesis hypothesis in hypotheses.OrderByDescending(static h => h.PriorScore))
        {
            if (!PatchCompatibilityValidator.Instance.Validate(hypothesis.Patch, capabilities).IsCompatible)
            {
                continue;
            }

            if (seen.Add(PatchCanonicalizer.CanonicalKey(hypothesis.Patch).Value))
            {
                result.Add(hypothesis);
                if (result.Count >= MaxHypotheses)
                {
                    break;
                }
            }
        }

        return result;
    }

    private static string Text(double ratio) => ratio.ToString("0.##", CultureInfo.InvariantCulture);

    private readonly record struct Pair(double Carrier, double Modulator, double Index, double Score);

    /// <summary>Target data prepared once for all hypotheses.</summary>
    private sealed class Context
    {
        private readonly TargetAnalysis _analysis;
        private readonly SynthesisCapabilities _capabilities;
        private readonly double[] _relative;
        private readonly double[] _salience;
        private readonly double[] _amplitude;
        private readonly double[] _persistence;
        private List<double> _candidates = [];
        private readonly double _salienceSum;
        private readonly OperatorEnvelope _outputEnvelope;
        private readonly OperatorEnvelope _brightnessEnvelope;
        private readonly PitchModel _pitch;

        public Context(TargetAnalysis analysis, SynthesisCapabilities capabilities)
        {
            _analysis = analysis;
            _capabilities = capabilities;
            IReadOnlyList<SpectralPartial> partials = analysis.Partials;
            double max = partials.Count > 0 ? partials.Max(static p => p.LevelDb) : 0.0;
            _relative = [.. partials.Select(static p => p.RelativeFrequency)];
            _salience = [.. partials.Select(p => Math.Max(0.0, 1.0 - ((max - p.LevelDb) / 50.0)))];
            _amplitude = [.. partials.Select(p => Math.Pow(10.0, (p.LevelDb - max) / 20.0))];

            // How long each partial rings: the lifetime of the partial track on it, if any.
            _persistence = [.. partials.Select(p => analysis.PartialTracks
                .Where(t => Math.Abs(1200.0 * Math.Log2(t.FrequencyHz / p.FrequencyHz)) < MatchCents)
                .Select(static t => t.DeathSeconds - t.BirthSeconds)
                .DefaultIfEmpty(0.0)
                .Max())];
            _salienceSum = _salience.Sum();
            _outputEnvelope = TemporalPriors.OutputEnvelope(analysis);
            _brightnessEnvelope = TemporalPriors.BrightnessEnvelope(analysis);
            _pitch = TemporalPriors.Pitch(analysis);

            int strongest = 0;
            for (int i = 1; i < _amplitude.Length; i++)
            {
                if (_amplitude[i] > _amplitude[strongest])
                {
                    strongest = i;
                }
            }

            StrongestRatio = _relative.Length > 0 ? Snap(_relative[strongest]) : 1.0;
            StrongestHarmonicBase = _relative.Length > 0 && _relative.Min() < 0.75 ? 0.5 : 1.0;
        }

        public double StrongestRatio { get; }

        public double StrongestHarmonicBase { get; }

        /// <summary>Ratios close to a simple value are snapped to it; others keep two decimals so that inharmonic partials survive.</summary>
        private static double Snap(double ratio)
        {
            foreach (double candidate in PatchParameterSchema.DefaultRatios)
            {
                if (Math.Abs(1200.0 * Math.Log2(ratio / candidate)) < 15.0)
                {
                    return candidate;
                }
            }

            return Math.Round(ratio, 2);
        }

        public IReadOnlyList<SpectralPartial> StrongestPartials(int count) =>
            [.. _analysis.Partials.OrderByDescending(static p => p.LevelDb).Take(count)];

        public double LevelAt(double ratio)
        {
            double best = 0.3;
            for (int i = 0; i < _relative.Length; i++)
            {
                if (Math.Abs(1200.0 * Math.Log2(_relative[i] / ratio)) < MatchCents)
                {
                    best = Math.Max(best, _amplitude[i]);
                }
            }

            return Math.Clamp(0.8 * best, 0.05, 0.8);
        }

        /// <summary>Salience-weighted share of the target partials that lie on one of the given relative frequencies.</summary>
        public double Coverage(IEnumerable<double> predicted)
        {
            if (_salienceSum <= 0)
            {
                return 0.0;
            }

            double[] list = [.. predicted];
            double covered = 0;
            for (int i = 0; i < _relative.Length; i++)
            {
                foreach (double p in list)
                {
                    if (p > 0.05 && Math.Abs(1200.0 * Math.Log2(_relative[i] / p)) < MatchCents)
                    {
                        covered += _salience[i];
                        break;
                    }
                }
            }

            return covered / _salienceSum;
        }

        public List<Pair> RankPairs()
        {
            var ratios = new List<double>(PatchParameterSchema.DefaultRatios);
            void Consider(double ratio, double limit)
            {
                // Both the snapped value and, when it differs, the measured one: a simple ratio for a sound that has
                // one, the exact value for an inharmonic sound whose partials lie just off a simple ratio.
                foreach (double candidate in new[] { Snap(ratio), Math.Round(ratio, 3) })
                {
                    if (candidate > 0.2 && candidate < limit && !ratios.Any(r => Math.Abs(1200.0 * Math.Log2(r / candidate)) < 2.0))
                    {
                        ratios.Add(candidate);
                    }
                }
            }

            IReadOnlyList<SpectralPartial> carriers = StrongestPartials(6);
            foreach (SpectralPartial partial in carriers)
            {
                Consider(partial.RelativeFrequency, 20.0);
            }

            // Differences between strong partials are candidate modulator ratios: that is the sideband spacing.
            IReadOnlyList<SpectralPartial> strong = StrongestPartials(8);
            for (int a = 0; a < strong.Count; a++)
            {
                for (int b = a + 1; b < strong.Count; b++)
                {
                    Consider(Math.Abs(strong[a].RelativeFrequency - strong[b].RelativeFrequency), 16.0);
                }
            }

            // A modulator above its carrier folds the lower sideband over zero: c - m is heard at m - c, so the
            // modulator is the sum of the carrier and that partial.
            foreach (SpectralPartial carrier in carriers)
            {
                foreach (SpectralPartial partial in StrongestPartials(12))
                {
                    Consider(carrier.RelativeFrequency + partial.RelativeFrequency, 16.0);
                }
            }

            _candidates = ratios;
            var pairs = new List<Pair>();
            foreach (double carrier in ratios)
            {
                if (carrier > 8.0)
                {
                    continue;
                }

                foreach (double modulator in ratios)
                {
                    Pair pair = ScorePair(carrier, modulator, _salience);
                    if (pair.Score > 0.05)
                    {
                        pairs.Add(Canonical(pair));
                    }
                }
            }

            // Canonical forms of different readings coincide; keep the best of each.
            pairs = [.. pairs
                .GroupBy(static p => (Math.Round(p.Carrier, 3), Math.Round(p.Modulator, 3)))
                .Select(static g => g.MaxBy(static p => p.Score))];
            pairs.Sort(static (a, b) =>
            {
                int result = b.Score.CompareTo(a.Score);
                if (result == 0)
                {
                    result = a.Carrier.CompareTo(b.Carrier);
                }

                return result != 0 ? result : a.Modulator.CompareTo(b.Modulator);
            });
            return pairs;
        }

        /// <summary>
        /// The reading of a pair whose carrier is the line that rings longest. A carrier at <c>c</c> and one at the
        /// sideband <c>|c + k·m|</c> predict much the same lines, but when the modulation dies away, as in struck and
        /// plucked sounds, only the carrier keeps sounding: the sidebands of a six-operator cymbal decay four to ten
        /// times faster than its carriers. A line only takes over when it outlasts the carrier clearly.
        /// </summary>
        private Pair Canonical(Pair pair)
        {
            int own = PartialAt(pair.Carrier);
            double ownPersistence = own >= 0 ? _persistence[own] : 0.0;
            int best = -1;
            double bestPersistence = ownPersistence * PersistenceMargin;
            for (int k = -2; k <= 2; k++)
            {
                if (k == 0)
                {
                    continue;
                }

                int line = PartialAt(Math.Abs(pair.Carrier + (k * pair.Modulator)));
                if (line >= 0 && _persistence[line] > bestPersistence)
                {
                    best = line;
                    bestPersistence = _persistence[line];
                }
            }

            // One step can land on another sideband that the carrier outlasts in turn: follow the lines until none
            // rings clearly longer. Every step multiplies the persistence by at least the margin, so this ends quickly.
            return best < 0 ? pair : Canonical(ScorePair(Math.Round(_relative[best], 3), pair.Modulator, _salience));
        }

        /// <summary>Index of the target partial within the match tolerance of a relative frequency; -1 if none.</summary>
        private int PartialAt(double ratio)
        {
            int found = -1;
            double closest = MatchCents;
            for (int i = 0; i < _relative.Length; i++)
            {
                double cents = ratio > 0.05 ? Math.Abs(1200.0 * Math.Log2(_relative[i] / ratio)) : double.MaxValue;
                if (cents < closest)
                {
                    closest = cents;
                    found = i;
                }
            }

            return found;
        }

        public double CoverScore(List<Pair> cover)
        {
            var predicted = new List<double>();
            foreach (Pair pair in cover)
            {
                predicted.AddRange(Sidebands(pair.Carrier, pair.Modulator, SidebandCount(pair.Index)));
            }

            return Coverage(predicted);
        }

        /// <summary>Greedy covers of the partial cloud: every stack is chosen for what the stacks before it left unexplained.</summary>
        public IEnumerable<List<Pair>> GreedyCovers(List<Pair> ranked, int stacks)
        {
            foreach (Pair first in ranked.Take(3))
            {
                var cover = new List<Pair> { first };
                double[] residual = (double[])_salience.Clone();
                Remove(residual, first);
                while (cover.Count < stacks)
                {
                    Pair best = default;
                    foreach (Pair candidate in ranked.Take(120))
                    {
                        Pair scored = ScorePair(candidate.Carrier, candidate.Modulator, residual);
                        if (scored.Score > best.Score)
                        {
                            best = scored;
                        }
                    }

                    if (best.Score <= 0.02)
                    {
                        break;
                    }

                    cover.Add(best);
                    Remove(residual, best);
                }

                if (cover.Count == stacks)
                {
                    yield return cover;
                }
            }
        }

        private void Remove(double[] residual, Pair pair)
        {
            foreach (double sideband in Sidebands(pair.Carrier, pair.Modulator, SidebandCount(pair.Index)))
            {
                for (int i = 0; i < _relative.Length; i++)
                {
                    if (sideband > 0.05 && Math.Abs(1200.0 * Math.Log2(_relative[i] / sideband)) < MatchCents)
                    {
                        residual[i] = 0;
                    }
                }
            }
        }

        private Pair ScorePair(double carrier, double modulator, double[] salience)
        {
            double total = salience.Sum();
            if (total <= 0 || _salienceSum <= 0)
            {
                return default;
            }

            // Carson: the significant sidebands reach about (I + 1) * fm to either side of the carrier.
            double reach = 0;
            for (int i = 0; i < _relative.Length; i++)
            {
                if (_salience[i] > 0.35)
                {
                    reach = Math.Max(reach, Math.Abs(_relative[i] - carrier));
                }
            }

            double index = Math.Clamp((reach / modulator) - 1.0, 0.3, 8.0);
            int count = SidebandCount(index);
            double covered = 0, spurious = 0, weights = 0;
            var hit = new bool[_relative.Length];
            for (int k = -count; k <= count; k++)
            {
                double sideband = Math.Abs(carrier + (k * modulator));
                if (sideband < 0.05)
                {
                    continue;
                }

                double weight = 1.0 / (1.0 + Math.Abs(k));
                bool found = false;
                for (int i = 0; i < _relative.Length; i++)
                {
                    double cents = Math.Abs(1200.0 * Math.Log2(_relative[i] / sideband));
                    if (cents < MatchCents)
                    {
                        found = true;
                        if (!hit[i])
                        {
                            // A sideband that lands exactly counts more than one at the edge of the tolerance, so that
                            // measured ratios win over nearby simple ones when the partials say so.
                            hit[i] = true;
                            covered += salience[i] * (1.0 - (0.3 * cents / MatchCents));
                        }
                    }
                }

                weights += weight;
                if (!found)
                {
                    spurious += weight;
                }
            }

            // Coverage is measured against the whole cloud so that scores of residual stacks stay comparable. Which
            // line of a family is the carrier is decided by Canonical, from how long the lines ring.
            double score = (covered / _salienceSum) - (0.6 * (weights > 0 ? spurious / weights : 0.0) * (total / _salienceSum));
            return new Pair(carrier, modulator, index, score);
        }

        /// <summary>
        /// Second modulators for the best pairs: <c>m2</c> modulating <c>m1</c>, or next to it on the carrier, puts lines at
        /// <c>|c + a·m1 + b·m2|</c>. A second modulator is only proposed when its lines explain the target clearly better
        /// than the pair alone; the score counts its missing lines as the pair score does, so three free ratios cannot
        /// fit the partials by coincidence without paying for the lines they predict in vain.
        /// </summary>
        public List<(Pair Pair, double Second, double Score)> SecondModulators(List<Pair> ranked)
        {
            var found = new List<(Pair Pair, double Second, double Score)>();
            foreach (Pair pair in ranked.Take(8))
            {
                int count = SidebandCount(pair.Index);
                foreach (double second in _candidates)
                {
                    if (Math.Abs(1200.0 * Math.Log2(second / pair.Modulator)) < MatchCents || Math.Abs(1200.0 * Math.Log2(second / pair.Carrier)) < MatchCents)
                    {
                        continue;
                    }

                    double score = LineScore(pair.Carrier, pair.Modulator, second, count, _salience);
                    if (score > pair.Score + 0.03)
                    {
                        found.Add((pair, second, score));
                    }
                }
            }

            return [.. found.OrderByDescending(static f => f.Score).DistinctBy(static f => (f.Pair.Carrier, f.Pair.Modulator)).Take(4)];
        }

        /// <summary>
        /// Three-operator readings, as a stack <c>c &lt;- m1 &lt;- m2</c> or with both modulators on the carrier, ranked by
        /// how well an FM spectrum model with fitted indices predicts the strengths of the partials. Candidates are
        /// all carriers on strong partials or of the best pairs with any two candidate modulators, of which the forty
        /// whose lines explain the target best are fitted.
        /// </summary>
        public List<(double Carrier, double First, double Second, bool Parallel, double Index, double SecondIndex, double Error)> ModelledStacks(List<Pair> ranked)
        {
            var carriers = new List<double>();
            foreach (double c in StrongestPartials(4).Select(static p => Math.Round(p.RelativeFrequency, 3)).Concat(ranked.Take(8).Select(static p => p.Carrier)))
            {
                if (!carriers.Any(x => Math.Abs(1200.0 * Math.Log2(x / c)) < 2.0))
                {
                    carriers.Add(c);
                }
            }

            var prefiltered = new List<(double Carrier, double First, double Second, double Score)>();
            foreach (double c in carriers)
            {
                foreach (double m1 in _candidates)
                {
                    foreach (double m2 in _candidates)
                    {
                        if (m1 == m2 || Math.Abs(1200.0 * Math.Log2(m1 / c)) < MatchCents || Math.Abs(1200.0 * Math.Log2(m2 / c)) < MatchCents)
                        {
                            continue;
                        }

                        prefiltered.Add((c, m1, m2, LineScore(c, m1, m2, 4, _salience)));
                    }
                }
            }

            var fitted = new List<(double Carrier, double First, double Second, bool Parallel, double Index, double SecondIndex, double Error)>();
            foreach (var t in prefiltered.OrderByDescending(static t => t.Score).Take(40))
            {
                foreach (bool parallel in new[] { false, true })
                {
                    if (parallel && t.First > t.Second)
                    {
                        continue;
                    }

                    var fit = FmSpectrumModel.FitStack(t.Carrier, t.First, t.Second, parallel, _analysis.Partials, _analysis.FundamentalHz);
                    fitted.Add((t.Carrier, t.First, t.Second, parallel, fit.Index, fit.SecondIndex, fit.Error));
                }
            }

            return [.. fitted.OrderBy(static f => f.Error).Take(6)];
        }

        /// <summary>Score of the lines |c + a·m1 + b·m2| (|a| up to <paramref name="count"/>, |b| up to 2) against the partials, as in the pair score.</summary>
        private double LineScore(double carrier, double first, double second, int count, double[] salience)
        {
            double highest = _relative.Length > 0 ? _relative.Max() * 1.1 : 0.0;
            double covered = 0, spurious = 0, weights = 0;
            var hit = new bool[_relative.Length];
            for (int a = -count; a <= count; a++)
            {
                for (int b = -2; b <= 2; b++)
                {
                    double line = Math.Abs(carrier + (a * first) + (b * second));
                    if (line < 0.05 || line > highest)
                    {
                        continue;
                    }

                    double weight = 1.0 / (1.0 + Math.Abs(a) + Math.Abs(b));
                    int partial = PartialAt(line);
                    weights += weight;
                    if (partial < 0)
                    {
                        spurious += weight;
                    }
                    else if (!hit[partial])
                    {
                        hit[partial] = true;
                        covered += salience[partial];
                    }
                }
            }

            return (covered / _salienceSum) - (0.6 * (weights > 0 ? spurious / weights : 0.0));
        }

        /// <summary>The strongest target partial among a carrier and its first two sidebands on each side, as a ratio.</summary>
        public double StrongestLine(double carrier, double modulator)
        {
            double best = carrier, loudest = -1.0;
            for (int k = -2; k <= 2; k++)
            {
                int partial = PartialAt(Math.Abs(carrier + (k * modulator)));
                if (partial >= 0 && _amplitude[partial] > loudest)
                {
                    loudest = _amplitude[partial];
                    best = k == 0 ? carrier : Math.Round(_relative[partial], 3);
                }
            }

            return best;
        }

        private static int SidebandCount(double index) => Math.Clamp((int)Math.Round(index) + 2, 2, 10);

        private static IEnumerable<double> Sidebands(double carrier, double modulator, int count)
        {
            for (int k = -count; k <= count; k++)
            {
                yield return Math.Abs(carrier + (k * modulator));
            }
        }

        public PatchHypothesis Build(string origin, double score, Action<Builder> fill)
        {
            var builder = new Builder(_capabilities, _analysis.FundamentalHz, _outputEnvelope, _brightnessEnvelope);
            fill(builder);
            return new PatchHypothesis(builder.ToPatch(_pitch), score, origin);
        }
    }

    /// <summary>Places operators into free slots of a patch.</summary>
    private sealed class Builder(SynthesisCapabilities capabilities, double fundamentalHz, OperatorEnvelope outputEnvelope, OperatorEnvelope brightnessEnvelope)
    {
        private readonly List<OperatorPatch> _operators = [];
        private readonly List<double> _levels = [];
        private readonly List<ModulationEdge> _edges = [];

        public int Carrier(double ratio, double level, double detuneHz = 0.0) => Add(ratio, level, outputEnvelope, detuneHz);

        public int Modulator(double ratio, int destination, double amount, double detuneHz = 0.0)
        {
            int index = Add(ratio, 0.0, brightnessEnvelope, detuneHz);
            if (index >= 0 && destination >= 0)
            {
                Edge(index, destination, amount);
            }

            return index;
        }

        public void Edge(int source, int destination, double amount)
        {
            if (source >= 0 && destination >= 0)
            {
                _edges.Add(new ModulationEdge(source, destination, true, Math.Clamp(amount, 0.05, 10.0)));
            }
        }

        private int Add(double ratio, double level, OperatorEnvelope envelope, double detuneHz)
        {
            if (_operators.Count >= capabilities.OperatorCount)
            {
                return -1;
            }

            OperatorFrequency frequency = capabilities.SupportsRatioFrequency
                ? OperatorFrequency.AtRatio(ratio)
                : OperatorFrequency.AtFixed(ratio * fundamentalHz);
            _operators.Add(OperatorPatch.Sine(frequency, envelope) with { DetuneHz = detuneHz });
            _levels.Add(level);
            return _operators.Count - 1;
        }

        public OperatorMatrixPatch ToPatch(PitchModel pitch)
        {
            int n = capabilities.OperatorCount;
            var operators = new OperatorPatch[n];
            var levels = new double[n];
            for (int i = 0; i < n; i++)
            {
                operators[i] = i < _operators.Count ? _operators[i] : OperatorPatch.Disabled;
                levels[i] = i < _levels.Count ? _levels[i] : 0.0;
            }

            return new OperatorMatrixPatch(operators, new PhaseModulationMatrix(n, _edges), levels, pitch);
        }
    }
}
