using HearSay.Editing;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay.IntegrationTests;

/// <summary>Muting operators to listen without them: a copy of the patch to play, the patch itself unchanged; fast.</summary>
public sealed class OperatorMuteTests
{
    /// <summary>OP2 modulates OP1, OP3 is heard on its own; OP2 also feeds back into itself.</summary>
    private static OperatorMatrixPatch Patch()
        => OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.01, 0.2, 0.6, 0.1)))
            .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(2), OperatorEnvelope.Adsr(0.01, 0.3, 0.4, 0.1)))
            .WithOperator(2, OperatorPatch.Sine(OperatorFrequency.AtRatio(3), OperatorEnvelope.Adsr(0.01, 0.2, 0.5, 0.1)))
            .WithOutputLevel(0, 0.8)
            .WithOutputLevel(2, 0.4)
            .WithEdge(new ModulationEdge(1, 0, true, 2.0))
            .WithEdge(new ModulationEdge(1, 1, true, 0.5));

    private static float[] Render(OperatorMatrixPatch patch) =>
        SynthesisBackends.RendererFor(SynthesisProfiles.SuperColliderFm7).Render(patch, RenderRequest.Create(220, 0.5, 0.2)).Samples;

    [Fact]
    public void Nothing_Muted_IsThePatchItself()
    {
        OperatorMatrixPatch patch = Patch();
        Assert.Same(patch, OperatorMute.Apply(patch, new HashSet<int>()));
    }

    [Fact]
    public void AMutedOperator_SoundsAsIfItWereNotThere_AndThePatchStaysAsItWas()
    {
        OperatorMatrixPatch patch = Patch();
        string before = patch.ComputeHash();

        // The modulator: its modulation of OP1 and its feedback are gone, as when it is removed.
        OperatorMatrixPatch withoutModulator = PatchEdits.RemoveOperator(1)(patch, SynthesisProfiles.SuperColliderFm7.Capabilities).Patch;
        Assert.Equal(Render(withoutModulator), Render(OperatorMute.Apply(patch, new HashSet<int> { 1 })));

        // A carrier: its output is gone.
        OperatorMatrixPatch withoutCarrier = PatchEdits.RemoveOperator(2)(patch, SynthesisProfiles.SuperColliderFm7.Capabilities).Patch;
        Assert.Equal(Render(withoutCarrier), Render(OperatorMute.Apply(patch, new HashSet<int> { 2 })));

        // Everything muted: silence.
        Assert.All(Render(OperatorMute.Apply(patch, new HashSet<int> { 0, 1, 2 })), static s => Assert.Equal(0f, s));

        Assert.Equal(before, patch.ComputeHash());
    }

    [Fact]
    public void Operators_OutsideThePatch_OrNotInUse_AreIgnored()
    {
        OperatorMatrixPatch patch = Patch();
        Assert.Equal(patch.ComputeHash(), OperatorMute.Apply(patch, new HashSet<int> { 4, 9, -1 }).ComputeHash());
    }
}
