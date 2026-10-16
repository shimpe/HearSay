using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.Synthesis.Tests;

public sealed class OperatorFilterModelTests
{
    private static OperatorMatrixPatch Carrier(OperatorFilter? filter, bool enabled = true) =>
        OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1)) with { Enabled = enabled, Filter = filter })
            .WithOutputLevel(0, 1.0);

    [Fact]
    public void PatchWithoutFilter_HasNoFilterInItsJson_AndKeepsItsHash()
    {
        OperatorMatrixPatch plain = ReferencePatches.Find("electric-piano")!.Patch;
        string json = plain.ToJson();
        Assert.DoesNotContain("\"filter\"", json, StringComparison.Ordinal);
        Assert.Equal(plain.ComputeHash(), OperatorMatrixPatch.FromJson(json).ComputeHash());
    }

    [Fact]
    public void Filter_RoundTripsThroughJson()
    {
        OperatorFilter filter = OperatorFilter.FormantAt(700, 1.8, 0.6, 8) with
        {
            EnvelopeOctaves = 1.5,
            Envelope = OperatorEnvelope.Adsr(0.01, 0.2, 0.3, 0.1),
        };
        OperatorMatrixPatch patch = Carrier(filter);
        OperatorMatrixPatch back = OperatorMatrixPatch.FromJson(patch.ToJson());

        Assert.Contains("\"filter\"", patch.ToJson(), StringComparison.Ordinal);
        Assert.Equal(patch.ComputeHash(), back.ComputeHash());
        Assert.Equal(OperatorFilterType.Formant, back.Operators[0].Filter!.Type);
        Assert.Equal(1.8, back.Operators[0].Filter!.FormantRatio);
    }

    public static TheoryData<OperatorFilter> InvalidFilters => new()
    {
        OperatorFilter.LowPass(4) with { Poles = 3 },
        OperatorFilter.BandPass(2, 2) with { Poles = 4 },
        OperatorFilter.LowPass(0.1),
        OperatorFilter.LowPass(80),
        OperatorFilter.LowPass(4, q: 0.2),
        OperatorFilter.LowPass(4, q: 25),
        OperatorFilter.FormantAt(700, 0.8, 0.5, 8),
        OperatorFilter.FormantAt(700, 2, 1.5, 8),
        OperatorFilter.PeakAt(1000, 30, 2),
        OperatorFilter.PeakAt(5, 6, 2),
        OperatorFilter.LowPass(4) with { EnvelopeOctaves = 5 },
        OperatorFilter.LowPass(double.NaN),
    };

    [Theory]
    [MemberData(nameof(InvalidFilters))]
    public void OutOfRangeFilter_IsNotWellFormed(OperatorFilter filter) =>
        Assert.Contains(Carrier(filter).Validate(), p => p.StartsWith("OP1 filter", StringComparison.Ordinal));

    [Fact]
    public void ValidFilters_AreWellFormed()
    {
        Assert.Empty(Carrier(OperatorFilter.LowPass(4, 2, 4)).Validate());
        Assert.Empty(Carrier(OperatorFilter.HighPass(0.5)).Validate());
        Assert.Empty(Carrier(OperatorFilter.Notch(3, 4)).Validate());
        Assert.Empty(Carrier(OperatorFilter.PeakAt(1200, -12, 3)).Validate());
        Assert.Empty(Carrier(OperatorFilter.FormantAt(300, 7.5, 0.2, 12) with { EnvelopeOctaves = -4, Envelope = OperatorEnvelope.Adsr(0.01, 0.1, 0.5, 0.1) }).Validate());
    }

    [Fact]
    public void OnlyFmx6_PlaysFilters_ButADisabledOperatorsFilterDoesNotMatter()
    {
        OperatorMatrixPatch filtered = Carrier(OperatorFilter.LowPass(4));
        Assert.True(PatchCompatibilityValidator.Instance.Validate(filtered, SynthesisProfiles.SuperColliderFmx6.Capabilities).IsCompatible);
        foreach (SynthesisProfile profile in new[] { SynthesisProfiles.SuperColliderFm7, SynthesisProfiles.SuperColliderNome6 })
        {
            PatchCompatibilityResult result = PatchCompatibilityValidator.Instance.Validate(filtered, profile.Capabilities);
            Assert.Contains(result.Issues, i => i.Code == PatchCompatibilityCodes.OperatorFilter);

            // Review focus 4: what cannot be heard does not have to be representable.
            OperatorMatrixPatch disabled = Carrier(OperatorFilter.LowPass(4), enabled: false)
                .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(1)))
                .WithOutputLevel(1, 1.0);
            Assert.True(PatchCompatibilityValidator.Instance.Validate(disabled, profile.Capabilities).IsCompatible);
        }

        SynthesisCapabilities fmx6 = SynthesisProfiles.SuperColliderFmx6.Capabilities;
        Assert.True(fmx6.SupportsOperatorFilters);
        Assert.False(fmx6.IsSubsetOf(fmx6 with { SupportsOperatorFilters = false }));
        Assert.True((fmx6 with { SupportsOperatorFilters = false }).IsSubsetOf(fmx6));
        Assert.Equal(2, SynthesisProfiles.SuperColliderFmx6.StructureLibraryVersion);
        Assert.Equal(1, SynthesisProfiles.SuperColliderFm7.StructureLibraryVersion);
        Assert.Equal(1, SynthesisProfiles.SuperColliderNome6.StructureLibraryVersion);
    }
}
