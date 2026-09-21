namespace HearSay.Audio;

/// <summary>
/// A mono recording of a single musical note.
/// </summary>
/// <param name="Samples">Mono samples, nominally in the range -1..+1.</param>
/// <param name="SampleRate">Sample rate in Hz.</param>
/// <param name="ExpectedFundamentalHz">Fundamental frequency of the played note in Hz, when known.</param>
public sealed record AudioNote(
    ReadOnlyMemory<float> Samples,
    int SampleRate,
    double? ExpectedFundamentalHz = null)
{
    /// <summary>Duration of the recording in seconds.</summary>
    public double DurationSeconds => SampleRate > 0 ? (double)Samples.Length / SampleRate : 0.0;

    /// <summary>Creates a note from a sample array without copying it.</summary>
    /// <param name="samples">Mono samples.</param>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="expectedFundamentalHz">Fundamental frequency of the note in Hz, when known.</param>
    public static AudioNote FromSamples(float[] samples, int sampleRate, double? expectedFundamentalHz = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        return new AudioNote(samples, sampleRate, expectedFundamentalHz);
    }
}
