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

    /// <inheritdoc />
    public string Name => "sine-phase-modulation";

    /// <summary>Largest number of hypotheses returned.</summary>
    public int MaxHypotheses { get; init; } = 128;

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

        // Relabelled twins and anything the profile cannot represent are dropped; the best-scored twin stays.
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
            foreach (SpectralPartial partial in StrongestPartials(6))
            {
                double snapped = Snap(partial.RelativeFrequency);
                if (!ratios.Contains(snapped) && snapped is > 0.2 and < 20.0)
                {
                    ratios.Add(snapped);
                }
            }

            // Differences between strong partials are candidate modulator ratios: that is the sideband spacing.
            IReadOnlyList<SpectralPartial> strong = StrongestPartials(8);
            for (int a = 0; a < strong.Count; a++)
            {
                for (int b = a + 1; b < strong.Count; b++)
                {
                    double spacing = Snap(Math.Abs(strong[a].RelativeFrequency - strong[b].RelativeFrequency));
                    if (spacing is > 0.2 and < 16.0 && !ratios.Contains(spacing))
                    {
                        ratios.Add(spacing);
                    }
                }
            }

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
                        pairs.Add(pair);
                    }
                }
            }

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
                    if (Math.Abs(1200.0 * Math.Log2(_relative[i] / sideband)) < MatchCents)
                    {
                        found = true;
                        if (!hit[i])
                        {
                            hit[i] = true;
                            covered += salience[i];
                        }
                    }
                }

                weights += weight;
                if (!found)
                {
                    spurious += weight;
                }
            }

            // Coverage is measured against the whole cloud so that scores of residual stacks stay comparable.
            double score = (covered / _salienceSum) - (0.6 * (weights > 0 ? spurious / weights : 0.0) * (total / _salienceSum));
            return new Pair(carrier, modulator, index, score);
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
