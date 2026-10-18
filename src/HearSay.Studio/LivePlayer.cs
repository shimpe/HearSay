using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using HearSay.Playing;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay.Studio;

/// <summary>What became of a note-on played on the sound shown at that moment, for the caption and the status line.</summary>
/// <param name="Note">MIDI note.</param>
/// <param name="Velocity">Key velocity, 0..1.</param>
/// <param name="Sound">The sound it was played with; null when no sound was shown.</param>
/// <param name="Played">True when it sounds; false when there was no sound or the sound cannot be played live.</param>
public sealed record PlayedNote(int Note, double Velocity, Sound? Sound, bool Played);

/// <summary>
/// Plays the Studio like a synth, one per window: held, polyphonic notes from the on-screen keyboards and from a MIDI
/// input device, on the sound shown at the moment each note starts. Owns the audio device, the voice mixer, the live
/// output and the MIDI input, and disposes them in the order they need.
/// </summary>
/// <remarks>
/// <para>
/// Notes from the MIDI device go to the mixer on the MIDI library's thread, without waiting for the interface thread;
/// the sound they play is the one <see cref="SoundChanged"/> last took from <see cref="CurrentSound"/>. What the
/// interface shows (<see cref="SoundingNotes"/>, <see cref="NotePlayed"/>, the device list) changes on the interface
/// thread only.
/// </para>
/// <para>
/// At start a background thread renders a short voice of every live profile into nothing, so that the first chord
/// does not run the voice code before the JIT has optimised it, which would make the output fall behind and drop
/// voices.
/// </para>
/// </remarks>
public sealed class LivePlayer : INotifyPropertyChanged, IDisposable
{
    /// <summary>The first entry of <see cref="MidiDevices"/>: no MIDI input.</summary>
    public const string NoDevice = "none";

    /// <summary>
    /// How long after a MIDI device is closed its notes are released a second time, to catch a note-on that was on
    /// its way from the device when it closed.
    /// </summary>
    private static readonly TimeSpan LateNoteMargin = TimeSpan.FromMilliseconds(250);

    private readonly string? _settingsPath;
    private readonly object _lateTimers = new();
    private readonly List<Timer> _pendingReleases = [];
    private Sound? _current;
    private string? _openName;
    private object? _midiSource;
    private Action<NoteEvent>? _midiHandler;
    private IReadOnlyCollection<int> _soundingNotes = [];
    private int _soundingPosted;
    private string? _shownWarning;
    private bool _disposed;

