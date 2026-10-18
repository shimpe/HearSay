using System.Globalization;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.Editing;

/// <summary>Which envelope of a part.</summary>
public enum EnvelopeKind
{
    /// <summary>The amplitude envelope of an operator; levels 0..1.</summary>
    Amplitude,

    /// <summary>The sweep of an operator's filter; levels 0..1 of its depth in octaves.</summary>
    Sweep,

    /// <summary>The envelope of a connection's amount; levels 0..1 of the amount.</summary>
    Amount,

    /// <summary>The pitch envelope of the patch; levels in cents.</summary>
    Pitch,
}

/// <summary>An envelope of a patch: the amplitude or sweep of an operator, the amount of a connection, or the pitch.</summary>
/// <param name="Part">The operator (amplitude, sweep), the connection (amount) or the pitch model (pitch).</param>
/// <param name="Kind">Which envelope of the part.</param>
public sealed record EnvelopeRef(PatchPart Part, EnvelopeKind Kind);

/// <summary>
/// The edits of an envelope: moving, adding and removing points, moving the sustain point (or making the envelope
/// one-shot) and changing the curve of a segment. Every result is a valid envelope (<see cref="OperatorEnvelope.Validate"/>)
/// or the edit is refused. Point indexes are zero-based; point 0 supplies the start level.
/// </summary>
public static class EnvelopeEdits
{
    /// <summary>The envelope a reference points at; null when the patch has none there.</summary>
    /// <param name="p">The patch.</param>
    /// <param name="r">The envelope.</param>
    public static OperatorEnvelope? Get(OperatorMatrixPatch p, EnvelopeRef r)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(r);
        return (r.Part, r.Kind) switch
        {
            (PatchPart.OperatorPart { Index: var op }, EnvelopeKind.Amplitude) when Has(p, op) => p.Operators[op].AmplitudeEnvelope,
            (PatchPart.OperatorPart { Index: var op }, EnvelopeKind.Sweep) when Has(p, op) => p.Operators[op].Filter?.Envelope,
            (PatchPart.ConnectionPart { Source: var s, Destination: var d }, EnvelopeKind.Amount) when Has(p, s) && Has(p, d) => p.ModulationMatrix.Find(s, d)?.AmountEnvelope,
            (PatchPart.PitchPart, EnvelopeKind.Pitch) => p.Pitch.PitchEnvelope,
            _ => null,
        };
    }

    /// <summary>The levels an envelope's points can have: 0..1, or -1200..1200 cents for the pitch.</summary>
    /// <param name="k">Which envelope.</param>
    public static (double Min, double Max) LevelRange(EnvelopeKind k) => k == EnvelopeKind.Pitch ? (-1200.0, 1200.0) : (0.0, 1.0);

    /// <summary>
    /// Moves a point: its time (the duration of the segment that ends at it) is at least 0 and stays 0 for the first
    /// point; its level is clamped to <see cref="LevelRange"/> and, for a connection, to what the modulation limit of its
    /// destination leaves.
    /// </summary>
    /// <param name="r">The envelope.</param>
    /// <param name="index">The point.</param>
    /// <param name="timeSeconds">The new duration of the segment that ends at the point.</param>
    /// <param name="level">The new level.</param>
    public static PatchEdit MovePoint(EnvelopeRef r, int index, double timeSeconds, double level) => (patch, c) =>
    {
        if (!double.IsFinite(timeSeconds) || !double.IsFinite(level))
        {
            return EditOutcome.Refused(patch, "The time and level of an envelope point must be numbers.");
        }

        if (Point(patch, r, index) is { } refusal)
        {
            return EditOutcome.Refused(patch, refusal);
        }

        OperatorEnvelope envelope = Get(patch, r)!;
        EnvelopePoint old = envelope.Points[index];

        // A level the envelope already has outside the range stays reachable, so that moving the point in time only
        // does not make its level jump.
        (double min, double max) = LevelRange(r.Kind);
        double low = Math.Min(min, old.Level);
        double high = Math.Max(max, old.Level);
        if (r is { Kind: EnvelopeKind.Amount, Part: PatchPart.ConnectionPart connection })
        {
            high = Math.Max(low, Math.Min(high, AmountLevelLimit(patch, c, connection.Source, connection.Destination, envelope)));
        }

        var moved = new EnvelopePoint(index == 0 ? 0.0 : Math.Max(0.0, timeSeconds), Math.Clamp(level, low, high), old.Curve);
        return With(patch, r, envelope with { Points = Replaced(envelope.Points, index, moved) });
    };

    /// <summary>
    /// Adds a point in the middle of the segment after a point: halfway in time, on the segment's curve, so that the
    /// envelope sounds as before; halfway in level on an exponential segment to or from a level below 0. The sustain
    /// point stays at its point.
    /// </summary>
    /// <param name="r">The envelope.</param>
    /// <param name="afterIndex">The point at the start of the segment to split.</param>
    public static PatchEdit AddPoint(EnvelopeRef r, int afterIndex) => (patch, _) =>
    {
        if (Point(patch, r, afterIndex) is { } refusal)
        {
            return EditOutcome.Refused(patch, refusal);
        }

        OperatorEnvelope envelope = Get(patch, r)!;
        if (afterIndex == envelope.Points.Count - 1)
        {
            return EditOutcome.Refused(patch, "There is no segment after the last point; add a point after an earlier one.");
        }

        EnvelopePoint start = envelope.Points[afterIndex];
        EnvelopePoint end = envelope.Points[afterIndex + 1];
        double half = end.TimeSeconds / 2.0;
        // An exponential segment to or from a level below 0, which the exponential floor does not render as drawn,
        // is split halfway in level.
        double level = end.Curve switch
        {
            EnvelopeCurve.Exponential when start.Level >= 0.0 && end.Level >= 0.0 => Math.Sqrt(envelope.EffectiveLevel(afterIndex) * envelope.EffectiveLevel(afterIndex + 1)),
            EnvelopeCurve.Step => end.Level,
            _ => (start.Level + end.Level) / 2.0,
        };

        var points = envelope.Points.ToList();
        points[afterIndex + 1] = end with { TimeSeconds = end.TimeSeconds - half };
        points.Insert(afterIndex + 1, new EnvelopePoint(half, level, end.Curve));
        int? sustain = envelope.SustainPoint is int s && s > afterIndex ? s + 1 : envelope.SustainPoint;
        return With(patch, r, Shaped(points, sustain));
    };

    /// <summary>
    /// Removes a point; the point after it keeps its place in time. Refused for the first point and when fewer than two
    /// points would remain. A sustain on the removed point moves to the point before it (or, after the first point, the
    /// one after it); the envelope becomes one-shot only when no point can hold.
    /// </summary>
    /// <param name="r">The envelope.</param>
    /// <param name="index">The point.</param>
    public static PatchEdit RemovePoint(EnvelopeRef r, int index) => (patch, _) =>
    {
        if (Point(patch, r, index) is { } refusal)
        {
            return EditOutcome.Refused(patch, refusal);
        }

        OperatorEnvelope envelope = Get(patch, r)!;
        if (index == 0)
        {
            return EditOutcome.Refused(patch, "The first point supplies the start level and cannot be removed.");
        }

        if (envelope.Points.Count <= 2)
        {
            return EditOutcome.Refused(patch, "An envelope needs at least two points.");
        }

        var points = envelope.Points.ToList();
        if (index + 1 < points.Count)
        {
            points[index + 1] = points[index + 1] with { TimeSeconds = points[index + 1].TimeSeconds + points[index].TimeSeconds };
        }

        points.RemoveAt(index);
        int? sustain = null;
        if (envelope.SustainPoint is int s)
        {
            int moved = s > index ? s - 1 : s == index ? index - 1 : s;

            // A sustain needs a point before it and one after it.
            moved = Math.Min(Math.Max(moved, 1), points.Count - 2);
            sustain = moved >= 1 ? moved : null;
        }

        return With(patch, r, Shaped(points, sustain));
    };

    /// <summary>
    /// Moves the sustain point, or makes the envelope one-shot (null). Refused for the first and the last point: a sustain
    /// needs a point after it to release to.
    /// </summary>
    /// <param name="r">The envelope.</param>
    /// <param name="index">The point to hold while the note is held; null for an envelope that runs once.</param>
    public static PatchEdit SetSustain(EnvelopeRef r, int? index) => (patch, _) =>
    {
        if (Get(patch, r) is not { } envelope)
        {
            return EditOutcome.Refused(patch, NoEnvelope(r));
        }

        if (index is int i && (i < 1 || i > envelope.Points.Count - 2))
        {
            return EditOutcome.Refused(patch, "The sustain point cannot be the first point and needs a point after it to release to.");
        }

        return With(patch, r, Shaped([.. envelope.Points], index));
    };

    /// <summary>
    /// Whether a segment of the envelope can be made exponential. An exponential segment floors every level of its
    /// envelope at <see cref="OperatorEnvelope.ExponentialLevelFloor"/>: harmless for a level of 0, which stays at
    /// -100 dB, but a level below 0 would be heard above 0. So no exponential segments on the pitch, whose levels are
    /// cents either way of the note, nor on an envelope with a level below 0.
    /// </summary>
    /// <param name="envelope">The envelope.</param>
    /// <param name="kind">Which envelope.</param>
    public static bool AllowsExponential(OperatorEnvelope envelope, EnvelopeKind kind) => ExponentialRefusal(envelope, kind) is null;

    /// <summary>
    /// Changes the curve of the segment that ends at a point. Refused for the first point, which ends none, and for an
    /// exponential curve where <see cref="AllowsExponential"/> says no.
    /// </summary>
    /// <param name="r">The envelope.</param>
    /// <param name="index">The point the segment ends at.</param>
    /// <param name="curve">The new curve.</param>
    public static PatchEdit SetCurve(EnvelopeRef r, int index, EnvelopeCurve curve) => (patch, _) =>
    {
        if (!Enum.IsDefined(curve))
        {
            return EditOutcome.Refused(patch, "Unknown curve.");
        }

        if (Point(patch, r, index) is { } refusal)
        {
            return EditOutcome.Refused(patch, refusal);
        }

        if (index == 0)
        {
            return EditOutcome.Refused(patch, "The first point supplies the start level; no segment leads into it.");
        }

        OperatorEnvelope envelope = Get(patch, r)!;
        if (curve == EnvelopeCurve.Exponential && envelope.Points[index].Curve != curve && ExponentialRefusal(envelope, r.Kind) is { } why)
        {
            return EditOutcome.Refused(patch, why);
        }

        return With(patch, r, envelope with { Points = Replaced(envelope.Points, index, envelope.Points[index] with { Curve = curve }) });
    };

    /// <summary>Why no segment of the envelope can be made exponential; null when one can (see <see cref="AllowsExponential"/>).</summary>
    private static string? ExponentialRefusal(OperatorEnvelope envelope, EnvelopeKind kind)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (kind == EnvelopeKind.Pitch)
        {
            return "The pitch envelope has no exponential curves: they would floor its cents at 0 and lose every bend below the note.";
        }

        return envelope.Points.Any(static p => p.Level < 0.0)
            ? "An envelope with a level below 0 has no exponential curves: they would floor that level at 0."
            : null;
    }

    /// <summary>
    /// The highest level a connection's envelope can reach before the connection brings its destination over the
    /// modulation limit; never below the envelope's own peak, so that the limit does not move a level the patch has.
    /// </summary>
    private static double AmountLevelLimit(OperatorMatrixPatch patch, SynthesisCapabilities c, int source, int destination, OperatorEnvelope envelope)
    {
        ModulationEdge edge = patch.ModulationMatrix.Find(source, destination)!;
        double amount = Math.Abs(edge.Amount);
        if (!edge.Enabled || !patch.Operators[source].Enabled || amount == 0.0)
        {
            return double.PositiveInfinity;
        }

        double left = c.MaxModulationRadiansPerDestination - PatchEdits.IncomingModulation(patch, destination, exceptSource: source);
        return Math.Max(left / amount, PatchEdits.EnvelopePeak(envelope));
    }

    private static bool Has(OperatorMatrixPatch patch, int op) => (uint)op < (uint)patch.OperatorCount;

    /// <summary>Why the envelope has no such point; null when it has.</summary>
    private static string? Point(OperatorMatrixPatch patch, EnvelopeRef r, int index)
    {
        if (Get(patch, r) is not { } envelope)
        {
            return NoEnvelope(r);
        }

        return (uint)index < (uint)envelope.Points.Count
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"The envelope has no point {index + 1}; it has {envelope.Points.Count}.");
    }

    private static string NoEnvelope(EnvelopeRef r) => (r.Part, r.Kind) switch
    {
        (PatchPart.OperatorPart { Index: var op }, EnvelopeKind.Amplitude) => $"There is no OP{op + 1}.",
        (PatchPart.OperatorPart { Index: var op }, EnvelopeKind.Sweep) => $"OP{op + 1} has no filter sweep.",
        (PatchPart.ConnectionPart { Source: var s, Destination: var d }, EnvelopeKind.Amount) => $"{PatchEdits.Route(s, d)} has no envelope.",
        (PatchPart.PitchPart, EnvelopeKind.Pitch) => "The patch has no pitch envelope.",
        _ => $"This part of the patch has no {r.Kind.ToString().ToLowerInvariant()} envelope.",
    };

    /// <summary>An envelope of these points, holding at a point or one-shot, with the release point that goes with it.</summary>
    private static OperatorEnvelope Shaped(IReadOnlyList<EnvelopePoint> points, int? sustain)
        => new(points, sustain, sustain is int s ? s + 1 : points.Count);

    private static EnvelopePoint[] Replaced(IReadOnlyList<EnvelopePoint> points, int index, EnvelopePoint point)
    {
        EnvelopePoint[] copy = [.. points];
        copy[index] = point;
        return copy;
    }

    /// <summary>The patch with the envelope replaced; refused when the envelope is not valid.</summary>
    private static EditOutcome With(OperatorMatrixPatch patch, EnvelopeRef r, OperatorEnvelope envelope)
    {
        IReadOnlyList<string> problems = envelope.Validate();
        if (problems.Count > 0)
        {
            return EditOutcome.Refused(patch, problems[0]);
        }

        OperatorMatrixPatch edited = (r.Part, r.Kind) switch
        {
            (PatchPart.OperatorPart { Index: var op }, EnvelopeKind.Amplitude) => patch.WithOperator(op, patch.Operators[op] with { AmplitudeEnvelope = envelope }),
            (PatchPart.OperatorPart { Index: var op }, EnvelopeKind.Sweep) => patch.WithOperator(op, patch.Operators[op] with { Filter = patch.Operators[op].Filter! with { Envelope = envelope } }),
            (PatchPart.ConnectionPart { Source: var s, Destination: var d }, EnvelopeKind.Amount) => patch.WithEdge(patch.ModulationMatrix.Find(s, d)! with { AmountEnvelope = envelope }),
            _ => patch with { Pitch = patch.Pitch with { PitchEnvelope = envelope } },
        };
        return EditOutcome.Done(edited);
    }
}
