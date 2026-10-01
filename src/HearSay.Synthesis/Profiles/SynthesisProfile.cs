using HearSay.Synthesis.Patches;

namespace HearSay.Synthesis.Profiles;

/// <summary>How faithfully a renderer follows the backend it stands in for.</summary>
public enum RenderProfile
{
    /// <summary>
    /// Reproduce the backend, including its aliasing, control-rate interpolation and one-sample feedback delay. No
    /// hidden oversampling: what is optimized is what the exported patch will sound like.
    /// </summary>
    BackendExact,

    /// <summary>Best available quality, free to oversample and to use band-limited oscillators.</summary>
    ExperimentalHighQuality,
}

/// <summary>What a synthesis backend can represent. A search never proposes structures outside these limits.</summary>
/// <param name="OperatorCount">Exact number of operators of a patch.</param>
/// <param name="SupportedModes">Operator modes the backend implements.</param>
/// <param name="SupportedWaveforms">Operator waveforms the backend implements.</param>
/// <param name="SupportsFullModulationMatrix">Any operator may modulate any other; otherwise only feed-forward routing towards lower indices.</param>
/// <param name="SupportsSelfFeedback">An operator may modulate itself.</param>
/// <param name="SupportsCircularFeedback">Feedback loops through two or more operators are allowed.</param>
/// <param name="SupportsDynamicModulationMatrix">Modulation amounts may follow control-rate envelopes.</param>
/// <param name="SupportsRatioFrequency">Operators may track the played note.</param>
/// <param name="SupportsFixedFrequency">Operators may run at a fixed frequency.</param>
/// <param name="SupportsIndependentOperatorOutputs">Every operator has its own gain into the output mix.</param>
/// <param name="MaxModulationRadiansPerDestination">
/// Largest total phase deviation, the sum of the absolute amounts that arrive at one operator, the backend handles
/// without numerical trouble.
/// </param>
/// <param name="SupportsSharedNoise">
/// One noise source shared by all operators, coloured by <see cref="OperatorMatrixPatch.Noise"/>, that noise operators
/// play and that roughens the phase (<see cref="OperatorPatch.NoisePhase"/>) and the elliptic parameter
/// (<see cref="OperatorPatch.NoiseM"/>) of the others.
/// </param>
public sealed record SynthesisCapabilities(
    int OperatorCount,
    IReadOnlySet<OperatorMode> SupportedModes,
    IReadOnlySet<OperatorWaveform> SupportedWaveforms,
    bool SupportsFullModulationMatrix,
    bool SupportsSelfFeedback,
    bool SupportsCircularFeedback,
    bool SupportsDynamicModulationMatrix,
    bool SupportsRatioFrequency,
    bool SupportsFixedFrequency,
    bool SupportsIndependentOperatorOutputs,
    double MaxModulationRadiansPerDestination = 24.0,
    bool SupportsSharedNoise = false)
{
    /// <summary>True when everything this set allows is also allowed by <paramref name="other"/>.</summary>
    /// <param name="other">Capabilities of, for example, a renderer.</param>
    public bool IsSubsetOf(SynthesisCapabilities other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return OperatorCount == other.OperatorCount
            && SupportedModes.IsSubsetOf(other.SupportedModes)
            && SupportedWaveforms.IsSubsetOf(other.SupportedWaveforms)
            && (!SupportsFullModulationMatrix || other.SupportsFullModulationMatrix)
            && (!SupportsSelfFeedback || other.SupportsSelfFeedback)
            && (!SupportsCircularFeedback || other.SupportsCircularFeedback)
            && (!SupportsDynamicModulationMatrix || other.SupportsDynamicModulationMatrix)
            && (!SupportsRatioFrequency || other.SupportsRatioFrequency)
            && (!SupportsFixedFrequency || other.SupportsFixedFrequency)
            && (!SupportsIndependentOperatorOutputs || other.SupportsIndependentOperatorOutputs)
            && MaxModulationRadiansPerDestination <= other.MaxModulationRadiansPerDestination
            && (!SupportsSharedNoise || other.SupportsSharedNoise);
    }
}

/// <summary>A named synthesis target: what may be searched and how it has to be rendered.</summary>
/// <param name="Name">Stable identifier, also used on the command line.</param>
/// <param name="Capabilities">What the backend can represent.</param>
/// <param name="RenderProfile">Required rendering fidelity.</param>
/// <param name="ControlBlockSize">Samples per control block of the backend; control signals change once per block.</param>
/// <param name="Description">Human-readable summary.</param>
public sealed record SynthesisProfile(
    string Name,
    SynthesisCapabilities Capabilities,
    RenderProfile RenderProfile,
    int ControlBlockSize,
    string Description);

