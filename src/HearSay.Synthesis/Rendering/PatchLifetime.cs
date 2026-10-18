using HearSay.Synthesis.Patches;

namespace HearSay.Synthesis.Rendering;

/// <summary>
/// How long a note of a patch can be heard at most: the time after which the SuperCollider export frees its synth,
/// shared by the exporter and the live voice mixer so that a live note never outlasts the exported one.
/// </summary>
/// <remarks>
/// Only audible carriers count. Key scaling can stretch envelopes; four octaves below the reference note is taken as a
/// generous bound, so every envelope time is stretched by <c>max(1, 2^(|RatePerOctave| × 4))</c>. A patch with at least
/// one sustaining carrier lives until <see cref="SecondsAfterGateOff"/> after its gate closes; a patch of one-shot
/// carriers only lives <see cref="SecondsAfterStart"/> from its start, whatever the gate does. Stateless and
/// thread-safe.
/// </remarks>
public static class PatchLifetime
{
    /// <summary>Time added to the longest envelope, so that its last segment is heard to its end.</summary>
    private const double Margin = 0.05;

    /// <summary>
    /// Seconds from the gate closing to the end of a note of a patch with a sustaining carrier:
    /// <c>max(release, one-shot) + 0.05</c>, over the stretched envelopes of its audible carriers.
    /// </summary>
    /// <param name="patch">Patch.</param>
    /// <returns>The time, or null when no audible carrier sustains, so that the note ends <see cref="SecondsAfterStart"/> after its start.</returns>
    public static double? SecondsAfterGateOff(OperatorMatrixPatch patch)
    {
        (bool sustaining, double release, double oneShot) = Measure(patch);
        return sustaining ? Math.Max(release, oneShot) + Margin : null;
    }

    /// <summary>
    /// Seconds from the start of a note to its end for a patch without a sustaining carrier: the longest stretched
    /// one-shot envelope of its audible carriers plus 0.05.
    /// </summary>
    /// <param name="patch">Patch.</param>
    /// <remarks>Also computed for a sustaining patch, where it is the time its one-shot carriers take plus the margin.</remarks>
    public static double SecondsAfterStart(OperatorMatrixPatch patch) => Measure(patch).OneShot + Margin;

    private static (bool Sustaining, double Release, double OneShot) Measure(OperatorMatrixPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        bool[] audible = patch.FindAudibleOperators();
        bool sustaining = false;
        double release = 0;
        double oneShot = 0;
        for (int i = 0; i < patch.OperatorCount; i++)
        {
            if (!audible[i] || patch.OutputLevels[i] == 0.0)
            {
                continue;
            }

            OperatorPatch op = patch.Operators[i];
            OperatorEnvelope envelope = op.AmplitudeEnvelope;

            // Key scaling can stretch envelopes; four octaves below the reference note is a generous bound.
            double stretch = Math.Max(1.0, Math.Pow(2.0, Math.Abs(op.KeyScaling.RatePerOctave) * 4.0));
            if (envelope.SustainPoint is null)
            {
                double total = 0;
                for (int p = 1; p < envelope.Points.Count; p++)
                {
                    total += envelope.Points[p].TimeSeconds;
                }

                oneShot = Math.Max(oneShot, total * stretch);
            }
            else
            {
                sustaining = true;
                release = Math.Max(release, envelope.ReleaseSeconds * stretch);
            }
        }

        return (sustaining, release, oneShot);
    }
}
