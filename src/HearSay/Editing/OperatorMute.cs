using HearSay.Synthesis.Patches;

namespace HearSay.Editing;

/// <summary>
/// Muting operators to listen without them, as the Studio's mute buttons do: a copy of the patch in which the muted
/// operators neither reach the output nor modulate anything, their own feedback included. The patch itself, and so what
/// is saved, exported or kept, does not change.
/// </summary>
public static class OperatorMute
{
    /// <summary>The patch as it sounds with some operators muted; the patch itself when none of them is in use.</summary>
    /// <param name="patch">The patch.</param>
    /// <param name="muted">The muted operators, zero-based; numbers outside the patch are ignored.</param>
    public static OperatorMatrixPatch Apply(OperatorMatrixPatch patch, IReadOnlySet<int> muted)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(muted);
        bool Muted(int op) => muted.Contains(op) && patch.Operators[op].Enabled;
        if (!Enumerable.Range(0, patch.OperatorCount).Any(Muted))
        {
            return patch;
        }

        // What a muted operator sends: its output, and every connection it is the source of.
        var edges = patch.ModulationMatrix.Edges.Where(e => !Muted(e.SourceOperator)).ToList();
        OperatorMatrixPatch silent = patch with { ModulationMatrix = new PhaseModulationMatrix(patch.OperatorCount, edges) };
        for (int op = 0; op < patch.OperatorCount; op++)
        {
            if (Muted(op))
            {
                silent = silent.WithOutputLevel(op, 0.0);
            }
        }

        return silent;
    }
}
