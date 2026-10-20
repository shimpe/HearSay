using HearSay.Editing;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.IntegrationTests;

/// <summary>Which parts and values of an edited patch differ from the original, as the editor marks them; fast, no rendering.</summary>
public sealed class EditMarksTests
{
    private static readonly SynthesisProfile Fmx6 = SynthesisProfiles.SuperColliderFmx6;
    private static readonly SynthesisProfile Nome6 = SynthesisProfiles.SuperColliderNome6;

    /// <summary>OP2 modulates OP1 by 2 rad; OP1 is heard; OP3 to OP6 are free.</summary>
    private static OperatorMatrixPatch TwoOperators()
        => OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.01, 0.2, 0.5, 0.1)))
            .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(2), OperatorEnvelope.Adsr(0.01, 0.4, 0.3, 0.1)))
            .WithOutputLevel(0, 1.0)
            .WithEdge(new ModulationEdge(1, 0, true, 2.0));

    private static OperatorMatrixPatch Done(OperatorMatrixPatch patch, SynthesisProfile profile, params PatchEdit[] edits)
    {
        var editor = new PatchEditor(patch, profile);
        foreach (PatchEdit edit in edits)
        {
            EditOutcome outcome = editor.Apply(edit);
            Assert.False(outcome.IsRefused, outcome.Refusal);
        }

        return editor.Current;
    }

    private static PatchPart[] AllParts(int n) =>
        [
            .. Enumerable.Range(0, n).Select(static i => (PatchPart)new PatchPart.OperatorPart(i)),
            .. Enumerable.Range(0, n).SelectMany(s => Enumerable.Range(0, n).Select(d => (PatchPart)new PatchPart.ConnectionPart(s, d))),
            new PatchPart.NoisePart(),
            new PatchPart.PitchPart(),
        ];

    private static PatchPart[] Edited(OperatorMatrixPatch original, OperatorMatrixPatch current)
        => [.. AllParts(original.OperatorCount).Where(p => EditMarks.IsEdited(original, current, p))];

    [Fact]
    public void Unchanged_MarksNothing()
    {
        OperatorMatrixPatch patch = TwoOperators();
        Assert.Empty(Edited(patch, patch));

        // An equal patch built anew is not edited either: parts are compared by what they hold.
        Assert.Empty(Edited(patch, TwoOperators()));
    }

    [Fact]
    public void AValueOfAnOperator_MarksThatOperatorAndThatValueOnly()
    {
        OperatorMatrixPatch original = TwoOperators();
        EditableValue ratio = EditableValues.For(original, Fmx6.Capabilities, new PatchPart.OperatorPart(1)).Single(v => v.Id == "op2.frequency.ratio");
        OperatorMatrixPatch edited = Done(original, Fmx6, ratio.Set(3.0));

        Assert.Equal([new PatchPart.OperatorPart(1)], Edited(original, edited));
        IReadOnlyList<EditableValue> values = EditableValues.For(edited, Fmx6.Capabilities, new PatchPart.OperatorPart(1));
        Assert.Equal(["op2.frequency.ratio"], values.Where(v => EditMarks.IsEdited(original, edited, new PatchPart.OperatorPart(1), v)).Select(static v => v.Id));
    }

    [Fact]
    public void TheOutputLevel_MarksTheOperator()
    {
        OperatorMatrixPatch original = TwoOperators();
        EditableValue output = EditableValues.For(original, Fmx6.Capabilities, new PatchPart.OperatorPart(0)).Single(v => v.Id == "op1.output.level");
        OperatorMatrixPatch edited = Done(original, Fmx6, output.Set(0.5));

        Assert.Equal([new PatchPart.OperatorPart(0)], Edited(original, edited));
        Assert.True(EditMarks.IsEdited(original, edited, new PatchPart.OperatorPart(0), output));
    }

    [Fact]
    public void Structure_MarksTheParts_ItChanged()
    {
        OperatorMatrixPatch original = TwoOperators();

        // A waveform, a filter, an envelope point: the operator.
        Assert.Equal([new PatchPart.OperatorPart(1)], Edited(original, Done(original, Fmx6, PatchEdits.SetWaveform(1, OperatorWaveform.Saw))));
        Assert.Equal([new PatchPart.OperatorPart(0)], Edited(original, Done(original, Fmx6, PatchEdits.SetFilter(0, OperatorFilterType.LowPass))));
        var amplitude = new EnvelopeRef(new PatchPart.OperatorPart(0), EnvelopeKind.Amplitude);
        Assert.Equal([new PatchPart.OperatorPart(0)], Edited(original, Done(original, Fmx6, EnvelopeEdits.MovePoint(amplitude, 1, 0.05, 1.0))));

        // A new connection, a connection gone, another amount or an envelope on it: that connection.
        Assert.Equal([new PatchPart.ConnectionPart(0, 1)], Edited(original, Done(original, Fmx6, PatchEdits.Connect(0, 1))));
        Assert.Equal([new PatchPart.ConnectionPart(1, 0)], Edited(original, Done(original, Fmx6, PatchEdits.Disconnect(1, 0))));
        Assert.Equal([new PatchPart.ConnectionPart(1, 0)], Edited(original, Done(original, Fmx6, PatchEdits.SetConnectionEnvelope(1, 0, true))));

        // The pitch and the shared noise.
        Assert.Equal([new PatchPart.PitchPart()], Edited(original, Done(original, Fmx6, PatchEdits.SetVibrato(true))));
        EditableValue colour = EditableValues.For(original, Nome6.Capabilities, new PatchPart.NoisePart()).First();
        Assert.Equal([new PatchPart.NoisePart()], Edited(original, Done(original, Nome6, colour.Set(colour.Get(original) == colour.Maximum ? colour.Minimum : colour.Maximum))));
    }

    [Fact]
    public void ANewOperator_IsMarked_WithItsConnections_AndARemovedOne_Too()
    {
        OperatorMatrixPatch original = TwoOperators();
        OperatorMatrixPatch duplicated = Done(original, Fmx6, PatchEdits.DuplicateOperator(1));
        Assert.Equal([new PatchPart.OperatorPart(2), new PatchPart.ConnectionPart(2, 0)], Edited(original, duplicated));

        // Every value of the new operator is new.
        IReadOnlyList<EditableValue> values = EditableValues.For(duplicated, Fmx6.Capabilities, new PatchPart.OperatorPart(2));
        Assert.All(values, v => Assert.True(EditMarks.IsEdited(original, duplicated, new PatchPart.OperatorPart(2), v), v.Id));

        // A removed operator differs too (its connection goes with it), though the diagram no longer draws it.
        OperatorMatrixPatch removed = Done(original, Fmx6, PatchEdits.RemoveOperator(1));
        Assert.Equal([new PatchPart.OperatorPart(1), new PatchPart.ConnectionPart(1, 0)], Edited(original, removed));

        // Free operators that differ only in what they would be are not edits: removing then adding leaves them alone.
        OperatorMatrixPatch addedAndRemoved = Done(original, Fmx6, PatchEdits.AddModulator(0), PatchEdits.RemoveOperator(2));
        Assert.Empty(Edited(original, addedAndRemoved));
    }

    [Fact]
    public void AnEditUndoneByHand_IsNotMarked()
    {
        OperatorMatrixPatch original = TwoOperators();
        EditableValue ratio = EditableValues.For(original, Fmx6.Capabilities, new PatchPart.OperatorPart(1)).Single(v => v.Id == "op2.frequency.ratio");
        OperatorMatrixPatch back = Done(original, Fmx6, ratio.Set(3.0), ratio.Set(2.0));
        Assert.Empty(Edited(original, back));
    }
}
