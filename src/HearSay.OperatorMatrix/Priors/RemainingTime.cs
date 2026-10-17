using System.Diagnostics;
using System.Globalization;

namespace HearSay.OperatorMatrix.Priors;

/// <summary>
/// The time left of a long job, such as building a structure library, from the share that is done and the pace since
/// the first share it was given: a job joined half-way is timed from there. Thread-safe.
/// </summary>
public sealed class RemainingTime
{
    private readonly Func<TimeSpan> _elapsed;
    private readonly Lock _lock = new();
    private (double Fraction, TimeSpan At)? _first;

    /// <summary>Starts the clock.</summary>
    public RemainingTime()
    {
        var clock = Stopwatch.StartNew();
        _elapsed = () => clock.Elapsed;
    }

    /// <summary>A clock that reads <paramref name="elapsed"/>, for tests.</summary>
    internal RemainingTime(Func<TimeSpan> elapsed) => _elapsed = elapsed;

    /// <summary>
    /// The time left; null until 2 % more is done and 3 seconds have passed since the first call, when the pace says
    /// little yet.
    /// </summary>
    /// <param name="fraction">Share done, 0..1.</param>
    public TimeSpan? Estimate(double fraction)
    {
        TimeSpan now = _elapsed();
        (double Fraction, TimeSpan At) first;
        lock (_lock)
        {
            first = _first ??= (fraction, now);
        }

        if (fraction >= 1.0)
        {
            return TimeSpan.Zero;
        }

        double done = fraction - first.Fraction;
        TimeSpan elapsed = now - first.At;
        return done < 0.02 || elapsed < TimeSpan.FromSeconds(3) ? null : elapsed * ((1.0 - fraction) / done);
    }

    /// <summary>For example "37% done, about 7 min left".</summary>
    /// <param name="fraction">Share done, 0..1.</param>
    public string Describe(double fraction)
    {
        string done = string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(Math.Clamp(fraction, 0.0, 1.0) * 100):0}% done, ");
        if (Estimate(fraction) is not { } left)
        {
            return done + "estimating the time left";
        }

        int minutes = (int)Math.Ceiling(left.TotalMinutes);
        return done + (left < TimeSpan.FromMinutes(1)
            ? "less than a minute left"
            : minutes < 60
                ? string.Create(CultureInfo.InvariantCulture, $"about {minutes} min left")
                : string.Create(CultureInfo.InvariantCulture, $"about {minutes / 60} h {minutes % 60} min left"));
    }
}
