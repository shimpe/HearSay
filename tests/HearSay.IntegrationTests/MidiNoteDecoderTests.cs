using HearSay.Playing;

namespace HearSay.IntegrationTests;

/// <summary>The decoder from raw MIDI bytes to note events.</summary>
public sealed class MidiNoteDecoderTests
{
    [Fact]
    public void Decode_NoteOn_NoteOff_AndZeroVelocity()
    {
        Assert.Equal(new NoteEvent(true, 60, 100 / 127.0), MidiNoteDecoder.Decode(0x90, 60, 100));
        Assert.Equal(new NoteEvent(false, 60, 64 / 127.0).IsOn, MidiNoteDecoder.Decode(0x80, 60, 64)!.Value.IsOn);
        Assert.Equal(60, MidiNoteDecoder.Decode(0x80, 60, 64)!.Value.Note);

        NoteEvent zero = MidiNoteDecoder.Decode(0x93, 60, 0)!.Value;
        Assert.False(zero.IsOn);
        Assert.Equal(60, zero.Note);

        NoteEvent high = MidiNoteDecoder.Decode(0x9E, 72, 127)!.Value;
        Assert.True(high.IsOn);
        Assert.Equal(72, high.Note);
        Assert.Equal(1.0, high.Velocity);
    }

    [Fact]
    public void Decode_IgnoresEverythingElse()
    {
        Assert.Null(MidiNoteDecoder.Decode(0xB0, 64, 127));
        Assert.Null(MidiNoteDecoder.Decode(0xE0, 0, 64));
        Assert.Null(MidiNoteDecoder.Decode(0xC0, 5, 0));
        Assert.Null(MidiNoteDecoder.Decode(0xF8, 0, 0));
    }
}
