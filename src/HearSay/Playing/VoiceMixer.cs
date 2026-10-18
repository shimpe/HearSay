using HearSay.Synthesis.Rendering;

namespace HearSay.Playing;

/// <summary>
/// Plays held, polyphonic notes: starts a voice per note-on, releases it at note-off, ends it when it has died away,
/// takes the oldest voice for a note beyond the limit, and mixes the voices into the buffers the audio output asks
/// for.
/// </summary>
/// <remarks>
/// <para>
/// Levels are those of a single note played today (<see cref="PlaybackLevel"/>): every sound has one gain, 0.5 over the
/// peak of its reference note at full velocity (<see cref="PrepareAsync"/>), and a patch without velocity sensitivity
/// gets the velocity as volume. The sum of the voices passes a soft limiter that leaves everything up to 0.9 unchanged and bends what is
/// above towards 1 with <c>tanh</c>, so that one note sounds as before and sixteen loud ones do not clip.
/// </para>
/// <para>
/// A voice ends when it has been silent (every sample under -90 dBFS) for 50 ms after its note-off, or after the
/// envelope of a one-shot patch has run; at the latest at the lifetime of the SuperCollider export
/// (<see cref="PatchLifetime"/>), where it fades out over 5 ms, as it does when it is taken for a new note or dropped
/// by <see cref="Shed"/>.
/// </para>
/// <para>
/// Thread safety: <see cref="PrepareAsync"/>, <see cref="NoteOn"/>, <see cref="NoteOff"/>, <see cref="ReleaseAll"/>,
/// <see cref="Shed"/> and the properties may be called from any thread while <see cref="Render"/> runs on the audio
/// thread. They take a lock only for short bookkeeping, which <see cref="Render"/> also holds only to pick up their
/// changes before a buffer and to remove ended voices after it, never while voices render. Changes take effect from
/// the next buffer <see cref="Render"/> starts; a note-off no earlier than <see cref="MinimumHoldSeconds"/> into its
/// note. <see cref="Render"/> itself must be called from one thread at a time.
/// </para>
/// </remarks>
public sealed class VoiceMixer
{
    /// <summary>
    /// Shortest time a note is held: a note-off within it closes the gate at its end, so that a tap whose note-on and
    /// note-off fall between two buffers, or arrive before the gain is known, plays its attack and then its release
    /// (the note of zero length is silent for an envelope that starts from zero).
    /// </summary>
    public const double MinimumHoldSeconds = 0.005;

    /// <summary>Largest number of samples rendered at a time; longer buffers are rendered in parts.</summary>
    private const int ChunkSize = 1024;

    /// <summary>-90 dBFS.</summary>
    private const float SilenceLevel = 3.1623e-5f;

    /// <summary>Level above which the soft limiter bends the sum.</summary>
    private const float LimiterThreshold = 0.9f;

    /// <summary>Number of sounds whose gain is kept.</summary>
    private const int KeptSounds = 32;

    private readonly object _lock = new();
    private readonly List<Voice> _voices = [];
    private readonly object _soundsLock = new();
    private readonly Dictionary<Sound, PreparedSound> _sounds = [];
    private readonly Queue<Sound> _soundOrder = new();
    private readonly Action<int> _renderVoice;
    private readonly int _fadeSamples;
    private readonly int _silenceSamples;
    private readonly int _minimumHoldSamples;

    // Owned by the thread that renders.
    private Voice[] _rendering;
    private int _chunkLength;

    private int _activeVoices;

