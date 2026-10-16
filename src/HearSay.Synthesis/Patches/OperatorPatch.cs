using System.Text.Json.Serialization;

namespace HearSay.Synthesis.Patches;

/// <summary>
/// How an operator turns its phase and its inputs into a signal. A structural parameter: a search never moves
/// between modes in small steps.
/// </summary>
public enum OperatorMode
{
    /// <summary>The weighted sum of the source operators is added to the phase of the oscillator.</summary>
    PhaseModulation,

    // Reserved for future synthesis profiles: RingModulation, WaveFolder, Filter, FilterFm.
}

/// <summary>
/// Oscillator waveform of an operator. A structural parameter: the values are unrelated categories and are never
/// treated as positions on a numerical axis.
/// </summary>
public enum OperatorWaveform
{
    /// <summary>Sine wave.</summary>
    Sine,

    /// <summary>Triangle wave.</summary>
    Triangle,

    /// <summary>Sawtooth wave.</summary>
    Saw,

    /// <summary>Square wave.</summary>
    Square,

    /// <summary>Pulse wave with variable width.</summary>
    Pulse,

    /// <summary>White noise; phase modulation has no effect on it, but it can modulate other operators.</summary>
    Noise,

    /// <summary>Sample-and-hold noise: a random value held for every cycle of the operator frequency.</summary>
    SampleHoldNoise,

    /// <summary>
    /// White noise through a two-pole filter whose cutoff is the operator frequency; <see cref="OperatorPatch.Shape"/>
    /// selects low-pass (0), band-pass (0.5) or high-pass (1), crossfaded in between. Phase modulation has no effect.
    /// </summary>
    FilteredNoise,

    /// <summary>
    /// Jacobi elliptic oscillator, as in the Nome6 UGen: a band-limited odd-harmonic series whose
    /// <see cref="OperatorPatch.Shape"/> is the elliptic parameter m (0 is a sine, towards 0.999999 ever more odd
    /// harmonics) and whose <see cref="OperatorPatch.Morph"/> crossfades from sn (0) to the peak-normalized sd (1).
    /// </summary>
    Elliptic,

    /// <summary>Sum of harmonics with individual levels.</summary>
    Additive,

    /// <summary>Single-cycle wavetable.</summary>
    Wavetable,
}

/// <summary>Properties of waveforms.</summary>
public static class OperatorWaveformExtensions
{
    /// <summary>True for the noise waveforms: they ignore the phase, and so whatever modulates them.</summary>
    /// <param name="waveform">Waveform.</param>
    public static bool IsNoise(this OperatorWaveform waveform) =>
        waveform is OperatorWaveform.Noise or OperatorWaveform.SampleHoldNoise or OperatorWaveform.FilteredNoise;
}

/// <summary>Whether an operator follows the played note.</summary>
public enum OperatorFrequencyMode
{
    /// <summary>The operator frequency is the note frequency multiplied by a ratio.</summary>
    Ratio,

    /// <summary>The operator frequency is fixed in hertz and does not transpose with the played note.</summary>
    Fixed,
}

/// <summary>Coarse frequency of an operator.</summary>
/// <param name="Mode">Ratio or fixed.</param>
/// <param name="Ratio">Multiple of the note frequency; used in <see cref="OperatorFrequencyMode.Ratio"/> mode.</param>
/// <param name="FixedFrequencyHz">Frequency in hertz; used in <see cref="OperatorFrequencyMode.Fixed"/> mode.</param>
public sealed record OperatorFrequency(OperatorFrequencyMode Mode, double Ratio = 1.0, double FixedFrequencyHz = 440.0)
{
    /// <summary>An operator at a multiple of the note frequency.</summary>
    /// <param name="ratio">Frequency ratio.</param>
    public static OperatorFrequency AtRatio(double ratio) => new(OperatorFrequencyMode.Ratio, ratio);

    /// <summary>An operator at a fixed frequency.</summary>
    /// <param name="hz">Frequency in hertz.</param>
    public static OperatorFrequency AtFixed(double hz) => new(OperatorFrequencyMode.Fixed, 1.0, hz);
}

