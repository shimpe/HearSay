namespace HearSay.Synthesis.Patches;

/// <summary>Shape of the envelope segment that leads into a point.</summary>
public enum EnvelopeCurve
{
    /// <summary>Straight line in the level domain.</summary>
    Linear,

    /// <summary>
    /// Constant ratio per unit of time, a straight line in decibels. Levels are floored at
    /// <see cref="OperatorEnvelope.ExponentialLevelFloor"/> because an exponential segment cannot reach zero.
    /// </summary>
    Exponential,

    /// <summary>Jumps to the level of the point at the start of the segment and stays there.</summary>
    Step,
}

/// <summary>One breakpoint of an <see cref="OperatorEnvelope"/>.</summary>
/// <param name="TimeSeconds">
/// Duration of the segment that ends at this point, in seconds. Ignored (and required to be zero) for the first
/// point, which only supplies the start level.
/// </param>
/// <param name="Level">Level reached at this point. The unit depends on what the envelope controls.</param>
/// <param name="Curve">Shape of the segment that ends at this point.</param>
public sealed record EnvelopePoint(double TimeSeconds, double Level, EnvelopeCurve Curve = EnvelopeCurve.Linear);

/// <summary>
/// Backend-independent breakpoint envelope. It is used for operator amplitudes (levels 0..1), modulation-edge
/// amounts (levels are multipliers of the edge amount) and pitch (levels in cents).
/// </summary>
/// <remarks>
/// <para>
/// <c>Points[0]</c> is the start level. Every following point is reached after its own
/// <see cref="EnvelopePoint.TimeSeconds"/>; times are segment durations, not absolute positions, so every point can
/// be moved independently without reordering.
/// </para>
/// <para>
/// With a <see cref="SustainPoint"/> the envelope holds at that point while the gate is open and continues with
/// <see cref="ReleasePoint"/> when the gate closes. Closing the gate early jumps to the release section from the
/// level reached so far. Without a sustain point the envelope runs through all points and ignores the gate.
/// </para>
/// </remarks>
/// <param name="Points">Breakpoints; at least two.</param>
/// <param name="SustainPoint">Index of the point that is held while the gate is open, or null for a one-shot envelope.</param>
/// <param name="ReleasePoint">
/// Index of the first point of the release section. <c>SustainPoint + 1</c> for sustaining envelopes,
/// <c>Points.Count</c> for one-shot envelopes.
/// </param>
public sealed record OperatorEnvelope(IReadOnlyList<EnvelopePoint> Points, int? SustainPoint, int ReleasePoint)
{
    /// <summary>Lowest level of an envelope that contains exponential segments (-100 dB).</summary>
    public const double ExponentialLevelFloor = 1e-5;

    /// <summary>True when any segment is exponential, which floors all levels at <see cref="ExponentialLevelFloor"/>.</summary>
    public bool HasExponentialSegments
    {
        get
        {
            for (int i = 1; i < Points.Count; i++)
            {
                if (Points[i].Curve == EnvelopeCurve.Exponential)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Level of a point as it is rendered and exported, including the exponential floor.</summary>
    /// <param name="index">Point index.</param>
    public double EffectiveLevel(int index)
    {
        double level = Points[index].Level;
        return HasExponentialSegments ? Math.Max(level, ExponentialLevelFloor) : level;
    }

    /// <summary>Total duration of the release section in seconds.</summary>
    public double ReleaseSeconds
    {
        get
        {
            double sum = 0;
            for (int i = Math.Max(1, ReleasePoint); i < Points.Count; i++)
            {
                sum += Points[i].TimeSeconds;
            }

            return sum;
        }
    }

    /// <summary>Returns the problems that make this envelope unusable; empty when it is valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Points is null || Points.Count < 2)
        {
            problems.Add("An envelope needs at least two points.");
            return problems;
        }

        for (int i = 0; i < Points.Count; i++)
        {
            EnvelopePoint point = Points[i];
            if (!double.IsFinite(point.Level) || !double.IsFinite(point.TimeSeconds))
            {
                problems.Add($"Point {i} is not finite.");
            }

            if (point.TimeSeconds < 0)
            {
                problems.Add($"Point {i} has a negative duration.");
            }
        }

        if (Points[0].TimeSeconds != 0)
        {
            problems.Add("The first point supplies the start level and must have a duration of zero.");
        }

        if (SustainPoint is int sustain)
        {
            if (sustain < 1 || sustain >= Points.Count - 1)
            {
                problems.Add("The sustain point must be followed by at least one release point and cannot be the first point.");
            }

            if (ReleasePoint != sustain + 1)
            {
                problems.Add("The release point must directly follow the sustain point.");
            }
        }
        else if (ReleasePoint != Points.Count)
        {
            problems.Add("A one-shot envelope has no release section; the release point must equal the point count.");
        }

        return problems;
    }

    /// <summary>Attack, decay to a sustain level, release. Decay and release are exponential.</summary>
    /// <param name="attackSeconds">Attack time.</param>
    /// <param name="decaySeconds">Decay time.</param>
    /// <param name="sustainLevel">Sustain level, 0..1.</param>
    /// <param name="releaseSeconds">Release time.</param>
    /// <param name="peakLevel">Level at the end of the attack.</param>
    public static OperatorEnvelope Adsr(double attackSeconds, double decaySeconds, double sustainLevel, double releaseSeconds, double peakLevel = 1.0) =>
        new(
            [
                new EnvelopePoint(0, 0),
                new EnvelopePoint(attackSeconds, peakLevel),
                new EnvelopePoint(decaySeconds, sustainLevel, EnvelopeCurve.Exponential),
                new EnvelopePoint(releaseSeconds, 0, EnvelopeCurve.Exponential),
            ],
            SustainPoint: 2,
            ReleasePoint: 3);

    /// <summary>
    /// Attack followed by a fast and a slow exponential decay, the shape of struck and plucked sounds. The envelope
    /// holds the final level while the gate is open.
    /// </summary>
    /// <param name="attackSeconds">Attack time.</param>
    /// <param name="fastDecaySeconds">Duration of the first decay.</param>
    /// <param name="breakLevel">Level at which the fast decay hands over to the slow decay.</param>
    /// <param name="slowDecaySeconds">Duration of the second decay.</param>
    /// <param name="finalLevel">Level at the end of the slow decay.</param>
    /// <param name="releaseSeconds">Release time.</param>
    public static OperatorEnvelope TwoStageDecay(
        double attackSeconds,
        double fastDecaySeconds,
        double breakLevel,
        double slowDecaySeconds,
        double finalLevel,
        double releaseSeconds) =>
        new(
            [
                new EnvelopePoint(0, 0),
                new EnvelopePoint(attackSeconds, 1.0),
                new EnvelopePoint(fastDecaySeconds, breakLevel, EnvelopeCurve.Exponential),
                new EnvelopePoint(slowDecaySeconds, finalLevel, EnvelopeCurve.Exponential),
                new EnvelopePoint(releaseSeconds, 0, EnvelopeCurve.Exponential),
            ],
            SustainPoint: 3,
            ReleasePoint: 4);

    /// <summary>Constant level while the gate is open, with a short linear fade at both ends.</summary>
    /// <param name="level">Held level.</param>
    /// <param name="fadeSeconds">Fade time at note start and release.</param>
    public static OperatorEnvelope Constant(double level = 1.0, double fadeSeconds = 0.005) =>
        new(
            [
                new EnvelopePoint(0, 0),
                new EnvelopePoint(fadeSeconds, level),
                new EnvelopePoint(fadeSeconds, 0),
            ],
            SustainPoint: 1,
            ReleasePoint: 2);
}
