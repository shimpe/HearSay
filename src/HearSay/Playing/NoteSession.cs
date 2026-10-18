namespace HearSay.Playing;

/// <summary>
/// The notes of one opened input device, played on a <see cref="VoiceMixer"/> with the session as their source: delivered
/// until the session is retired and dropped after it. Retiring releases every note the session started, so a note that
/// was on its way from the device while it was closed is either released with the others or dropped, never left
/// sounding, however the threads are scheduled.
/// </summary>
/// <param name="mixer">Mixer the notes play on.</param>
public sealed class NoteSession(VoiceMixer mixer)
{
    private readonly object _gate = new();
    private bool _retired;

    /// <summary>True once <see cref="Retire"/> has been called.</summary>
    public bool IsRetired
    {
        get
        {
            lock (_gate)
            {
                return _retired;
            }
        }
    }

    /// <summary>
    /// Plays a note of the session unless it is retired. The play action runs under the session's lock, so that
    /// <see cref="Retire"/> waits for it; it must return quickly and must not retire the session itself.
    /// </summary>
    /// <param name="note">The note-on or note-off.</param>
    /// <param name="play">Plays it on the mixer, with the given object (the session) as the note's source.</param>
    /// <returns>True when it was played; false when the session is retired.</returns>
    public bool Deliver(NoteEvent note, Action<NoteEvent, object> play)
    {
        ArgumentNullException.ThrowIfNull(play);
        lock (_gate)
        {
            if (_retired)
            {
                return false;
            }

            play(note, this);
            return true;
        }
    }

    /// <summary>
    /// Ends the session: no note is delivered from now on, and every note it started is released. A delivery under way
    /// finishes first, so its note is released as well. Calling it again only releases again.
    /// </summary>
    public void Retire()
    {
        lock (_gate)
        {
            _retired = true;
        }

        mixer.ReleaseAll(this);
    }
}
