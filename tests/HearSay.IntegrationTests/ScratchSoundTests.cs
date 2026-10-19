using HearSay.Editing;
using HearSay.OperatorMatrix.Priors;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay.IntegrationTests;

/// <summary>The sound the Studio starts from scratch with: one sine, valid and playable on every profile.</summary>
public sealed class ScratchSoundTests
{
    [Fact]
    public void ScratchPatch_IsValidAndCompatible_OnEveryProfile()
    {
        OperatorMatrixPatch patch = ScratchSound.Patch;
        OperatorMatrixPatch expected = OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.01, 0.3, 0.7, 0.3)))
            .WithOutputLevel(0, 0.8);
        Assert.Equal(expected.ComputeHash(), patch.ComputeHash());
        Assert.Same(patch, ScratchSound.Patch);
        Assert.Empty(patch.Validate());

        foreach (SynthesisProfile profile in new[] { SynthesisProfiles.SuperColliderFm7, SynthesisProfiles.SuperColliderFmx6, SynthesisProfiles.SuperColliderNome6 })
        {
            PatchCompatibilityResult compatibility = PatchCompatibilityValidator.Instance.Validate(patch, profile.Capabilities);
            Assert.True(compatibility.IsCompatible, $"{profile.Name}: {compatibility}");
            Assert.Contains("SynthDef", SynthesisBackends.ExporterFor(profile)!.Export(patch), StringComparison.Ordinal);
            RenderedAudio audio = SynthesisBackends.RendererFor(profile).Render(patch, StructureLibrary.ReferenceRequest);
            Assert.True(audio.Peak > 0.1f, $"{profile.Name}: peak {audio.Peak}");
        }
    }
}
