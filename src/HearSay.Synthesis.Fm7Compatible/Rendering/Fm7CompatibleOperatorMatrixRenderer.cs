using HearSay.Synthesis.Compilation;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay.Synthesis.Fm7Compatible.Rendering;

/// <summary>
/// Offline reference renderer that reproduces the discrete-time behaviour of the SuperCollider <c>FM7</c> UGen, so
/// that a patch optimized here sounds the same when its exported SynthDef is played.
/// </summary>
/// <remarks>
/// <para>What is reproduced, all taken from the FM7 source:</para>
/// <list type="bullet">
/// <item>
/// Previous-sample modulation. In every sample all phase deviations are computed first, from the operator outputs of
/// the previous sample, and only then are the six outputs updated. Every edge therefore has a delay of exactly one
/// sample, which is what makes self-feedback and feedback loops through several operators well defined.
/// </item>
/// <item>The output of an operator includes its amplitude, so an amplitude envelope also shapes the modulation it sends.</item>
/// <item>32-bit fixed-point phase with truncated increments and a linearly interpolated 8192-point sine table.</item>
/// <item>Frequency, amplitude and matrix inputs are control-rate signals, ramped linearly across each control block.</item>
/// <item>The unit computes one sample when it is constructed; the audible output starts one sample into the cycle.</item>
/// <item>No oversampling and no band-limiting: aliasing is part of the sound of the backend.</item>
/// </list>
/// <para>
/// The kernel runs as a voice, one control block at a time, so a held note from <see cref="StartVoice"/> plays the same
/// samples as the offline render of a note of the same duration.
/// </para>
/// <para>Instances are immutable and thread-safe.</para>
/// </remarks>
public sealed class Fm7CompatibleOperatorMatrixRenderer : IOperatorMatrixRenderer, IVoiceRenderer
{
    private readonly SynthesisProfile _profile;

    /// <summary>Creates a renderer for the <see cref="SynthesisProfiles.SuperColliderFm7"/> profile.</summary>
    public Fm7CompatibleOperatorMatrixRenderer()
        : this(SynthesisProfiles.SuperColliderFm7)
    {
    }

    /// <summary>Creates a renderer for a profile with FM7 semantics and, for example, another control block size.</summary>
    /// <param name="profile">Profile; its capabilities must not exceed those of FM7.</param>
    /// <exception cref="ArgumentException">The profile asks for more than FM7 can play.</exception>
    public Fm7CompatibleOperatorMatrixRenderer(SynthesisProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.Capabilities.IsSubsetOf(SynthesisProfiles.SuperColliderFm7.Capabilities))
        {
            throw new ArgumentException($"Profile '{profile.Name}' needs capabilities that the FM7-compatible renderer does not have.", nameof(profile));
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

    /// <inheritdoc />
    public IOperatorMatrixVoice StartVoice(OperatorMatrixPatch patch, VoiceRequest request)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(request);
        EnsureCompatible(patch);
        int blockSize = _profile.ControlBlockSize;
        return new Fm7Voice(request.SampleRate, firstGateOpen => new ControlStream(patch, request, blockSize, firstGateOpen));
    }

    /// <summary>Renders an already compiled patch.</summary>
    /// <param name="compiled">Compiled patch; all operators must be sine phase-modulation operators.</param>
    public static float[] Render(CompiledOperatorMatrixPatch compiled)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        var output = new float[compiled.SampleCount];
        if (compiled.OperatorCount > 0 && output.Length > 0)
        {
            new Fm7Voice(compiled.Controls()).Render(output);
        }

        return output;
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
