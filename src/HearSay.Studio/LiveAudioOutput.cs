using System.Diagnostics;
using System.Runtime.InteropServices;
using HearSay.Playing;
using Silk.NET.OpenAL;

namespace HearSay.Studio;

/// <summary>
/// Plays the live notes of a <see cref="VoiceMixer"/>: a streaming OpenAL source on the device and context of an
/// <see cref="AudioPlayer"/>, beside its one-shot source, fed with <see cref="BufferCount"/> queued buffers of
/// <see cref="BufferSamples"/> mono 16-bit samples at <see cref="SampleRate"/> Hz (5.3 ms each) by a thread of its own
/// at above-normal priority.
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
/// When a buffer was not ready in time (the source ran dry and stopped, or fewer than <see cref="MinimumQueued"/>
/// buffers were left queued), it drops the oldest voice (<see cref="VoiceMixer.Shed"/>), counts an underrun, refills
/// the queue and restarts the source: a search running on every core keeps its full speed, and the notes may stutter
/// then. The thread never throws: after an error it stops the source and starts again with the next note.
/// </para>
/// <para>
/// The device adds its own buffering: OpenAL Soft mixes in updates of the period_size in alsoft.ini next to the program
/// (256 samples asked for; Windows mixes every 10 ms in shared mode, and OpenAL Soft keeps 22 ms there), and without that
/// file in updates of 960 samples, nearly the whole queue, so that the source keeps running dry.
/// </para>
/// <para>Dispose it before the <see cref="AudioPlayer"/>, whose context it plays in.</para>
/// </remarks>
public sealed unsafe class LiveAudioOutput : IDisposable
{
    /// <summary>Sample rate of the live notes, in hertz; the mixer must render at it.</summary>
    public const int SampleRate = 48000;

    /// <summary>Samples per buffer.</summary>
    public const int BufferSamples = 256;

    /// <summary>Buffers queued on the source.</summary>
    public const int BufferCount = 4;

    /// <summary>Buffers that must still be queued when the thread looks; fewer means it fell behind.</summary>
    private const int MinimumQueued = 2;

    /// <summary>AL_SEC_OFFSET_LATENCY_SOFT: the playing offset and the device latency, in seconds, as two doubles.</summary>
    private const int SecondOffsetLatency = 0x1201;

    /// <summary>Weight of a new latency measurement in the running average: about the last quarter second counts.</summary>
    private const double LatencyWeight = 1.0 / 48;

    private static readonly TimeSpan IdleCheck = TimeSpan.FromMilliseconds(50);

    private readonly AL? _al;
    private readonly VoiceMixer _mixer;
    private readonly uint _source;
    private readonly uint[] _buffers = new uint[BufferCount];
    private readonly delegate* unmanaged[Cdecl]<uint, int, double*, void> _getSourceDoubles;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread? _thread;
    private readonly Action _onSoundingNotesChanged;

    // Owned by the feeding thread.
    private readonly Stack<uint> _free = new(BufferCount);
    private readonly float[] _mix = new float[BufferSamples];
    private readonly short[] _pcm = new short[BufferSamples];

    private volatile bool _stopping;
    private double _latencySeconds = BufferCount * BufferSamples / (double)SampleRate;
    private int _underruns;
    private long _longestRenderTicks;
    private int _disposed;

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
            _al.GenBuffers(BufferCount, buffers);
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

        foreach (uint buffer in _buffers)
        {
            _free.Push(buffer);
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

    /// <summary>Number of times a buffer was not ready in time and a voice was dropped, since <see cref="ResetStatistics"/>.</summary>
    public int Underruns => Volatile.Read(ref _underruns);

    /// <summary>Longest time the mixer took to render one buffer, in milliseconds, since <see cref="ResetStatistics"/>.</summary>
    public double LongestRenderMilliseconds => Volatile.Read(ref _longestRenderTicks) * 1000.0 / Stopwatch.Frequency;

    /// <summary>Sets <see cref="Underruns"/> and <see cref="LongestRenderMilliseconds"/> back to zero.</summary>
    public void ResetStatistics()
    {
        Interlocked.Exchange(ref _underruns, 0);
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
            _al.DeleteBuffers(BufferCount, buffers);
        }
    }

    private void Run()
    {
        using var pacer = new Pacer();
        while (!_stopping)
        {
            try
            {
                if (_mixer.ActiveVoices == 0)
                {
                    _wake.WaitOne(IdleCheck);
                    continue;
                }

                Stream(pacer);
            }
            catch (Exception)
            {
                // The audio thread must survive whatever goes wrong; it starts again with the next note.
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
            Reclaim(al);
            int queued = BufferCount - _free.Count;
            al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
            bool playing = state == (int)SourceState.Playing;
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

            if (feeding && (!playing || queued < MinimumQueued))
            {
                _mixer.Shed(1);
                Interlocked.Increment(ref _underruns);
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

    /// <summary>Renders the next buffer and queues it.</summary>
    private void QueueNext(AL al)
    {
        long start = Stopwatch.GetTimestamp();
        _mixer.Render(_mix);
        long ticks = Stopwatch.GetTimestamp() - start;
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
        uint* buffers = stackalloc uint[BufferCount];
        processed = Math.Min(processed, BufferCount - _free.Count);
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
        foreach (uint buffer in _buffers)
        {
            _free.Push(buffer);
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