    /// <summary>
    /// Opens the default audio device and the live output on it, starts the warm-up of the voice code, and opens the
    /// remembered MIDI device when it is connected (no device, without an error, when it is not).
    /// </summary>
    /// <param name="settingsPath">Where the Studio settings are kept; the default location when null.</param>
    public LivePlayer(string? settingsPath = null)
    {
        _settingsPath = settingsPath;
        Device = new AudioPlayer();
        Mixer = new VoiceMixer(LiveAudioOutput.SampleRate);
        Output = new LiveAudioOutput(Device, Mixer);
        Mixer.SoundingNotesChanged += OnSoundingNotesChanged;
        Midi = new MidiInput();
        Midi.DeviceLost += () => Dispatcher.UIThread.Post(OnDeviceLost);
        Midi.DevicesChanged += () => Dispatcher.UIThread.Post(RefreshMidiDevices);
        StartWarmUp();

        string? remembered = StudioSettings.Load(_settingsPath).MidiInput;
        IReadOnlyList<string> names = MidiInput.DeviceNames();
        if (remembered is not null && names.Contains(remembered) && OpenMidi(remembered))
        {
            _openName = remembered;
        }

        UpdateDeviceList(names);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised on the interface thread after a note-on on the shown sound, from the keyboard or the MIDI device.</summary>
    public event EventHandler<PlayedNote>? NotePlayed;

    /// <summary>The audio device, shared by the live notes and the one-shot playback of target files.</summary>
    public AudioPlayer Device { get; }

    /// <summary>The voices of the live notes.</summary>
    public VoiceMixer Mixer { get; }

    /// <summary>The streaming output that plays the mixer.</summary>
    public LiveAudioOutput Output { get; }

    /// <summary>The MIDI input device.</summary>
    public MidiInput Midi { get; }

    /// <summary>True when live notes can be heard: an audio device is open and the streaming source could be made.</summary>
    public bool CanPlay => Output.IsAvailable;

    /// <summary>
    /// The sound a new note plays: the sound of the visible tab, or null when it has none. Read on the interface
    /// thread, by a note from the on-screen keyboard and by <see cref="SoundChanged"/>.
    /// </summary>
    public Func<Sound?>? CurrentSound { get; set; }

    /// <summary>The notes held down now, from the keyboards or the MIDI device; changes on the interface thread.</summary>
    public IReadOnlyCollection<int> SoundingNotes
    {
        get => _soundingNotes;
        private set
        {
            _soundingNotes = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Choices of the MIDI in selector: <see cref="NoDevice"/>, then the input devices by name.</summary>
    public ObservableCollection<string> MidiDevices { get; } = [NoDevice];

    /// <summary>
    /// The MIDI device the notes come from, by name, or <see cref="NoDevice"/>. Choosing one opens it, releases the
    /// notes of the one before, and remembers the choice for the next session; a device that cannot be opened leaves
    /// <see cref="NoDevice"/> and says so through <see cref="StatusChanged"/>. Null is ignored. Its change is
    /// announced also when the value stays the same (a device that could not be opened), so that a selector that
    /// showed the device goes back.
    /// </summary>
    public string? SelectedMidiDevice
    {
        get => _openName ?? NoDevice;
        set
        {
            if (value is null || value == SelectedMidiDevice)
            {
                return;
            }

            CloseMidi();
            _openName = null;
            string? remembered = null;
            if (value != NoDevice)
            {
                if (OpenMidi(value))
                {
                    _openName = value;
                    remembered = value;
                    StatusChanged?.Invoke(this, $"Playing from the MIDI device {value}.");
                }
                else
                {
                    StatusChanged?.Invoke(this, $"The MIDI device {value} could not be opened.");
                }
            }

            Remember(remembered);
            OnPropertyChanged();
        }
    }

    /// <summary>Raised on the interface thread with a line for the status bar: a device lost or not opened, a warning of the output.</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>
    /// Takes the sound a new note plays from <see cref="CurrentSound"/> and computes its gain ahead of the first note;
    /// call it on the interface thread whenever the shown sound or the visible tab changes.
    /// </summary>
    public void SoundChanged()
    {
        Sound? sound = CurrentSound?.Invoke();
        Volatile.Write(ref _current, sound);
        Prepare(sound);
    }

    /// <summary>Computes the gain of a sound ahead of its first note, off the interface thread; nothing for null.</summary>
    /// <param name="sound">Sound, for example one shown in a tab that is not visible.</param>
    public void Prepare(Sound? sound)
    {
        if (sound is null || !CanPlay)
        {
            return;
        }

        // A sound whose reference note cannot be rendered is declined by NoteOn, which the status line then says.
        _ = Mixer.PrepareAsync(sound).ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    /// <summary>Starts a note on the sound shown now, and raises <see cref="NotePlayed"/>; nothing without an audio output.</summary>
    /// <param name="note">MIDI note.</param>
    /// <param name="velocity">Key velocity, 0..1.</param>
    /// <param name="source">Where the note comes from, for <see cref="NoteOff"/>.</param>
    /// <returns>True when the note sounds.</returns>
    public bool NoteOn(int note, double velocity, object source)
    {
        if (!CanPlay)
        {
            return false;
        }

        Sound? sound;
        if (Dispatcher.UIThread.CheckAccess())
        {
            sound = CurrentSound?.Invoke();
            Volatile.Write(ref _current, sound);
        }
        else
        {
            sound = Volatile.Read(ref _current);
        }

        bool played = sound is not null && Play(sound, note, velocity, source);
        Report(new PlayedNote(note, velocity, sound, played));
        return played;
    }

    /// <summary>Starts a note on a given sound, without <see cref="NotePlayed"/>; nothing without an audio output.</summary>
    /// <param name="sound">Sound the note plays.</param>
    /// <param name="note">MIDI note.</param>
    /// <param name="velocity">Key velocity, 0..1.</param>
    /// <param name="source">Where the note comes from, for <see cref="NoteOff"/>.</param>
    /// <returns>True when the note sounds; false when there is no output or the sound cannot be played live.</returns>
    public bool Play(Sound sound, int note, double velocity, object source)
    {
        ArgumentNullException.ThrowIfNull(sound);
        if (!CanPlay)
        {
            return false;
        }

        try
        {
            return Mixer.NoteOn(note, velocity, sound, source);
        }
        catch (Exception)
        {
            // A voice that cannot start is a note that does not play, said on the status line; on the MIDI thread
            // nobody could catch it, and on the interface thread it must not take the window down.
            return false;
        }
    }

    /// <summary>Releases a note started from a source; a note-off without such a note is ignored.</summary>
    /// <param name="note">MIDI note.</param>
    /// <param name="source">Source of its note-on.</param>
    public void NoteOff(int note, object source) => Mixer.NoteOff(note, source);

    /// <summary>Why a sound does not play live, for the status line.</summary>
    /// <param name="sound">Sound that <see cref="Play"/> declined.</param>
    public static string CannotPlay(Sound sound)
    {
        ArgumentNullException.ThrowIfNull(sound);
        bool hasVoices;
        try
        {
            hasVoices = SynthesisBackends.RendererFor(sound.Profile) is IVoiceRenderer;
        }
        catch (NotSupportedException)
        {
            hasVoices = false;
        }

        return hasVoices
            ? "This sound cannot be played live: its renderer cannot play the patch, or its level could not be computed."
            : "This sound's profile cannot be played live.";
    }

    /// <summary>
    /// The output's warning (<see cref="LiveAudioOutput.Warning"/>) when it is set and has not been shown yet; null
    /// otherwise.
    /// </summary>
    public string? NewWarning()
    {
        string? warning = Output.Warning;
        if (warning is null || warning == _shownWarning)
        {
            return null;
        }

        _shownWarning = warning;
        return warning;
    }

    /// <summary>Reads the MIDI input devices again, for example when the selector opens.</summary>
    public void RefreshMidiDevices() => UpdateDeviceList(MidiInput.DeviceNames());

    /// <summary>Stops the live output, closes the MIDI device and then the audio device.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_lateTimers)
        {
            foreach (Timer timer in _pendingReleases)
            {
                timer.Dispose();
            }

            _pendingReleases.Clear();
        }

        Mixer.SoundingNotesChanged -= OnSoundingNotesChanged;

        // The output plays in the device's context: it goes first.
        Output.Dispose();
        Midi.Dispose();
        Device.Dispose();
    }

    /// <summary>
    /// A patch per live profile that runs the code a chord runs: six operators of the profile's waveforms in a chain,
    /// self-feedback where the profile has it, and an envelope with a sustain point.
    /// </summary>
    internal static OperatorMatrixPatch WarmUpPatch(SynthesisProfile profile)
    {
        OperatorWaveform[] waveforms =
        [
            .. Enum.GetValues<OperatorWaveform>().Where(w => w is not (OperatorWaveform.Additive or OperatorWaveform.Wavetable)
                && profile.Capabilities.SupportedWaveforms.Contains(w)),
        ];
        OperatorMatrixPatch patch = OperatorMatrixPatch.Silent(6);
        for (int i = 0; i < 6; i++)
        {
            OperatorPatch op = OperatorPatch.Sine(OperatorFrequency.AtRatio(i + 1), OperatorEnvelope.Adsr(0.01, 0.1, 0.7, 0.2));
            patch = patch.WithOperator(i, waveforms.Length > 0 ? op.WithWaveform(waveforms[i % waveforms.Length]) : op);
            if (i > 0)
            {
                patch = patch.WithEdge(new ModulationEdge(i, i - 1, true, 1.0));
            }
        }

        if (profile.Capabilities.SupportsSelfFeedback)
        {
            patch = patch.WithEdge(new ModulationEdge(5, 5, true, 0.5));
        }

        return patch.WithOutputLevel(0, 1.0);
    }

    /// <summary>
    /// Renders a held and released voice of every live profile into a scratch buffer, on a background thread: 0.1 s
    /// of audio per profile, every 100 ms for 2 s.
    /// </summary>
    /// <remarks>
    /// One pass is not enough: the runtime starts counting the calls of a method only once no new code has been
    /// compiled for 100 ms, and optimises it after 30 calls counted then, twice with profile-guided optimisation. Spread
    /// over 2 s, the first chord of 16 Nome6 voices renders in a tenth of the time (measured in an optimised build:
    /// 60 buffers in 25 ms instead of 250 ms, none over the 5.3 ms a buffer lasts, against 20 over without it). It
    /// costs a few milliseconds of one core per pass.
    /// </remarks>
    private static void StartWarmUp()
    {
        var thread = new Thread(WarmUp) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "HearSay voice warm-up" };
        thread.Start();
    }

    private static void WarmUp()
    {
        float[] buffer = new float[LiveAudioOutput.BufferSamples];
        SynthesisProfile[] profiles = [SynthesisProfiles.SuperColliderFm7, SynthesisProfiles.SuperColliderFmx6, SynthesisProfiles.SuperColliderNome6];
        for (int pass = 0; pass < 20; pass++)
        {
            foreach (SynthesisProfile profile in profiles)
            {
                try
                {
                    if (SynthesisBackends.RendererFor(profile) is not IVoiceRenderer renderer)
                    {
                        continue;
                    }

                    // About 0.1 s of a held note and its release, in the buffers the output asks for.
                    IOperatorMatrixVoice voice = renderer.StartVoice(WarmUpPatch(profile), new VoiceRequest(220.0, 57, 0.8, LiveAudioOutput.SampleRate));
                    int buffers = LiveAudioOutput.SampleRate / 10 / buffer.Length;
                    for (int b = 0; b < buffers; b++)
                    {
                        if (b == buffers / 2)
                        {
                            voice.Release();
                        }

                        voice.Render(buffer);
                    }
                }
                catch (Exception)
                {
                    // Only the first chord is slower without it.
                }
            }

            Thread.Sleep(100);
        }
    }

    private bool OpenMidi(string name)
    {
        var source = new object();
        Action<NoteEvent> handler = e => OnMidiNote(e, source);
        Midi.Note += handler;
        if (!Midi.Open(name))
        {
            Midi.Note -= handler;
            return false;
        }

        _midiSource = source;
        _midiHandler = handler;
        return true;
    }

    /// <summary>Closes the MIDI device and releases its notes, now and once more a little later for a note that was on its way.</summary>
    private void CloseMidi()
    {
        Midi.Close();
        RetireMidiSource();
    }

    private void RetireMidiSource()
    {
        if (_midiHandler is { } handler)
        {
            Midi.Note -= handler;
        }

        if (_midiSource is not { } source)
        {
            return;
        }

        _midiHandler = null;
        _midiSource = null;
        Mixer.ReleaseAll(source);
        lock (_lateTimers)
        {
            if (_disposed)
            {
                return;
            }

            Timer? timer = null;
            timer = new Timer(_ =>
            {
                Mixer.ReleaseAll(source);
                lock (_lateTimers)
                {
                    _pendingReleases.Remove(timer!);
                }

                timer!.Dispose();
            });
            _pendingReleases.Add(timer);
            timer.Change(LateNoteMargin, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>A note from the MIDI device, on the library's thread.</summary>
    private void OnMidiNote(NoteEvent e, object source)
    {
        if (e.IsOn)
        {
            NoteOn(e.Note, e.Velocity, source);
        }
        else
        {
            Mixer.NoteOff(e.Note, source);
        }
    }

    /// <summary>The open device is gone: its notes are released and the selector says none; the device stays remembered.</summary>
    private void OnDeviceLost()
    {
        if (_openName is not { } lost || Midi.OpenDevice == lost)
        {
            return;
        }

        RetireMidiSource();
        _openName = null;
        OnPropertyChanged(nameof(SelectedMidiDevice));
        StatusChanged?.Invoke(this, $"The MIDI device {lost} was disconnected.");
        RefreshMidiDevices();
    }

    /// <summary>Brings <see cref="MidiDevices"/> up to date in place, so that the selector keeps its selection.</summary>
    private void UpdateDeviceList(IReadOnlyList<string> names)
    {
        List<string> wanted = [NoDevice, .. names.Where(static n => n != NoDevice).Distinct().Order(StringComparer.CurrentCultureIgnoreCase)];
        if (_openName is { } open && !wanted.Contains(open))
        {
            // Shown until it is reported lost, so that the selector does not go blank meanwhile.
            wanted.Add(open);
        }

        for (int i = MidiDevices.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(MidiDevices[i]))
            {
                MidiDevices.RemoveAt(i);
            }
        }

        for (int i = 0; i < wanted.Count; i++)
        {
            if (i >= MidiDevices.Count || MidiDevices[i] != wanted[i])
            {
                int at = MidiDevices.IndexOf(wanted[i]);
                if (at >= 0)
                {
                    MidiDevices.Move(at, i);
                }
                else
                {
                    MidiDevices.Insert(i, wanted[i]);
                }
            }
        }

        // A selector may have lost its selection while the list changed.
        OnPropertyChanged(nameof(SelectedMidiDevice));
    }

    private void Remember(string? device)
    {
        try
        {
            new StudioSettings(device).Save(_settingsPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            StatusChanged?.Invoke(this, "The MIDI device could not be remembered: " + e.Message);
        }
    }

    /// <summary>Hands a note-on to the interface thread, at once when it is on it.</summary>
    private void Report(PlayedNote played)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            NotePlayed?.Invoke(this, played);
        }
        else
        {
            Dispatcher.UIThread.Post(() => NotePlayed?.Invoke(this, played));
        }
    }

    /// <summary>Raised by the mixer on any thread; the interface reads the notes once per burst of changes.</summary>
    private void OnSoundingNotesChanged()
    {
        if (Interlocked.Exchange(ref _soundingPosted, 1) == 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Volatile.Write(ref _soundingPosted, 0);
                SoundingNotes = Mixer.SoundingNotes.ToArray();
                if (NewWarning() is { } warning)
                {
                    StatusChanged?.Invoke(this, warning);
                }
            });
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