/// <summary>Built-in synthesis profiles.</summary>
public static class SynthesisProfiles
{
    /// <summary>
    /// Six sine phase-modulation operators with a full modulation matrix, as played by the <c>FM7</c> UGen of the
    /// SuperCollider sc3-plugins. The first production profile.
    /// </summary>
    public static SynthesisProfile SuperColliderFm7 { get; } = new(
        "supercollider-fm7",
        new SynthesisCapabilities(
            OperatorCount: 6,
            SupportedModes: new HashSet<OperatorMode> { OperatorMode.PhaseModulation },
            SupportedWaveforms: new HashSet<OperatorWaveform> { OperatorWaveform.Sine },
            SupportsFullModulationMatrix: true,
            SupportsSelfFeedback: true,
            SupportsCircularFeedback: true,
            SupportsDynamicModulationMatrix: true,
            SupportsRatioFrequency: true,
            SupportsFixedFrequency: true,
            SupportsIndependentOperatorOutputs: true,
            MaxModulationRadiansPerDestination: 24.0),
        RenderProfile.BackendExact,
        ControlBlockSize: 64,
        "Six sine phase-modulation operators, full 6x6 matrix, SuperCollider FM7 UGen.");

    /// <summary>
    /// Six phase-modulation operators with selectable waveforms, as played by the <c>FMX6</c> UGen
    /// (https://github.com/shimpe/fmx6): sine, band-limited triangle, saw, square and pulse, white and
    /// sample-and-hold noise, and filtered noise, over the same full matrix as FM7. Needs FMX6 with waveform 7.
    /// </summary>
    public static SynthesisProfile SuperColliderFmx6 { get; } = new(
        "supercollider-fmx6",
        SuperColliderFm7.Capabilities with
        {
            SupportedWaveforms = new HashSet<OperatorWaveform>
            {
                OperatorWaveform.Sine,
                OperatorWaveform.Triangle,
                OperatorWaveform.Saw,
                OperatorWaveform.Square,
                OperatorWaveform.Pulse,
                OperatorWaveform.Noise,
                OperatorWaveform.SampleHoldNoise,
                OperatorWaveform.FilteredNoise,
            },
        },
        RenderProfile.BackendExact,
        ControlBlockSize: 64,
        "Six operators with sine, triangle, saw, square, pulse, noise and filtered-noise waveforms, full 6x6 matrix, SuperCollider FMX6 UGen.");

    /// <summary>
    /// Six phase-modulation operators that are Jacobi elliptic oscillators, as played by the <c>Nome6</c> UGen: a
    /// sine (m = 0) or an odd-harmonic elliptic waveform per operator, morphing from sn to the peak-normalized sd, over
    /// the same full matrix as FM7, rendered at twice the sample rate, and one filtered noise source that noise operators
    /// play and that roughens the phase and the elliptic parameter of the others. Needs the Nome6 plugin with noise.
    /// </summary>
    public static SynthesisProfile SuperColliderNome6 { get; } = new(
        "supercollider-nome6",
        SuperColliderFm7.Capabilities with
        {
            SupportedWaveforms = new HashSet<OperatorWaveform> { OperatorWaveform.Sine, OperatorWaveform.Elliptic, OperatorWaveform.Noise },
            SupportsSharedNoise = true,
        },
        RenderProfile.BackendExact,
        ControlBlockSize: 64,
        "Six Jacobi elliptic phase-modulation operators (sine at m = 0) and a shared filtered noise source, full 6x6 matrix, 2x oversampled, SuperCollider Nome6 UGen.");

    /// <summary>
    /// Experimental profile that adds triangle operators. It exists to prove that waveform is a structural search
    /// dimension and that backends reject what they cannot play; it has no export backend yet.
    /// </summary>
    public static SynthesisProfile ExperimentalSineTriangle { get; } = new(
        "experimental-sine-triangle",
        SuperColliderFm7.Capabilities with
        {
            SupportedWaveforms = new HashSet<OperatorWaveform> { OperatorWaveform.Sine, OperatorWaveform.Triangle },
        },
        RenderProfile.ExperimentalHighQuality,
        ControlBlockSize: 64,
        "Six sine or triangle phase-modulation operators; rendered by the extended renderer only.");

    /// <summary>
    /// Reserved profile of a future HearSay synthesis engine with all waveforms. No renderer implements it yet; it
    /// documents the direction and lets compatibility checks be written against it today.
    /// </summary>
    public static SynthesisProfile HearSayExtended { get; } = new(
        "hearsay-extended",
        SuperColliderFm7.Capabilities with
        {
            SupportedWaveforms = new HashSet<OperatorWaveform>(Enum.GetValues<OperatorWaveform>()),
        },
        RenderProfile.ExperimentalHighQuality,
        ControlBlockSize: 64,
        "Reserved: six operators with arbitrary waveforms for a future custom UGen.");

    /// <summary>All built-in profiles.</summary>
    public static IReadOnlyList<SynthesisProfile> All { get; } = [SuperColliderFm7, SuperColliderFmx6, SuperColliderNome6, ExperimentalSineTriangle, HearSayExtended];

    /// <summary>Finds a built-in profile by name, ignoring case; null when unknown.</summary>
    /// <param name="name">Profile name.</param>
    public static SynthesisProfile? Find(string name)
    {
        foreach (SynthesisProfile profile in All)
        {
            if (string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return profile;
            }
        }

        return null;
    }
}
