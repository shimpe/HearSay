using System.Diagnostics;
using System.Runtime.InteropServices;
using HearSay.Playing;
using Silk.NET.OpenAL;

namespace HearSay.Studio;

/// <summary>
/// Plays the live notes of a <see cref="VoiceMixer"/>: a streaming OpenAL source on the device and context of an
/// <see cref="AudioPlayer"/>, beside its one-shot source, fed with <see cref="BufferCount"/> queued buffers of
/// <see cref="BufferSamples"/> mono 16-bit samples at <see cref="SampleRate"/> Hz (5.3 ms each), or more for a device
/// that takes more at a time, by a thread of its own at above-normal priority.
/// </summary>
/// <remarks>
/// <para>
/// While voices sound, the thread looks at the source about every millisecond (on Windows with a high-resolution waitable
/// timer, as the default timer tick there is 15.6 ms) and renders a buffer for every one OpenAL has played. When the
/// mixer has no voice left it renders nothing more: it lets the queue play out, and then waits for a note without
/// spending any time: woken by <see cref="VoiceMixer.SoundingNotesChanged"/>, which a note-on raises, and looking at
/// <see cref="VoiceMixer.ActiveVoices"/> every 50 ms as well, in case another handler of that event threw before this
/// one ran.
/// </para>
/// <para>
/// Only a real underrun, the source having run dry and stopped while voices were fed, counts in
/// <see cref="Underruns"/> and restarts the source after refilling it; a queue that is merely short is refilled at once.
/// An underrun doubles the queue, up to <see cref="MaxBufferCount"/> buffers, so that a device that takes more audio at a
/// time than the queue holds is fed from then on; only a queue that runs dry at its longest drops the oldest voice
/// (<see cref="VoiceMixer.Shed"/>), as the mixer then cannot keep up. A search running on every core keeps its full
/// speed, and the notes may stutter then. The thread never throws: an error is counted in <see cref="Errors"/>, and the
/// thread stops the source and starts again with the next note.
/// </para>
/// <para>
/// The device adds its own buffering: OpenAL Soft mixes in updates of the period_size in the shipped alsoft.ini
/// (256 samples asked for; Windows mixes every 10 ms in shared mode, and OpenAL Soft keeps 22 ms there), and without that
/// file in updates of 960 samples, nearly the whole queue; the queue then starts long enough for twice an update. The
/// sound server may take more still: on Linux a JACK client such as the SuperCollider server makes PipeWire or JACK ask
/// for 1024 samples or more at a time. <see cref="Warning"/> says so when the queue starts or grows longer than
/// <see cref="BufferCount"/> buffers, or when the first second of live notes runs dry <see cref="StormUnderruns"/> times
/// or more at its longest.
/// </para>
/// <para>Dispose it before the <see cref="AudioPlayer"/>, whose context it plays in.</para>
/// </remarks>
public sealed unsafe class LiveAudioOutput : IDisposable
{
    /// <summary>Sample rate of the live notes, in hertz; the mixer must render at it.</summary>
    public const int SampleRate = 48000;

    /// <summary>Samples per buffer.</summary>
    public const int BufferSamples = 256;

    /// <summary>Buffers queued on the source at first, when the device mixes no more than half of them at a time.</summary>
    public const int BufferCount = 4;

    /// <summary>Most buffers queued on the source: 8192 samples, 171 ms.</summary>
    public const int MaxBufferCount = 32;

    /// <summary>Buffers in the first second of live notes, within which <see cref="StormUnderruns"/> raises <see cref="Warning"/>.</summary>
    private const int FirstSecondBuffers = SampleRate / BufferSamples;

    /// <summary>Underruns within the first second of live notes that mean the device does not suit the queue.</summary>
    private const int StormUnderruns = 5;

    /// <summary>AL_SEC_OFFSET_LATENCY_SOFT: the playing offset and the device latency, in seconds, as two doubles.</summary>
    private const int SecondOffsetLatency = 0x1201;

