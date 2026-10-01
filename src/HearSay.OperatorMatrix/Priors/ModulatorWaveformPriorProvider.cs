using HearSay.Analysis;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.OperatorMatrix.Priors;

/// <summary>
/// For profiles with band-limited waveforms: the best hypotheses of another provider with one modulator at a time
/// turned into a triangle, saw, square, narrow pulse or elliptic waveform. A modulator with harmonics puts its sidebands on the same grid
/// as a sine modulator at the same ratio, so sideband arithmetic finds the ratios of such a structure and misses only
/// the waveform; without this the search imitates the richer modulator with feedback on a sine.
/// </summary>
public sealed class ModulatorWaveformPriorProvider : ISynthesisPriorProvider
{
    private static readonly (OperatorWaveform Waveform, double Shape)[] Waveforms =
    [
        (OperatorWaveform.Triangle, 0.5),
        (OperatorWaveform.Saw, 0.5),
        (OperatorWaveform.Square, 0.5),
        (OperatorWaveform.Pulse, 0.25),
        (OperatorWaveform.Elliptic, OperatorPatch.DefaultEllipticM),
        (OperatorWaveform.Elliptic, 0.99999),
    ];

    private readonly ISynthesisPriorProvider _inner;
    private readonly int _structures;

    /// <summary>Creates the provider.</summary>
    /// <param name="inner">Provider whose hypotheses are varied.</param>
    /// <param name="structures">Number of best inner hypotheses whose modulators are varied.</param>
    public ModulatorWaveformPriorProvider(ISynthesisPriorProvider inner, int structures = 12)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _structures = structures;
    }

    /// <inheritdoc />
    public string Name => "modulator waveforms of " + _inner.Name;

    /// <inheritdoc />
    public IReadOnlyList<PatchHypothesis> Generate(TargetAnalysis analysis, SynthesisCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(capabilities);
        var result = new List<PatchHypothesis>();
        (OperatorWaveform Waveform, double Shape)[] allowed = [.. Waveforms.Where(w => capabilities.SupportedWaveforms.Contains(w.Waveform))];
        if (allowed.Length == 0)
        {
            return result;
        }

        foreach (PatchHypothesis hypothesis in _inner.Generate(analysis, capabilities).Take(_structures))
        {
            OperatorMatrixPatch patch = hypothesis.Patch;
            bool[] audible = patch.FindAudibleOperators();
            for (int i = 0; i < patch.OperatorCount; i++)
            {
                OperatorPatch op = patch.Operators[i];
                if (!audible[i] || patch.OutputLevels[i] != 0.0 || op.Waveform != OperatorWaveform.Sine)
                {
                    continue;
                }

                foreach ((OperatorWaveform waveform, double shape) in allowed)
                {
                    OperatorMatrixPatch variant = patch.WithOperator(i, op with { Waveform = waveform, Shape = shape });
                    result.Add(new PatchHypothesis(variant, hypothesis.PriorScore - 0.01, $"{hypothesis.Origin} [OP{i + 1} {waveform}]"));
                }
            }
        }

        return result;
    }
}
