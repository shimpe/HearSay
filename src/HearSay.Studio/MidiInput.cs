using HearSay.Playing;
using OwnAudio.Midi.IO;

namespace HearSay.Studio;

/// <summary>
/// A MIDI input device that raises note events: lists the devices, opens one by name, and tells when the open one is
/// gone. Opening never throws; where there is no live MIDI input (Android, a missing driver) it just returns false.
/// </summary>
/// <remarks>
/// The events are raised on the library thread or a timer thread; a handler that throws is swallowed there, as nobody
/// could catch it. The lock guards state only: the library is never called while it is held.
/// </remarks>
public sealed class MidiInput : IDisposable
{
    private static readonly TimeSpan CheckPeriod = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private IMidiInputPort? _port;
    private Timer? _timer;
    private string? _name;
    private bool _monitoring;
    private bool _disposed;

    /// <summary>Raised for every note-on and note-off, on the library background thread.</summary>
    public event Action<NoteEvent>? Note;

    /// <summary>Raised when devices are plugged in or out.</summary>
    public event Action? DevicesChanged;

    /// <summary>
    /// Raised once when the open device is no longer among <see cref="DeviceNames"/>; the port is closed by then and
    /// <see cref="OpenDevice"/> is null. Detected on a change of the ports and by a check every 2 seconds.
    /// </summary>
    public event Action? DeviceLost;

    /// <summary>Name of the open device, or null when none is open.</summary>
    public string? OpenDevice
    {
        get
        {
            lock (_gate)
            {
                return _name;
            }
        }
    }

    /// <summary>The names of the MIDI input devices present now (always enumerated afresh); empty when that fails.</summary>
    public static IReadOnlyList<string> DeviceNames() => TryDeviceNames() ?? [];

    /// <summary>Opens the named device, closing any other; false, without an exception, when that is not possible.</summary>
    /// <param name="name">Device name from <see cref="DeviceNames"/>.</param>
    public bool Open(string name)
    {
        Close();
        IMidiInputPort? port = null;
        try
        {
            port = MidiPortFactory.OpenInput(name);
            port.MessageReceived += OnMessage;
            port.Start();
        }
        catch (Exception)
        {
            Discard(port);
            return false;
        }

        bool startMonitoring = false;
        bool installed = false;
        lock (_gate)
        {
            if (!_disposed && _port is null)
            {
                _port = port;
                _name = name;
                _timer = new Timer(_ => CheckPresence(), null, CheckPeriod, CheckPeriod);
                startMonitoring = !_monitoring;
                _monitoring = true;
                installed = true;
            }
        }

        if (!installed)
        {
            Discard(port);
            return false;
        }

        if (startMonitoring)
        {
            try
            {
                MidiPortFactory.PortsChanged += OnPortsChanged;
                MidiPortFactory.StartMonitoring();
            }
            catch (Exception)
            {
                // No hot-plug here: the timer still notices a lost device.
            }
        }

        return true;
    }

    /// <summary>Closes the open device, if any.</summary>
    public void Close() => Release(Detach());

    /// <inheritdoc />
    public void Dispose()
    {
        bool stopMonitoring;
        (IMidiInputPort? Port, Timer? Timer) detached;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            stopMonitoring = _monitoring;
            _monitoring = false;
            detached = DetachLocked();
        }

        Release(detached);
        if (stopMonitoring)
        {
            try
            {
                MidiPortFactory.PortsChanged -= OnPortsChanged;
                MidiPortFactory.StopMonitoring();
            }
            catch (Exception)
            {
                // Nothing to stop.
            }
        }
    }

    private static IReadOnlyList<string>? TryDeviceNames()
    {
        try
        {
            return MidiPortFactory.GetInputPortNames().ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Discard(IMidiInputPort? port)
    {
        if (port is null)
        {
            return;
        }

        try
        {
            port.Stop();
        }
        catch (Exception)
        {
            // Never started, or already gone.
        }

        try
        {
            port.Dispose();
        }
        catch (Exception)
        {
            // Already gone.
        }
    }

    private static void Raise(Action? handlers)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception)
            {
                // Raised on a library or timer thread, where nobody could catch it.
            }
        }
    }

    private (IMidiInputPort? Port, Timer? Timer) Detach()
    {
        lock (_gate)
        {
            return DetachLocked();
        }
    }

    private (IMidiInputPort? Port, Timer? Timer) DetachLocked()
    {
        var result = (_port, _timer);
        _port = null;
        _timer = null;
        _name = null;
        return result;
    }

    private void Release((IMidiInputPort? Port, Timer? Timer) detached)
    {
        detached.Timer?.Dispose();
        if (detached.Port is { } port)
        {
            port.MessageReceived -= OnMessage;
            Discard(port);
        }
    }

    private void OnMessage(MidiMessage message)
    {
        if (MidiNoteDecoder.Decode(message.Status, message.Data1, message.Data2) is not { } note)
        {
            return;
        }

        Action<NoteEvent>? handlers = Note;
        if (handlers is null)
        {
            return;
        }

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<NoteEvent>)handler)(note);
            }
            catch (Exception)
            {
                // Raised on the library thread, where nobody could catch it.
            }
        }
    }

    private void OnPortsChanged()
    {
        Raise(DevicesChanged);
        CheckPresence();
    }

    private void CheckPresence()
    {
        string? open = OpenDevice;
        if (!MidiDevicePresence.IsLost(open, TryDeviceNames()))
        {
            return;
        }

        (IMidiInputPort? Port, Timer? Timer) detached;
        lock (_gate)
        {
            if (_name != open)
            {
                return; // Closed, replaced or already reported by another thread.
            }

            detached = DetachLocked();
        }

        Release(detached);
        Raise(DeviceLost);
    }
}
