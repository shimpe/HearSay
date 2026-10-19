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
    public void For_FormantFilter_ListsFiveFormants()
    {
        // F1 at 500 Hz, Q 6; F2 with no Q of its own; F3 on with Q 24; F4 and F5 off.
        OperatorFilter formant = OperatorFilter.Formants(500, 6, new FormantPeak(2.0, 0.5, 0), new FormantPeak(5.0, 0.2, 24.0));
        OperatorMatrixPatch patch = TwoOperators();
        patch = patch.WithOperator(0, patch.Operators[0] with { Filter = formant });
        IReadOnlyList<EditableValue> values = [.. EditableValues.For(patch, Fmx6.Capabilities, new PatchPart.OperatorPart(0)).Where(static v => v.Id.Contains(".operatorFilter.", StringComparison.Ordinal))];
        string[] names = ["fixedHz", "q", "formantRatio", "formantLevel", "formantQ2", "formant3Ratio", "formant3Level", "formant3Q", "formant4Ratio", "formant4Level", "formant4Q", "formant5Ratio", "formant5Level", "formant5Q"];
        Assert.Equal(names.Select(static n => "op1.operatorFilter." + n), values.Select(static v => v.Id));
        Assert.Equal(
            ["F1", "Q1", "F2 ratio", "F2 level", "F2 Q", "F3 ratio", "F3 level", "F3 Q", "F4 ratio", "F4 level", "F4 Q", "F5 ratio", "F5 level", "F5 Q"],
            values.Select(static v => v.Label));
        Assert.Equal("Hz", Value(values, "op1.operatorFilter.fixedHz").Unit);

        // The model's ranges: ratios up to 32, levels 0..1, every Q 0.5..50, absolute (not the search's Q2 / Q).
        foreach (string n in new[] { "formantRatio", "formant3Ratio", "formant4Ratio", "formant5Ratio" })
        {
            EditableValue ratio = Value(values, "op1.operatorFilter." + n);
            Assert.Equal((1.0, 32.0, true), (ratio.Minimum, ratio.Maximum, ratio.Logarithmic));
        }

        foreach (string n in new[] { "formantLevel", "formant3Level", "formant4Level", "formant5Level" })
        {
            EditableValue level = Value(values, "op1.operatorFilter." + n);
            Assert.Equal((0.0, 1.0, false), (level.Minimum, level.Maximum, level.Logarithmic));
        }

        foreach (string n in new[] { "q", "formantQ2", "formant3Q", "formant4Q", "formant5Q" })
        {
            EditableValue q = Value(values, "op1.operatorFilter." + n);
            Assert.Equal((0.5, 50.0, true), (q.Minimum, q.Maximum, q.Logarithmic));
        }

        // An own Q that is "same as Q" shows the Q it plays at; setting it stores it.
        EditableValue q2 = Value(values, "op1.operatorFilter.formantQ2");
        Assert.Equal(6.0, q2.Get(patch));
        Assert.Equal(24.0, Value(values, "op1.operatorFilter.formant3Q").Get(patch));
        Assert.Equal(6.0, Value(values, "op1.operatorFilter.formant4Q").Get(patch));
        Assert.Equal(1.0, Value(values, "op1.operatorFilter.formant5Ratio").Get(patch));
        Assert.Equal(0.0, Value(values, "op1.operatorFilter.formant5Level").Get(patch));
        Assert.Same(patch, Done(patch, Fmx6, q2.Set(6.0)));
        OperatorMatrixPatch ownQ = Done(patch, Fmx6, q2.Set(40.0));
        Assert.Equal(40.0, ownQ.Operators[0].Filter!.FormantQ2);
        Assert.Equal(6.0, ownQ.Operators[0].Filter!.Q);
        Assert.Equal(50.0, Done(patch, Fmx6, q2.Set(80.0)).Operators[0].Filter!.FormantQ2);

        // The values reach the model's limits: Q 45, an F2 at 30 times F1, F5 on.
        OperatorMatrixPatch wide = Done(patch, Fmx6, Value(values, "op1.operatorFilter.q").Set(45.0));
        Assert.Equal(45.0, wide.Operators[0].Filter!.Q);
        wide = Done(wide, Fmx6, Value(values, "op1.operatorFilter.formantRatio").Set(30.0));
        Assert.Equal(30.0, wide.Operators[0].Filter!.FormantRatio);
        wide = Done(wide, Fmx6, Value(values, "op1.operatorFilter.formant5Level").Set(0.01));
        Assert.Equal(0.01, wide.Operators[0].Filter!.Formant5Level);
        wide = Done(wide, Fmx6, Value(values, "op1.operatorFilter.formant5Ratio").Set(9.0));
        Assert.Equal(9.0, wide.Operators[0].Filter!.Formant5Ratio);

        // Another filter type keeps one Q, up to 20, labelled "Q", and has no formants.
        OperatorMatrixPatch lowPass = patch.WithOperator(0, patch.Operators[0] with { Filter = OperatorFilter.LowPass(4) });
        IReadOnlyList<EditableValue> lowValues = EditableValues.For(lowPass, Fmx6.Capabilities, new PatchPart.OperatorPart(0));
        EditableValue lowQ = Value(lowValues, "op1.operatorFilter.q");
        Assert.Equal(("Q", 0.5, 20.0), (lowQ.Label, lowQ.Minimum, lowQ.Maximum));
        Assert.DoesNotContain(lowValues, static v => v.Id.Contains("formant", StringComparison.Ordinal));

        // A formant filter on an operator that is not heard (no schema values) has the same rows and ranges.
        OperatorMatrixPatch silent = patch.WithOutputLevel(0, 0.0).WithOperator(1, patch.Operators[1] with { Filter = formant });
        IReadOnlyList<EditableValue> unheard = [.. EditableValues.For(silent, Fmx6.Capabilities, new PatchPart.OperatorPart(1)).Where(static v => v.Id.Contains(".operatorFilter.", StringComparison.Ordinal))];
        Assert.Equal(names.Select(static n => "op2.operatorFilter." + n), unheard.Select(static v => v.Id));
        Assert.Equal(50.0, Value(unheard, "op2.operatorFilter.q").Maximum);
        Assert.Equal(32.0, Value(unheard, "op2.operatorFilter.formantRatio").Maximum);
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

    [Fact]
    public void Nudge_OutputLevel_ReachesZeroSmoothly_AndStaysWithoutMovement()
    {
        // OP2 is a modulator, not heard: its output level is 0.
        OperatorMatrixPatch patch = TwoOperators();
        EditableValue output = Value(EditableValues.For(patch, Fm7.Capabilities, new PatchPart.OperatorPart(1)), "op2.output.level");
        Assert.Equal(0.0, output.Get(patch));

        // No movement, no change: a silent modulator stays silent, and a tiny level does not jump.
        Assert.Equal(0.0, output.Nudge(0, 0, fine: false, snap: false));
        Assert.Equal(0.0005, output.Nudge(0.0005, 0, fine: false, snap: false));
        Assert.Equal(0.0005, output.Nudge(0.0005, 0, fine: true, snap: false));

        // Out and back: the drag reports the distance from where it began, so back is 0 pixels again.
        double up = output.Nudge(0, 30, fine: false, snap: false);
        Assert.True(up > 0);
        Assert.Equal(0.0, output.Nudge(0, 0, fine: false, snap: false));
        Assert.Equal(0.0, output.Nudge(up, -30, fine: false, snap: false), 12);

        // Smooth near 0: one pixel up from 0 is a small level, not a jump to the logarithmic floor.
        double onePixel = output.Nudge(0, 1, fine: false, snap: false);
        Assert.InRange(onePixel, 1e-6, 0.001);
        Assert.True(output.Nudge(0, 2, fine: false, snap: false) > onePixel);

        // Well above the knee it is nearly logarithmic: the same drag scales 0.1 and 0.4 alike, within a percent.
        double low = output.Nudge(0.1, 20, false, false) / 0.1;
        double high = output.Nudge(0.4, 20, false, false) / 0.4;
        Assert.True(low > 1.5);
        Assert.InRange(high / low, 0.99, 1.01);
    }

    [Fact]
    public void Nudge_FollowsTheSchemasScale()
    {
        // The elliptic parameter moves along its nome: 50 pixels from m = 0.99 is a clear step, not the end of the range.
        OperatorMatrixPatch patch = TwoOperators();
        OperatorMatrixPatch elliptic = patch.WithOperator(0, patch.Operators[0].WithWaveform(OperatorWaveform.Elliptic));
        EditableValue m = Value(EditableValues.For(elliptic, Nome6.Capabilities, new PatchPart.OperatorPart(0)), "op1.elliptic");
        Assert.Equal(0.99, m.Get(elliptic));
        double fifty = m.Nudge(0.99, 50, fine: false, snap: false);
        Assert.True(fifty < OperatorPatch.MaxEllipticM);
        Assert.True(fifty > 0.99 + 1e-4);
        Assert.True(m.Nudge(0.99, 25, fine: false, snap: false) < fifty);
        Assert.True(m.Nudge(0.99, 25, fine: false, snap: false) > 0.99);

        // A soft-logarithmic value (detune, -8 .. 8 Hz) is finer near 0 than a linear step would be.
        EditableValue detune = Value(EditableValues.For(patch, Fm7.Capabilities, new PatchPart.OperatorPart(0)), "op1.detune");
        double linearStep = 2.0 / 200.0 * (detune.Maximum - detune.Minimum);
        double soft = detune.Nudge(0, 2, fine: false, snap: false);
        Assert.InRange(soft, 1e-6, linearStep / 2);
        Assert.Equal(-soft, detune.Nudge(0, -2, fine: false, snap: false), 12);
        Assert.Equal(8.0, detune.Nudge(0, 200, fine: false, snap: false), 9);

        // A connection amount follows the schema's soft-logarithmic scale over the profile's whole range.
        EditableValue amount = Assert.Single(EditableValues.For(patch, Fm7.Capabilities, new PatchPart.ConnectionPart(1, 0)));
        Assert.InRange(amount.Nudge(0, 2, fine: false, snap: false), 1e-6, 2.0 / 200.0 * amount.Maximum / 2);
        Assert.Equal(amount.Maximum, amount.Nudge(0, 200, fine: false, snap: false), 9);
    }

    [Fact]
    public void For_PulseOperator_HasHarmonics()
    {
        // A pulse that the library sound left at 8 harmonics: the row shows them, right after the pulse width.
        OperatorMatrixPatch patch = TwoOperators();
        OperatorMatrixPatch pulse = patch.WithOperator(0, patch.Operators[0].WithWaveform(OperatorWaveform.Pulse) with { HarmonicLimit = 8 });
        IReadOnlyList<EditableValue> values = EditableValues.For(pulse, Fmx6.Capabilities, new PatchPart.OperatorPart(0));
        EditableValue harmonics = Value(values, "op1.harmonics");
        Assert.Equal(("harmonics", "", 1.0, 128.0, true), (harmonics.Label, harmonics.Unit, harmonics.Minimum, harmonics.Maximum, harmonics.Logarithmic));
        Assert.True(harmonics.WholeNumbers);
        Assert.Equal(8.0, harmonics.Get(pulse));
        List<string> ids = [.. values.Select(static v => v.Id)];
        Assert.Equal(ids.IndexOf("op1.shape") + 1, ids.IndexOf("op1.harmonics"));
        Assert.Equal(64, Done(pulse, Fmx6, harmonics.Set(64)).Operators[0].HarmonicLimit);

        // Every waveform the FMX6 renderer builds from harmonics has the row.
        foreach (OperatorWaveform waveform in new[] { OperatorWaveform.Triangle, OperatorWaveform.Saw, OperatorWaveform.Square })
        {
            OperatorMatrixPatch other = patch.WithOperator(1, patch.Operators[1].WithWaveform(waveform));
            Assert.Equal(32.0, Value(EditableValues.For(other, Fmx6.Capabilities, new PatchPart.OperatorPart(1)), "op2.harmonics").Get(other));
        }

        // It is the editor's own: no parameter of the search has its id.
        PatchParameterSchema schema = PatchParameterSchema.Build(pulse, Fmx6.Capabilities);
        Assert.DoesNotContain(schema.Parameters, static p => p.Id.EndsWith(".harmonics", StringComparison.Ordinal));
    }

    [Fact]
    public void Harmonics_AreWholeNumbers_1To128()
    {
        OperatorMatrixPatch patch = TwoOperators();
        OperatorMatrixPatch saw = patch.WithOperator(0, patch.Operators[0].WithWaveform(OperatorWaveform.Saw) with { HarmonicLimit = 8 });
        EditableValue harmonics = Value(EditableValues.For(saw, Fmx6.Capabilities, new PatchPart.OperatorPart(0)), "op1.harmonics");

        // Typed values are rounded and kept within 1..128, as the UGen clamps them.
        Assert.Equal(10, Done(saw, Fmx6, harmonics.Set(10.4)).Operators[0].HarmonicLimit);
        Assert.Equal(11, Done(saw, Fmx6, harmonics.Set(10.6)).Operators[0].HarmonicLimit);
        Assert.Equal(1, Done(saw, Fmx6, harmonics.Set(0)).Operators[0].HarmonicLimit);
        Assert.Equal(1, Done(saw, Fmx6, harmonics.Set(-3)).Operators[0].HarmonicLimit);
        Assert.Equal(128, Done(saw, Fmx6, harmonics.Set(500)).Operators[0].HarmonicLimit);
        Assert.Same(saw, Done(saw, Fmx6, harmonics.Set(8.2)));

        // A drag moves in ratios and lands on whole numbers: half the range up from 8 is 8 × √128 ≈ 90.5.
        Assert.Equal(8.0, harmonics.Nudge(8, 0, fine: false, snap: false));
        Assert.Equal(91.0, harmonics.Nudge(8, 100, fine: false, snap: false));
        Assert.Equal(128.0, harmonics.Nudge(8, 1000, fine: false, snap: false));
        Assert.Equal(1.0, harmonics.Nudge(8, -1000, fine: false, snap: false));
        for (int pixels = -60; pixels <= 60; pixels += 7)
        {
            double value = harmonics.Nudge(8, pixels, fine: pixels % 2 == 0, snap: false);
            Assert.Equal(Math.Round(value), value);
            Assert.InRange(value, 1.0, 128.0);
        }

        // Other values are not rounded.
        Assert.False(Value(EditableValues.For(saw, Fmx6.Capabilities, new PatchPart.OperatorPart(0)), "op1.detune").WholeNumbers);
    }

    [Fact]
    public void For_SineOperator_HasNoHarmonics()
    {
        OperatorMatrixPatch patch = TwoOperators();
        static bool HasHarmonics(OperatorMatrixPatch p, SynthesisProfile profile)
            => EditableValues.For(p, profile.Capabilities, new PatchPart.OperatorPart(0)).Any(static v => v.Id == "op1.harmonics");

        // A sine (FM7 has nothing else), the noises and an elliptic operator are not built from a number of harmonics.
        Assert.False(HasHarmonics(patch, Fm7));
        Assert.False(HasHarmonics(patch, Fmx6));
        foreach (OperatorWaveform waveform in new[] { OperatorWaveform.Noise, OperatorWaveform.SampleHoldNoise, OperatorWaveform.FilteredNoise })
        {
            Assert.False(HasHarmonics(patch.WithOperator(0, patch.Operators[0].WithWaveform(waveform)), Fmx6));
        }

        Assert.False(HasHarmonics(patch.WithOperator(0, patch.Operators[0].WithWaveform(OperatorWaveform.Elliptic)), Nome6));
    }
}
