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
    private Action<MidiMessage>? _portHandler;
    private Timer? _timer;
    private string? _name;
    private bool _monitoring;
    private bool _disposed;

    /// <summary>
    /// Raised for every note-on and note-off of the open device, on the library background thread, with the session
    /// object given to <see cref="Open"/>. A message from a port that has been closed or replaced is dropped; one that
    /// was already past that check carries the session of its own device, so that its receiver can drop it too.
    /// </summary>
    public event Action<NoteEvent, object>? Note;

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

    /// <summary>
    /// Opens the named device in place of the open one, which is closed only once the new one is open: when the new
    /// one cannot be opened, the open one stays open and false is returned, without an exception.
    /// </summary>
    /// <param name="name">Device name from <see cref="DeviceNames"/>.</param>
    /// <param name="session">Handed with every note of this device to the <see cref="Note"/> handlers.</param>
    public bool Open(string name, object session)
    {
        ArgumentNullException.ThrowIfNull(session);
        IMidiInputPort? port = null;
        Action<MidiMessage>? handler = null;
        try
        {
            port = MidiPortFactory.OpenInput(name);
            IMidiInputPort opened = port;
            handler = message => OnMessage(opened, session, message);
            port.MessageReceived += handler;
            port.Start();
        }
        catch (Exception)
        {
            Discard(port, handler);
            return false;
        }

        bool startMonitoring = false;
        bool installed = false;
        (IMidiInputPort? Port, Action<MidiMessage>? Handler, Timer? Timer) replaced = default;
        lock (_gate)
        {
            if (!_disposed)
            {
                replaced = DetachLocked();
                _port = port;
                _portHandler = handler;
                _name = name;
                _timer = new Timer(_ => CheckPresence(), null, CheckPeriod, CheckPeriod);
                startMonitoring = !_monitoring;
                _monitoring = true;
                installed = true;
            }
        }

        // The device it replaces is closed outside the lock: no library call is made while it is held.
        Release(replaced);
        if (!installed)
        {
            Discard(port, handler);
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
        (IMidiInputPort? Port, Action<MidiMessage>? Handler, Timer? Timer) detached;
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

    private static void Discard(IMidiInputPort? port, Action<MidiMessage>? handler)
    {
        if (port is null)
        {
            return;
        }

        if (handler is not null)
        {
            port.MessageReceived -= handler;
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

    private (IMidiInputPort? Port, Action<MidiMessage>? Handler, Timer? Timer) Detach()
    {
        lock (_gate)
        {
            return DetachLocked();
        }
    }

    private (IMidiInputPort? Port, Action<MidiMessage>? Handler, Timer? Timer) DetachLocked()
    {
        var result = (_port, _portHandler, _timer);
        _port = null;
        _portHandler = null;
        _timer = null;
        _name = null;
        return result;
    }

    private static void Release((IMidiInputPort? Port, Action<MidiMessage>? Handler, Timer? Timer) detached)
    {
        detached.Timer?.Dispose();
        Discard(detached.Port, detached.Handler);
    }

    /// <summary>A message of a port, on the library thread: notes of the port that is open now go to the handlers.</summary>
    private void OnMessage(IMidiInputPort port, object session, MidiMessage message)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_port, port))
            {
                return; // Closed or replaced since; its session is retired as well.
            }
        }

        if (MidiNoteDecoder.Decode(message.Status, message.Data1, message.Data2) is not { } note)
        {
            return;
        }

        Action<NoteEvent, object>? handlers = Note;
        if (handlers is null)
        {
            return;
        }

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<NoteEvent, object>)handler)(note, session);
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

        (IMidiInputPort? Port, Action<MidiMessage>? Handler, Timer? Timer) detached;
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