    /// <summary>Weight of a new latency measurement in the running average: about the last quarter second counts.</summary>
    private const double LatencyWeight = 1.0 / 48;

    private static readonly TimeSpan IdleCheck = TimeSpan.FromMilliseconds(50);

    /// <summary>How often the thread asks whether the audio device is still there, idle or playing.</summary>
    private static readonly long ConnectionCheckTicks = Stopwatch.Frequency;

    private readonly AL? _al;
    private readonly AudioPlayer _device;
    private readonly VoiceMixer _mixer;
    private readonly uint _source;
    private readonly uint[] _buffers = new uint[MaxBufferCount];
    private readonly delegate* unmanaged[Cdecl]<uint, int, double*, void> _getSourceDoubles;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread? _thread;
    private readonly Action _onSoundingNotesChanged;

    // Owned by the feeding thread.
    private readonly Stack<uint> _free = new(MaxBufferCount);
    private readonly float[] _mix = new float[BufferSamples];
    private readonly short[] _pcm = new short[BufferSamples];

    // Owned by the feeding thread: the buffers in use, the first ones of _buffers.
    private int _bufferCount = BufferCount;

    private volatile bool _stopping;
    private volatile bool _deviceLost;
    private long _nextConnectionCheck;
    private double _latencySeconds;
    private int _underruns;
    private int _errors;
    private long _longestRenderTicks;
    private volatile string? _warning;
    private int _disposed;

    // Owned by the feeding thread: buffers queued and underruns, both counted up to the end of the first second.
    private int _firstSecondBuffers;
    private int _firstSecondUnderruns;

    /// <summary>
    /// Creates the streaming source on the device of a player and starts the thread that feeds it. Without an audio
    /// device nothing plays and <see cref="IsAvailable"/> is false.
    /// </summary>
    /// <param name="device">Player whose device and context the source shares.</param>
    /// <param name="mixer">Mixer whose voices are played; it must render at <see cref="SampleRate"/>.</param>
    public LiveAudioOutput(AudioPlayer device, VoiceMixer mixer)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(mixer);
        if (mixer.SampleRate != SampleRate)
        {
            throw new ArgumentException($"The mixer renders at {mixer.SampleRate} Hz; the live output plays {SampleRate} Hz.", nameof(mixer));
        }

        _mixer = mixer;
        _device = device;
        _onSoundingNotesChanged = Wake;
        _al = device.Api;
        if (_al is null)
        {
            return;
        }

        _al.GetError();
        _source = _al.GenSource();
        fixed (uint* buffers = _buffers)
        {
            _al.GenBuffers(MaxBufferCount, buffers);
        }

        if (_al.GetError() != AudioError.NoError)
        {
            ReleaseSource();
            _al = null;
            return;
        }

        if (_al.IsExtensionPresent("AL_SOFT_source_latency"))
        {
            _getSourceDoubles = (delegate* unmanaged[Cdecl]<uint, int, double*, void>)_al.GetProcAddress("alGetSourcedvSOFT");
        }

        // A refill must fit between two updates of the device: the queue holds at least twice an update.
        while (_bufferCount < MaxBufferCount && _bufferCount * BufferSamples < 2 * device.DeviceUpdateSamples)
        {
            _bufferCount *= 2;
        }

        _latencySeconds = QueueSeconds;
        Rewind();
        if (_bufferCount > BufferCount)
        {
            _warning = $"The audio device mixes {device.DeviceUpdateSamples} samples at a time, so the live notes wait {1000 * QueueSeconds:F0} ms in their queue: OpenAL Soft did not take the settings shipped in alsoft.ini.";
        }

