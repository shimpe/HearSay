using HearSay.OperatorMatrix.Canonicalization;
using HearSay.OperatorMatrix.Parameters;
using HearSay.OperatorMatrix.Search;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.OperatorMatrix.Tests;

public sealed class ParameterTests
{
    private static readonly SynthesisCapabilities Fm7 = SynthesisProfiles.SuperColliderFm7.Capabilities;

    internal static OperatorMatrixPatch Stack(double amount = 2.0) =>
        OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.TwoStageDecay(0.005, 0.2, 0.5, 1.0, 0.1, 0.2)))
            .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(2), OperatorEnvelope.TwoStageDecay(0.002, 0.1, 0.3, 0.8, 0.05, 0.1)))
            .WithOutputLevel(0, 0.8)
            .WithEdge(new ModulationEdge(1, 0, true, amount));

    [Fact]
    public void Schema_HasTheDocumentedHierarchy()
    {
        PatchParameterSchema schema = PatchParameterSchema.Build(Stack(), Fm7);

        Assert.Equal("Patch", schema.Root.Name);
        string[] top = [.. schema.Root.Children.Select(static c => c.Name)];
        Assert.Equal(["Operator 1", "Operator 2", "Operator 3", "Operator 4", "Operator 5", "Operator 6", "Modulation Matrix"], top);
        ParameterGroup op1 = schema.Root.Children[0];
        Assert.Equal(["Frequency", "Amplitude Envelope", "Output"], op1.Children.Select(static c => c.Name));
        Assert.Contains(op1.Parameters, static p => p.Id == "op1.enabled");
        Assert.Contains(op1.Parameters, static p => p.Id == "op1.mode");
        Assert.Contains(op1.Parameters, static p => p.Id == "op1.waveform");
        Assert.Equal(36, schema.Root.Children[6].Children.Count);
        Assert.NotNull(schema.Find("edge.6>1.amount"));
        Assert.NotNull(schema.Find("edge.4>4.enabled"));
    }

    [Fact]
    public void StructuralAndContinuousParameters_AreKeptApart()
    {
        PatchParameterSchema schema = PatchParameterSchema.Build(Stack(), Fm7);

        foreach (string id in new[] { "op1.enabled", "op1.waveform", "op1.mode", "op1.frequency.mode", "op1.frequency.ratio", "op1.output.enabled", "edge.2>1.enabled" })
        {
            PatchParameter parameter = schema.Find(id)!;
            Assert.Equal(ParameterSearchKind.StructuralDiscrete, parameter.Kind);
            Assert.False(parameter.IsSmooth);
            Assert.NotNull(parameter.Choices);
        }

        foreach (string id in new[] { "edge.2>1.amount", "op1.output.level", "op1.fineTune", "op1.detune", "op1.env.p2.time", "op1.env.p2.level" })
        {
            Assert.True(schema.Find(id)!.IsSmooth, id);
        }

        Assert.Equal(ParameterSearchKind.WarpedContinuous, schema.Find("edge.2>1.amount")!.Kind);
        Assert.Equal(ParameterSearchKind.Continuous, schema.Find("op1.fineTune")!.Kind);
        Assert.Throws<ArgumentException>(() => new ActiveParameterSet([schema.Find("op1.waveform")!]));
    }

    [Fact]
    public void StructuralChoices_ComeFromTheProfile()
    {
        PatchParameterSchema fm7 = PatchParameterSchema.Build(Stack(), Fm7);
        PatchParameterSchema experimental = PatchParameterSchema.Build(Stack(), SynthesisProfiles.ExperimentalSineTriangle.Capabilities);
        PatchParameterSchema noFeedback = PatchParameterSchema.Build(Stack(), Fm7 with { SupportsSelfFeedback = false, SupportsFullModulationMatrix = false });

        Assert.Equal([(double)(int)OperatorWaveform.Sine], fm7.Find("op3.waveform")!.Choices);
        Assert.Equal([(double)(int)OperatorWaveform.Sine, (int)OperatorWaveform.Triangle], experimental.Find("op3.waveform")!.Choices);
        Assert.Equal([(double)(int)OperatorMode.PhaseModulation], fm7.Find("op3.mode")!.Choices);
        Assert.Null(noFeedback.Find("edge.1>1.enabled"));
        Assert.Null(noFeedback.Find("edge.1>2.enabled"));
        Assert.NotNull(noFeedback.Find("edge.2>1.enabled"));
    }

    [Fact]
    public void ConditionalActivity_FollowsOperatorsAndEdges()
    {
        OperatorMatrixPatch patch = Stack();
        PatchParameterSchema schema = PatchParameterSchema.Build(patch, Fm7);

        Assert.True(schema.Find("edge.2>1.amount")!.IsActive(patch));
        Assert.True(schema.Find("op2.env.p2.time")!.IsActive(patch));
        Assert.False(schema.Find("op2.output.level")!.IsActive(patch));
        Assert.False(schema.Find("op3.fineTune")!.IsActive(patch));
        Assert.False(schema.Find("edge.3>1.amount")!.IsActive(patch));

        // Edge disabled: its amount goes inactive, and the modulator with nothing left to do goes with it.
        OperatorMatrixPatch noEdge = schema.Find("edge.2>1.enabled")!.Set(patch, 0);
        Assert.False(schema.Find("edge.2>1.amount")!.IsActive(noEdge));
        Assert.False(schema.Find("op2.env.p2.time")!.IsActive(noEdge));
        Assert.False(schema.Find("op2.fineTune")!.IsActive(noEdge));

        // Operator disabled: frequency, waveform, outgoing edges and output gain all go inactive.
        OperatorMatrixPatch noCarrier = schema.Find("op1.enabled")!.Set(patch, 0);
        foreach (string id in new[] { "op1.waveform", "op1.frequency.ratio", "op1.fineTune", "op1.output.level", "op1.output.enabled", "edge.2>1.amount", "edge.2>1.enabled" })
        {
            Assert.False(schema.Find(id)!.IsActive(noCarrier), id);
        }

        Assert.DoesNotContain(schema.ActiveSmooth(patch), static p => p.Id.StartsWith("op3.", StringComparison.Ordinal));
    }

    [Fact]
    public void Transforms_RoundTripAndWarpTheAxis()
    {
        PatchParameterSchema schema = PatchParameterSchema.Build(Stack(), Fm7);
        PatchParameter time = schema.Find("op1.env.p2.time")!;
        PatchParameter amount = schema.Find("edge.2>1.amount")!;

        foreach (double position in new[] { 0.0, 0.13, 0.5, 0.87, 1.0 })
        {
            Assert.Equal(position, time.Normalize(time.Denormalize(position)), 9);
            Assert.Equal(position, amount.Normalize(amount.Denormalize(position)), 9);
        }

        // Equal steps on a logarithmic axis are equal ratios.
        double ratioLow = time.Denormalize(0.3) / time.Denormalize(0.2);
        double ratioHigh = time.Denormalize(0.8) / time.Denormalize(0.7);
        Assert.Equal(ratioLow, ratioHigh, 6);
        Assert.Equal(0.0, amount.Denormalize(0.0), 12);
        foreach (IParameterTransform transform in new[] { ParameterTransforms.Linear, ParameterTransforms.Logarithmic, ParameterTransforms.SoftLogarithmic(0.25) })
        {
            Assert.Equal(1.7, transform.ToNativeSpace(transform.ToSearchSpace(1.7)), 9);
        }
    }

    [Fact]
    public void TimeLimits_BoundEnvelopeTimesByTheNote()
    {
        PatchParameterSchema schema = PatchParameterSchema.Build(Stack(), Fm7, null, new EnvelopeTimeLimits(1.5, 0.4));

        Assert.Equal(1.5, schema.Find("op1.env.p2.time")!.Maximum);
        Assert.Equal(1.5, schema.Find("op1.env.p3.time")!.Maximum);
        Assert.Equal(0.4, schema.Find("op1.env.p4.time")!.Maximum);
    }

    [Fact]
    public void ActiveSet_MovesOnlyItsParametersAndFreezesTheRest()
    {
        OperatorMatrixPatch patch = Stack();
        PatchParameterSchema schema = PatchParameterSchema.Build(patch, Fm7);
        var set = new ActiveParameterSet([schema.Find("edge.2>1.amount")!, schema.Find("op1.output.level")!]);

        double[] vector = set.ToVector(patch);
        vector[0] = 0.9;
        OperatorMatrixPatch moved = set.Apply(patch, vector);

        Assert.Equal(2, set.Count);
        Assert.True(moved.ModulationMatrix[0, 1] > patch.ModulationMatrix[0, 1]);
        Assert.Equal(patch.OutputLevels[0], moved.OutputLevels[0], 9);
        Assert.Equal(patch.Operators[0], moved.Operators[0]);
        Assert.True(PatchEnvelopesEqual(patch.Operators[1].AmplitudeEnvelope, moved.Operators[1].AmplitudeEnvelope));
    }

    [Fact]
    public void Tie_MovesAllMembersTogetherAndCanBeSplitLater()
    {
        OperatorMatrixPatch patch = Stack();
        PatchParameterSchema schema = PatchParameterSchema.Build(patch, Fm7);
        PatchParameter a = schema.Find("op1.env.p2.time")!;
        PatchParameter b = schema.Find("op2.env.p2.time")!;
        PatchParameter macro = ParameterTie.Create("macro.decay", [a, b], patch);

        OperatorMatrixPatch longer = macro.Set(patch, 0.2);

        Assert.Equal(0.0, macro.Get(patch), 9);
        Assert.Equal(0.2, macro.Get(longer), 6);
        Assert.Equal(a.GetNormalized(patch) + 0.2, a.GetNormalized(longer), 6);
        Assert.Equal(b.GetNormalized(patch) + 0.2, b.GetNormalized(longer), 6);

        // Split: the members are ordinary parameters again and move on their own.
        OperatorMatrixPatch split = a.SetNormalized(longer, 0.1);
        Assert.Equal(b.GetNormalized(longer), b.GetNormalized(split), 9);
        Assert.Throws<ArgumentException>(() => ParameterTie.Create("bad", [schema.Find("op1.waveform")!], patch));
    }

    [Fact]
    public void RegimeKey_CapturesStructureAndIgnoresContinuousValues()
    {
        OperatorMatrixPatch patch = Stack(2.0);
        OperatorMatrixRegimeKey key = OperatorMatrixRegimeKey.FromPatch(patch);

        Assert.Equal(key, OperatorMatrixRegimeKey.FromPatch(Stack(3.5)));
        Assert.Equal(key, OperatorMatrixRegimeKey.FromPatch(patch.WithOutputLevel(0, 0.3)));
        Assert.Contains("PhaseModulation/Sine", key.Value);

        Assert.NotEqual(key, OperatorMatrixRegimeKey.FromPatch(patch.WithOperator(1, patch.Operators[1] with { Waveform = OperatorWaveform.Triangle })));
        Assert.NotEqual(key, OperatorMatrixRegimeKey.FromPatch(patch.WithOperator(1, patch.Operators[1] with { Frequency = OperatorFrequency.AtRatio(3) })));
        Assert.NotEqual(key, OperatorMatrixRegimeKey.FromPatch(patch.WithOperator(1, patch.Operators[1] with { Frequency = OperatorFrequency.AtFixed(440) })));
        Assert.NotEqual(key, OperatorMatrixRegimeKey.FromPatch(patch.WithEdge(new ModulationEdge(1, 1, true, 0.5))));
        Assert.NotEqual(key, OperatorMatrixRegimeKey.FromPatch(patch.WithOutputLevel(1, 0.3)));
    }

    [Fact]
    public void RegimeKey_SeparatesFilterTypesAndPoles_ButNotTheirValues()
    {
        OperatorMatrixPatch plain = FilteredSaw(OperatorFilter.LowPass(4));
        plain = plain.WithOperator(0, plain.Operators[0] with { Filter = null });
        OperatorMatrixRegimeKey none = OperatorMatrixRegimeKey.FromPatch(plain);
        Assert.Equal("op1:PhaseModulation/Saw/r1/out;|", none.Value);

        OperatorMatrixRegimeKey lowPass = OperatorMatrixRegimeKey.FromPatch(FilteredSaw(OperatorFilter.LowPass(4)));
        Assert.NotEqual(none, lowPass);
        Assert.Equal(lowPass, OperatorMatrixRegimeKey.FromPatch(FilteredSaw(OperatorFilter.LowPass(9, 3))));
        Assert.NotEqual(lowPass, OperatorMatrixRegimeKey.FromPatch(FilteredSaw(OperatorFilter.LowPass(4, poles: 4))));
        Assert.NotEqual(lowPass, OperatorMatrixRegimeKey.FromPatch(FilteredSaw(OperatorFilter.HighPass(4))));
        Assert.NotEqual(lowPass, OperatorMatrixRegimeKey.FromPatch(FilteredSaw(OperatorFilter.BandPass(4, 2))));
        Assert.NotEqual(lowPass, OperatorMatrixRegimeKey.FromPatch(FilteredSaw(OperatorFilter.LowPass(4) with { EnvelopeOctaves = 1, Envelope = OperatorEnvelope.Adsr(0.01, 0.2, 0.3, 0.1) })));

        // The survivor families keep a filtered structure apart from the same routing without a filter.
        Assert.NotEqual(OperatorMatrixPatchSearchCoordinator.Family(plain), OperatorMatrixPatchSearchCoordinator.Family(FilteredSaw(OperatorFilter.LowPass(4))));
    }

    [Fact]
    public void Canonicalization_MakesRelabelledPatchesEqual()
    {
        OperatorMatrixPatch patch = Stack();
        OperatorMatrixPatch relabelled = OperatorMatrixPatch.Silent(6)
            .WithOperator(4, patch.Operators[0])
            .WithOperator(2, patch.Operators[1])
            .WithOutputLevel(4, 0.8)
            .WithEdge(new ModulationEdge(2, 4, true, 2.0))
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(9)));

        Assert.NotEqual(OperatorMatrixRegimeKey.FromPatch(patch), OperatorMatrixRegimeKey.FromPatch(relabelled));
        Assert.Equal(PatchCanonicalizer.CanonicalKey(patch), PatchCanonicalizer.CanonicalKey(relabelled));
        Assert.Equal(PatchCanonicalizer.Canonicalize(patch).ComputeHash(), PatchCanonicalizer.Canonicalize(relabelled).ComputeHash());
    }

    [Fact]
    public void Canonicalization_KeepsTheSharedNoise()
    {
        OperatorMatrixPatch patch = Stack() with { Noise = new SharedNoise(-0.6, 1500.0) };
        Assert.Equal(patch.Noise, PatchCanonicalizer.Canonicalize(patch).Noise);
    }

    [Fact]
    public void Complexity_GrowsWithStructureAndPenalizesTinyContributions()
    {
        OperatorMatrixPatch single = OperatorMatrixPatch.Silent(6).WithOperator(0, Stack().Operators[0]).WithOutputLevel(0, 0.8);
        OperatorMatrixPatch stack = Stack();
        OperatorMatrixPatch tinyEdge = stack.WithEdge(new ModulationEdge(0, 0, true, 0.01));
        OperatorMatrixPatch detuned = stack.WithOperator(0, stack.Operators[0] with { DetuneHz = 7.5 });

        Assert.Equal(0.0, PatchComplexity.Measure(single), 9);
        Assert.True(PatchComplexity.Measure(stack) > 0.1);
        Assert.True(PatchComplexity.Measure(tinyEdge) > PatchComplexity.Measure(stack) + 0.1);
        Assert.True(PatchComplexity.Measure(detuned) > PatchComplexity.Measure(stack));
        Assert.True(PatchComplexity.Measure(stack) < 0.5, "complexity must stay small next to typical audio losses once weighted");
    }

    private static bool PatchEnvelopesEqual(OperatorEnvelope a, OperatorEnvelope b) =>
        a.SustainPoint == b.SustainPoint && a.ReleasePoint == b.ReleasePoint && a.Points.SequenceEqual(b.Points);

    private static OperatorMatrixPatch FilteredSaw(OperatorFilter filter) =>
        OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1)) with { Waveform = OperatorWaveform.Saw, Filter = filter })
            .WithOutputLevel(0, 1.0);

    [Fact]
    public void FilterParameters_FollowTheFilterType()
    {
        SynthesisCapabilities fmx6 = SynthesisProfiles.SuperColliderFmx6.Capabilities;
        OperatorMatrixPatch lowPass = FilteredSaw(OperatorFilter.LowPass(4, 2));
        var ids = PatchParameterSchema.Build(lowPass, fmx6).ActiveSmooth(lowPass).Select(static p => p.Id).ToHashSet();
        Assert.Contains("op1.operatorFilter.cutoff", ids);
        Assert.Contains("op1.operatorFilter.q", ids);
        Assert.DoesNotContain("op1.operatorFilter.fixedHz", ids);
        Assert.DoesNotContain("op1.operatorFilter.octaves", ids);

        var sweep = OperatorEnvelope.Adsr(0.01, 0.2, 0.3, 0.1);
        OperatorMatrixPatch formant = FilteredSaw(OperatorFilter.FormantAt(700, 1.8, 0.5, 8) with { EnvelopeOctaves = 1, Envelope = sweep });
        PatchParameterSchema schema = PatchParameterSchema.Build(formant, fmx6);
        ids = schema.ActiveSmooth(formant).Select(static p => p.Id).ToHashSet();
        Assert.Contains("op1.operatorFilter.fixedHz", ids);
        Assert.Contains("op1.operatorFilter.formantRatio", ids);
        Assert.Contains("op1.operatorFilter.formantLevel", ids);
        Assert.Contains("op1.operatorFilter.octaves", ids);
        Assert.Contains("op1.operatorFilter.env.p1.time", ids);
        Assert.DoesNotContain("op1.operatorFilter.cutoff", ids);

        // Writing through a parameter changes only the filter and stays in range: up to 50 for a formant filter, 20
        // for the others.
        PatchParameter q = schema.Find("op1.operatorFilter.q")!;
        OperatorMatrixPatch changed = q.Set(formant, 80.0);
        Assert.Equal(50.0, changed.Operators[0].Filter!.Q);
        Assert.Empty(changed.Validate());
        OperatorMatrixPatch sharp = PatchParameterSchema.Build(lowPass, fmx6).Find("op1.operatorFilter.q")!.Set(lowPass, 50.0);
        Assert.Equal(20.0, sharp.Operators[0].Filter!.Q);
        Assert.Empty(sharp.Validate());

        // Profiles without filters have no filter parameters at all.
        Assert.DoesNotContain(PatchParameterSchema.Build(lowPass, SynthesisProfiles.SuperColliderFm7.Capabilities).Parameters, static p => p.Id.Contains("operatorFilter", StringComparison.Ordinal));
    }

    [Fact]
    public void Schema_FormantFilter_HasF3AndOwnQ2()
    {
        SynthesisCapabilities fmx6 = SynthesisProfiles.SuperColliderFmx6.Capabilities;
        string[] f3 = ["op1.operatorFilter.formant3Ratio", "op1.operatorFilter.formant3Level", "op1.operatorFilter.formant3Q"];

        // A formant filter with a third peak: F2's own Q and F3's ratio, level and Q are searched, F4 and F5 are not.
        OperatorMatrixPatch three = FilteredSaw(OperatorFilter.Formants(500, 6, new(2.4, 0.5, 12), new(5.6, 0.2, 18)));
        PatchParameterSchema schema = PatchParameterSchema.Build(three, fmx6);
        var ids = schema.ActiveSmooth(three).Select(static p => p.Id).ToHashSet();
        Assert.Contains("op1.operatorFilter.formantQ2", ids);
        Assert.All(f3, id => Assert.Contains(id, ids));
        Assert.DoesNotContain(schema.Parameters, static p => p.Id.Contains("formant4", StringComparison.Ordinal) || p.Id.Contains("formant5", StringComparison.Ordinal));

        // Ratios and Qs on a log scale, the level linear. The own Qs are searched relative to the filter's Q (1 = the
        // same Q), from 1/8 to 8 times it.
        PatchParameter q2 = schema.Find("op1.operatorFilter.formantQ2")!, ratio3 = schema.Find(f3[0])!, level3 = schema.Find(f3[1])!, q3 = schema.Find(f3[2])!;
        Assert.Equal((0.125, 8.0), (q2.Minimum, q2.Maximum));
        Assert.Equal((0.125, 8.0), (q3.Minimum, q3.Maximum));
        Assert.Equal((OperatorFilter.MinQ, OperatorFilter.MaxFormantQ), (schema.Find("op1.operatorFilter.q")!.Minimum, schema.Find("op1.operatorFilter.q")!.Maximum));
        Assert.Equal((1.0, 16.0), (ratio3.Minimum, ratio3.Maximum));
        Assert.Equal((0.0, 1.0), (level3.Minimum, level3.Maximum));
        Assert.Same(ParameterTransforms.Logarithmic, q2.Transform);
        Assert.Same(ParameterTransforms.Logarithmic, ratio3.Transform);
        Assert.Same(ParameterTransforms.Logarithmic, q3.Transform);
        Assert.Same(ParameterTransforms.Linear, level3.Transform);
        Assert.Equal((2.0, 5.6, 0.2, 3.0), (q2.Get(three), ratio3.Get(three), level3.Get(three), q3.Get(three)));

        // Writing them sets the model's fields, clamped to its ranges: 8 x Q 6 is an own Q of 48, 1/8 x 6 is 0.75.
        OperatorFilter written = q3.Set(level3.Set(ratio3.Set(q2.Set(three, 80.0), 40.0), 0.7), 0.1).Operators[0].Filter!;
        Assert.Equal((48.0, 16.0, 0.7, 0.75), (written.FormantQ2, written.Formant3Ratio, written.Formant3Level, written.Formant3Q));
        Assert.Empty(FilteredSaw(written).Validate());

        // The effective own Q stays within 0.5..50 whatever the filter's Q: 8 x 20 is 50, 1/8 x 2 is 0.5.
        OperatorMatrixPatch sharp = FilteredSaw(OperatorFilter.Formants(500, 20, new FormantPeak(2.4, 0.5, 0)));
        Assert.Equal(50.0, q2.Set(sharp, 8.0).Operators[0].Filter!.FormantQ2);
        OperatorMatrixPatch broad = FilteredSaw(OperatorFilter.Formants(500, 2, new FormantPeak(2.4, 0.5, 0)));
        Assert.Equal(0.5, q2.Set(broad, 0.125).Operators[0].Filter!.FormantQ2);

        // Two formants: F2's own Q is searched, starting at the shared Q; F3 is off, so it adds no dead directions.
        // Reading and writing back the shared Q leaves "same as Q", so a first write changes nothing.
        OperatorMatrixPatch two = FilteredSaw(OperatorFilter.FormantAt(700, 1.8, 0.5, 8));
        PatchParameterSchema twoSchema = PatchParameterSchema.Build(two, fmx6);
        ids = twoSchema.ActiveSmooth(two).Select(static p => p.Id).ToHashSet();
        Assert.Contains("op1.operatorFilter.formantQ2", ids);
        Assert.All(f3, id => Assert.DoesNotContain(id, ids));
        PatchParameter sharedQ2 = twoSchema.Find("op1.operatorFilter.formantQ2")!;
        Assert.Equal(1.0, sharedQ2.Get(two), 10);
        Assert.Equal(two.ComputeHash(), sharedQ2.SetNormalized(two, sharedQ2.GetNormalized(two)).ComputeHash());
        Assert.Equal(12.0, sharedQ2.Set(two, 1.5).Operators[0].Filter!.FormantQ2, 10);

        // A hand-made own Q far from the filter's Q widens the range, so the first write does not move it.
        OperatorMatrixPatch narrow = FilteredSaw(OperatorFilter.Formants(500, 2, new(2.4, 0.5, 40), new(5.6, 0.2, 0)));
        PatchParameter narrowQ2 = PatchParameterSchema.Build(narrow, fmx6).Find("op1.operatorFilter.formantQ2")!;
        Assert.Equal(20.0, narrowQ2.Maximum, 10);
        Assert.Equal(40.0, narrowQ2.SetNormalized(narrow, narrowQ2.GetNormalized(narrow)).Operators[0].Filter!.FormantQ2, 6);
        OperatorMatrixPatch wide = FilteredSaw(OperatorFilter.Formants(500, 8, new(2.4, 0.5, 0), new(5.6, 0.2, 0.5)));
        PatchParameter wideQ3 = PatchParameterSchema.Build(wide, fmx6).Find(f3[2])!;
        Assert.Equal(0.0625, wideQ3.Minimum, 10);
        Assert.Equal(0.5, wideQ3.SetNormalized(wide, wideQ3.GetNormalized(wide)).Operators[0].Filter!.Formant3Q, 6);

        // Other filter types have none of the formant values.
        OperatorMatrixPatch lowPass = FilteredSaw(OperatorFilter.LowPass(4));
        PatchParameterSchema lowSchema = PatchParameterSchema.Build(lowPass, fmx6);
        Assert.DoesNotContain(lowSchema.ActiveSmooth(lowPass), static p => p.Id.Contains("formant", StringComparison.Ordinal));
        Assert.Equal(OperatorFilter.MaxQ, lowSchema.Find("op1.operatorFilter.q")!.Maximum);
    }

    [Fact]
    public void FormantQ2_IsRelativeToQ_SoMovingQAloneKeepsF2Tied()
    {
        // A two-formant filter without an own F2 Q, optimized over its Q and F2's own Q together.
        SynthesisCapabilities fmx6 = SynthesisProfiles.SuperColliderFmx6.Capabilities;
        OperatorMatrixPatch two = FilteredSaw(OperatorFilter.FormantAt(700, 1.8, 0.5, 8));
        PatchParameterSchema schema = PatchParameterSchema.Build(two, fmx6);
        PatchParameter q = schema.Find("op1.operatorFilter.q")!, q2 = schema.Find("op1.operatorFilter.formantQ2")!;
        var set = new ActiveParameterSet([q, q2]);
        double[] start = set.ToVector(two);

        // The starting vector gives the patch back: F2 keeps "same as Q" and the hash is unchanged. (With Q in the set,
        // Q itself comes back within an ulp from the log axis, as every log parameter does, so the exact hash is checked
        // for F2's coordinate alone.)
        OperatorFilter back = set.Apply(two, start).Operators[0].Filter!;
        Assert.Equal(8.0, back.Q, 12);
        Assert.Equal(0.0, back.FormantQ2);
        Assert.False(back.UsesExtendedFormants);
        var own2 = new ActiveParameterSet([q2]);
        Assert.Equal(two.ComputeHash(), own2.Apply(two, own2.ToVector(two)).ComputeHash());

        // Moving Q alone moves F2's Q with it: still "same as Q", so the export keeps today's rows.
        double[] sharper = [q.Normalize(10.0), start[1]];
        OperatorFilter moved = set.Apply(two, sharper).Operators[0].Filter!;
        Assert.Equal(10.0, moved.Q, 10);
        Assert.Equal(0.0, moved.FormantQ2);
        Assert.False(moved.UsesExtendedFormants);

        // Moving F2's coordinate gives it its own Q, relative to the filter's.
        double[] own = [q.Normalize(10.0), q2.Normalize(1.5)];
        OperatorFilter separate = set.Apply(two, own).Operators[0].Filter!;
        Assert.Equal(15.0, separate.FormantQ2, 6);
        Assert.True(separate.UsesExtendedFormants);
    }

    [Fact]
    public void Schema_Fm7AndNome6_HaveNoFormantIds()
    {
        foreach (SynthesisProfile profile in new[] { SynthesisProfiles.SuperColliderFm7, SynthesisProfiles.SuperColliderNome6 })
        {
            PatchParameterSchema schema = PatchParameterSchema.Build(Stack(), profile.Capabilities);
            Assert.DoesNotContain(schema.Parameters, static p => p.Id.Contains("formant", StringComparison.OrdinalIgnoreCase) || p.Id.Contains("operatorFilter", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void FilterParameters_KeepAHandMadeValueOutsideTheSearchRange()
    {
        // F1 at 50 Hz and F2 at 12 x F1 are valid patches but outside the search ranges (80..8000 Hz, 1..8).
        OperatorMatrixPatch patch = FilteredSaw(OperatorFilter.FormantAt(50, 12, 0.5, 8));
        PatchParameterSchema schema = PatchParameterSchema.Build(patch, SynthesisProfiles.SuperColliderFmx6.Capabilities);
        foreach ((string id, double value) in new[] { ("op1.operatorFilter.fixedHz", 50.0), ("op1.operatorFilter.formantRatio", 12.0) })
        {
            PatchParameter parameter = schema.Find(id)!;
            OperatorMatrixPatch same = parameter.SetNormalized(patch, parameter.GetNormalized(patch));
            OperatorFilter filter = same.Operators[0].Filter!;
            Assert.Equal(value, id.EndsWith("fixedHz", StringComparison.Ordinal) ? filter.FixedHz : filter.FormantRatio, 6);
        }

        // F2 at 24 x F1 and F3 at 28 x F1 are within the model's 32 but outside the search's 8 and 16.
        OperatorMatrixPatch wide = FilteredSaw(OperatorFilter.Formants(50, 8, new(24, 0.5, 0), new(28, 0.3, 0)));
        PatchParameterSchema wideSchema = PatchParameterSchema.Build(wide, SynthesisProfiles.SuperColliderFmx6.Capabilities);
        foreach ((string id, double value) in new[] { ("op1.operatorFilter.formantRatio", 24.0), ("op1.operatorFilter.formant3Ratio", 28.0) })
        {
            PatchParameter parameter = wideSchema.Find(id)!;
            Assert.Equal(value, parameter.Maximum);
            Assert.Equal(value, parameter.Get(parameter.SetNormalized(wide, parameter.GetNormalized(wide))), 6);
        }
    }

    [Fact]
    public void Filter_CostsALittleComplexity()
    {
        OperatorMatrixPatch plain = FilteredSaw(OperatorFilter.LowPass(4));
        plain = plain.WithOperator(0, plain.Operators[0] with { Filter = null });
        double none = PatchComplexity.Measure(plain);
        double filter = PatchComplexity.Measure(FilteredSaw(OperatorFilter.LowPass(4)));
        double sweep = PatchComplexity.Measure(FilteredSaw(OperatorFilter.LowPass(4) with { EnvelopeOctaves = 1, Envelope = OperatorEnvelope.Adsr(0.01, 0.2, 0.3, 0.1) }));
        Assert.Equal(none + 0.05, filter, 10);
        Assert.Equal(filter + 0.03, sweep, 10);

        // An envelope with no depth moves nothing and costs nothing, as the compiler and the exporter ignore it.
        double inert = PatchComplexity.Measure(FilteredSaw(OperatorFilter.LowPass(4) with { EnvelopeOctaves = 0, Envelope = OperatorEnvelope.Adsr(0.01, 0.2, 0.3, 0.1) }));
        Assert.Equal(filter, inert, 10);
    }
}
