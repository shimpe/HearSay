using HearSay.Audio;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.Synthesis.Rendering;

/// <summary>One note to render.</summary>
/// <param name="FrequencyHz">Frequency of the played note.</param>
/// <param name="MidiNote">MIDI note for key scaling; derived from the frequency when null.</param>
/// <param name="Velocity">Key velocity, 0..1.</param>
/// <param name="NoteDuration">Time during which the gate is open.</param>
/// <param name="ReleaseDuration">Time rendered after the gate closes.</param>
/// <param name="SampleRate">Output sample rate in hertz.</param>
public sealed record RenderRequest(
    double FrequencyHz,
    int? MidiNote,
    double Velocity,
    TimeSpan NoteDuration,
    TimeSpan ReleaseDuration,
    int SampleRate)
{
    /// <summary>Note used for key scaling; fractional when derived from the frequency.</summary>
    public double EffectiveMidiNote => MidiNote ?? 69.0 + (12.0 * Math.Log2(FrequencyHz / 440.0));

    /// <summary>Number of samples during which the gate is open.</summary>
    public int NoteSamples => (int)Math.Round(NoteDuration.TotalSeconds * SampleRate);

    /// <summary>Total number of samples.</summary>
    public int TotalSamples => NoteSamples + (int)Math.Round(ReleaseDuration.TotalSeconds * SampleRate);

    /// <summary>A note at full velocity.</summary>
    /// <param name="frequencyHz">Note frequency.</param>
    /// <param name="noteSeconds">Gate time.</param>
    /// <param name="releaseSeconds">Time rendered after the gate closes.</param>
    /// <param name="sampleRate">Sample rate.</param>
    public static RenderRequest Create(double frequencyHz, double noteSeconds, double releaseSeconds, int sampleRate = 48000) =>
        new(frequencyHz, null, 1.0, TimeSpan.FromSeconds(noteSeconds), TimeSpan.FromSeconds(releaseSeconds), sampleRate);
}

/// <summary>Mono result of an offline render.</summary>
/// <param name="Samples">Samples; not normalized.</param>
/// <param name="SampleRate">Sample rate in hertz.</param>
public sealed record RenderedAudio(float[] Samples, int SampleRate)
{
    /// <summary>Largest absolute sample value.</summary>
    public float Peak
    {
        get
        {
            float peak = 0;
            foreach (float sample in Samples)
            {
                peak = Math.Max(peak, Math.Abs(sample));
            }

            return peak;
        }
    }

    /// <summary>Wraps the audio for the comparison API.</summary>
    /// <param name="expectedFundamentalHz">Known fundamental, when there is one.</param>
    public AudioNote ToAudioNote(double? expectedFundamentalHz = null) => new(Samples, SampleRate, expectedFundamentalHz);

    /// <summary>Writes the audio as a WAV file, scaled down only when it would clip.</summary>
    /// <param name="path">Destination path.</param>
    /// <param name="normalizePeakTo">When set, scale so that the peak has this value.</param>
    public void WriteWav(string path, float? normalizePeakTo = null)
    {
        float peak = Peak;
        float gain = 1f;
        if (normalizePeakTo is float target && peak > 0)
        {
            gain = target / peak;
        }
        else if (peak > 1f)
        {
            gain = 0.999f / peak;
        }

        float[] data = Samples;
        if (gain != 1f)
        {
            data = new float[Samples.Length];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = Samples[i] * gain;
            }
        }

        WavFile.Write(path, data, SampleRate);
    }
}

/// <summary>
/// Renders canonical patches offline. Implementations are deterministic, need no audio device, produce mono audio
/// and are safe to call from many threads at once.
/// </summary>
public interface IOperatorMatrixRenderer
{
    /// <summary>Everything this renderer can play.</summary>
    SynthesisCapabilities Capabilities { get; }

    /// <summary>Fidelity contract of this renderer.</summary>
    RenderProfile RenderProfile { get; }

    /// <summary>Renders one note.</summary>
    /// <param name="patch">Patch to render.</param>
    /// <param name="request">Note to play.</param>
    /// <exception cref="PatchNotSupportedException">The patch uses something this renderer cannot play.</exception>
    RenderedAudio Render(OperatorMatrixPatch patch, RenderRequest request);
}

/// <summary>A renderer or exporter was given a patch it cannot represent.</summary>
public sealed class PatchNotSupportedException : Exception
{
    /// <summary>Creates the exception from a failed compatibility check.</summary>
    /// <param name="backend">Name of the backend.</param>
    /// <param name="result">Failed check.</param>
    public PatchNotSupportedException(string backend, PatchCompatibilityResult result)
        : base($"The patch is not compatible with {backend}:{Environment.NewLine}{result}")
    {
        Result = result;
    }

    /// <summary>The failed check with all issues.</summary>
    public PatchCompatibilityResult Result { get; }
}
