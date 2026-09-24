using HearSay.Analysis;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.OperatorMatrix.Priors;

/// <summary>
/// For targets with a noise component, as measured by <see cref="TargetAnalysis.Noise"/>: the best hypotheses of
/// another provider with noise added in their free slots, so that the search starts with noise instead of having to
/// add it later. Added later, it rarely helps: by then the structure has usually spent operators on imitating the
/// noise, and real noise on top only doubles it.
/// </summary>
/// <remarks>
/// Two ways of adding it: a noise band, a sine at the centroid of the noise that a noise operator modulates deeply,
/// which spreads it into a band around that frequency; and plain white noise straight into the mix. The level follows
/// the share of the noise in the target, and half of it is proposed too.
/// </remarks>
public sealed class NoisePriorProvider : ISynthesisPriorProvider
{
    private const double BandDepthRadians = 4.0;

    private readonly ISynthesisPriorProvider _inner;
    private readonly int _structures;
    private readonly double _minimumFraction;

    /// <summary>Creates the provider.</summary>
    /// <param name="inner">Provider of the structures that receive noise.</param>
    /// <param name="structures">Number of best inner hypotheses that receive noise.</param>
    /// <param name="minimumFraction">
    /// Share of noise in the target below which nothing is proposed. Pure FM notes, even dense inharmonic ones, stay
    /// below 0.06; a noise wash at −8 dB next to the partials comes out around 0.2.
    /// </param>
    public NoisePriorProvider(ISynthesisPriorProvider inner, int structures = 12, double minimumFraction = 0.1)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _structures = structures;
        _minimumFraction = minimumFraction;
    }

    /// <inheritdoc />
    public string Name => "noise of " + _inner.Name;

    /// <inheritdoc />
    public IReadOnlyList<PatchHypothesis> Generate(TargetAnalysis analysis, SynthesisCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(capabilities);
        var result = new List<PatchHypothesis>();
        double fraction = analysis.Noise.Fraction;
        if (fraction < _minimumFraction || !capabilities.SupportedWaveforms.Contains(OperatorWaveform.Noise) || !capabilities.SupportsIndependentOperatorOutputs)
        {
            return result;
        }

        // Amplitude of the noise relative to the loudest operator of the structure, from the share of its energy.
        double relative = Math.Clamp(Math.Sqrt(fraction / Math.Max(1e-3, 1.0 - fraction)), 0.1, 1.0);
        double centroid = Math.Clamp(analysis.Noise.CentroidHz, 100.0, 0.45 * analysis.Audio.SampleRate);
        OperatorEnvelope envelope = TemporalPriors.OutputEnvelope(analysis);
        OperatorPatch whiteNoise = OperatorPatch.Sine(OperatorFrequency.AtRatio(1), envelope) with { Waveform = OperatorWaveform.Noise };
        OperatorPatch noiseSource = OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Constant()) with { Waveform = OperatorWaveform.Noise };
        OperatorFrequency bandFrequency = capabilities.SupportsFixedFrequency
            ? OperatorFrequency.AtFixed(Math.Round(centroid))
            : OperatorFrequency.AtRatio(Math.Round(centroid / analysis.FundamentalHz, 2));
        bool band = capabilities.SupportedWaveforms.Contains(OperatorWaveform.Sine);
        string colour = FormattableString.Invariant($"{centroid:0} Hz");

        foreach (PatchHypothesis hypothesis in _inner.Generate(analysis, capabilities).Take(_structures))
        {
            OperatorMatrixPatch patch = hypothesis.Patch;
            bool[] audible = patch.FindAudibleOperators();
            int[] free = [.. Enumerable.Range(0, patch.OperatorCount).Where(i => !audible[i])];
            if (free.Length == 0)
            {
                continue;
            }

            double loudest = Math.Max(0.1, patch.OutputLevels.Max());
            foreach (double share in new[] { 1.0, 0.5 })
            {
                double level = relative * share * loudest;
                string label = FormattableString.Invariant($"level {relative * share:0.##}");

                // The band: a feed-forward pair in the last two free slots, the noise source in the higher one.
                if (band && free.Length >= 2)
                {
                    int carrier = free[^2], source = free[^1];
                    OperatorMatrixPatch banded = patch
                        .WithOperator(carrier, OperatorPatch.Sine(bandFrequency, envelope))
                        .WithOutputLevel(carrier, level)
                        .WithOperator(source, noiseSource)
                        .WithOutputLevel(source, 0.0)
                        .WithEdge(new ModulationEdge(source, carrier, true, Math.Min(BandDepthRadians, capabilities.MaxModulationRadiansPerDestination)));
                    result.Add(new PatchHypothesis(banded, hypothesis.PriorScore, $"{hypothesis.Origin} + noise band at {colour} ({label})"));
                }

                OperatorMatrixPatch parallel = patch.WithOperator(free[^1], whiteNoise).WithOutputLevel(free[^1], level);
                result.Add(new PatchHypothesis(parallel, hypothesis.PriorScore - 0.01, $"{hypothesis.Origin} + white noise ({label})"));
            }
        }

        return result;
    }
}