    /// <summary>Creates a mixer without voices.</summary>
    /// <param name="sampleRate">Sample rate of the voices and of the mix, in hertz.</param>
    /// <param name="maxVoices">Number of notes that sound at once; one more takes the oldest voice.</param>
    public VoiceMixer(int sampleRate = 48000, int maxVoices = 16)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxVoices);
        SampleRate = sampleRate;
        MaxVoices = maxVoices;
        _fadeSamples = Math.Max(1, (int)Math.Round(0.005 * sampleRate));
        _silenceSamples = Math.Max(1, (int)Math.Round(0.05 * sampleRate));
        _minimumHoldSamples = Math.Max(1, (int)Math.Round(MinimumHoldSeconds * sampleRate));
        _rendering = new Voice[2 * maxVoices];
        _renderVoice = i => RenderVoice(_rendering[i], _chunkLength);
        VoiceRenderCrew.EnsureStarted();
    }

    /// <summary>
    /// Raised after the <see cref="SoundingNotes"/> changed: by a note method on its caller's thread, or by
    /// <see cref="Render"/> on the audio thread when a held note ends by itself. Handlers must return quickly.
    /// </summary>
    public event Action? SoundingNotesChanged;

    /// <summary>Sample rate of the voices and of the mix, in hertz.</summary>
    public int SampleRate { get; }

    /// <summary>Number of notes that sound at once.</summary>
    public int MaxVoices { get; }

    /// <summary>Number of voices: held, releasing, fading out, or waiting for the gain of their sound.</summary>
    public int ActiveVoices => Volatile.Read(ref _activeVoices);

    /// <summary>
    /// The notes held down: note-on received, no note-off yet, and the voice neither taken for another note, dropped,
    /// nor ended by itself. A copy, taken now.
    /// </summary>
    public IReadOnlySet<int> SoundingNotes
    {
        get
        {
            var notes = new HashSet<int>();
            lock (_lock)
            {
                foreach (Voice voice in _voices)
                {
                    if (voice.Held)
                    {
                        notes.Add(voice.Note);
                    }
                }
            }

            return notes;
        }
    }

    /// <summary>
    /// Computes the gain of a sound on a thread of its own, not on the thread pool, and keeps it, so that its first note
    /// starts at once, even while a search keeps the pool busy. A note of a sound that is not prepared yet prepares it
    /// and waits for it.
    /// </summary>
    /// <param name="sound">Sound.</param>
    /// <returns>
    /// A task that completes when the gain is known; at once for a sound <see cref="NoteOn"/> cannot play. It fails
    /// when the reference note cannot be rendered, and the notes of the sound are then not played.
    /// </returns>
    public Task PrepareAsync(Sound sound)
    {
        ArgumentNullException.ThrowIfNull(sound);
        return Prepare(sound).Gain;
    }

    /// <summary>
    /// Starts a note. It sounds from the next buffer, or as soon as the gain of its sound is known. The same note held
    /// from the same source is released first; with <see cref="MaxVoices"/> voices playing, the oldest fades out.
    /// </summary>
    /// <param name="note">MIDI note; the frequency is <c>440 × 2^((note − 69) / 12)</c> Hz.</param>
    /// <param name="velocity">Key velocity, 0..1.</param>
    /// <param name="sound">Sound the note keeps until it ends, whatever is played later.</param>
    /// <param name="source">Where the note comes from, such as the MIDI input or the mouse, for <see cref="NoteOff"/> and <see cref="ReleaseAll"/>.</param>
    /// <returns>
    /// True when the note plays. False, and nothing changes, when the sound cannot be played live: its profile's
    /// renderer has no voices (only FM7, FMX6 and Nome6 have), the renderer cannot play the patch, or the gain could not
    /// be computed.
    /// </returns>
    public bool NoteOn(int note, double velocity, Sound sound, object source)
    {
        ArgumentNullException.ThrowIfNull(sound);
        ArgumentNullException.ThrowIfNull(source);
        if (double.IsNaN(velocity))
        {
            throw new ArgumentOutOfRangeException(nameof(velocity), "The velocity is not a number.");
        }

        velocity = Math.Clamp(velocity, 0.0, 1.0);
        PreparedSound prepared = Prepare(sound);
        if (prepared.Renderer is null || prepared.Gain.IsFaulted || prepared.Gain.IsCanceled)
        {
            return false;
        }

        IOperatorMatrixVoice played;
        try
        {
            played = prepared.Renderer.StartVoice(sound.Patch, new VoiceRequest(440.0 * Math.Pow(2.0, (note - 69) / 12.0), note, velocity, SampleRate));
        }
        catch (PatchNotSupportedException)
        {
            return false;
        }

        var voice = new Voice(played, note, source, prepared, PlaybackLevel.VelocityVolume(sound.Patch, velocity), ChunkSize);
        lock (_lock)
        {
            int playing = 0;
            foreach (Voice other in _voices)
            {
                if (other.Held && other.Note == note && Equals(other.Source, source))
                {
                    other.Held = false;
                    other.ReleaseRequested = true;
                }

                if (!other.FadeRequested)
                {
                    playing++;
                }
            }

            for (int i = 0; i < _voices.Count && playing >= MaxVoices; i++)
            {
                if (!_voices[i].FadeRequested)
                {
                    _voices[i].FadeRequested = true;
                    _voices[i].Held = false;
                    playing--;
                }
            }

            voice.Held = true;
            _voices.Add(voice);
            Volatile.Write(ref _activeVoices, _voices.Count);
        }

        SoundingNotesChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Releases the note held from a source, from the next buffer but no earlier than <see cref="MinimumHoldSeconds"/>
    /// after it started sounding; a note-off without such a note is ignored.
    /// </summary>
    /// <param name="note">MIDI note.</param>
    /// <param name="source">Source of the note-on.</param>
    public void NoteOff(int note, object source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Release(v => v.Note == note && Equals(v.Source, source)))
        {
            SoundingNotesChanged?.Invoke();
        }
    }

    /// <summary>Releases every note held from a source, for example when its device is closed or lost.</summary>
    /// <param name="source">Source of the note-ons.</param>
    public void ReleaseAll(object source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Release(v => Equals(v.Source, source)))
        {
            SoundingNotesChanged?.Invoke();
        }
    }

    /// <summary>
    /// Drops the oldest voices that are not fading out already, each with a 5 ms fade; for an output that cannot keep
    /// up with the voices sounding.
    /// </summary>
    /// <param name="voices">Number of voices to drop; zero or less drops none.</param>
    public void Shed(int voices)
    {
        bool changed = false;
        lock (_lock)
        {
            for (int i = 0; i < _voices.Count && voices > 0; i++)
            {
                Voice voice = _voices[i];
                if (!voice.FadeRequested)
                {
                    voice.FadeRequested = true;
                    changed |= voice.Held;
                    voice.Held = false;
                    voices--;
                }
            }
        }

        if (changed)
        {
            SoundingNotesChanged?.Invoke();
        }
    }

    /// <summary>
    /// Renders the next samples of every voice and mixes them. Voices render in parallel, on this thread and on helper
    /// threads; this thread waits only for voices a helper is rendering. Never throws: a voice that fails ends.
    /// </summary>
    /// <param name="output">Receives the mix; overwritten, not mixed into. Any length.</param>
    public void Render(Span<float> output)
    {
        output.Clear();
        for (int position = 0; position < output.Length; position += ChunkSize)
        {
            RenderChunk(output.Slice(position, Math.Min(ChunkSize, output.Length - position)));
        }
    }

    private bool Release(Func<Voice, bool> matches)
    {
        bool changed = false;
        lock (_lock)
        {
            foreach (Voice voice in _voices)
            {
                if (voice.Held && matches(voice))
                {
                    voice.Held = false;
                    voice.ReleaseRequested = true;
                    changed = true;
                }
            }
        }

        return changed;
    }

    private void RenderChunk(Span<float> output)
    {
        int count = 0;
        lock (_lock)
        {
            if (_voices.Count == 0)
            {
                return;
            }

            if (_rendering.Length < _voices.Count)
            {
                _rendering = new Voice[2 * _voices.Count];
            }

            foreach (Voice voice in _voices)
            {
                if (voice.ReleaseRequested && voice.ReleaseAt == long.MaxValue)
                {
                    // From this buffer on, but not before the note has sounded for a moment: a tap within one
                    // buffer, or before the gain is known, still plays its attack and then its release.
                    voice.ReleaseAt = Math.Max(voice.Position, _minimumHoldSamples);
                }

                if (voice.FadeRequested && voice.FadeLeft < 0)
                {
                    voice.FadeLeft = _fadeSamples;
                }

                if (!voice.Ready)
                {
                    Task<float> gain = voice.Sound.Gain;
                    if (!gain.IsCompleted)
                    {
                        // Waits, held or released, until the gain is known; then it plays from its start.
                        continue;
                    }

                    if (!gain.IsCompletedSuccessfully)
                    {
                        voice.Ended = true;
                        continue;
                    }

                    voice.Level = gain.Result * voice.Volume;
                    voice.Ready = true;
                }

                _rendering[count++] = voice;
            }
        }

        _chunkLength = output.Length;
        VoiceRenderCrew.Run(_renderVoice, count);
        for (int k = 0; k < count; k++)
        {
            ReadOnlySpan<float> samples = _rendering[k].Buffer.AsSpan(0, output.Length);
            for (int i = 0; i < output.Length; i++)
            {
                output[i] += samples[i];
            }
        }

        Limit(output);
        Array.Clear(_rendering, 0, count);
        bool changed = false;
        lock (_lock)
        {
            for (int i = _voices.Count - 1; i >= 0; i--)
            {
                if (_voices[i].Ended)
                {
                    changed |= _voices[i].Held;
                    _voices.RemoveAt(i);
                }
            }

            Volatile.Write(ref _activeVoices, _voices.Count);
        }

        if (changed)
        {
            try
            {
                SoundingNotesChanged?.Invoke();
            }
            catch (Exception)
            {
                // The audio thread must survive a faulty handler.
            }
        }
    }

    /// <summary>Renders one voice into its buffer at its level, with its fade, and decides whether it has ended.</summary>
    private void RenderVoice(Voice voice, int length)
    {
        Span<float> buffer = voice.Buffer.AsSpan(0, length);
        try
        {
            // The gate closes at its sample; a voice keeps its partly rendered blocks, so splitting changes nothing else.
            int held = voice.Released ? 0 : (int)Math.Clamp(voice.ReleaseAt - voice.Position, 0, length);
            if (held > 0)
            {
                voice.Played.Render(buffer[..held]);
            }

            if (held < length)
            {
                if (!voice.Released)
                {
                    voice.Played.Release();
                    voice.Released = true;
                    if (voice.Sound.SamplesAfterGateOff is long lifetime)
                    {
                        voice.End = Math.Min(voice.End, voice.ReleaseAt + lifetime);
                    }
                }

                voice.Played.Render(buffer[held..]);
            }
        }
        catch (Exception)
        {
            // A voice that fails ends instead of taking the audio thread down.
            buffer.Clear();
            voice.Ended = true;
            return;
        }

        float level = voice.Level;
        long position = voice.Position;
        for (int i = 0; i < buffer.Length; i++, position++)
        {
            if (voice.Ended)
            {
                buffer[i] = 0f;
                continue;
            }

            // The lifetime ends with a fade whose last sample is at the end.
            if (voice.FadeLeft < 0 && position >= voice.End - _fadeSamples)
            {
                voice.FadeLeft = (int)Math.Max(0, voice.End - position);
            }

            float x = buffer[i] * level;
            if (Math.Abs(x) < SilenceLevel)
            {
                voice.SilentRun = Math.Min(voice.SilentRun + 1, _silenceSamples);
            }
            else
            {
                voice.SilentRun = 0;
                voice.HasSounded = true;
            }

            if (voice.FadeLeft >= 0)
            {
                if (voice.FadeLeft > 0)
                {
                    voice.FadeLeft--;
                    x *= voice.FadeLeft / (float)_fadeSamples;
                }
                else
                {
                    x = 0f;
                }

                voice.Ended = voice.FadeLeft == 0;
            }
            else if (voice.HasSounded && voice.SilentRun >= _silenceSamples && (voice.Released || position >= voice.Sound.SilentFrom))
            {
                // Silent long enough after the note-off or after a one-shot envelope; a silent start does not count.
                voice.Ended = true;
            }

            buffer[i] = x;
        }

        voice.Position = position;
    }

    /// <summary>Leaves samples up to the threshold as they are and bends louder ones towards 1.</summary>
    private static void Limit(Span<float> samples)
    {
        const float headroom = 1f - LimiterThreshold;
        for (int i = 0; i < samples.Length; i++)
        {
            float magnitude = Math.Abs(samples[i]);
            if (magnitude > LimiterThreshold)
            {
                samples[i] = MathF.CopySign(LimiterThreshold + (headroom * MathF.Tanh((magnitude - LimiterThreshold) / headroom)), samples[i]);
            }
        }
    }

    private PreparedSound Prepare(Sound sound)
    {
        lock (_soundsLock)
        {
            if (_sounds.TryGetValue(sound, out PreparedSound? known))
            {
                return known;
            }

            PreparedSound prepared = PrepareNew(sound);
            if (_sounds.Count >= KeptSounds)
            {
                _sounds.Remove(_soundOrder.Dequeue());
            }

            _sounds.Add(sound, prepared);
            _soundOrder.Enqueue(sound);
            return prepared;
        }
    }

    private PreparedSound PrepareNew(Sound sound)
    {
        IVoiceRenderer? renderer;
        try
        {
            renderer = SynthesisBackends.RendererFor(sound.Profile) as IVoiceRenderer;
        }
        catch (NotSupportedException)
        {
            renderer = null;
        }

        if (renderer is null)
        {
            return new PreparedSound(null, Task.FromResult(1f), null, 0, 0);
        }

        long? afterGateOff = PatchLifetime.SecondsAfterGateOff(sound.Patch) is double seconds ? Samples(seconds) : null;
        long afterStart = afterGateOff is null ? Samples(PatchLifetime.SecondsAfterStart(sound.Patch)) : long.MaxValue;
        return new PreparedSound(
            renderer,
            ComputeGainOnItsOwnThread(sound),
            afterGateOff,
            afterStart,
            afterGateOff is null ? afterStart - _silenceSamples : long.MaxValue);
    }

    /// <summary>
    /// Computes the gain of a sound on a thread of its own, above normal priority: not on the thread pool, which a
    /// running search keeps busy for as long as it runs, so that the first note of a newly shown sound does not wait
    /// for it. Continuations of the task run asynchronously, never on that thread.
    /// </summary>
    private static Task<float> ComputeGainOnItsOwnThread(Sound sound)
    {
        var gain = new TaskCompletionSource<float>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                gain.SetResult(sound.ComputeGain());
            }
            catch (Exception e)
            {
                // Reported through the task: PrepareAsync rethrows it, NoteOn declines the sound.
                gain.SetException(e);
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "HearSay sound gain",
        };
        thread.Start();
        return gain.Task;
    }

    private long Samples(double seconds) => (long)Math.Round(seconds * SampleRate);

    /// <summary>What every note of a sound shares.</summary>
    /// <param name="Renderer">Renderer that starts the voices; null when the profile has none.</param>
    /// <param name="Gain">Gain of the sound, computed off the calling thread.</param>
    /// <param name="SamplesAfterGateOff">Lifetime after the gate closes for a sustaining patch; null for a one-shot one.</param>
    /// <param name="SamplesAfterStart">Lifetime from the start for a one-shot patch; <see cref="long.MaxValue"/> for a sustaining one.</param>
    /// <param name="SilentFrom">
    /// Sample from which silence ends a voice that is still held: the end of the envelopes of a one-shot patch, 50 ms
    /// before its lifetime; <see cref="long.MaxValue"/> for a sustaining one.
    /// </param>
    private sealed record PreparedSound(IVoiceRenderer? Renderer, Task<float> Gain, long? SamplesAfterGateOff, long SamplesAfterStart, long SilentFrom);

    /// <summary>One note: the voice that plays it and the state of its end.</summary>
    private sealed class Voice(IOperatorMatrixVoice played, int note, object source, PreparedSound sound, float volume, int chunkSize)
    {
        public IOperatorMatrixVoice Played { get; } = played;

        public int Note { get; } = note;

        public object Source { get; } = source;

        public PreparedSound Sound { get; } = sound;

        /// <summary>Velocity as volume, or 1 for a patch that plays the velocity itself.</summary>
        public float Volume { get; } = volume;

        public float[] Buffer { get; } = new float[chunkSize];

        // Guarded by the mixer's lock; set by the note methods.

        /// <summary>Note-on received, no note-off yet, neither taken nor dropped nor ended.</summary>
        public bool Held { get; set; }

        public bool ReleaseRequested { get; set; }

        public bool FadeRequested { get; set; }

        // Owned by the thread that renders.

        /// <summary>Sample at which the gate closes; <see cref="long.MaxValue"/> until the note-off is picked up.</summary>
        public long ReleaseAt { get; set; } = long.MaxValue;

        public bool Released { get; set; }

        public bool Ready { get; set; }

        public float Level { get; set; }

        /// <summary>Samples rendered.</summary>
        public long Position { get; set; }

        /// <summary>Sample at which the voice is over at the latest.</summary>
        public long End { get; set; } = sound.SamplesAfterStart;

        /// <summary>Samples left in the fade-out, the last of them silent; negative when not fading.</summary>
        public int FadeLeft { get; set; } = -1;

        public int SilentRun { get; set; }

        public bool HasSounded { get; set; }

        public bool Ended { get; set; }
    }
}
