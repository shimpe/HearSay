using HearSay.Synthesis.Patches;

namespace HearSay.Editing;

/// <summary>
/// The sound the Studio starts from when a new one is made from scratch: OP1 a sine at ratio 1 with a plain ADSR
/// envelope, heard at 0.8, every other operator off, no routing and a constant pitch. It is valid and plays on every
/// profile; like any other sound, its edits are kept by its patch and profile.
/// </summary>
public static class ScratchSound
{
    /// <summary>Its name, as a favourite of it is first called.</summary>
    public const string Title = "New sound";

    /// <summary>Where it came from, as a favourite of it says; an edit of it says the same.</summary>
    public const string Origin = "made from scratch";

    /// <summary>The header comments of its code.</summary>
    public static IReadOnlyList<string> HeaderComments { get; } = ["Made from scratch in HearSay Studio."];

    /// <summary>The patch: six operators, OP1 alone in use.</summary>
    public static OperatorMatrixPatch Patch { get; } = OperatorMatrixPatch.Silent(6)
        .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1.0), OperatorEnvelope.Adsr(0.01, 0.3, 0.7, 0.3)))
        .WithOutputLevel(0, 0.8);
}
