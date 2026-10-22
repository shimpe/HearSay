using HearSay.Synthesis.Compilation;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay.Synthesis.Nome6Compatible.Rendering;

/// <summary>
/// Offline reference renderer that reproduces the discrete-time behaviour of the SuperCollider <c>Nome6</c> UGen, so
/// that a patch optimized here sounds the same when its exported SynthDef is played.
/// </summary>
/// <remarks>
/// <para>What is reproduced, all taken from the Nome6 source (<c>Nome6DSP.hpp</c>):</para>
/// <list type="bullet">
/// <item>
/// Jacobi elliptic oscillators from their odd-harmonic Fourier series in the nome q, at most 24 terms, cut where the
/// harmonics would pass 47.5 % of the internal sample rate and renormalized to a peak of exactly one at a quarter
/// cycle; sn and the peak-normalized sd crossfaded by the morph. m = 0 is a sine.
/// </item>
/// <item>The sine of the series from the UGen's eleventh-order polynomial with quadrant reduction, and the odd harmonics from a recurrence.</item>
/// <item>
/// The whole matrix at twice the host rate: every edge has a delay of one internal sample, and the six outputs are
/// decimated by the UGen's 31-tap half-band filter.
/// </item>
/// <item>
/// Double-precision phase in cycles; frequency, amplitude and matrix ramped linearly across each control block, from
/// the values of the previous block (the construction values for the first) to the values the block reads, as in FM7;
/// the elliptic parameter, the morph and the noise settings are held.
/// </item>
/// <item>No warm-up sample: the first internal sample is at the initial phase.</item>
/// <item>
/// One noise source for all operators at the internal rate, drawn only while an operator uses it: xorshift white noise
/// through a one-pole crossover, coloured by the patch. A noise operator is a row whose amplitude drives that noise
/// instead of its oscillator; phase noise adds to the phase modulation; m noise interpolates the normalized
/// coefficients between m − depth and m + depth. The UGen seeds its generator from the address of the unit, so noise
/// matches the UGen in level and colour, never sample by sample.
/// </item>
/// </list>
/// <para>
/// The kernel runs as a voice, one control block at a time, so a held note from <see cref="StartVoice"/> plays the same
/// samples as the offline render of a note of the same duration; every voice starts its noise from the same seed.
/// </para>
/// <para>Instances are immutable and thread-safe.</para>
/// </remarks>
public sealed class Nome6CompatibleOperatorMatrixRenderer : IOperatorMatrixRenderer, IVoiceRenderer
{
    /// <summary>Oversampling factor of the exported UGen and of this renderer.</summary>
    public const int Oversample = 2;

    private readonly SynthesisProfile _profile;

    /// <summary>Creates a renderer for the <see cref="SynthesisProfiles.SuperColliderNome6"/> profile.</summary>
    public Nome6CompatibleOperatorMatrixRenderer()
        : this(SynthesisProfiles.SuperColliderNome6)
    {
    }

    /// <summary>Creates a renderer for a profile with Nome6 semantics.</summary>
    /// <param name="profile">Profile; its capabilities must not exceed those of Nome6.</param>
    /// <exception cref="ArgumentException">The profile asks for more than Nome6 can play.</exception>
    public Nome6CompatibleOperatorMatrixRenderer(SynthesisProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.Capabilities.IsSubsetOf(SynthesisProfiles.SuperColliderNome6.Capabilities))
        {
            throw new ArgumentException($"Profile '{profile.Name}' needs capabilities that the Nome6-compatible renderer does not have.", nameof(profile));
        }

        _profile = profile;
    }

    /// <inheritdoc />
    public SynthesisCapabilities Capabilities => _profile.Capabilities;

    /// <inheritdoc />
    public RenderProfile RenderProfile => RenderProfile.BackendExact;

    /// <inheritdoc />
    public RenderedAudio Render(OperatorMatrixPatch patch, RenderRequest request)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(request);
        EnsureCompatible(patch);

        CompiledOperatorMatrixPatch compiled = PatchCompiler.Compile(patch, request, _profile.ControlBlockSize);
        return new RenderedAudio(Render(compiled), request.SampleRate);
    }

    /// <summary>The elliptic parameter m an operator gets in the UGen: the shape of an elliptic operator, zero for a sine.</summary>
    /// <param name="op">Operator.</param>
    public static double ParameterOf(OperatorPatch op)
    {
        ArgumentNullException.ThrowIfNull(op);
        return op.Waveform == OperatorWaveform.Elliptic ? op.Shape : 0.0;
    }

    /// <summary>The sn-to-sd morph an operator gets in the UGen: the morph of an elliptic operator, zero for a sine.</summary>
    /// <param name="op">Operator.</param>
    public static double FamilyOf(OperatorPatch op)
    {
        ArgumentNullException.ThrowIfNull(op);
        return op.Waveform == OperatorWaveform.Elliptic ? op.Morph : 0.0;
    }

    /// <summary>The phase noise an operator gets in the UGen: its phase roughness, zero for a noise operator.</summary>
    /// <param name="op">Operator.</param>
    public static double NoisePhaseOf(OperatorPatch op)
    {
        ArgumentNullException.ThrowIfNull(op);
        return op.Waveform == OperatorWaveform.Noise ? 0.0 : op.NoisePhase;
    }

    /// <summary>The m noise an operator gets in the UGen: the roughness of an elliptic operator, zero for the others.</summary>
    /// <param name="op">Operator.</param>
    public static double NoiseMOf(OperatorPatch op)
    {
        ArgumentNullException.ThrowIfNull(op);
        return op.Waveform == OperatorWaveform.Elliptic ? op.NoiseM : 0.0;
    }

    /// <summary>Renders an already compiled patch.</summary>
    /// <param name="compiled">Compiled patch.</param>
    public static float[] Render(CompiledOperatorMatrixPatch compiled)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        var output = new float[compiled.SampleCount];
        if (compiled.OperatorCount > 0 && output.Length > 0)
        {
            new Nome6Voice(compiled.Controls()).Render(output);
        }

        return output;
    }

    /// <inheritdoc />
    public IOperatorMatrixVoice StartVoice(OperatorMatrixPatch patch, VoiceRequest request)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(request);
        EnsureCompatible(patch);
        int blockSize = _profile.ControlBlockSize;
        return new Nome6Voice(request.SampleRate, firstGateOpen => new ControlStream(patch, request, blockSize, firstGateOpen));
    }

    private void EnsureCompatible(OperatorMatrixPatch patch)
    {
        PatchCompatibilityResult compatibility = PatchCompatibilityValidator.Instance.Validate(patch, _profile.Capabilities);
        if (!compatibility.IsCompatible)
        {
            throw new PatchNotSupportedException(_profile.Name, compatibility);
        }
    }
}
