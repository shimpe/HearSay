namespace HearSay.Playing;

/// <summary>Decides whether an open MIDI device has been lost, from the device list as last enumerated.</summary>
public static class MidiDevicePresence
{
    /// <summary>
    /// True when a device is open and an enumeration that succeeded no longer lists it. A failed enumeration
    /// (<paramref name="names"/> null) never counts as a loss, an empty list does.
    /// </summary>
    /// <param name="openDevice">Name of the open device, or null when none is open.</param>
    /// <param name="names">The enumerated device names, or null when the enumeration failed.</param>
    public static bool IsLost(string? openDevice, IReadOnlyList<string>? names) =>
        openDevice is not null && names is not null && !names.Contains(openDevice);
}