/// <summary>How the level and the envelope speed of an operator change along the keyboard.</summary>
/// <param name="LevelDbPerOctave">Level change in decibels per octave above <paramref name="ReferenceMidiNote"/>.</param>
/// <param name="RatePerOctave">
/// Envelope speed-up per octave above the reference note: all envelope times are multiplied by
/// <c>2^(-RatePerOctave * octaves)</c>.
/// </param>
/// <param name="ReferenceMidiNote">Note at which the scaling is neutral.</param>
public sealed record KeyScaling(double LevelDbPerOctave = 0.0, double RatePerOctave = 0.0, double ReferenceMidiNote = 60.0)
{
    /// <summary>No key scaling.</summary>
    public static KeyScaling None { get; } = new();

    /// <summary>True when neither level nor rate depend on the note.</summary>
    public bool IsNeutral => LevelDbPerOctave == 0.0 && RatePerOctave == 0.0;

    /// <summary>Linear level multiplier for a note.</summary>
    /// <param name="midiNote">Played note, fractional values allowed.</param>
    public double LevelFactor(double midiNote) =>
        Math.Pow(10.0, LevelDbPerOctave * ((midiNote - ReferenceMidiNote) / 12.0) / 20.0);

    /// <summary>Multiplier of all envelope times for a note.</summary>
    /// <param name="midiNote">Played note, fractional values allowed.</param>
    public double TimeFactor(double midiNote) =>
        Math.Pow(2.0, -RatePerOctave * ((midiNote - ReferenceMidiNote) / 12.0));
}

