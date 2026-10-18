namespace HearSay.Playing;

/// <summary>A played note: pressed or released, with the note number and the velocity from 0 to 1.</summary>
/// <param name="IsOn">True for a key press, false for a release.</param>
/// <param name="Note">MIDI note number.</param>
/// <param name="Velocity">Key velocity divided by 127.</param>
public readonly record struct NoteEvent(bool IsOn, int Note, double Velocity);

/// <summary>Decodes raw MIDI messages into note events; every channel counts, all other messages are ignored.</summary>
public static class MidiNoteDecoder
{
    /// <summary>Decodes a message: note-on, note-off (a note-on with velocity 0 is a note-off), or null for anything else.</summary>
    /// <param name="status">Status byte, including the channel in its low nibble.</param>
    /// <param name="data1">First data byte, the note number.</param>
    /// <param name="data2">Second data byte, the velocity.</param>
    public static NoteEvent? Decode(byte status, byte data1, byte data2) => (status & 0xF0) switch
    {
        0x90 when data2 > 0 => new NoteEvent(true, data1, data2 / 127.0),
        0x90 or 0x80 => new NoteEvent(false, data1, data2 / 127.0),
        _ => null,
    };
}
