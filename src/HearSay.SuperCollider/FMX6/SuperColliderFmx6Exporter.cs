using System.Globalization;
using HearSay.Synthesis.Filters;
using HearSay.Synthesis.Fmx6Compatible.Rendering;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.SuperCollider.FMX6;

/// <summary>
/// Exports to the <c>FMX6</c> UGen (https://github.com/shimpe/fmx6): the FM7 matrix interface plus a waveform, a
/// shape, a harmonic limit and a filter per operator,
/// <c>FMX6.ar(ctlMatrix, modMatrix, waves:, shapes:, harmonicLimits:, filters:)</c>. <c>filters:</c> is written only
/// when an audible operator has a filter, so every other patch runs on FMX6 builds without operator filters.
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
        bool filtered = Enumerable.Range(0, patch.OperatorCount).Any(i => audible[i] && patch.Operators[i].Filter is not null);
        string filters = filtered
            ? $",\n\t\tfilters: [{PerOperator(patch, audible, FilterRow, Off)}] /* [type, freq, octaves, q, formantRatio, formantLevel, gainDb] */"
            : string.Empty;
        return $$"""
            operators = FMX6.ar(ctlMatrix, modMatrix,
            		waves: [{{waves}}], // 0 sine, 1 triangle, 2 saw, 3 square, 4 pulse, 5 sample-and-hold noise, 6 white noise, 7 filtered noise
            		shapes: [{{shapes}}], // pulse duty cycle; filtered noise: 0 low-pass, 0.5 band-pass, 1 high-pass
            		harmonicLimits: [{{harmonicLimits}}]{{filters}});
            """;
    }

    private const string Off = "[0, 0, 0, 1, 1, 0, 0]";

    /// <summary>One operator's filter row; filter types 1 LP2, 2 LP4, 3 HP2, 4 HP4, 5 BP, 6 notch, 7 peak, 8 formant.</summary>
    private static string FilterRow(OperatorPatch op)
    {
        if (op.Filter is not { } filter)
        {
            return Off;
        }

        string baseHz = filter.IsKeyTracked ? $"(freq * {Number(filter.CutoffRatio)})" : Number(filter.FixedHz);
        string octaves = filter.Envelope is null || filter.EnvelopeOctaves == 0.0
            ? "0"
            : $"({Number(filter.EnvelopeOctaves)} * {EnvGen(filter.Envelope, TimeScale(op))})";
        string type = OperatorFilterKernel.Code(filter).ToString(CultureInfo.InvariantCulture);
        return $"[{type}, {baseHz}, {octaves}, {Number(filter.Q)}, {Number(filter.FormantRatio)}, {Number(filter.FormantLevel)}, {Number(filter.PeakGainDb)}]";
    }

    private static string PerOperator(OperatorMatrixPatch patch, bool[] audible, Func<OperatorPatch, string> value, string silent) =>
        List(patch.OperatorCount, i => audible[i] ? value(patch.Operators[i]) : silent);
}
