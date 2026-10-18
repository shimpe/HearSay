using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.Editing;

/// <summary>What an edit made of a patch: the new patch, or the unchanged one with the reason it was refused.</summary>
/// <param name="Patch">The edited patch, or the unchanged one when refused.</param>
/// <param name="Refusal">Why the edit was refused; null when it was done.</param>
public readonly record struct EditOutcome(OperatorMatrixPatch Patch, string? Refusal)
{
    /// <summary>True when the edit was not made.</summary>
    public bool IsRefused => Refusal is not null;

    /// <summary>An edit that cannot be made.</summary>
    /// <param name="unchanged">The patch as it was.</param>
    /// <param name="why">The reason, in words for the user.</param>
    public static EditOutcome Refused(OperatorMatrixPatch unchanged, string why) => new(unchanged, why);

    /// <summary>An edit that was made.</summary>
    /// <param name="patch">The edited patch.</param>
    public static EditOutcome Done(OperatorMatrixPatch patch) => new(patch, null);
}

/// <summary>A part of a patch an edit can address.</summary>
public abstract record PatchPart
{
    /// <summary>One operator.</summary>
    /// <param name="Index">Its zero-based index.</param>
    public sealed record OperatorPart(int Index) : PatchPart;

    /// <summary>One connection of the modulation matrix.</summary>
    /// <param name="Source">The modulating operator.</param>
    /// <param name="Destination">The modulated operator.</param>
    public sealed record ConnectionPart(int Source, int Destination) : PatchPart;

    /// <summary>The shared noise.</summary>
    public sealed record NoisePart : PatchPart;

    /// <summary>The pitch model.</summary>
    public sealed record PitchPart : PatchPart;
}

/// <summary>A change to a patch; it returns the edited patch or a refusal.</summary>
/// <param name="patch">The patch to change; it is not modified.</param>
/// <param name="capabilities">What the backend of the sound's profile can represent.</param>
public delegate EditOutcome PatchEdit(OperatorMatrixPatch patch, SynthesisCapabilities capabilities);

/// <summary>
/// The edit model of one sound: its original patch, the patch as edited, and the undo and redo history. Every result is
/// validated, so the edited patch is always valid and playable by the profile. One completed change is one step: an
/// <see cref="Apply"/>, or a whole gesture between <see cref="BeginGesture"/> and <see cref="EndGesture"/>.
/// </summary>
public sealed class PatchEditor
{
    private readonly List<OperatorMatrixPatch> undo = [];
    private readonly List<OperatorMatrixPatch> redo = [];
    private OperatorMatrixPatch? gestureStart;
    private bool showingOriginal;

