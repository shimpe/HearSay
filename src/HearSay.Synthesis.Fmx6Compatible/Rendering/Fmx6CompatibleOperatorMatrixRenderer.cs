using HearSay.Synthesis.Compilation;
using HearSay.Synthesis.Filters;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay.Synthesis.Fmx6Compatible.Rendering;

/// <summary>
/// Offline reference renderer that reproduces the discrete-time behaviour of the SuperCollider <c>FMX6</c> UGen
/// (https://github.com/shimpe/fmx6), so that a patch optimized here sounds the same when its exported SynthDef is
/// played.
/// </summary>
/// <remarks>
/// <para>What is reproduced, all taken from the FMX6 source:</para>
/// <list type="bullet">
/// <item>Previous-sample modulation over the full matrix, as in FM7: every edge has a delay of exactly one sample.</item>
/// <item>
/// Band-limited waveforms as additive Fourier series (triangle, saw, square, pulse with a duty cycle), with the
/// number of harmonics capped per sample so that the highest one stays below 45 % of the sample rate at the
/// instantaneous frequency, which includes the sample-to-sample change of the phase modulation.
/// </item>
/// <item>White noise and sample-and-hold noise from an xorshift generator seeded by operator slot; the held value changes when the base phase wraps.</item>
/// <item>Filtered noise: the white noise through a TPT state-variable filter in double precision, cutoff at the operator frequency.</item>
/// <item>Double-precision phase in cycles, single-precision frequency, phase offset, amplitude and matrix, all ramped linearly across each control block.</item>
/// <item>No warm-up sample at construction: the first output sample is at phase zero. FM7 differs here.</item>
/// <item>Operator filters: the TPT state-variable filters of <see cref="OperatorFilterKernel"/> between the waveform and the amplitude.</item>
/// <item>No oversampling: the aliasing of deep modulation is part of the sound of the backend.</item>
/// </list>
/// <para>
/// The kernel runs as a voice, one control block at a time, so a held note from <see cref="StartVoice"/> plays the same
/// samples as the offline render of a note of the same duration.
/// </para>
/// <para>Instances are immutable and thread-safe.</para>
/// </remarks>
public sealed class Fmx6CompatibleOperatorMatrixRenderer : IOperatorMatrixRenderer, IVoiceRenderer
{
    private readonly SynthesisProfile _profile;

    /// <summary>Creates a renderer for the <see cref="SynthesisProfiles.SuperColliderFmx6"/> profile.</summary>
    public Fmx6CompatibleOperatorMatrixRenderer()
        : this(SynthesisProfiles.SuperColliderFmx6)
    {
    }

    /// <summary>Creates a renderer for a profile with FMX6 semantics.</summary>
    /// <param name="profile">Profile; its capabilities must not exceed those of FMX6.</param>
    /// <exception cref="ArgumentException">The profile asks for more than FMX6 can play.</exception>
    public Fmx6CompatibleOperatorMatrixRenderer(SynthesisProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.Capabilities.IsSubsetOf(SynthesisProfiles.SuperColliderFmx6.Capabilities))
        {
            throw new ArgumentException($"Profile '{profile.Name}' needs capabilities that the FMX6-compatible renderer does not have.", nameof(profile));
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

    /// <summary>Renders an already compiled patch.</summary>
    /// <param name="compiled">Compiled patch.</param>
    public static float[] Render(CompiledOperatorMatrixPatch compiled)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        var output = new float[compiled.SampleCount];
        if (compiled.OperatorCount > 0 && output.Length > 0)
        {
            new Fmx6Voice(compiled.Controls()).Render(output);
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
        return new Fmx6Voice(request.SampleRate, firstGateOpen => new ControlStream(patch, request, blockSize, firstGateOpen));
    }


    /// <summary>Waveform selector of the UGen.</summary>
    /// <param name="waveform">Canonical waveform.</param>
    /// <exception cref="NotSupportedException">FMX6 has no such waveform.</exception>
    public static int WaveIndex(OperatorWaveform waveform) =>
        waveform switch
        {
            OperatorWaveform.Sine => 0,
            OperatorWaveform.Triangle => 1,
            OperatorWaveform.Saw => 2,
            OperatorWaveform.Square => 3,
            OperatorWaveform.Pulse => 4,
            OperatorWaveform.SampleHoldNoise => 5,
            OperatorWaveform.Noise => 6,
            OperatorWaveform.FilteredNoise => 7,
            _ => throw new NotSupportedException($"FMX6 has no {waveform} waveform."),
        };

    private void EnsureCompatible(OperatorMatrixPatch patch)
    {
        PatchCompatibilityResult compatibility = PatchCompatibilityValidator.Instance.Validate(patch, _profile.Capabilities);
        if (!compatibility.IsCompatible)
        {
            throw new PatchNotSupportedException(_profile.Name, compatibility);
        }
    }
}
