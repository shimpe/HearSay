using System.Runtime.InteropServices;
using System.Text;
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
/// size of the live buffers; OpenAL Soft 1.23 takes the update size from its settings instead, which alsoft.ini
/// (alsoft.conf outside Windows) in the program's folder sets, pointed at through <c>ALSOFT_CONF</c> so that it is
/// found however the program is started (Windows rounds the update up to 10 ms in shared mode).
/// </remarks>
public sealed unsafe class AudioPlayer : IDisposable
{
    /// <summary>ALC_FREQUENCY: the sample rate the device mixes at.</summary>
    private const int ContextFrequency = 0x1007;

    /// <summary>ALC_REFRESH: how many times per second the device mixes an update.</summary>
    private const int ContextRefresh = 0x1008;

    /// <summary>ALC_CONNECTED (ALC_EXT_disconnect): 0 once the device is gone.</summary>
    private const int ContextConnected = 0x313;

    /// <summary>Environment variable that names a settings file OpenAL Soft reads after all others.</summary>
    private const string SettingsVariable = "ALSOFT_CONF";

    private readonly object _lock = new();
    private readonly AL? _al;
    private readonly ALContext? _alc;
    private readonly Device* _device;
    private readonly bool _reportsConnection;
    private readonly Context* _context;
    private uint _source;
    private uint _buffer;

    /// <summary>Opens the default audio device.</summary>
    public AudioPlayer()
    {
        UseShippedSettings();
        try
        {
            _alc = ALContext.GetApi(soft: true);
            _al = AL.GetApi(soft: true);
            _device = _alc.OpenDevice(string.Empty);
            if (_device == null)
            {
                return;
            }

            _reportsConnection = _alc.IsExtensionPresent(_device, "ALC_EXT_disconnect");

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
            DeviceUpdateSamples = UpdateSamples(_alc, _device);
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

    /// <summary>
    /// Samples the device mixes at a time, as OpenAL Soft reports it (its frequency over its refresh rate): 480 on
    /// Windows with the shipped settings, 960 without them; 0 when no device is open or it does not say.
    /// </summary>
    internal int DeviceUpdateSamples { get; }

    /// <summary>
    /// False once the audio device is gone, for example unplugged headphones or a USB interface; true while it is there,
    /// and also when OpenAL cannot tell. Safe on any thread.
    /// </summary>
    internal bool IsConnected
    {
        get
        {
            if (!_reportsConnection || _alc is null || _device == null)
            {
                return true;
            }

            int connected = 1;
            _alc.GetContextProperty(_device, (GetContextInteger)ContextConnected, 1, &connected);
            return connected != 0;
        }
    }

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

    /// <summary>
    /// Points OpenAL Soft at the settings shipped next to the program, unless <c>ALSOFT_CONF</c> already names a file:
    /// on its own it looks for them next to the process binary, which is dotnet itself for <c>dotnet HearSay.Studio.dll</c>
    /// or a test host. Must run before OpenAL Soft is loaded; it reads its settings once per process. OpenAL Soft reads
    /// the variable with the C runtime's <c>getenv</c>, which does not see <see cref="Environment.SetEnvironmentVariable(string, string)"/>
    /// (the C runtime keeps its own copy on Windows, and .NET keeps its own elsewhere), so the C runtime's is set as well.
    /// When that fails, <see cref="LiveAudioOutput.Warning"/> says so.
    /// </summary>
    private static void UseShippedSettings()
    {
        try
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SettingsVariable)))
            {
                return;
            }

            string path = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "alsoft.ini" : "alsoft.conf");
            if (File.Exists(path))
            {
                Environment.SetEnvironmentVariable(SettingsVariable, path);
                NativeEnvironment.Set(SettingsVariable, path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            // Without the settings the live notes stutter, and LiveAudioOutput.Warning says why.
        }
    }

    private static int UpdateSamples(ALContext alc, Device* device)
    {
        int frequency = 0;
        int refresh = 0;
        alc.GetContextProperty(device, (GetContextInteger)ContextFrequency, 1, &frequency);
        alc.GetContextProperty(device, (GetContextInteger)ContextRefresh, 1, &refresh);
        return frequency > 0 && refresh > 0 ? (int)Math.Round(frequency / (double)refresh) : 0;
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

    /// <summary>Sets a variable in the environment of the C runtime that OpenAL Soft reads it from.</summary>
    private static class NativeEnvironment
    {
        public static void Set(string name, string value)
        {
            if (OperatingSystem.IsWindows())
            {
                // OpenAL Soft uses the shared Universal C Runtime.
                _ = WindowsPutEnvironment(name, value);
                return;
            }

            if ((NativeLibrary.TryLoad("libc.so.6", out IntPtr libc) || NativeLibrary.TryLoad("libc", out libc))
                && NativeLibrary.TryGetExport(libc, "setenv", out IntPtr setenv))
            {
                byte[] nameBytes = Encoding.UTF8.GetBytes(name + '\0');
                byte[] valueBytes = Encoding.UTF8.GetBytes(value + '\0');
                fixed (byte* n = nameBytes)
                fixed (byte* v = valueBytes)
                {
                    _ = ((delegate* unmanaged<byte*, byte*, int, int>)setenv)(n, v, 1);
                }
            }
        }

        [DllImport("ucrtbase.dll", EntryPoint = "_wputenv_s", CharSet = CharSet.Unicode)]
        private static extern int WindowsPutEnvironment(string name, string value);
    }
}
