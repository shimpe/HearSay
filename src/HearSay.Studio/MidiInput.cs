using HearSay.Playing;
using OwnAudio.Midi.IO;

namespace HearSay.Studio;

/// <summary>
/// A MIDI input device that raises note events: lists the devices, opens one by name, and tells when the open one is
/// gone. Opening never throws; where there is no live MIDI input (Android, a missing driver) it just returns false.
/// </summary>
public sealed class MidiInput : IDisposable
{
    private readonly object _gate = new();
    private IMidiInputPort? _port;
    private string? _name;
    private bool _monitoring;
    private bool _disposed;

    /// <summary>Raised for every note-on and note-off, on the library's background thread.</summary>
    public event Action<NoteEvent>? Note;

    /// <summary>Raised when devices are plugged in or out.</summary>
    public event Action? DevicesChanged;

    /// <summary>Raised when the open device is no longer among <see cref="DeviceNames"/> after a change.</summary>
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
    public static IReadOnlyList<string> DeviceNames()
    {
        try
        {
            return MidiPortFactory.GetInputPortNames().ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Opens the named device, closing any other; false, without an exception, when that is not possible.</summary>
    /// <param name="name">Device name from <see cref="DeviceNames"/>.</param>
    public bool Open(string name)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            CloseLocked();
            try
            {
                IMidiInputPort port = MidiPortFactory.OpenInput(name);
                port.MessageReceived += OnMessage;
                try
                {
                    port.Start();
                }
                catch (Exception)
                {
                    port.MessageReceived -= OnMessage;
                    port.Dispose();
                    return false;
                }

                _port = port;
                _name = name;
                StartMonitoring();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Closes the open device, if any.</summary>
    public void Close()
    {
        lock (_gate)
        {
            CloseLocked();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CloseLocked();
            if (_monitoring)
            {
                _monitoring = false;
                MidiPortFactory.PortsChanged -= OnPortsChanged;
                try
                {
                    MidiPortFactory.StopMonitoring();
                }
                catch (Exception)
                {
                    // Nothing to stop.
                }
            }
        }
    }

    private void StartMonitoring()
    {
        if (_monitoring)
        {
            return;
        }

        try
        {
            MidiPortFactory.PortsChanged += OnPortsChanged;
            MidiPortFactory.StartMonitoring();
            _monitoring = true;
        }
        catch (Exception)
        {
            // No hot-plug here: the device list is still enumerated afresh on demand.
            MidiPortFactory.PortsChanged -= OnPortsChanged;
        }
    }

    private void CloseLocked()
    {
        IMidiInputPort? port = _port;
        _port = null;
        _name = null;
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
            // Already gone.
        }

        port.MessageReceived -= OnMessage;
        try
        {
            port.Dispose();
        }
        catch (Exception)
        {
            // Already gone.
        }
    }

    private void OnMessage(MidiMessage message)
    {
        NoteEvent? note = MidiNoteDecoder.Decode(message.Status, message.Data1, message.Data2);
        if (note is { } value)
        {
            Note?.Invoke(value);
        }
    }

    private void OnPortsChanged()
    {
        DevicesChanged?.Invoke();
        string? open = OpenDevice;
        if (open is not null && !DeviceNames().Contains(open))
        {
            DeviceLost?.Invoke();
        }
    }
}
