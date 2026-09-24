using System.Globalization;
using HearSay.Synthesis.Fmx6Compatible.Rendering;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.SuperCollider.FMX6;

/// <summary>
/// Exports to the <c>FMX6</c> UGen (https://github.com/shimpe/fmx6): the FM7 matrix interface plus a waveform, a
/// shape and a harmonic limit per operator, <c>FMX6.ar(ctlMatrix, modMatrix, waves:, shapes:, harmonicLimits:)</c>.
/// Stateless.
/// </summary>
public sealed class SuperColliderFmx6Exporter : SuperColliderOperatorMatrixExporter
{
    /// <inheritdoc />
    public override string BackendName => "SuperCollider FMX6";

    /// <inheritdoc />
    public override SynthesisCapabilities Capabilities => SynthesisProfiles.SuperColliderFmx6.Capabilities;

    /// <inheritdoc />
    public override int ControlBlockSize => SynthesisProfiles.SuperColliderFmx6.ControlBlockSize;

    /// <inheritdoc />
    protected override string Requirement => "the FMX6 plugin (https://github.com/shimpe/fmx6)";

    /// <inheritdoc />
    protected override string UGenCall(OperatorMatrixPatch patch, bool[] audible)
    {
        string waves = PerOperator(patch, audible, static o => Fmx6CompatibleOperatorMatrixRenderer.WaveIndex(o.Waveform).ToString(CultureInfo.InvariantCulture), "0");
        string shapes = PerOperator(patch, audible, static o => Number(o.Shape), "0.5");
        string harmonicLimits = PerOperator(patch, audible, static o => o.HarmonicLimit.ToString(CultureInfo.InvariantCulture), "32");
        return $$"""
            operators = FMX6.ar(ctlMatrix, modMatrix,
            		waves: [{{waves}}], // 0 sine, 1 triangle, 2 saw, 3 square, 4 pulse, 5 sample-and-hold noise, 6 white noise
            		shapes: [{{shapes}}], // pulse duty cycle
            		harmonicLimits: [{{harmonicLimits}}]);
            """;
    }

    private static string PerOperator(OperatorMatrixPatch patch, bool[] audible, Func<OperatorPatch, string> value, string silent) =>
        List(patch.OperatorCount, i => audible[i] ? value(patch.Operators[i]) : silent);
}
