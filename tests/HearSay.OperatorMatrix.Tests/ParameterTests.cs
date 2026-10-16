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

        // Writing through a parameter changes only the filter and stays in range.
        PatchParameter q = schema.Find("op1.operatorFilter.q")!;
        OperatorMatrixPatch changed = q.Set(formant, 50.0);
        Assert.Equal(20.0, changed.Operators[0].Filter!.Q);
        Assert.Empty(changed.Validate());

        // Profiles without filters have no filter parameters at all.
        Assert.DoesNotContain(PatchParameterSchema.Build(lowPass, SynthesisProfiles.SuperColliderFm7.Capabilities).Parameters, static p => p.Id.Contains("operatorFilter", StringComparison.Ordinal));
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
    }
}
