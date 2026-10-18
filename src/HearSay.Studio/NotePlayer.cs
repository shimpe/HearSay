using HearSay.Synthesis.Rendering;

namespace HearSay.Studio;

/// <summary>
/// Plays audio as it is, one sound at a time: target files and previews. Notes of patches are played live by the
/// <see cref="LivePlayer"/>, on the same device, so both can sound at once.
/// </summary>
public sealed class NotePlayer(AudioPlayer player)
{
    /// <summary>True when an audio device could be opened.</summary>
    public bool CanPlay => player.IsAvailable;

    /// <summary>Plays audio as it is, stopping what this player was playing.</summary>
    /// <param name="audio">Audio.</param>
    public void Play(RenderedAudio audio) => player.Play(audio);

    /// <summary>Stops playback.</summary>
    public void Stop() => player.Stop();
}
