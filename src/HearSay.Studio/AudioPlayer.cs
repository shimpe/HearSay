using HearSay.Synthesis.Rendering;
using Silk.NET.OpenAL;

namespace HearSay.Studio;

/// <summary>
/// Plays rendered audio through OpenAL Soft, which runs on Windows, Linux and macOS. One voice at a time: a new
/// note stops the previous one. When no audio device can be opened, playback is silently unavailable and
/// <see cref="IsAvailable"/> says so.
/// </summary>
/// <remarks>
/// The device and its context are shared with a <see cref="LiveAudioOutput"/>, which streams live notes on a source of
/// its own, so that both sound at the same time. The context mixes at 48 kHz and asks for updates of 256 samples, the
/// size of the live buffers; OpenAL Soft 1.23 takes the update size from its settings instead, which alsoft.ini in
/// the program's folder sets (Windows rounds it up to 10 ms in shared mode).
/// </remarks>
public sealed unsafe class AudioPlayer : IDisposable
{
    /// <summary>ALC_FREQUENCY: the sample rate the device mixes at.</summary>
    private const int ContextFrequency = 0x1007;

    /// <summary>ALC_REFRESH: how many times per second the device mixes an update.</summary>
    private const int ContextRefresh = 0x1008;

    private readonly object _lock = new();
    private readonly AL? _al;
    private readonly ALContext? _alc;
    private readonly Device* _device;
    private readonly Context* _context;
    private uint _source;
    private uint _buffer;

    /// <summary>Opens the default audio device.</summary>
    public AudioPlayer()
    {
        try
        {
            _alc = ALContext.GetApi(soft: true);
            _al = AL.GetApi(soft: true);
            _device = _alc.OpenDevice(string.Empty);
            if (_device == null)
            {
                return;
            }

            // A short update (48000 / 256 a second) keeps the device's own latency small for the live notes; OpenAL Soft
            // logs ALC_REFRESH but takes the update size from period_size in alsoft.ini.
            int* attributes = stackalloc int[]
            {
                ContextFrequency, LiveAudioOutput.SampleRate,
                ContextRefresh, LiveAudioOutput.SampleRate / LiveAudioOutput.BufferSamples,
                0,
            };
            _context = _alc.CreateContext(_device, attributes);
            if (_context == null)
            {
                return;
            }

            _alc.MakeContextCurrent(_context);
            _source = _al.GenSource();
            _buffer = _al.GenBuffer();
            IsAvailable = true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or TypeInitializationException)
        {
            IsAvailable = false;
        }
    }

    /// <summary>True when an audio device is open.</summary>
    public bool IsAvailable { get; }

    /// <summary>The OpenAL API whose context is current, for the live output's source; null when no device is open.</summary>
    internal AL? Api => IsAvailable ? _al : null;

    /// <summary>Plays audio from the start, stopping whatever was playing.</summary>
    /// <param name="audio">Mono audio.</param>
    public void Play(RenderedAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (!IsAvailable || _al is null)
        {
            return;
        }

        // 16-bit PCM is what every OpenAL implementation takes; the float extension is not guaranteed.
        var pcm = new short[audio.Samples.Length];
        for (int i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (short)Math.Clamp(audio.Samples[i] * 32767f, -32768f, 32767f);
        }

        lock (_lock)
        {
            _al.SourceStop(_source);
            _al.SetSourceProperty(_source, SourceInteger.Buffer, 0);
            fixed (short* data = pcm)
            {
                _al.BufferData(_buffer, BufferFormat.Mono16, data, pcm.Length * sizeof(short), audio.SampleRate);
            }

            _al.SetSourceProperty(_source, SourceInteger.Buffer, (int)_buffer);
            _al.SourcePlay(_source);
        }
    }

    /// <summary>Stops playback.</summary>
    public void Stop()
    {
        if (IsAvailable && _al is not null)
        {
            lock (_lock)
            {
                _al.SourceStop(_source);
            }
        }
    }

    public void Dispose()
    {
        if (_al is null || _alc is null)
        {
            return;
        }

        lock (_lock)
        {
            if (IsAvailable)
            {
                _al.SourceStop(_source);
                _al.DeleteSource(_source);
                _al.DeleteBuffer(_buffer);
            }

            _alc.MakeContextCurrent(null);
            if (_context != null)
            {
                _alc.DestroyContext(_context);
            }

            if (_device != null)
            {
                _alc.CloseDevice(_device);
            }
        }
    }
}
