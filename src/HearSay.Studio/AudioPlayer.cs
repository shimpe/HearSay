using HearSay.Synthesis.Rendering;
using Silk.NET.OpenAL;

namespace HearSay.Studio;

/// <summary>
/// Plays rendered audio through OpenAL Soft, which runs on Windows, Linux and macOS. One voice at a time: a new
/// note stops the previous one. When no audio device can be opened, playback is silently unavailable and
/// <see cref="IsAvailable"/> says so.
/// </summary>
public sealed unsafe class AudioPlayer : IDisposable
{
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

            _context = _alc.CreateContext(_device, null);
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
