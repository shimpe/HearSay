using HearSay.Synthesis.Nome6Compatible.Rendering;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.SuperCollider.Nome6;

/// <summary>
/// Exports to the <c>Nome6</c> UGen: the FM7 matrix interface with operator rows <c>[freq, phase, amp, m, family,
/// bandwidth, noiseAmp, noisePhase, noiseM]</c>, <c>Nome6.ar(ctlMatrix, modMatrix, oversample, noiseColor,
/// noiseCutoff)</c>. Sine operators get m = 0; a noise operator moves its amplitude into the noiseAmp field, so that
/// its envelope drives the shared noise instead of the oscillator; every bandwidth is 0 (the anti-alias ceiling of the
/// UGen) and the oversampling is that of the renderer. Stateless.
/// </summary>
public sealed class SuperColliderNome6Exporter : SuperColliderOperatorMatrixExporter
{
    /// <inheritdoc />
    public override string BackendName => "SuperCollider Nome6";

    /// <inheritdoc />
    public override SynthesisCapabilities Capabilities => SynthesisProfiles.SuperColliderNome6.Capabilities;

    /// <inheritdoc />
    public override int ControlBlockSize => SynthesisProfiles.SuperColliderNome6.ControlBlockSize;

    /// <inheritdoc />
    protected override string Requirement => "the Nome6 plugin (with its shared noise source)";

    /// <inheritdoc />
    protected override string UGenCall(OperatorMatrixPatch patch, bool[] audible)
    {
        string m = PerOperator(patch, audible, static o => Number(Nome6CompatibleOperatorMatrixRenderer.ParameterOf(o)));
        string family = PerOperator(patch, audible, static o => Number(Nome6CompatibleOperatorMatrixRenderer.FamilyOf(o)));
        string noise = PerOperator(patch, audible, static o => o.Waveform == OperatorWaveform.Noise ? "1" : "0");
        string phaseNoise = PerOperator(patch, audible, static o => Number(Nome6CompatibleOperatorMatrixRenderer.NoisePhaseOf(o)));
        string mNoise = PerOperator(patch, audible, static o => Number(Nome6CompatibleOperatorMatrixRenderer.NoiseMOf(o)));
        return $$"""
            // Rows become [freq, phase, amp, m, family, bandwidth, noiseAmp, noisePhase, noiseM]. m 0 is a sine, towards 1
            	// more odd harmonics; family 0 sn, 1 sd; bandwidth 0 = anti-alias ceiling. A noise operator (noise 1) plays
            	// the shared noise with its amplitude; phaseNoise and mNoise roughen the others with the same noise.
            	operators = Nome6.ar(
            		ctlMatrix.collect { |row, i|
            			var m = [{{m}}][i];
            			var family = [{{family}}][i];
            			var noise = [{{noise}}][i];
            			var phaseNoise = [{{phaseNoise}}][i];
            			var mNoise = [{{mNoise}}][i];
            			if(noise > 0) { [row[0], row[1], 0, 0, 0, 0, row[2], 0, 0] } { row ++ [m, family, 0, 0, phaseNoise, mNoise] }
            		},
            		modMatrix, {{Nome6CompatibleOperatorMatrixRenderer.Oversample}},
            		noiseColor: {{Number(patch.Noise.Color)}}, // -1 low-passed, 0 white, 1 high-passed
            		noiseCutoff: {{Number(patch.Noise.CutoffHz)}});
            """;
    }

    private static string PerOperator(OperatorMatrixPatch patch, bool[] audible, Func<OperatorPatch, string> value) =>
        List(patch.OperatorCount, i => audible[i] ? value(patch.Operators[i]) : "0");
}
