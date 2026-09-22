using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.SuperCollider.FM7;

/// <summary>
/// Exports to the <c>FM7</c> UGen of the sc3-plugins: six sine operators over the full matrix,
/// <c>FM7.ar(ctlMatrix, modMatrix)</c>. Stateless.
/// </summary>
public sealed class SuperColliderFm7Exporter : SuperColliderOperatorMatrixExporter
{
    /// <inheritdoc />
    public override string BackendName => "SuperCollider FM7";

    /// <inheritdoc />
    public override SynthesisCapabilities Capabilities => SynthesisProfiles.SuperColliderFm7.Capabilities;

    /// <inheritdoc />
    public override int ControlBlockSize => SynthesisProfiles.SuperColliderFm7.ControlBlockSize;

    /// <inheritdoc />
    protected override string Requirement => "sc3-plugins (FM7 UGen)";

    /// <inheritdoc />
    protected override string UGenCall(OperatorMatrixPatch patch, bool[] audible) => "operators = FM7.ar(ctlMatrix, modMatrix);";
}
