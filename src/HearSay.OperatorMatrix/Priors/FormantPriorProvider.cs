using HearSay.Analysis;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.OperatorMatrix.Priors;

/// <summary>
/// Hypotheses for voice-like targets on profiles with operator filters: a rich carrier (saw, or a pulse) through a
/// formant filter whose peaks are read from the spectral envelope (<see cref="FilterPriors"/>): with three peaks (F1 to
/// F3) when there are three, with the two strongest, with the strongest alone, and with the first and the third when
/// there are three.
/// </summary>
public sealed class FormantPriorProvider : ISynthesisPriorProvider
{
    /// <inheritdoc />
    public string Name => "formants";

    /// <inheritdoc />
    public IReadOnlyList<PatchHypothesis> Generate(TargetAnalysis analysis, SynthesisCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(capabilities);
        var hypotheses = new List<PatchHypothesis>();
        if (!capabilities.SupportsOperatorFilters || !capabilities.SupportsRatioFrequency || capabilities.OperatorCount < 1)
        {
            return hypotheses;
        }

        IReadOnlyList<SpectralPeak> peaks = FilterPriors.Peaks(analysis, 3);
        if (peaks.Count == 0)
        {
            return hypotheses;
        }

        var filters = new List<(OperatorFilter Filter, string Name)>();
        SpectralPeak[] byFrequency = [.. peaks.OrderBy(static p => p.Hz)];
        if (byFrequency.Length == 3)
        {
            OperatorFilter three = FilterPriors.Formant(peaks)!;
            filters.Add((three, FormattableString.Invariant($"formants {three.FixedHz:0} Hz / {three.FixedHz * three.FormantRatio:0} Hz / {three.FixedHz * three.Formant3Ratio:0} Hz")));
        }

        // The two strongest peaks, without a third.
        if (peaks.Count >= 2)
        {
            OperatorFilter two = FilterPriors.Formant([peaks[0], peaks[1]])!;
            filters.Add((two, FormattableString.Invariant($"formant {two.FixedHz:0} Hz / {two.FixedHz * two.FormantRatio:0} Hz")));
        }

        SpectralPeak strongest = peaks[0];
        filters.Add((FilterPriors.Formant([strongest])!, FormattableString.Invariant($"one formant {strongest.Hz:0} Hz")));
        if (byFrequency.Length == 3)
        {
            filters.Add((FilterPriors.Formant([byFrequency[0], byFrequency[2]])!, "formants F1 + F3"));
        }

        OperatorEnvelope output = TemporalPriors.OutputEnvelope(analysis);
        int rank = 0;
        foreach ((OperatorFilter filter, string name) in filters)
        {
            foreach ((OperatorWaveform waveform, double shape) in new[] { (OperatorWaveform.Saw, 0.5), (OperatorWaveform.Pulse, 0.2) })
            {
                if (!capabilities.SupportedWaveforms.Contains(waveform))
                {
                    continue;
                }

                OperatorPatch carrier = OperatorPatch.Sine(OperatorFrequency.AtRatio(1), output) with { Waveform = waveform, Shape = shape, HarmonicLimit = 64, Filter = filter };
                OperatorMatrixPatch patch = OperatorMatrixPatch.Silent(capabilities.OperatorCount).WithOperator(0, carrier).WithOutputLevel(0, 0.8);
                hypotheses.Add(new PatchHypothesis(patch, 0.55 - (0.01 * rank++), $"{waveform.ToString().ToLowerInvariant()} with {name}"));
            }
        }

        return hypotheses;
    }
}
