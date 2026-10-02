using HearSay.Analysis;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.OperatorMatrix.Priors;

/// <summary>
/// Hypotheses looked up instead of read: the library patches whose sound is closest to the target, by
/// <see cref="SpectralFingerprint"/>, each as it is and with the envelopes of the target. A neighbour brings its
/// structure together with amounts that already fit it; the right structure with default amounts screens worse than
/// many wrong ones and does not survive.
/// </summary>
/// <remarks>
/// On 20 dense musical FM7 patches (four to six operators) a library of 300 000 patches takes the audio loss after
/// 4000 evaluations from 0.196 to 0.164, on 20 sparse ones (two or three operators) from 0.100 to 0.077, better on 27
/// of the 40 and worse on 4. The neighbours are seldom the hidden structure itself; they are patches that sound alike,
/// and the search finishes the job from there.
/// </remarks>
/// <param name="library">Library to look up.</param>
/// <param name="neighbours">Number of neighbours; each gives two hypotheses.</param>
public sealed class NearestStructurePriorProvider(StructureLibrary library, int neighbours = 8) : ISynthesisPriorProvider
{
    /// <summary>How the origin of every hypothesis of this provider begins, so that the search can tell them apart.</summary>
    public const string OriginPrefix = "library neighbour";

    /// <inheritdoc />
    public string Name => "nearest library structures";

    /// <inheritdoc />
    public IReadOnlyList<PatchHypothesis> Generate(TargetAnalysis analysis, SynthesisCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(capabilities);
        var result = new List<PatchHypothesis>();
        if (analysis.FundamentalHz <= 0)
        {
            return result;
        }

        byte[] print = SpectralFingerprint.Of(analysis.Audio.Samples, analysis.Audio.SampleRate, analysis.FundamentalHz);
        OperatorEnvelope output = TemporalPriors.OutputEnvelope(analysis);
        OperatorEnvelope brightness = TemporalPriors.BrightnessEnvelope(analysis);
        int rank = 0;
        foreach ((int seed, long _) in library.Nearest(print, neighbours))
        {
            OperatorMatrixPatch patch = library.Patch(seed);
            OperatorMatrixPatch retimed = patch;
            for (int i = 0; i < patch.OperatorCount; i++)
            {
                retimed = retimed.WithOperator(i, retimed.Operators[i] with { AmplitudeEnvelope = patch.OutputLevels[i] != 0.0 ? output : brightness });
            }

            result.Add(new PatchHypothesis(patch, 1.0 - (0.01 * rank), $"{OriginPrefix} {rank + 1} (seed {seed})"));
            result.Add(new PatchHypothesis(retimed, 0.995 - (0.01 * rank), $"{OriginPrefix} {rank + 1} (seed {seed}) with the envelopes of the target"));
            rank++;
        }

        return result;
    }
}
