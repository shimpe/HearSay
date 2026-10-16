using HearSay.Analysis;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.OperatorMatrix.Priors;

/// <summary>
/// Hypotheses for subtractive targets on profiles with operator filters: a saw or square carrier through a low-pass
/// whose cutoff and slope are read from the harmonics, with the filter sweep of the brightness when it moves.
/// </summary>
public sealed class SubtractivePriorProvider : ISynthesisPriorProvider
{
    /// <inheritdoc />
    public string Name => "subtractive";

    /// <inheritdoc />
    public IReadOnlyList<PatchHypothesis> Generate(TargetAnalysis analysis, SynthesisCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(capabilities);
        var hypotheses = new List<PatchHypothesis>();
        if (!capabilities.SupportsOperatorFilters || !capabilities.SupportsRatioFrequency || capabilities.OperatorCount < 1 || FilterPriors.LowPass(analysis) is not { } lowPass)
        {
            return hypotheses;
        }

        SweepPrior? sweep = FilterPriors.Sweep(analysis);
        OperatorEnvelope output = TemporalPriors.OutputEnvelope(analysis);
        int rank = 0;
        foreach (OperatorWaveform waveform in new[] { OperatorWaveform.Saw, OperatorWaveform.Square })
        {
            if (!capabilities.SupportedWaveforms.Contains(waveform))
            {
                continue;
            }

            foreach (double q in new[] { OperatorFilter.ButterworthQ, 2.0 })
            {
                // An opening sweep sustains at its top, so its base is the measured cutoff an octave count lower.
                double cutoff = sweep is { Rising: true } ? lowPass.CutoffRatio / Math.Pow(2.0, sweep.Octaves) : lowPass.CutoffRatio;
                OperatorFilter filter = OperatorFilter.LowPass(Math.Clamp(cutoff, 0.25, 64.0), q, lowPass.Poles);
                if (sweep is not null)
                {
                    filter = filter with { EnvelopeOctaves = sweep.Octaves, Envelope = sweep.Envelope };
                }

                OperatorPatch carrier = OperatorPatch.Sine(OperatorFrequency.AtRatio(1), output) with { Waveform = waveform, HarmonicLimit = 64, Filter = filter };
                OperatorMatrixPatch patch = OperatorMatrixPatch.Silent(capabilities.OperatorCount).WithOperator(0, carrier).WithOutputLevel(0, 0.8);
                hypotheses.Add(new PatchHypothesis(patch, 0.5 - (0.01 * rank++), FormattableString.Invariant($"{waveform.ToString().ToLowerInvariant()} through a {lowPass.Poles}-pole low-pass at {filter.CutoffRatio:0.##}x, Q {q:0.##}{(sweep is null ? string.Empty : " with a sweep")}")));
            }
        }

        return hypotheses;
    }
}
