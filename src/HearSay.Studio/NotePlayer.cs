using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay.Studio;

/// <summary>
/// Plays notes of patches for both tabs: renders off the UI thread, keeps one gain per patch from its reference note so
/// that the differences between keys and velocities survive, and plays only the note asked for last.
/// </summary>
public sealed class NotePlayer(AudioPlayer player)
{
    private object? _gainPatch;
    private float _gain;
    private int _version;

    /// <summary>True when an audio device could be opened.</summary>
    public bool CanPlay => player.IsAvailable;

    /// <summary>True when the patch reacts to key velocity; otherwise the keyboard applies it as volume.</summary>
    /// <param name="patch">Patch.</param>
    public static bool RespondsToVelocity(OperatorMatrixPatch patch) => patch.Operators.Any(static o => o.Enabled && o.VelocitySensitivity > 0);

    /// <summary>Renders a note and plays it, unless another note was asked for meanwhile.</summary>
    /// <param name="patch">Patch.</param>
    /// <param name="profile">Profile whose renderer plays it.</param>
    /// <param name="gainReference">Request whose loudest sample, at full velocity, sets the gain of the patch.</param>
    /// <param name="note">The note to play.</param>
    /// <returns>True when the note was played.</returns>
    public async Task<bool> PlayAsync(OperatorMatrixPatch patch, SynthesisProfile profile, RenderRequest gainReference, RenderRequest note)
    {
        int version = Interlocked.Increment(ref _version);
        bool known = ReferenceEquals(_gainPatch, patch);
        float knownGain = _gain;
        (RenderedAudio audio, float gain) = await Task.Run(() =>
        {
            IOperatorMatrixRenderer renderer = SynthesisBackends.RendererFor(profile);
            float g = knownGain;
            if (!known)
            {
                RenderedAudio full = renderer.Render(patch, gainReference with { Velocity = 1.0 });
                g = full.Peak > 0 ? 0.5f / full.Peak : 1f;
            }

            return (renderer.Render(patch, note), g);
        });
        if (version != Volatile.Read(ref _version))
        {
            return false;
        }

        (_gainPatch, _gain) = (patch, gain);
        float level = gain * (RespondsToVelocity(patch) ? 1f : (float)note.Velocity);
        player.Play(Scale(audio, Math.Min(level, 0.98f / Math.Max(1e-6f, audio.Peak))));
        return true;
    }

    /// <summary>Plays audio as it is.</summary>
    /// <param name="audio">Audio.</param>
    public void Play(RenderedAudio audio)
    {
        Interlocked.Increment(ref _version);
        player.Play(audio);
    }

    /// <summary>Stops playback.</summary>
    public void Stop()
    {
        Interlocked.Increment(ref _version);
        player.Stop();
    }

    private static RenderedAudio Scale(RenderedAudio audio, float gain)
    {
        var samples = new float[audio.Samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = audio.Samples[i] * gain;
        }

        return new RenderedAudio(samples, audio.SampleRate);
    }
}
