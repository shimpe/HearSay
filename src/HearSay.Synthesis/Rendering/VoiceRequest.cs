namespace HearSay.Synthesis.Rendering;

/// <summary>
/// One note as a voice plays it: pitch, key and velocity, but no duration, because the gate is decided block by block
/// by whoever drives the voice.
/// </summary>
/// <param name="FrequencyHz">Frequency of the played note.</param>
/// <param name="MidiNote">MIDI note for key scaling; derived from the frequency when null.</param>
/// <param name="Velocity">Key velocity, 0..1.</param>
/// <param name="SampleRate">Output sample rate in hertz.</param>
public sealed record VoiceRequest(double FrequencyHz, int? MidiNote, double Velocity, int SampleRate = 48000)
{
    /// <summary>Note used for key scaling; fractional when derived from the frequency.</summary>
    public double EffectiveMidiNote => MidiNote ?? 69.0 + (12.0 * Math.Log2(FrequencyHz / 440.0));
}
