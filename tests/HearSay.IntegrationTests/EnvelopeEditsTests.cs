using HearSay.Editing;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.IntegrationTests;

/// <summary>Editing the points, sustain and curves of an envelope; every result stays a valid envelope; fast, no rendering.</summary>
public sealed class EnvelopeEditsTests
{
    private static readonly SynthesisProfile Fm7 = SynthesisProfiles.SuperColliderFm7;
    private static readonly SynthesisProfile Fmx6 = SynthesisProfiles.SuperColliderFmx6;

    /// <summary>The amplitude envelope of OP1.</summary>
    private static readonly EnvelopeRef Amplitude = new(new PatchPart.OperatorPart(0), EnvelopeKind.Amplitude);

    /// <summary>The pitch envelope.</summary>
    private static readonly EnvelopeRef Pitch = new(new PatchPart.PitchPart(), EnvelopeKind.Pitch);

    /// <summary>
    /// OP2 modulates OP1 by 2 rad; OP1 is heard. OP1's amplitude envelope is
    /// 0 -(0.01 s, linear)-> 1 -(0.2 s, exponential)-> 0.5 (sustain) -(0.1 s, exponential)-> 0.
    /// </summary>
    private static OperatorMatrixPatch TwoOperators()
        => OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.01, 0.2, 0.5, 0.1)))
            .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(2), OperatorEnvelope.Adsr(0.01, 0.4, 0.3, 0.1)))
            .WithOutputLevel(0, 1.0)
            .WithEdge(new ModulationEdge(1, 0, true, 2.0));

    private static OperatorMatrixPatch Done(OperatorMatrixPatch patch, PatchEdit edit, SynthesisProfile? profile = null)
    {
        var editor = new PatchEditor(patch, profile ?? Fm7);
        EditOutcome outcome = editor.Apply(edit);
        Assert.False(outcome.IsRefused, outcome.Refusal);
        return outcome.Patch;
    }

    private static string Refused(OperatorMatrixPatch patch, PatchEdit edit)
    {
        EditOutcome outcome = edit(patch, Fm7.Capabilities);
        Assert.True(outcome.IsRefused);
        Assert.Same(patch, outcome.Patch);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Refusal));
        return outcome.Refusal!;
    }

    private static OperatorEnvelope Envelope(OperatorMatrixPatch patch, EnvelopeRef r)
    {
        OperatorEnvelope envelope = EnvelopeEdits.Get(patch, r)!;
        Assert.NotNull(envelope);
        Assert.Empty(envelope.Validate());
        return envelope;
    }

    [Fact]
    public void MovePoint_ClampsTimeAndLevel()
    {
        OperatorMatrixPatch patch = TwoOperators();

        OperatorEnvelope moved = Envelope(Done(patch, EnvelopeEdits.MovePoint(Amplitude, 2, 0.35, 0.25)), Amplitude);
        Assert.Equal(new EnvelopePoint(0.35, 0.25, EnvelopeCurve.Exponential), moved.Points[2]);

        // No negative durations, no level beyond the range.
        OperatorEnvelope clamped = Envelope(Done(patch, EnvelopeEdits.MovePoint(Amplitude, 1, -1, 2)), Amplitude);
        Assert.Equal(new EnvelopePoint(0, 1), clamped.Points[1]);

        // The first point only supplies the start level: its time stays 0.
        OperatorEnvelope first = Envelope(Done(patch, EnvelopeEdits.MovePoint(Amplitude, 0, 0.5, -0.3)), Amplitude);
        Assert.Equal(new EnvelopePoint(0, 0), first.Points[0]);

        Refused(patch, EnvelopeEdits.MovePoint(Amplitude, 1, double.NaN, 0.5));
        Refused(patch, EnvelopeEdits.MovePoint(Amplitude, 1, 0.1, double.PositiveInfinity));
        Refused(patch, EnvelopeEdits.MovePoint(Amplitude, 9, 0.1, 0.5));
        Refused(patch, EnvelopeEdits.MovePoint(new EnvelopeRef(new PatchPart.OperatorPart(0), EnvelopeKind.Sweep), 1, 0.1, 0.5));

        // A connection envelope's level stops where the modulation limit is reached: OP2 -> OP1 at 40 rad with an
        // envelope that peaks at a half, OP3 -> OP1 at 4 rad, 24 rad allowed: the peak can grow to a half, no further.
        var half = new OperatorEnvelope([new EnvelopePoint(0, 0), new EnvelopePoint(0.01, 0.5), new EnvelopePoint(0.1, 0.25), new EnvelopePoint(0.1, 0)], SustainPoint: 2, ReleasePoint: 3);
        OperatorMatrixPatch limited = patch
            .WithOperator(2, OperatorPatch.Sine(OperatorFrequency.AtRatio(3)))
            .WithEdge(new ModulationEdge(1, 0, true, 40.0, half))
            .WithEdge(new ModulationEdge(2, 0, true, 4.0));
        var amount = new EnvelopeRef(new PatchPart.ConnectionPart(1, 0), EnvelopeKind.Amount);
        Assert.Equal(0.5, Envelope(Done(limited, EnvelopeEdits.MovePoint(amount, 2, 0.1, 1.0)), amount).Points[2].Level, 12);
        Assert.Equal(0.4, Envelope(Done(limited, EnvelopeEdits.MovePoint(amount, 2, 0.1, 0.4)), amount).Points[2].Level, 12);
    }

    [Fact]
    public void AddPoint_SplitsTheSegment()
    {
        OperatorMatrixPatch patch = TwoOperators();

        // The linear attack: a point halfway in time and level; the sustain moves along with its point.
        OperatorEnvelope attack = Envelope(Done(patch, EnvelopeEdits.AddPoint(Amplitude, 0)), Amplitude);
        Assert.Equal(5, attack.Points.Count);
        Assert.Equal(new EnvelopePoint(0.005, 0.5), attack.Points[1]);
        Assert.Equal(new EnvelopePoint(0.005, 1.0), attack.Points[2]);
        Assert.Equal(3, attack.SustainPoint);
        Assert.Equal(4, attack.ReleasePoint);

        // The exponential decay: the new point lies on the curve, so the envelope sounds as before.
        OperatorEnvelope decay = Envelope(Done(patch, EnvelopeEdits.AddPoint(Amplitude, 1)), Amplitude);
        Assert.Equal(0.1, decay.Points[2].TimeSeconds, 12);
        Assert.Equal(Math.Sqrt(0.5), decay.Points[2].Level, 12);
        Assert.Equal(EnvelopeCurve.Exponential, decay.Points[2].Curve);
        Assert.Equal(new EnvelopePoint(0.1, 0.5, EnvelopeCurve.Exponential), decay.Points[3]);
        Assert.Equal(3, decay.SustainPoint);

        // After the sustain point, in the release: the sustain stays where it is.
        OperatorEnvelope release = Envelope(Done(patch, EnvelopeEdits.AddPoint(Amplitude, 2)), Amplitude);
        Assert.Equal(2, release.SustainPoint);
        Assert.Equal(3, release.ReleasePoint);
        Assert.Equal(0.05, release.Points[3].TimeSeconds, 12);

        // There is no segment after the last point.
        Refused(patch, EnvelopeEdits.AddPoint(Amplitude, 3));
    }

    [Fact]
    public void RemovePoint_AtTheSustainPoint_KeepsTheEnvelopeValid()
    {
        OperatorMatrixPatch patch = TwoOperators();

        // The point before takes over the hold; the points after keep their place in time.
        OperatorEnvelope removed = Envelope(Done(patch, EnvelopeEdits.RemovePoint(Amplitude, 2)), Amplitude);
        Assert.Equal(3, removed.Points.Count);
        Assert.Equal(1, removed.SustainPoint);
        Assert.Equal(2, removed.ReleasePoint);
        Assert.Equal(0.3, removed.Points[2].TimeSeconds, 12);

        // Removing the only release point moves the hold back one point.
        OperatorEnvelope noRelease = Envelope(Done(patch, EnvelopeEdits.RemovePoint(Amplitude, 3)), Amplitude);
        Assert.Equal(3, noRelease.Points.Count);
        Assert.Equal(1, noRelease.SustainPoint);
        Assert.Equal(2, noRelease.ReleasePoint);

        // Before the sustain point: it moves down with its point.
        OperatorEnvelope before = Envelope(Done(patch, EnvelopeEdits.RemovePoint(Amplitude, 1)), Amplitude);
        Assert.Equal(1, before.SustainPoint);
        Assert.Equal(0.21, before.Points[1].TimeSeconds, 12);
        Assert.Equal(0.5, before.Points[1].Level);

        // One-shot envelopes stay one-shot.
        OperatorMatrixPatch oneShot = Done(patch, EnvelopeEdits.SetSustain(Amplitude, null));
        OperatorEnvelope shorter = Envelope(Done(oneShot, EnvelopeEdits.RemovePoint(Amplitude, 2)), Amplitude);
        Assert.Null(shorter.SustainPoint);
        Assert.Equal(3, shorter.ReleasePoint);
    }

    [Fact]
    public void RemovePoint_TheFirstOrDownToOne_IsRefused()
    {
        OperatorMatrixPatch patch = TwoOperators();
        Refused(patch, EnvelopeEdits.RemovePoint(Amplitude, 0));
        Refused(patch, EnvelopeEdits.RemovePoint(Amplitude, 4));

        OperatorMatrixPatch withPitch = Done(patch, PatchEdits.SetPitchEnvelope(true));
        Assert.Equal(2, Envelope(withPitch, Pitch).Points.Count);
        Refused(withPitch, EnvelopeEdits.RemovePoint(Pitch, 1));

        // No envelope at all: nothing to remove.
        Refused(patch, EnvelopeEdits.RemovePoint(Pitch, 1));
    }

    [Fact]
    public void SetSustain_NullMakesItOneShot()
    {
        OperatorMatrixPatch patch = TwoOperators();

        OperatorEnvelope oneShot = Envelope(Done(patch, EnvelopeEdits.SetSustain(Amplitude, null)), Amplitude);
        Assert.Null(oneShot.SustainPoint);
        Assert.Equal(4, oneShot.ReleasePoint);

        OperatorEnvelope earlier = Envelope(Done(patch, EnvelopeEdits.SetSustain(Amplitude, 1)), Amplitude);
        Assert.Equal(1, earlier.SustainPoint);
        Assert.Equal(2, earlier.ReleasePoint);

        // Not the first point, and not the last: a sustain needs a release after it.
        Refused(patch, EnvelopeEdits.SetSustain(Amplitude, 0));
        Refused(patch, EnvelopeEdits.SetSustain(Amplitude, 3));
    }

    [Fact]
    public void SetCurve_ChangesOneSegment()
    {
        OperatorMatrixPatch patch = TwoOperators();
        OperatorEnvelope before = Envelope(patch, Amplitude);

        OperatorEnvelope after = Envelope(Done(patch, EnvelopeEdits.SetCurve(Amplitude, 1, EnvelopeCurve.Step)), Amplitude);
        Assert.Equal(EnvelopeCurve.Step, after.Points[1].Curve);
        Assert.Equal(before.Points[1] with { Curve = EnvelopeCurve.Step }, after.Points[1]);
        for (int i = 0; i < before.Points.Count; i++)
        {
            if (i != 1)
            {
                Assert.Equal(before.Points[i], after.Points[i]);
            }
        }

        // The first point has no segment leading into it.
        Refused(patch, EnvelopeEdits.SetCurve(Amplitude, 0, EnvelopeCurve.Linear));
        Refused(patch, EnvelopeEdits.SetCurve(Amplitude, 1, (EnvelopeCurve)42));

        // A filter sweep is edited the same way.
        OperatorMatrixPatch swept = Done(Done(patch, PatchEdits.SetFilter(0, OperatorFilterType.LowPass), Fmx6), PatchEdits.SetSweep(0, true), Fmx6);
        var sweep = new EnvelopeRef(new PatchPart.OperatorPart(0), EnvelopeKind.Sweep);
        OperatorEnvelope linear = Envelope(Done(swept, EnvelopeEdits.SetCurve(sweep, 2, EnvelopeCurve.Linear), Fmx6), sweep);
        Assert.Equal(EnvelopeCurve.Linear, linear.Points[2].Curve);
    }

    [Fact]
    public void PitchEnvelope_LevelsAreCents()
    {
        Assert.Equal((-1200.0, 1200.0), EnvelopeEdits.LevelRange(EnvelopeKind.Pitch));
        Assert.Equal((0.0, 1.0), EnvelopeEdits.LevelRange(EnvelopeKind.Amplitude));
        Assert.Equal((0.0, 1.0), EnvelopeEdits.LevelRange(EnvelopeKind.Sweep));
        Assert.Equal((0.0, 1.0), EnvelopeEdits.LevelRange(EnvelopeKind.Amount));

        OperatorMatrixPatch patch = Done(TwoOperators(), PatchEdits.SetPitchEnvelope(true));
        Assert.Equal(-500.0, Envelope(Done(patch, EnvelopeEdits.MovePoint(Pitch, 0, 0, -500)), Pitch).Points[0].Level);
        Assert.Equal(1200.0, Envelope(Done(patch, EnvelopeEdits.MovePoint(Pitch, 1, 0.2, 5000)), Pitch).Points[1].Level);
        Assert.Equal(-1200.0, Envelope(Done(patch, EnvelopeEdits.MovePoint(Pitch, 1, 0.2, -5000)), Pitch).Points[1].Level);
    }
}