        IsAvailable = true;
        _mixer.SoundingNotesChanged += _onSoundingNotesChanged;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "HearSay live audio",
        };
        _thread.Start();
    }

    /// <summary>True when the live notes play: an audio device is open and the source could be created.</summary>
    public bool IsAvailable { get; }

    /// <summary>
    /// Time from a note-on to its sound at the most, in milliseconds: the audio queued on the source just after a
    /// refill, which a note started then waits for (until the next refill renders it, and then behind what is left),
    /// plus the latency OpenAL reports for the device (<c>AL_SOFT_source_latency</c>, where available). A note-on just
    /// before a refill sounds sooner, by up to the time between refills (10 ms on Windows, which mixes every 10 ms). A
    /// running average over about a quarter of a second; until the first note, the length of the queue alone.
    /// </summary>
    public double MeasuredLatencyMilliseconds => 1000 * Volatile.Read(ref _latencySeconds);

    /// <summary>
    /// Number of times the source ran dry while voices were fed, since <see cref="ResetStatistics"/>: the queue grew, or,
    /// at its longest, a voice was dropped.
    /// </summary>
    public int Underruns => Volatile.Read(ref _underruns);

    /// <summary>Number of errors the feeding thread caught and recovered from, since <see cref="ResetStatistics"/>.</summary>
    public int Errors => Volatile.Read(ref _errors);

    /// <summary>Longest time the mixer took to render one buffer, in milliseconds, since <see cref="ResetStatistics"/>.</summary>
    public double LongestRenderMilliseconds => Volatile.Read(ref _longestRenderTicks) * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// Why the live notes wait longer than usual or stutter, for the status line; null while nothing is known to be
    /// wrong. Set when the output is created, if the device mixes more than half the first queue at a time (OpenAL Soft
    /// did not read the shipped alsoft.ini), whenever the queue grows, and during the first second of live notes, if the
    /// source runs dry 5 times or more in it at the longest queue. Once set it stays until another replaces it.
    /// </summary>
    public string? Warning => _warning;

    /// <summary>True once the audio device is gone; nothing more is heard from this output.</summary>
    public bool IsDeviceLost => _deviceLost;

    /// <summary>
    /// Raised once, on the audio thread, when the audio device is gone (unplugged headphones or interface). The voices
    /// that still sound are then rendered to nowhere until they end, so that their keys go dark.
    /// </summary>
    public event Action? DeviceLost;

    /// <summary>Sets <see cref="Underruns"/>, <see cref="Errors"/> and <see cref="LongestRenderMilliseconds"/> back to zero.</summary>
    public void ResetStatistics()
    {
        Interlocked.Exchange(ref _underruns, 0);
        Interlocked.Exchange(ref _errors, 0);
        Interlocked.Exchange(ref _longestRenderTicks, 0);
    }

    /// <summary>Stops the thread and releases the source and its buffers.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _mixer.SoundingNotesChanged -= _onSoundingNotesChanged;
        _stopping = true;
        Wake();
        _thread?.Join();
        ReleaseSource();
        _wake.Dispose();
    }

    private void Wake()
    {
        try
        {
            _wake.Set();
        }
        catch (ObjectDisposedException)
        {
            // A note-on that raced with Dispose.
        }
    }

    private void ReleaseSource()
    {
        if (_al is null)
        {
            return;
        }

        _al.SourceStop(_source);
        _al.SetSourceProperty(_source, SourceInteger.Buffer, 0);
        _al.DeleteSource(_source);
        fixed (uint* buffers = _buffers)
        {
            _al.DeleteBuffers(MaxBufferCount, buffers);
        }
    }

    /// <summary>Length of the queue, in seconds.</summary>
    private double QueueSeconds => _bufferCount * BufferSamples / (double)SampleRate;

    private void Run()
    {
        using var pacer = new Pacer();
        while (!_stopping)
        {
            try
            {
                if (_deviceLost)
                {
                    // Nothing can be heard: the voices still sounding play on unheard until they end.
                    if (_mixer.ActiveVoices > 0)
                    {
                        _mixer.Render(_mix);
                        pacer.Wait();
                    }
                    else
                    {
                        _wake.WaitOne(IdleCheck);
                    }

                    continue;
                }

                if (_mixer.ActiveVoices == 0)
                {
                    CheckConnection(now: false);
                    _wake.WaitOne(IdleCheck);
                    continue;
                }

                Stream(pacer);
            }
            catch (Exception)
            {
                // The audio thread must survive whatever goes wrong; it starts again with the next note.
                Interlocked.Increment(ref _errors);
                Rewind();
                Thread.Sleep(10);
            }
        }

        Rewind();
    }

    /// <summary>Plays while voices sound; returns once they are over and the queue has played out.</summary>
    private void Stream(Pacer pacer)
    {
        AL al = _al!;
        while (_free.Count > 0)
        {
            QueueNext(al);
        }

        al.SourcePlay(_source);

        // False while the queue plays out after the last voice: the source stopping then is no underrun.
        bool feeding = true;
        while (!_stopping)
        {
            pacer.Wait();

            // The state before reclaiming: a source seen stopped has played every buffer, and all of them are reclaimed
            // below, so that a restart cannot play one again.
            al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
            bool playing = state == (int)SourceState.Playing;

            // A source that stopped while fed may mean the device is gone: asked at once then, else once a second.
            if (CheckConnection(now: feeding && !playing))
            {
                Rewind();
                return;
            }

            Reclaim(al);
            int queued = _bufferCount - _free.Count;
            if (_mixer.ActiveVoices == 0)
            {
                if (queued == 0 && !playing)
                {
                    return;
                }

                feeding = false;
                continue;
            }

            if (_free.Count == 0 && playing)
            {
                continue;
            }

            if (feeding && !playing)
            {
                Underrun();
            }

            feeding = true;
            while (_free.Count > 0)
            {
                QueueNext(al);
            }

            if (!playing)
            {
                al.SourcePlay(_source);
            }

            MeasureLatency(al);
        }
    }

    /// <summary>
    /// Asks the device whether it is still there, at most once a second unless <paramref name="now"/>; the first time it
    /// is not, marks the output lost and raises <see cref="DeviceLost"/>. True when the device is gone.
    /// </summary>
    private bool CheckConnection(bool now)
    {
        if (_deviceLost)
        {
            return true;
        }

        long time = Stopwatch.GetTimestamp();
        if (!now && time < _nextConnectionCheck)
        {
            return false;
        }

        _nextConnectionCheck = time + ConnectionCheckTicks;
        if (_device.IsConnected)
        {
            return false;
        }

        _deviceLost = true;
        try
        {
            DeviceLost?.Invoke();
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _errors);
        }

        return true;
    }

    /// <summary>
    /// The source ran dry while voices were fed: doubles the queue, for a device that takes more at a time than it held;
    /// at its longest, the mixer cannot keep up, so drops the oldest voice, and warns when that happens again and again
    /// in the first second of live notes.
    /// </summary>
    private void Underrun()
    {
        Interlocked.Increment(ref _underruns);
        if (_bufferCount < MaxBufferCount)
        {
            for (int i = _bufferCount; i < 2 * _bufferCount; i++)
            {
                _free.Push(_buffers[i]);
            }

            _bufferCount *= 2;
            _warning = $"The audio device takes more at a time than the live notes had queued, so they now wait {1000 * QueueSeconds:F0} ms in their queue; on Linux a JACK client, such as the SuperCollider server, can make the sound server do so.";
            return;
        }

        _mixer.Shed(1);
        if (_firstSecondBuffers < FirstSecondBuffers && ++_firstSecondUnderruns >= StormUnderruns)
        {
            _warning = $"The live notes ran dry {_firstSecondUnderruns} times in their first second and will stutter: the computer cannot render them fast enough, or the audio device takes too much at a time.";
        }
    }

    /// <summary>Renders the next buffer and queues it.</summary>
    private void QueueNext(AL al)
    {
        long start = Stopwatch.GetTimestamp();
        _mixer.Render(_mix);
        long ticks = Stopwatch.GetTimestamp() - start;
        if (_firstSecondBuffers < FirstSecondBuffers)
        {
            _firstSecondBuffers++;
        }

        if (ticks > Volatile.Read(ref _longestRenderTicks))
        {
            Volatile.Write(ref _longestRenderTicks, ticks);
        }

        // 16-bit PCM is what every OpenAL implementation takes; the float extension is not guaranteed.
        for (int i = 0; i < _pcm.Length; i++)
        {
            _pcm[i] = (short)Math.Clamp(_mix[i] * 32767f, -32768f, 32767f);
        }

        uint buffer = _free.Pop();
        fixed (short* data = _pcm)
        {
            al.BufferData(buffer, BufferFormat.Mono16, data, BufferSamples * sizeof(short), SampleRate);
        }

        al.SourceQueueBuffers(_source, 1, &buffer);
    }

    /// <summary>Takes the buffers the source has played off its queue.</summary>
    private void Reclaim(AL al)
    {
        al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out int processed);
        uint* buffers = stackalloc uint[MaxBufferCount];
        processed = Math.Min(processed, _bufferCount - _free.Count);
        if (processed <= 0)
        {
            return;
        }

        al.SourceUnqueueBuffers(_source, processed, buffers);
        for (int i = 0; i < processed; i++)
        {
            _free.Push(buffers[i]);
        }
    }

    /// <summary>Stops the source and takes every buffer off its queue.</summary>
    private void Rewind()
    {
        try
        {
            _al?.SourceStop(_source);
            _al?.SetSourceProperty(_source, SourceInteger.Buffer, 0);
        }
        catch (Exception)
        {
            // Nothing more to be done on the audio thread.
        }

        _free.Clear();
        for (int i = 0; i < _bufferCount; i++)
        {
            _free.Push(_buffers[i]);
        }
    }

    /// <summary>Adds the audio queued ahead of a note started now, and the device latency, to the running average.</summary>
    private void MeasureLatency(AL al)
    {
        al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out int queued);
        double played;
        double device = 0;
        if (_getSourceDoubles != null)
        {
            double* values = stackalloc double[2];
            _getSourceDoubles(_source, SecondOffsetLatency, values);
            played = values[0];
            device = values[1];
        }
        else
        {
            al.GetSourceProperty(_source, GetSourceInteger.SampleOffset, out int sample);
            played = sample / (double)SampleRate;
        }

        double latency = (queued * BufferSamples / (double)SampleRate) - played + device;
        if (double.IsFinite(latency) && latency >= 0)
        {
            double average = Volatile.Read(ref _latencySeconds);
            Volatile.Write(ref _latencySeconds, average + (LatencyWeight * (latency - average)));
        }
    }

    /// <summary>
    /// Waits about a millisecond: on Windows on a high-resolution waitable timer, which does not round up to the
    /// 15.6 ms timer tick as a sleep does; elsewhere, and on Windows before version 1803, with a sleep.
    /// </summary>
    private sealed class Pacer : IDisposable
    {
        private const uint HighResolution = 0x2;
        private const uint AllAccess = 0x1F0003;
        private const long Interval = -10_000; // 1 ms, relative, in 100 ns units.

        private readonly IntPtr _timer;

        public Pacer()
        {
            if (OperatingSystem.IsWindows())
            {
                _timer = NativeMethods.CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, HighResolution, AllAccess);
            }
        }

        public void Wait()
        {
            long due = Interval;
            if (_timer == IntPtr.Zero
                || !NativeMethods.SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)
                || NativeMethods.WaitForSingleObject(_timer, 100) != 0)
            {
                Thread.Sleep(1);
            }
        }

        public void Dispose()
        {
            if (_timer != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(_timer);
            }
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, IntPtr name, uint flags, uint access);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
