using HearSay.Playing;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay.IntegrationTests;

/// <summary>The notes of one opened MIDI device: delivered until the session is retired, released by it, dropped after it.</summary>
public sealed class NoteSessionTests
{
    private const int Rate = 48000;

    private static readonly Sound Sound = new(
        OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.01, 0.1, 0.7, 0.2)))
            .WithOutputLevel(0, 0.8),
        SynthesisProfiles.SuperColliderFm7,
        RenderRequest.Create(220, 1.0, 0.3));

    [Fact]
    public void ANoteArrivingForARetiredSession_IsDropped_AndRetireLeavesNothingSounding()
    {
        var mixer = new VoiceMixer();
        var session = new NoteSession(mixer);
        var other = new NoteSession(mixer);

        Assert.True(session.Deliver(new NoteEvent(true, 60, 0.8), Play(mixer)));
        Assert.True(other.Deliver(new NoteEvent(true, 64, 0.8), Play(mixer)));
        Assert.Equal([60, 64], mixer.SoundingNotes.Order());

        session.Retire();
        Assert.True(session.IsRetired);
        Assert.Equal([64], mixer.SoundingNotes);

        // The device's last note-on, late: dropped, not left sounding.
        Assert.False(session.Deliver(new NoteEvent(true, 62, 0.8), Play(mixer)));
        Assert.Equal([64], mixer.SoundingNotes);

        // The next device's notes are not touched.
        Assert.True(other.Deliver(new NoteEvent(false, 64, 0), Play(mixer)));
        Assert.Empty(mixer.SoundingNotes);
    }

    [Fact]
    public void RetireWhileNotesArriveOnAnotherThread_LeavesNothingSounding()
    {
        var mixer = new VoiceMixer();
        for (int round = 0; round < 50; round++)
        {
            var session = new NoteSession(mixer);
            using var started = new ManualResetEventSlim();
            var device = new Thread(() =>
            {
                for (int i = 0; i < 2000; i++)
                {
                    if (i == 3)
                    {
                        started.Set();
                    }

                    // Note-ons only: each would hold until its note-off, which never comes.
                    if (!session.Deliver(new NoteEvent(true, 40 + (i % 40), 0.8), Play(mixer)))
                    {
                        return;
                    }
                }
            });
            device.Start();
            started.Wait();
            session.Retire();
            device.Join();

            Assert.Empty(mixer.SoundingNotes);

            // The released and stolen voices end, so that the next round starts from an empty mixer.
            mixer.Render(new float[Rate]);
        }
    }

    private static Action<NoteEvent, object> Play(VoiceMixer mixer) => (note, source) =>
    {
        if (note.IsOn)
        {
            Assert.True(mixer.NoteOn(note.Note, note.Velocity, Sound, source));
        }
        else
        {
            mixer.NoteOff(note.Note, source);
        }
    };
}
