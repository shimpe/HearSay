using HearSay.Editing;
using HearSay.OperatorMatrix.Parameters;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.IntegrationTests;

/// <summary>The values of a patch the editor shows as numbers: which exist, their ranges, setting and nudging them; fast, no rendering.</summary>
public sealed class EditableValuesTests
{
    private static readonly SynthesisProfile Fm7 = SynthesisProfiles.SuperColliderFm7;
    private static readonly SynthesisProfile Fmx6 = SynthesisProfiles.SuperColliderFmx6;
    private static readonly SynthesisProfile Nome6 = SynthesisProfiles.SuperColliderNome6;

    /// <summary>OP2 modulates OP1 by 2 rad; OP1 is heard; OP3 to OP6 are free.</summary>
    private static OperatorMatrixPatch TwoOperators()
        => OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.01, 0.2, 0.5, 0.1)))
            .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(2), OperatorEnvelope.Adsr(0.01, 0.4, 0.3, 0.1)))
            .WithOutputLevel(0, 1.0)
            .WithEdge(new ModulationEdge(1, 0, true, 2.0));

    private static EditableValue Value(IReadOnlyList<EditableValue> values, string id)
        => Assert.Single(values, v => v.Id == id);

    private static OperatorMatrixPatch Done(OperatorMatrixPatch patch, SynthesisProfile profile, PatchEdit edit)
    {
        var editor = new PatchEditor(patch, profile);
        EditOutcome outcome = editor.Apply(edit);
        Assert.False(outcome.IsRefused, outcome.Refusal);
        return outcome.Patch;
    }

    [Fact]
    public void For_Operator_ListsItsValuesWithSchemaRanges()
    {
        // A sine at a ratio on FM7: no fixed frequency, no waveform shape, no filter, no roughness.
        OperatorMatrixPatch patch = TwoOperators();
        IReadOnlyList<EditableValue> values = EditableValues.For(patch, Fm7.Capabilities, new PatchPart.OperatorPart(0));
        Assert.Equal(
            ["op1.frequency.ratio", "op1.fineTune", "op1.detune", "op1.phase", "op1.output.level", "op1.velocity", "op1.keyScaling.level", "op1.keyScaling.rate"],
            values.Select(static v => v.Id));
        Assert.All(values, static v => Assert.False(string.IsNullOrWhiteSpace(v.Label)));

        PatchParameterSchema schema = PatchParameterSchema.Build(patch, Fm7.Capabilities);
        foreach (string id in new[] { "op1.fineTune", "op1.detune", "op1.phase", "op1.velocity", "op1.keyScaling.level", "op1.keyScaling.rate" })
        {
            PatchParameter parameter = schema.Find(id)!;
            EditableValue value = Value(values, id);
            Assert.Equal(parameter.Minimum, value.Minimum);
            Assert.Equal(parameter.Maximum, value.Maximum);
        }

        EditableValue fine = Value(values, "op1.fineTune");
        Assert.Equal("fine tune", fine.Label);
        Assert.Equal("cents", fine.Unit);
        Assert.False(fine.Logarithmic);
        Assert.Equal("Hz", Value(values, "op1.detune").Unit);
        Assert.Equal("rad", Value(values, "op1.phase").Unit);

        // The output level is logarithmic up to the schema's maximum, and reaches 0 (not heard).
        EditableValue output = Value(values, "op1.output.level");
        Assert.True(output.Logarithmic);
        Assert.Equal(schema.Find("op1.output.level")!.Maximum, output.Maximum);
        Assert.Equal(0.0, output.Minimum);
        Assert.Equal(1.0, output.Get(patch));

        // A fixed-frequency operator has its hertz instead of a ratio and a fine tune.
        OperatorMatrixPatch fixedHz = patch.WithOperator(1, patch.Operators[1] with { Frequency = OperatorFrequency.AtFixed(300) });
        IReadOnlyList<string> fixedIds = [.. EditableValues.For(fixedHz, Fm7.Capabilities, new PatchPart.OperatorPart(1)).Select(static v => v.Id)];
        Assert.Contains("op2.frequency.fixedHz", fixedIds);
        Assert.DoesNotContain("op2.frequency.ratio", fixedIds);
        Assert.DoesNotContain("op2.fineTune", fixedIds);
        EditableValue hz = Value(EditableValues.For(fixedHz, Fm7.Capabilities, new PatchPart.OperatorPart(1)), "op2.frequency.fixedHz");
        Assert.Equal(1.0, hz.Minimum);
        Assert.Equal(12000.0, hz.Maximum);
        Assert.True(hz.Logarithmic);
        Assert.Equal(300.0, hz.Get(fixedHz));

        // A pulse with a low-pass filter on FMX6: its width and the filter's values.
        OperatorMatrixPatch pulse = patch.WithOperator(0, patch.Operators[0].WithWaveform(OperatorWaveform.Pulse) with { Filter = OperatorFilter.LowPass(4) });
        IReadOnlyList<EditableValue> pulseValues = EditableValues.For(pulse, Fmx6.Capabilities, new PatchPart.OperatorPart(0));
        IReadOnlyList<string> pulseIds = [.. pulseValues.Select(static v => v.Id)];
        Assert.Contains("op1.shape", pulseIds);
        Assert.Contains("op1.operatorFilter.cutoff", pulseIds);
        Assert.Contains("op1.operatorFilter.q", pulseIds);
        Assert.DoesNotContain("op1.operatorFilter.fixedHz", pulseIds);
        Assert.DoesNotContain("op1.operatorFilter.octaves", pulseIds);
        Assert.DoesNotContain("op1.filter", pulseIds);
        Assert.DoesNotContain("op1.noisePhase", pulseIds);
        EditableValue cutoff = Value(pulseValues, "op1.operatorFilter.cutoff");
        Assert.Equal(0.25, cutoff.Minimum);
        Assert.Equal(64.0, cutoff.Maximum);
        Assert.True(cutoff.Logarithmic);
        Assert.Equal(0.01, Value(pulseValues, "op1.shape").Minimum);

        // A roughened elliptic operator on Nome6: its m, morph and both roughnesses.
        OperatorMatrixPatch elliptic = patch.WithOperator(0, patch.Operators[0].WithWaveform(OperatorWaveform.Elliptic) with { NoisePhase = 0.3, NoiseM = 0.1 });
        IReadOnlyList<string> ellipticIds = [.. EditableValues.For(elliptic, Nome6.Capabilities, new PatchPart.OperatorPart(0)).Select(static v => v.Id)];
        Assert.Contains("op1.elliptic", ellipticIds);
        Assert.Contains("op1.morph", ellipticIds);
        Assert.Contains("op1.noisePhase", ellipticIds);
        Assert.Contains("op1.noiseM", ellipticIds);
        Assert.DoesNotContain("op1.shape", ellipticIds);

        // A smooth operator has no roughness to edit.
        Assert.DoesNotContain("op1.noisePhase", EditableValues.For(patch, Nome6.Capabilities, new PatchPart.OperatorPart(0)).Select(static v => v.Id));

        // A value outside the schema's range widens it, so that touching it does not make it jump.
        OperatorMatrixPatch wide = patch.WithOperator(0, patch.Operators[0] with { FineTuneCents = 80 });
        EditableValue wideFine = Value(EditableValues.For(wide, Fm7.Capabilities, new PatchPart.OperatorPart(0)), "op1.fineTune");
        Assert.Equal(-50.0, wideFine.Minimum);
        Assert.Equal(80.0, wideFine.Maximum);
    }

    [Fact]
    public void For_Connection_ListsTheAmount()
    {
        OperatorMatrixPatch patch = TwoOperators();
        EditableValue amount = Assert.Single(EditableValues.For(patch, Fm7.Capabilities, new PatchPart.ConnectionPart(1, 0)));
        Assert.Equal("edge.2>1.amount", amount.Id);
        Assert.Equal("rad", amount.Unit);
        Assert.Equal(0.0, amount.Minimum);
        Assert.Equal(Fm7.Capabilities.MaxModulationRadiansPerDestination, amount.Maximum);
        Assert.Equal(2.0, amount.Get(patch));

        OperatorMatrixPatch edited = Done(patch, Fm7, amount.Set(3.5));
        Assert.Equal(3.5, edited.ModulationMatrix.Find(1, 0)!.Amount);

        // No connection, no values; the shared noise on a profile with it; the vibrato once there is one.
        Assert.Empty(EditableValues.For(patch, Fm7.Capabilities, new PatchPart.ConnectionPart(0, 1)));
        Assert.Empty(EditableValues.For(patch, Fm7.Capabilities, new PatchPart.NoisePart()));
        Assert.Equal(["noise.color", "noise.cutoff"], EditableValues.For(patch, Nome6.Capabilities, new PatchPart.NoisePart()).Select(static v => v.Id));
        Assert.Empty(EditableValues.For(patch, Fm7.Capabilities, new PatchPart.PitchPart()));
        OperatorMatrixPatch vibrato = patch with { Pitch = patch.Pitch with { Vibrato = new LfoSettings(5, 10) } };
        Assert.Equal(
            ["pitch.vibrato.rate", "pitch.vibrato.depth", "pitch.vibrato.delay", "pitch.vibrato.fadeIn"],
            EditableValues.For(vibrato, Fm7.Capabilities, new PatchPart.PitchPart()).Select(static v => v.Id));
    }

    [Fact]
    public void Ratio_IsContinuous_AndSnapsWhenAsked()
    {
        OperatorMatrixPatch patch = TwoOperators();
        EditableValue ratio = Value(EditableValues.For(patch, Fm7.Capabilities, new PatchPart.OperatorPart(1)), "op2.frequency.ratio");
        Assert.Equal(1.0 / 16.0, ratio.Minimum);
        Assert.Equal(32.0, ratio.Maximum);
        Assert.True(ratio.Logarithmic);
        Assert.Equal(PatchParameterSchema.DefaultRatios, ratio.SnapPoints);

        // Any ratio can be set, not only the search's list.
        OperatorMatrixPatch edited = Done(patch, Fm7, ratio.Set(2.37));
        Assert.Equal(2.37, edited.Operators[1].Frequency.Ratio, 12);

        // A drag moves it continuously; with snapping it lands on the nearest musical ratio.
        double dragged = ratio.Nudge(2.0, 7, fine: false, snap: false);
        Assert.True(dragged > 2.0);
        Assert.DoesNotContain(dragged, PatchParameterSchema.DefaultRatios);
        Assert.Equal(2.5, ratio.Nudge(2.0, 7, fine: false, snap: true));
        Assert.Equal(2.0, ratio.Nudge(2.0, 2, fine: false, snap: true));

        // Logarithmic: the same drag doubles 1 and 4 alike.
        Assert.Equal(ratio.Nudge(1.0, 50, false, false) / 1.0, ratio.Nudge(4.0, 50, false, false) / 4.0, 9);
    }

    [Fact]
    public void SetClamped_RejectsNonNumbersAndClampsTheRest()
    {
        OperatorMatrixPatch patch = TwoOperators();
        EditableValue fine = Value(EditableValues.For(patch, Fm7.Capabilities, new PatchPart.OperatorPart(0)), "op1.fineTune");

        foreach (double bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            EditOutcome refused = fine.Set(bad)(patch, Fm7.Capabilities);
            Assert.True(refused.IsRefused);
            Assert.False(string.IsNullOrWhiteSpace(refused.Refusal));
            Assert.Same(patch, refused.Patch);
        }

        Assert.Equal(-50.0, Done(patch, Fm7, fine.Set(-300)).Operators[0].FineTuneCents);
        Assert.Equal(50.0, Done(patch, Fm7, fine.Set(300)).Operators[0].FineTuneCents);
        Assert.Equal(12.5, Done(patch, Fm7, fine.Set(12.5)).Operators[0].FineTuneCents);

        EditableValue ratio = Value(EditableValues.For(patch, Fm7.Capabilities, new PatchPart.OperatorPart(0)), "op1.frequency.ratio");
        Assert.Equal(1.0 / 16.0, Done(patch, Fm7, ratio.Set(0)).Operators[0].Frequency.Ratio);
        Assert.Equal(32.0, Done(patch, Fm7, ratio.Set(1000)).Operators[0].Frequency.Ratio);

        // A value whose part has gone is refused.
        OperatorMatrixPatch fixedHz = patch.WithOperator(0, patch.Operators[0] with { Frequency = OperatorFrequency.AtFixed(440) });
        Assert.True(ratio.Set(2)(fixedHz, Fm7.Capabilities).IsRefused);
    }

    [Fact]
    public void EdgeAmount_StopsAtTheModulationLimit()
    {
        // OP2 modulates OP1 by 20 rad, OP3 by 1 rad; the profile allows 24 rad into one operator.
        OperatorMatrixPatch patch = TwoOperators()
            .WithOperator(2, OperatorPatch.Sine(OperatorFrequency.AtRatio(3)))
            .WithEdge(new ModulationEdge(1, 0, true, 20.0))
            .WithEdge(new ModulationEdge(2, 0, true, 1.0));
        Assert.Equal(24.0, Fm7.Capabilities.MaxModulationRadiansPerDestination);

        EditableValue amount = Assert.Single(EditableValues.For(patch, Fm7.Capabilities, new PatchPart.ConnectionPart(2, 0)));
        OperatorMatrixPatch edited = Done(patch, Fm7, amount.Set(20));
        Assert.Equal(4.0, edited.ModulationMatrix.Find(2, 0)!.Amount, 12);
        Assert.True(PatchCompatibilityValidator.Instance.Validate(edited, Fm7.Capabilities).IsCompatible);

        // An envelope on the other connection counts at its peak: at most half the amount leaves 14 rad.
        var half = new OperatorEnvelope([new EnvelopePoint(0, 0), new EnvelopePoint(0.01, 0.5), new EnvelopePoint(0.1, 0)], SustainPoint: 1, ReleasePoint: 2);
        OperatorMatrixPatch enveloped = patch.WithEdge(patch.ModulationMatrix.Find(1, 0)! with { AmountEnvelope = half });
        Assert.Equal(14.0, Done(enveloped, Fm7, amount.Set(20)).ModulationMatrix.Find(2, 0)!.Amount, 12);
    }

    [Fact]
    public void Nudge_FineIsTenTimesSmaller()
    {
        OperatorMatrixPatch patch = TwoOperators();
        IReadOnlyList<EditableValue> values = EditableValues.For(patch, Fm7.Capabilities, new PatchPart.OperatorPart(0));

        // Linear: 1/200 of the range per pixel, 1/2000 when fine.
        EditableValue fine = Value(values, "op1.fineTune");
        Assert.Equal(5.0, fine.Nudge(0, 10, fine: false, snap: false), 9);
        Assert.Equal(0.5, fine.Nudge(0, 10, fine: true, snap: false), 9);
        Assert.Equal(-50.0, fine.Nudge(0, -1000, fine: false, snap: false));

        // Logarithmic: the same, in ratios.
        EditableValue ratio = Value(values, "op1.frequency.ratio");
        double coarse = Math.Log(ratio.Nudge(1.0, 10, fine: false, snap: false));
        double small = Math.Log(ratio.Nudge(1.0, 10, fine: true, snap: false));
        Assert.Equal(coarse / 10.0, small, 9);
        Assert.Equal(Math.Log(32.0 * 16.0) / 20.0, coarse, 9);
        Assert.Equal(32.0, ratio.Nudge(1.0, 1e6, fine: false, snap: false));
    }
}
