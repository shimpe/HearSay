using HearSay.Analysis;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.OperatorMatrix.Priors;

/// <summary>
/// Hypotheses for voice-like targets on profiles with operator filters: the voice source (<see cref="VoiceSource"/>, a
/// 1 % pulse with 64 harmonics) through a formant filter whose peaks are the target's formants
/// (<see cref="FilterPriors.FormantPeaks"/>): with three peaks (F1 to F3) when there are three, with the two strongest,
/// with the strongest alone, and with the first and the third when there are three. Every one of these filters has F3,
/// as the search tunes F1 to F3 on every formant filter: the heard third peak in the first, the search's default (at
/// the level floor) in the others.
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

        IReadOnlyList<SpectralPeak> peaks = FilterPriors.FormantPeaks(analysis, 3);
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

        // The two strongest peaks, with F3 at its default rather than on the third heard peak.
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

        if (!capabilities.SupportedWaveforms.Contains(OperatorWaveform.Pulse))
        {
            return hypotheses;
        }

        OperatorEnvelope output = TemporalPriors.OutputEnvelope(analysis);
        int rank = 0;
        foreach ((OperatorFilter filter, string name) in filters)
        {
            OperatorPatch carrier = VoiceSource.AsVoiceSource(OperatorPatch.Sine(OperatorFrequency.AtRatio(1), output) with { Filter = filter }, keepPulseWidth: false);
            OperatorMatrixPatch patch = OperatorMatrixPatch.Silent(capabilities.OperatorCount).WithOperator(0, carrier).WithOutputLevel(0, 0.8);
            hypotheses.Add(new PatchHypothesis(patch, 0.55 - (0.01 * rank++), $"voice source with {name}"));
        }

        return hypotheses;
    }
}
