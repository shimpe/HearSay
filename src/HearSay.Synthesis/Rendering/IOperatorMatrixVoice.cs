using HearSay.Synthesis.Compilation;
using HearSay.Synthesis.Filters;
using HearSay.Synthesis.Patches;

namespace HearSay.Synthesis.Rendering;

/// <summary>
/// One note being played: the samples of a patch, rendered as far as the caller asks, with a gate that stays open
/// until <see cref="Release"/>. A voice that is held for a duration and then released renders the same samples as the
/// offline renderer for a note of that duration.
/// </summary>
/// <remarks>Mutable; render from one thread at a time. <see cref="Release"/> may be called from another thread.</remarks>
public interface IOperatorMatrixVoice
{
    /// <summary>Sample rate in hertz.</summary>
    int SampleRate { get; }

    /// <summary>True once <see cref="Release"/> has been called.</summary>
    bool IsReleased { get; }

    /// <summary>Number of samples rendered so far.</summary>
    long SamplesRendered { get; }

    /// <summary>
    /// Closes the gate from the next control block not yet started; a block already partly rendered keeps the gate
    /// open. Releasing before the first <see cref="Render"/> plays the note of zero length. Calling it again does
    /// nothing.
    /// </summary>
    void Release();

    /// <summary>
    /// Renders the next samples. Any length is allowed; the rest of a partly used control block is kept for the next
    /// call, so splitting a render into calls of other sizes gives the same samples.
    /// </summary>
    /// <param name="output">Receives the samples; overwritten, not mixed into.</param>
    void Render(Span<float> output);
}

/// <summary>A renderer that can also play held notes, whose end is not known when they start.</summary>
public interface IVoiceRenderer
{
    /// <summary>Starts a voice with its gate open.</summary>
    /// <param name="patch">Patch to play.</param>
    /// <param name="request">Note to play.</param>
    /// <exception cref="PatchNotSupportedException">The patch uses something this renderer cannot play.</exception>
    IOperatorMatrixVoice StartVoice(OperatorMatrixPatch patch, VoiceRequest request);
}

/// <summary>
/// The control signals of a patch bound to one voice: the static parts, and the current values one control block at
/// a time. Before the first <see cref="Advance"/> the current values are the values a unit sees when it is
/// constructed; after the advance for block <c>b</c> they are the values during block <c>b</c>, so a renderer ramps
/// from the previous values to the current ones across a block. Implemented by <see cref="ControlStream"/>, whose gate
/// is decided live, and by <see cref="CompiledOperatorMatrixPatch.Controls"/>, whose gate is already in its arrays.
/// </summary>
/// <remarks>
/// The arrays are owned by the source and must not be changed; the current values are overwritten in place by every
/// <see cref="Advance"/>.
/// </remarks>
public interface IControlSource
{
    /// <summary>Sample rate in hertz.</summary>
    int SampleRate { get; }

    /// <summary>Samples per control block.</summary>
    int BlockSize { get; }

    /// <summary>Number of audible operators.</summary>
    int OperatorCount { get; }

    /// <summary>Number of live edges.</summary>
    int EdgeCount { get; }

    /// <summary>Patch index of every audible operator.</summary>
    int[] OperatorIndices { get; }

    /// <summary>Waveform per audible operator.</summary>
    OperatorWaveform[] Waveforms { get; }

    /// <summary>Phase offset in radians per audible operator.</summary>
    float[] Phases { get; }

    /// <summary>Waveform shape per audible operator (pulse duty cycle, filter response, elliptic parameter m).</summary>
    float[] Shapes { get; }

    /// <summary>Second waveform parameter per audible operator (elliptic sn-to-sd morph).</summary>
    float[] Morphs { get; }

    /// <summary>Phase roughness per audible operator, radians per unit of shared noise.</summary>
    float[] NoisePhases { get; }

    /// <summary>Elliptic-parameter roughness per audible operator.</summary>
    float[] NoiseMs { get; }

    /// <summary>Colour of the shared noise, -1..1.</summary>
    float NoiseColor { get; }

    /// <summary>Crossover of the shared noise filter in hertz.</summary>
    float NoiseCutoffHz { get; }

    /// <summary>Harmonic limit per audible operator.</summary>
    int[] HarmonicLimits { get; }

    /// <summary>Linear output gain per audible operator.</summary>
    float[] OutputGains { get; }

    /// <summary>Source of every live edge as an index into the audible operators.</summary>
    int[] EdgeSources { get; }

    /// <summary>Destination of every live edge as an index into the audible operators.</summary>
    int[] EdgeDestinations { get; }

    /// <summary>True when any audible operator has a filter; the filter parts below are those of no filter otherwise.</summary>
    bool HasFilters { get; }

    /// <summary>Filter type code per audible operator, <see cref="OperatorFilterKernel.Off"/> for none.</summary>
    int[] FilterTypes { get; }

    /// <summary>Filter cutoff, centre or F1 per audible operator, before the envelope.</summary>
    float[] FilterBaseHz { get; }

    /// <summary>Filter resonance per audible operator.</summary>
    float[] FilterQ { get; }

    /// <summary>F2 / F1 per audible operator.</summary>
    float[] FormantRatios { get; }

    /// <summary>Level of F2 per audible operator.</summary>
    float[] FormantLevels { get; }

    /// <summary>Peak filter gain in decibels per audible operator.</summary>
    float[] FilterGainsDb { get; }

    /// <summary>Current frequency in hertz per audible operator.</summary>
    float[] Frequencies { get; }

    /// <summary>Current amplitude per audible operator.</summary>
    float[] Amplitudes { get; }

    /// <summary>Current amount in radians per live edge.</summary>
    float[] EdgeAmounts { get; }

    /// <summary>Current filter envelope offset in octaves per audible operator; zero without a filter envelope.</summary>
    float[] FilterOctaves { get; }

    /// <summary>Computes the values of the next control block.</summary>
    /// <param name="gateOpen">Whether the gate is open during that block; ignored by a source whose gate is fixed.</param>
    void Advance(bool gateOpen);
}
