using HearSay.Analysis;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.OperatorMatrix.Priors;

/// <summary>
/// Experimental provider for profiles with more than one waveform. It takes the hypotheses of another provider and
/// adds variants in which the operators that feed the output use each of the other allowed waveforms. Waveform is a
/// structural choice, so every variant is a hypothesis of its own and ends up in its own basin; nothing ever moves
/// gradually from one waveform to another.
/// </summary>
/// <remarks>
/// It reads the target only through the inner provider. Waveform-specific spectral reasoning, such as "odd harmonics
/// only suggests a square-like operator", belongs in dedicated providers and can use the partial cloud that the
/// target analysis already contains.
/// </remarks>
public sealed class WaveformVariantPriorProvider : ISynthesisPriorProvider
{
    private readonly ISynthesisPriorProvider _inner;
    private readonly int _variantsOfBest;
    private readonly bool _includeInner;

    /// <summary>Creates the provider.</summary>
    /// <param name="inner">Provider whose hypotheses are varied.</param>
    /// <param name="variantsOfBest">Number of best inner hypotheses that receive waveform variants.</param>
    /// <param name="includeInner">
    /// Whether the inner hypotheses are returned too. False when the inner provider is registered on its own next to
    /// this one: the variants then form a group of their own in the hypothesis cut and cannot crowd out the originals.
    /// </param>
    public WaveformVariantPriorProvider(ISynthesisPriorProvider inner, int variantsOfBest = 24, bool includeInner = true)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _variantsOfBest = variantsOfBest;
        _includeInner = includeInner;
    }

    /// <inheritdoc />
    public string Name => _includeInner ? _inner.Name + "+waveform-variants" : "waveform-variants of " + _inner.Name;

    /// <inheritdoc />
    public IReadOnlyList<PatchHypothesis> Generate(TargetAnalysis analysis, SynthesisCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        IReadOnlyList<PatchHypothesis> inner = _inner.Generate(analysis, capabilities);
        var result = _includeInner ? new List<PatchHypothesis>(inner) : [];
        if (capabilities.SupportedWaveforms.Count < 2)
        {
            return result;
        }

        foreach (PatchHypothesis hypothesis in inner.Take(_variantsOfBest))
        {
            foreach (OperatorWaveform waveform in capabilities.SupportedWaveforms.Order())
            {
                OperatorMatrixPatch patch = hypothesis.Patch;
                bool changed = false;
                for (int i = 0; i < patch.OperatorCount; i++)
                {
                    OperatorPatch op = patch.Operators[i];
                    if (op.Enabled && patch.OutputLevels[i] != 0.0 && op.Waveform != waveform)
                    {
                        patch = patch.WithOperator(i, op with { Waveform = waveform });
                        changed = true;
                    }
                }

                if (changed)
                {
                    result.Add(new PatchHypothesis(patch, hypothesis.PriorScore - 0.01, $"{hypothesis.Origin} [{waveform} output operators]"));
                }
            }
        }

        return result;
    }
}
