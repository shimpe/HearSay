using HearSay.Synthesis.Patches;

namespace HearSay.Editing;

/// <summary>
/// What an edit changed, part by part and value by value, so that the editor can mark it: a part is edited when what it
/// holds differs from the original's, compared by content (an edit undone by hand is no edit). An operator that is free in
/// both is never edited, whatever its settings; a connection that is disabled counts as absent.
/// </summary>
public static class EditMarks
{
    /// <summary>True when a part of the edited patch differs from the same part of the original.</summary>
    /// <param name="original">The original patch.</param>
    /// <param name="current">The edited patch.</param>
    /// <param name="part">The part.</param>
    public static bool IsEdited(OperatorMatrixPatch original, OperatorMatrixPatch current, PatchPart part)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(part);
        bool Has(int op) => (uint)op < (uint)original.OperatorCount && (uint)op < (uint)current.OperatorCount;
        switch (part)
        {
            case PatchPart.OperatorPart { Index: var op } when Has(op):
                OperatorPatch before = original.Operators[op], after = current.Operators[op];
                if (!before.Enabled && !after.Enabled)
                {
                    return false;
                }

                return original.OutputLevels[op] != current.OutputLevels[op] || Differ(before, after);
            case PatchPart.ConnectionPart { Source: var s, Destination: var d } when Has(s) && Has(d):
                return Differ(Connection(original, s, d), Connection(current, s, d));
            case PatchPart.NoisePart:
                return Differ(original.Noise, current.Noise);
            case PatchPart.PitchPart:
                return Differ(original.Pitch, current.Pitch);
            default:
                return false;
        }
    }

    /// <summary>
    /// True when a part is in the edited patch but not in the original: an operator that was free, a connection that was
    /// not there. Every value of a new part is an edit.
    /// </summary>
    /// <param name="original">The original patch.</param>
    /// <param name="current">The edited patch.</param>
    /// <param name="part">The part.</param>
    public static bool IsNew(OperatorMatrixPatch original, OperatorMatrixPatch current, PatchPart part)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(current);
        bool Has(int op) => (uint)op < (uint)original.OperatorCount && (uint)op < (uint)current.OperatorCount;
        return part switch
        {
            PatchPart.OperatorPart { Index: var op } when Has(op) => !original.Operators[op].Enabled && current.Operators[op].Enabled,
            PatchPart.ConnectionPart { Source: var s, Destination: var d } when Has(s) && Has(d) => Connection(original, s, d) is null && Connection(current, s, d) is not null,
            _ => false,
        };
    }

    /// <summary>
    /// True when a value of a part differs from the original's, when only one of them has it, or when the part is new
    /// (<see cref="IsNew"/>).
    /// </summary>
    /// <param name="original">The original patch.</param>
    /// <param name="current">The edited patch.</param>
    /// <param name="part">The part the value belongs to.</param>
    /// <param name="value">The value.</param>
    public static bool IsEdited(OperatorMatrixPatch original, OperatorMatrixPatch current, PatchPart part, EditableValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return IsNew(original, current, part) || !value.Get(original).Equals(value.Get(current));
    }

    private static ModulationEdge? Connection(OperatorMatrixPatch patch, int source, int destination)
        => patch.ModulationMatrix.Find(source, destination) is { Enabled: true } edge ? edge : null;

    /// <summary>Compares by content, as the patch file holds it: records with envelopes compare their lists by reference.</summary>
    private static bool Differ<T>(T? a, T? b)
        => !string.Equals(HearSayJson.Serialize(a, indented: false), HearSayJson.Serialize(b, indented: false), StringComparison.Ordinal);
}