/// <summary>
/// One operator of an <see cref="OperatorMatrixPatch"/>. An operator is not a carrier or a modulator by type: its
/// role follows from the modulation matrix and the output levels of the patch, and it can have both roles at once.
/// </summary>
/// <remarks>
/// The frequency of a ratio operator is <c>note * Ratio * 2^(FineTuneCents / 1200) + DetuneHz</c>, multiplied by the
/// pitch model of the patch. A fixed operator runs at <c>FixedFrequencyHz + DetuneHz</c> and ignores the note and
/// the pitch model. Fine tune is multiplicative and transposes; detune is additive and gives the same beat rate on
/// every key.
/// </remarks>
/// <param name="Enabled">A disabled operator is silent and sends no modulation.</param>
/// <param name="Mode">Operator mode; structural.</param>
/// <param name="Waveform">Oscillator waveform; structural.</param>
/// <param name="Frequency">Coarse frequency; structural.</param>
/// <param name="FineTuneCents">Multiplicative fine tuning in cents.</param>
/// <param name="DetuneHz">Additive detune in hertz.</param>
/// <param name="InitialPhase">Phase offset in radians.</param>
/// <param name="AmplitudeEnvelope">
/// Amplitude envelope, levels 0..1. It scales everything the operator sends: its modulation of other operators as
/// well as its contribution to the output mix.
/// </param>
/// <param name="VelocitySensitivity">
/// 0..1. The operator amplitude is multiplied by <c>1 - sensitivity + sensitivity * velocity</c>.
/// </param>
/// <param name="KeyScaling">Level and rate scaling along the keyboard.</param>
/// <remarks>Waveform, <see cref="Shape"/> and <see cref="HarmonicLimit"/> only matter to backends that implement more than sine operators.</remarks>
public sealed record OperatorPatch(
    bool Enabled,
    OperatorMode Mode,
    OperatorWaveform Waveform,
    OperatorFrequency Frequency,
    double FineTuneCents,
    double DetuneHz,
    double InitialPhase,
    OperatorEnvelope AmplitudeEnvelope,
    double VelocitySensitivity,
    KeyScaling KeyScaling)
{
    /// <summary>
    /// Waveform shape parameter. For <see cref="OperatorWaveform.Pulse"/> it is the duty cycle, 0.01..0.99; for
    /// <see cref="OperatorWaveform.FilteredNoise"/> the filter response, 0 (low-pass) to 1 (high-pass); for
    /// <see cref="OperatorWaveform.Elliptic"/> the elliptic parameter m, 0..<see cref="MaxEllipticM"/>; other
    /// waveforms ignore it and keep it within 0.01..0.99.
    /// </summary>
    public double Shape { get; init; } = 0.5;

    /// <summary>
    /// Second waveform parameter, 0..1. For <see cref="OperatorWaveform.Elliptic"/> the crossfade from the
    /// frequency-normalized sn (0) to the peak-normalized sd (1); other waveforms ignore it.
    /// </summary>
    public double Morph { get; init; }

    /// <summary>
    /// Phase roughness: radians of phase deviation per unit of the shared noise of the patch, for backends with a
    /// shared noise source (<see cref="Profiles.SynthesisCapabilities.SupportsSharedNoise"/>); zero elsewhere.
    /// </summary>
    public double NoisePhase { get; init; }

    /// <summary>
    /// Spectral roughness of an elliptic operator: the shared noise moves its elliptic parameter by up to this much
    /// around <see cref="Shape"/>, -1..1, the sign giving the direction; for backends with a shared noise source only.
    /// </summary>
    public double NoiseM { get; init; }

    /// <summary>
    /// Filter between the waveform and the amplitude, for backends with operator filters
    /// (<see cref="Profiles.SynthesisCapabilities.SupportsOperatorFilters"/>); null for none. Left out of the JSON when
    /// null, so that patches without filters keep their serialized form and their hash.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OperatorFilter? Filter { get; init; }

    /// <summary>Largest elliptic parameter m: m = 1 has an infinite period and is no oscillator.</summary>
    public const double MaxEllipticM = 0.999999;

    /// <summary>
    /// Elliptic parameter an operator gets when it becomes elliptic: m = 0.99, nome 0.26, so that every odd harmonic is
    /// some 12 to 14 dB below the one before; clearly not a sine, still mellower than a square.
    /// </summary>
    public const double DefaultEllipticM = 0.99;

    /// <summary>
    /// This operator with another waveform and a shape that is valid for it: becoming elliptic sets m to
    /// <see cref="DefaultEllipticM"/>, leaving elliptic sets the shape back to 0.5, and otherwise the shape is kept
    /// within the range of the new waveform.
    /// </summary>
    /// <param name="waveform">New waveform.</param>
    public OperatorPatch WithWaveform(OperatorWaveform waveform)
    {
        if (waveform == Waveform)
        {
            return this;
        }

        double shape = (Waveform, waveform) switch
        {
            (_, OperatorWaveform.Elliptic) => DefaultEllipticM,
            (OperatorWaveform.Elliptic, _) => 0.5,
            (_, OperatorWaveform.FilteredNoise) => Shape,
            _ => Math.Clamp(Shape, 0.01, 0.99),
        };
        return this with { Waveform = waveform, Shape = shape };
    }

    /// <summary>Largest number of harmonics a band-limited non-sine waveform is built from, 1..128; sine and noise ignore it.</summary>
    public int HarmonicLimit { get; init; } = 32;

    /// <summary>A disabled sine phase-modulation operator at ratio 1.</summary>
    public static OperatorPatch Disabled { get; } = new(
        Enabled: false,
        OperatorMode.PhaseModulation,
        OperatorWaveform.Sine,
        OperatorFrequency.AtRatio(1.0),
        FineTuneCents: 0,
        DetuneHz: 0,
        InitialPhase: 0,
        OperatorEnvelope.Constant(),
        VelocitySensitivity: 0,
        KeyScaling.None);

    /// <summary>An enabled sine phase-modulation operator.</summary>
    /// <param name="frequency">Coarse frequency.</param>
    /// <param name="envelope">Amplitude envelope; a constant level when omitted.</param>
    public static OperatorPatch Sine(OperatorFrequency frequency, OperatorEnvelope? envelope = null) =>
        Disabled with { Enabled = true, Frequency = frequency, AmplitudeEnvelope = envelope ?? OperatorEnvelope.Constant() };

    /// <summary>Operator frequency in hertz for a note, without the pitch model.</summary>
    /// <param name="noteFrequencyHz">Frequency of the played note.</param>
    public double FrequencyHz(double noteFrequencyHz) =>
        Frequency.Mode == OperatorFrequencyMode.Ratio
            ? (noteFrequencyHz * Frequency.Ratio * Math.Pow(2.0, FineTuneCents / 1200.0)) + DetuneHz
            : Frequency.FixedFrequencyHz + DetuneHz;

    /// <summary>Amplitude multiplier for a key velocity.</summary>
    /// <param name="velocity">Velocity 0..1.</param>
    public double VelocityFactor(double velocity) => 1.0 - VelocitySensitivity + (VelocitySensitivity * velocity);
}
