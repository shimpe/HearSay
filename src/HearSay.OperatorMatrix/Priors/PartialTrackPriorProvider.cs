using System.Globalization;
using HearSay.Analysis;
using HearSay.Partials;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.OperatorMatrix.Priors;

/// <summary>
/// Hypotheses read from the partial tracks of the target rather than from its average spectrum: operators exactly on
/// the partials that ring on, each with the decay measured for its own partial. This is the natural reading of a bell,
/// a bar or a gong, where every partial decays at its own rate and the frequencies follow no simple ratios, so that
/// snapping them to the ratios of an FM stack puts every sideband next to the partial it should be.
/// </summary>
/// <remarks>
/// <para>Two families. Additive: two operators up to one per operator slot. With an attack modulator: the partials
/// with one sine modulator whose modulation dies within the attack, for the bright burst of sidebands that many struck
/// sounds start with; its ratio is the spacing that puts the most short-lived attack partials on sidebands of the
/// carriers.</para>
/// <para>Stable partials are tracks that last at least 0.3 s with a confidence of 0.5; short partials are tracks born
/// in the first 0.1 s that die within 0.3 s.</para>
/// </remarks>
public sealed class PartialTrackPriorProvider : ISynthesisPriorProvider
{
    private const double MatchCents = 35.0;
    private const double AttackAmountRadians = 3.0;

    private readonly ISynthesisPriorProvider? _explanation;
    private readonly double _explainedAbove;

    /// <summary>Creates the provider.</summary>
    /// <param name="explanation">
    /// Provider of structures that explain the spectrum by other means, such as sideband arithmetic; when its best
    /// hypothesis explains the spectrum well, the long-ringing sidebands of real FM are no separate partials and this
    /// provider stays out. Null to always propose.
    /// </param>
    /// <param name="explainedAbove">Prior score of the best explanation above which nothing is proposed.</param>
    public PartialTrackPriorProvider(ISynthesisPriorProvider? explanation = null, double explainedAbove = 0.9)
    {
        _explanation = explanation;
        _explainedAbove = explainedAbove;
    }

    /// <inheritdoc />
    public string Name => "partial-tracks";

