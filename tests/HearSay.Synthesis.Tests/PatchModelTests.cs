using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.Synthesis.Tests;

public sealed class PatchModelTests
{
    internal static OperatorEnvelope Flat { get; } = new(
        [new EnvelopePoint(0, 1), new EnvelopePoint(0.001, 1), new EnvelopePoint(0.001, 0)],
        SustainPoint: 1,
        ReleasePoint: 2);

    internal static OperatorMatrixPatch TwoOperator(double amount = 2.0) =>
        OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), Flat))
            .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(2), Flat))
            .WithOutputLevel(0, 1.0)
            .WithEdge(new ModulationEdge(1, 0, true, amount));

    [Fact]
    public void Json_RoundTripsEveryPartOfAPatch()
    {
        OperatorMatrixPatch patch = new RandomPatchGenerator(SynthesisProfiles.SuperColliderFm7.Capabilities).Generate(7) with
        {
            Pitch = new PitchModel(
                new OperatorEnvelope([new EnvelopePoint(0, 30), new EnvelopePoint(0.05, 0)], null, 2),
                new LfoSettings(5, 10, 0.1, 0.2)),
        };

        string json = patch.ToJson();
        OperatorMatrixPatch back = OperatorMatrixPatch.FromJson(json);

        Assert.Equal(json, back.ToJson());
        Assert.Equal(patch.ComputeHash(), back.ComputeHash());
        Assert.Equal(patch.ModulationMatrix, back.ModulationMatrix);
        Assert.Contains("\"waveform\": \"Sine\"", json);
        Assert.Contains("\"mode\": \"PhaseModulation\"", json);
    }

    [Fact]
    public void Hash_IsStableAndSensitiveToContent()
    {
        OperatorMatrixPatch a = TwoOperator(2.0);
        OperatorMatrixPatch b = TwoOperator(2.0);
        OperatorMatrixPatch c = TwoOperator(2.0001);

        Assert.Equal(a.ComputeHash(), b.ComputeHash());
        Assert.NotEqual(a.ComputeHash(), c.ComputeHash());
    }

    [Fact]
    public void Matrix_IsIndexedByDestinationThenSource()
    {
        PhaseModulationMatrix matrix = PhaseModulationMatrix.FromEdges(6, (Source: 1, Destination: 0, Amount: 2.5), (Source: 3, Destination: 3, Amount: 0.7));

        Assert.Equal(2.5, matrix[0, 1]);
        Assert.Equal(0.0, matrix[1, 0]);
        Assert.Equal(0.7, matrix[3, 3]);
        Assert.True(matrix.Find(3, 3)!.IsSelfFeedback);
        Assert.Equal(0.0, matrix.With(new ModulationEdge(1, 0, false, 2.5))[0, 1]);
    }

    [Fact]
    public void Matrix_RejectsDuplicateAndOutOfRangeEdges()
    {
        Assert.Throws<ArgumentException>(() => PhaseModulationMatrix.FromEdges(6, (1, 0, 1.0), (1, 0, 2.0)));
        Assert.Throws<ArgumentException>(() => PhaseModulationMatrix.FromEdges(6, (6, 0, 1.0)));
    }

    [Fact]
    public void AnOperatorCanModulateAndFeedTheOutputAtTheSameTime()
    {
        OperatorMatrixPatch patch = TwoOperator().WithOutputLevel(1, 0.5);

        bool[] audible = patch.FindAudibleOperators();

        Assert.True(audible[0]);
        Assert.True(audible[1]);
        Assert.False(audible[2]);
        Assert.Empty(patch.Validate());
    }

    [Fact]
    public void AudibleOperators_FollowModulationChainsButSkipDeadEnds()
    {
        OperatorMatrixPatch patch = TwoOperator()
            .WithOperator(2, OperatorPatch.Sine(OperatorFrequency.AtRatio(3)))
            .WithOperator(3, OperatorPatch.Sine(OperatorFrequency.AtRatio(4)))
            .WithEdge(new ModulationEdge(2, 1, true, 1.0))
            .WithEdge(new ModulationEdge(0, 3, true, 1.0));

        bool[] audible = patch.FindAudibleOperators();

        Assert.True(audible[2]);
        Assert.False(audible[3]);
    }

    [Fact]
    public void Envelope_ValidationCatchesInconsistentSustainAndRelease()
    {
        var broken = new OperatorEnvelope([new EnvelopePoint(0, 0), new EnvelopePoint(0.1, 1), new EnvelopePoint(0.1, 0)], SustainPoint: 1, ReleasePoint: 1);
        var oneShotWithRelease = new OperatorEnvelope([new EnvelopePoint(0, 0), new EnvelopePoint(0.1, 1)], null, 1);

        Assert.NotEmpty(broken.Validate());
        Assert.NotEmpty(oneShotWithRelease.Validate());
        Assert.Empty(OperatorEnvelope.Adsr(0.01, 0.1, 0.5, 0.2).Validate());
        Assert.Empty(OperatorEnvelope.TwoStageDecay(0.001, 0.1, 0.3, 1.0, 0.05, 0.2).Validate());
    }

    [Fact]
    public void Fm7Profile_AllowsExactlySixSinePhaseModulationOperators()
    {
        SynthesisCapabilities fm7 = SynthesisProfiles.SuperColliderFm7.Capabilities;

        Assert.Equal(6, fm7.OperatorCount);
        Assert.Equal([OperatorMode.PhaseModulation], fm7.SupportedModes);
        Assert.Equal([OperatorWaveform.Sine], fm7.SupportedWaveforms);
        Assert.True(fm7.SupportsFullModulationMatrix && fm7.SupportsSelfFeedback && fm7.SupportsCircularFeedback);
        Assert.True(fm7.SupportsRatioFrequency && fm7.SupportsFixedFrequency && fm7.SupportsIndependentOperatorOutputs);
        Assert.True(fm7.IsSubsetOf(SynthesisProfiles.HearSayExtended.Capabilities));
        Assert.False(SynthesisProfiles.ExperimentalSineTriangle.Capabilities.IsSubsetOf(fm7));
    }

    [Fact]
    public void Validator_RejectsATriangleOperatorForFm7ButNotForTheExperimentalProfile()
    {
        OperatorMatrixPatch patch = TwoOperator();
        patch = patch.WithOperator(1, patch.Operators[1] with { Waveform = OperatorWaveform.Triangle });

        PatchCompatibilityResult fm7 = PatchCompatibilityValidator.Instance.Validate(patch, SynthesisProfiles.SuperColliderFm7.Capabilities);
        PatchCompatibilityResult experimental = PatchCompatibilityValidator.Instance.Validate(patch, SynthesisProfiles.ExperimentalSineTriangle.Capabilities);

        PatchCompatibilityIssue issue = Assert.Single(fm7.Issues);
        Assert.Equal(PatchCompatibilityCodes.UnsupportedWaveform, issue.Code);
        Assert.Equal(1, issue.OperatorIndex);
        Assert.True(experimental.IsCompatible);
    }

    [Fact]
    public void Validator_IgnoresWhatCannotBeHeard()
    {
        OperatorMatrixPatch patch = TwoOperator();
        patch = patch.WithOperator(4, OperatorPatch.Disabled with { Waveform = OperatorWaveform.Saw });

        Assert.True(PatchCompatibilityValidator.Instance.Validate(patch, SynthesisProfiles.SuperColliderFm7.Capabilities).IsCompatible);
    }

    [Fact]
    public void Validator_ReportsFeedbackOperatorCountAndRangeProblems()
    {
        SynthesisCapabilities strict = SynthesisProfiles.SuperColliderFm7.Capabilities with
        {
            SupportsSelfFeedback = false,
            SupportsCircularFeedback = false,
            SupportsDynamicModulationMatrix = false,
        };
        OperatorMatrixPatch patch = TwoOperator()
            .WithEdge(new ModulationEdge(0, 0, true, 0.5))
            .WithEdge(new ModulationEdge(0, 1, true, 0.5, OperatorEnvelope.Adsr(0.01, 0.1, 0.5, 0.1)))
            .WithEdge(new ModulationEdge(1, 0, true, 30.0));

        IReadOnlyList<string> codes = [.. PatchCompatibilityValidator.Instance.Validate(patch, strict).Issues.Select(static i => i.Code)];

        Assert.Contains(PatchCompatibilityCodes.SelfFeedback, codes);
        Assert.Contains(PatchCompatibilityCodes.CircularFeedback, codes);
        Assert.Contains(PatchCompatibilityCodes.DynamicModulation, codes);
        Assert.Contains(PatchCompatibilityCodes.ModulationRange, codes);
        Assert.Contains(
            PatchCompatibilityCodes.OperatorCount,
            PatchCompatibilityValidator.Instance.Validate(OperatorMatrixPatch.Silent(4), strict).Issues.Select(static i => i.Code));
    }

    [Fact]
    public void RandomPatches_AreReproducibleAndAlwaysCompatible()
    {
        var generator = new RandomPatchGenerator(SynthesisProfiles.SuperColliderFm7.Capabilities, new RandomPatchOptions { EdgeDensity = 0.5 });
        for (int seed = 0; seed < 200; seed++)
        {
            OperatorMatrixPatch patch = generator.Generate(seed);
            Assert.True(PatchCompatibilityValidator.Instance.Validate(patch, SynthesisProfiles.SuperColliderFm7.Capabilities).IsCompatible);
            Assert.Equal(patch.ComputeHash(), generator.Generate(seed).ComputeHash());
        }
    }
}
