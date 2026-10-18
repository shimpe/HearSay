using HearSay.Playing;

namespace HearSay.IntegrationTests;

/// <summary>When an open MIDI device counts as lost.</summary>
public sealed class MidiDevicePresenceTests
{
    [Fact]
    public void IsLost_OnlyWhenASuccessfulListLacksTheOpenDevice()
    {
        Assert.False(MidiDevicePresence.IsLost("A", null)); // failed enumeration
        Assert.False(MidiDevicePresence.IsLost("A", ["A", "B"]));
        Assert.True(MidiDevicePresence.IsLost("A", ["B"]));
        Assert.True(MidiDevicePresence.IsLost("A", []));
        Assert.False(MidiDevicePresence.IsLost(null, []));
    }
}