    /// <inheritdoc />
    public IReadOnlyList<PatchHypothesis> Generate(TargetAnalysis analysis, SynthesisCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(capabilities);
        var hypotheses = new List<PatchHypothesis>();
        int slots = capabilities.OperatorCount;
        if (slots < 2
            || !capabilities.SupportedWaveforms.Contains(OperatorWaveform.Sine)
            || !capabilities.SupportedModes.Contains(OperatorMode.PhaseModulation)
            || !capabilities.SupportsIndependentOperatorOutputs
            || !(capabilities.SupportsRatioFrequency || capabilities.SupportsFixedFrequency))
        {
            return hypotheses;
        }

        List<PartialTrack> stable = [.. analysis.PartialTracks
            .Where(static t => t.Confidence >= 0.5 && t.DeathSeconds - t.BirthSeconds >= 0.3 && t.FrequencyHz > 0)
            .OrderByDescending(static t => t.PeakLevelDb)];
        if (stable.Count < 2 || (_explanation is not null && _explanation.Generate(analysis, capabilities).Select(static h => h.PriorScore).DefaultIfEmpty(0.0).Max() >= _explainedAbove))
        {
            return hypotheses;
        }

        List<PartialTrack> brief = [.. analysis.PartialTracks
            .Where(static t => t.Confidence >= 0.15 && t.BirthSeconds < 0.1 && t.DeathSeconds - t.BirthSeconds < 0.3 && t.FrequencyHz > 0)];
        double loudest = stable[0].PeakLevelDb;
        double energy = stable.Sum(t => Math.Pow(10.0, (t.PeakLevelDb - loudest) / 10.0));
        double f0 = analysis.FundamentalHz;

        OperatorPatch Ring(PartialTrack track) =>
            OperatorPatch.Sine(Frequency(track.FrequencyHz), TemporalPriors.PartialEnvelope(analysis, track));
        double Level(PartialTrack track) => 0.8 * Math.Pow(10.0, (track.PeakLevelDb - loudest) / 20.0);
        double Covered(int count) => stable.Take(count).Sum(t => Math.Pow(10.0, (t.PeakLevelDb - loudest) / 10.0)) / energy;
        string List(int count) => string.Join(", ", stable.Take(count).Select(static t => t.FrequencyHz.ToString("0", CultureInfo.InvariantCulture)));

        // Additive.
        for (int count = 2; count <= Math.Min(stable.Count, slots); count++)
        {
            OperatorMatrixPatch patch = OperatorMatrixPatch.Silent(slots);
            for (int i = 0; i < count; i++)
            {
                patch = patch.WithOperator(i, Ring(stable[i])).WithOutputLevel(i, Level(stable[i]));
            }

            hypotheses.Add(new PatchHypothesis(patch, 0.5 + (0.4 * Covered(count)) - (0.02 * count), $"{count} partials at {List(count)} Hz"));
        }

        // With an attack modulator on all of them.
        double? modulatorHz = AttackModulator(stable, brief);
        if (modulatorHz is double hz)
        {
            double burst = Math.Clamp(brief.Average(static t => t.DeathSeconds), 0.03, 0.4);
            for (int count = 2; count <= Math.Min(stable.Count, slots - 1); count++)
            {
                int modulator = count;
                OperatorMatrixPatch patch = OperatorMatrixPatch.Silent(slots)
                    .WithOperator(modulator, OperatorPatch.Sine(Frequency(hz), capabilities.SupportsDynamicModulationMatrix ? OperatorEnvelope.Constant() : OperatorEnvelope.Adsr(0.001, burst, 0.0, 0.05)));
                for (int i = 0; i < count; i++)
                {
                    patch = patch
                        .WithOperator(i, Ring(stable[i]))
                        .WithOutputLevel(i, Level(stable[i]))
                        .WithEdge(new ModulationEdge(
                            modulator,
                            i,
                            true,
                            Math.Min(AttackAmountRadians, capabilities.MaxModulationRadiansPerDestination),
                            capabilities.SupportsDynamicModulationMatrix ? OperatorEnvelope.Adsr(0.001, burst, 0.0, 0.05) : null));
                }

                hypotheses.Add(new PatchHypothesis(patch, 0.5 + (0.4 * Covered(count)) - (0.02 * count) + 0.01, FormattableString.Invariant($"{count} partials at {List(count)} Hz with an attack modulator at {hz:0} Hz")));
            }
        }

        return hypotheses;

        OperatorFrequency Frequency(double hz) => capabilities.SupportsRatioFrequency
            ? OperatorFrequency.AtRatio(Math.Round(hz / f0, 3))
            : OperatorFrequency.AtFixed(Math.Round(hz, 1));
    }

    /// <summary>
    /// The modulator frequency that puts the most short-lived attack partials on first- to third-order sidebands of
    /// the ringing partials; null when there is no attack burst to explain.
    /// </summary>
    private static double? AttackModulator(List<PartialTrack> stable, List<PartialTrack> brief)
    {
        if (brief.Count < 2)
        {
            return null;
        }

        double[] carriers = [.. stable.Take(3).Select(static t => t.FrequencyHz)];
        var candidates = new List<double>();
        foreach (double carrier in carriers)
        {
            foreach (PartialTrack partial in brief)
            {
                double spacing = Math.Abs(partial.FrequencyHz - carrier);
                if (spacing > 20.0)
                {
                    candidates.Add(spacing);
                }
            }
        }

        double? best = null;
        int bestCount = 1;
        foreach (double modulator in candidates.Order())
        {
            int count = 0;
            foreach (PartialTrack partial in brief)
            {
                bool explained = false;
                foreach (double carrier in carriers)
                {
                    for (int k = -3; k <= 3 && !explained; k++)
                    {
                        double sideband = Math.Abs(carrier + (k * modulator));
                        explained = k != 0 && sideband > 20.0 && Math.Abs(1200.0 * Math.Log2(partial.FrequencyHz / sideband)) < MatchCents;
                    }
                }

                count += explained ? 1 : 0;
            }

            if (count > bestCount)
            {
                bestCount = count;
                best = modulator;
            }
        }

        return best;
    }
}