    /// <summary>Starts editing a sound; the original is never changed.</summary>
    /// <param name="original">The patch as it was.</param>
    /// <param name="profile">The profile the edited patch must stay compatible with.</param>
    public PatchEditor(OperatorMatrixPatch original, SynthesisProfile profile)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(profile);
        Original = original;
        Profile = profile;
        Current = original;
    }

    /// <summary>Raised when <see cref="Current"/> or <see cref="ShowingOriginal"/> changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The patch as it was; never changed.</summary>
    public OperatorMatrixPatch Original { get; }

    /// <summary>The profile the edited patch must stay compatible with.</summary>
    public SynthesisProfile Profile { get; }

    /// <summary>The patch as edited.</summary>
    public OperatorMatrixPatch Current { get; private set; }

    /// <summary>True when the edited patch differs from the original.</summary>
    public bool IsEdited => Current.ComputeHash() != Original.ComputeHash();

    /// <summary>True when there is a step to undo.</summary>
    public bool CanUndo => undo.Count > 0;

    /// <summary>True when there is an undone step to redo.</summary>
    public bool CanRedo => redo.Count > 0;

    /// <summary>True between <see cref="BeginGesture"/> and <see cref="EndGesture"/> or <see cref="CancelGesture"/>.</summary>
    public bool InGesture => gestureStart is not null;

    /// <summary>
    /// True to show the original instead of the edit. Setting it first ends an active gesture, committing it as one step.
    /// </summary>
    public bool ShowingOriginal
    {
        get => showingOriginal;
        set
        {
            EndGesture();
            if (showingOriginal == value)
            {
                return;
            }

            showingOriginal = value;
            OnChanged();
        }
    }

    /// <summary>The original while <see cref="ShowingOriginal"/>, else the edit.</summary>
    public OperatorMatrixPatch Displayed => showingOriginal ? Original : Current;

    /// <summary>Makes an edit as one step; a refused edit changes nothing and adds no step.</summary>
    /// <param name="edit">The change.</param>
    /// <returns>The outcome; its patch is the new current patch when done.</returns>
    public EditOutcome Apply(PatchEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        EndGesture();
        EditOutcome outcome = Evaluate(edit, Current);
        if (outcome.IsRefused || SamePatch(outcome.Patch, Current))
        {
            return outcome;
        }

        undo.Add(Current);
        redo.Clear();
        Current = outcome.Patch;
        OnChanged();
        return outcome;
    }

    /// <summary>Starts a gesture, such as a drag: its previews are one step in the end.</summary>
    public void BeginGesture()
    {
        EndGesture();
        gestureStart = Current;
    }

    /// <summary>
    /// Shows an edit of the patch as it was when the gesture began, without adding a step; a refused edit changes nothing.
    /// </summary>
    /// <param name="edit">The change.</param>
    /// <returns>The outcome.</returns>
    public EditOutcome Preview(PatchEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (gestureStart is null)
        {
            throw new InvalidOperationException("Preview needs BeginGesture first.");
        }

        EditOutcome outcome = Evaluate(edit, gestureStart);
        if (!outcome.IsRefused && !SamePatch(outcome.Patch, Current))
        {
            Current = outcome.Patch;
            OnChanged();
        }

        return outcome;
    }

    /// <summary>Ends the gesture: one step when the patch changed, none otherwise.</summary>
    public void EndGesture()
    {
        if (gestureStart is not { } start)
        {
            return;
        }

        gestureStart = null;
        if (!SamePatch(start, Current))
        {
            undo.Add(start);
            redo.Clear();
        }
    }

    /// <summary>Ends the gesture and goes back to how the patch was when it began.</summary>
    public void CancelGesture()
    {
        if (gestureStart is not { } start)
        {
            return;
        }

        gestureStart = null;
        if (!SamePatch(start, Current))
        {
            Current = start;
            OnChanged();
        }
    }

    /// <summary>Goes back one step; a gesture in progress is committed first.</summary>
    public void Undo()
    {
        EndGesture();
        if (undo.Count == 0)
        {
            return;
        }

        redo.Add(Current);
        Current = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        OnChanged();
    }

    /// <summary>Goes forward one undone step; a gesture in progress is committed first.</summary>
    public void Redo()
    {
        EndGesture();
        if (redo.Count == 0)
        {
            return;
        }

        undo.Add(Current);
        Current = redo[^1];
        redo.RemoveAt(redo.Count - 1);
        OnChanged();
    }

    private EditOutcome Evaluate(PatchEdit edit, OperatorMatrixPatch from)
    {
        EditOutcome outcome = edit(from, Profile.Capabilities);
        if (outcome.IsRefused)
        {
            return EditOutcome.Refused(from, outcome.Refusal!);
        }

        PatchCompatibilityResult result = PatchCompatibilityValidator.Instance.Validate(outcome.Patch, Profile.Capabilities);
        return result.IsCompatible
            ? outcome
            : EditOutcome.Refused(from, result.Issues[0].Message);
    }

    private static bool SamePatch(OperatorMatrixPatch a, OperatorMatrixPatch b)
        => ReferenceEquals(a, b) || a.ComputeHash() == b.ComputeHash();

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// The editors of the sounds edited while the program runs, one per original patch and profile, so that going to
/// another sound and back keeps the edits and their history.
/// </summary>
public sealed class EditSessions
{
    private readonly object gate = new();
    private readonly Dictionary<(string Profile, string Hash), PatchEditor> editors = [];

    /// <summary>The editor of a sound; the same one each time for the same profile and original patch.</summary>
    /// <param name="original">The patch as it was.</param>
    /// <param name="profile">Its profile.</param>
    /// <returns>The editor.</returns>
    public PatchEditor For(OperatorMatrixPatch original, SynthesisProfile profile)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(profile);
        var key = (profile.Name, original.ComputeHash());
        lock (gate)
        {
            if (!editors.TryGetValue(key, out PatchEditor? editor))
            {
                editor = new PatchEditor(original, profile);
                editors[key] = editor;
            }

            return editor;
        }
    }
}
