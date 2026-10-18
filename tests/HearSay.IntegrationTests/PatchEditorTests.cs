using HearSay.Editing;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.IntegrationTests;

/// <summary>The patch editor's undo, redo, gestures and sessions; fast, no search.</summary>
public sealed class PatchEditorTests
{
    private static readonly SynthesisProfile Fm7 = SynthesisProfiles.SuperColliderFm7;

    private static OperatorMatrixPatch Heard(double ratio = 1)
        => OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(ratio), OperatorEnvelope.Adsr(0.01, 0.2, 0.5, 0.1)))
            .WithOutputLevel(0, 0.5);

    private static PatchEdit SetRatio(double ratio)
        => (patch, _) => EditOutcome.Done(patch.WithOperator(0, patch.Operators[0] with { Frequency = OperatorFrequency.AtRatio(ratio) }));

    [Fact]
    public void Revert_ForgetsTheEditAndItsHistory()
    {
        var editor = new PatchEditor(Heard(), Fm7);
        editor.Apply(SetRatio(2));
        editor.Apply(SetRatio(3));
        editor.Undo();
        editor.BeginGesture();
        editor.Preview(SetRatio(5));
        editor.ShowingOriginal = true;
        int changes = 0;
        editor.Changed += (_, _) => changes++;

        editor.Revert();

        Assert.Equal(editor.Original.ComputeHash(), editor.Current.ComputeHash());
        Assert.False(editor.IsEdited);
        Assert.False(editor.CanUndo);
        Assert.False(editor.CanRedo);
        Assert.False(editor.InGesture);
        Assert.False(editor.ShowingOriginal);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Apply_ThenUndo_RestoresTheExactPatch()
    {
        var editor = new PatchEditor(Heard(), Fm7);
        string before = editor.Current.ComputeHash();

        EditOutcome outcome = editor.Apply(SetRatio(2));
        string edited = editor.Current.ComputeHash();
        Assert.False(outcome.IsRefused);
        Assert.NotEqual(before, edited);
        Assert.True(editor.IsEdited);
        Assert.True(editor.CanUndo);

        editor.Undo();
        Assert.Equal(before, editor.Current.ComputeHash());
        Assert.False(editor.IsEdited);
        Assert.True(editor.CanRedo);

        editor.Redo();
        Assert.Equal(edited, editor.Current.ComputeHash());
    }

    [Fact]
    public void NewStep_ClearsRedo()
    {
        var editor = new PatchEditor(Heard(), Fm7);
        editor.Apply(SetRatio(2));
        editor.Undo();
        Assert.True(editor.CanRedo);

        editor.Apply(SetRatio(3));
        Assert.False(editor.CanRedo);
    }

    [Fact]
    public void Gesture_IsOneStep()
    {
        var editor = new PatchEditor(Heard(), Fm7);
        string start = editor.Current.ComputeHash();

        editor.BeginGesture();
        Assert.True(editor.InGesture);
        for (int i = 1; i <= 10; i++)
        {
            editor.Preview(SetRatio(1 + i * 0.1));
        }

        editor.EndGesture();
        Assert.False(editor.InGesture);
        Assert.NotEqual(start, editor.Current.ComputeHash());

        editor.Undo();
        Assert.Equal(start, editor.Current.ComputeHash());
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public void CancelGesture_RestoresTheStart()
    {
        var editor = new PatchEditor(Heard(), Fm7);
        string start = editor.Current.ComputeHash();

        editor.BeginGesture();
        editor.Preview(SetRatio(4));
        Assert.NotEqual(start, editor.Current.ComputeHash());
        editor.CancelGesture();

        Assert.False(editor.InGesture);
        Assert.Equal(start, editor.Current.ComputeHash());
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public void UnchangedGesture_AddsNoStep()
    {
        var editor = new PatchEditor(Heard(), Fm7);
        editor.BeginGesture();
        editor.Preview(SetRatio(5));
        editor.Preview(SetRatio(1));
        editor.EndGesture();

        Assert.False(editor.CanUndo);
        Assert.False(editor.IsEdited);
    }

    [Fact]
    public void RefusedEdit_ChangesNothing()
    {
        var editor = new PatchEditor(Heard(), Fm7);
        string start = editor.Current.ComputeHash();
        int changes = 0;
        editor.Changed += (_, _) => changes++;

        EditOutcome outcome = editor.Apply((patch, _) => EditOutcome.Done(
            patch.WithOperator(0, patch.Operators[0] with { Waveform = OperatorWaveform.Saw })));

        Assert.True(outcome.IsRefused);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Refusal));
        Assert.Equal(start, editor.Current.ComputeHash());
        Assert.False(editor.CanUndo);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Original_IsNeverChanged()
    {
        OperatorMatrixPatch original = Heard();
        string hash = original.ComputeHash();
        var editor = new PatchEditor(original, Fm7);

        editor.Apply(SetRatio(2));
        editor.Undo();
        editor.Redo();

        Assert.Same(original, editor.Original);
        Assert.Equal(hash, editor.Original.ComputeHash());
    }

    [Fact]
    public void Changed_IsRaisedOncePerApplyUndoRedo()
    {
        var editor = new PatchEditor(Heard(), Fm7);
        int changes = 0;
        editor.Changed += (_, _) => changes++;

        editor.Apply(SetRatio(2));
        Assert.Equal(1, changes);
        editor.Undo();
        Assert.Equal(2, changes);
        editor.Redo();
        Assert.Equal(3, changes);
    }

    [Fact]
    public void Sessions_KeepTheEditsOfEachOriginal()
    {
        var sessions = new EditSessions();
        OperatorMatrixPatch a = Heard(1);
        OperatorMatrixPatch b = Heard(1.5);

        PatchEditor editorA = sessions.For(a, Fm7);
        PatchEditor editorB = sessions.For(b, Fm7);
        Assert.NotSame(editorA, editorB);
        editorA.Apply(SetRatio(2));
        editorB.Apply(SetRatio(3));

        Assert.Same(editorA, sessions.For(Heard(1), Fm7));
        Assert.Same(editorB, sessions.For(b, Fm7));
        Assert.Equal(SetRatioResult(a, 2), editorA.Current.ComputeHash());
        Assert.Equal(SetRatioResult(b, 3), editorB.Current.ComputeHash());
    }

    private static string SetRatioResult(OperatorMatrixPatch patch, double ratio)
        => patch.WithOperator(0, patch.Operators[0] with { Frequency = OperatorFrequency.AtRatio(ratio) }).ComputeHash();

    [Fact]
    public void Switch_DuringAGesture_CommitsItFirst()
    {
        var editor = new PatchEditor(Heard(), Fm7);
        string start = editor.Current.ComputeHash();

        editor.BeginGesture();
        editor.Preview(SetRatio(2));
        string edited = editor.Current.ComputeHash();
        editor.ShowingOriginal = true;

        Assert.False(editor.InGesture);
        Assert.True(editor.CanUndo);
        Assert.Equal(start, editor.Displayed.ComputeHash());

        editor.ShowingOriginal = false;
        Assert.Equal(edited, editor.Displayed.ComputeHash());

        editor.Undo();
        Assert.Equal(start, editor.Current.ComputeHash());
        Assert.False(editor.CanUndo);
    }
}
