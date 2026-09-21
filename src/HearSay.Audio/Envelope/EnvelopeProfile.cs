using System.Text.Json.Serialization;

namespace HearSay.Envelope;

/// <summary>Configuration of the amplitude-envelope analysis.</summary>
public sealed record EnvelopeOptions
{
    /// <summary>Time between envelope points in milliseconds.</summary>
    public double HopMs { get; init; } = 1.0;

    /// <summary>
    /// Shortest RMS window in milliseconds. The actual window is the smallest whole number of fundamental periods
    /// that is at least this long, which removes ripple at the note frequency from the envelope.
    /// </summary>
    public double MinimumWindowMs { get; init; } = 3.0;

    /// <summary>Lowest envelope level, in dB relative to the peak, that is represented.</summary>
    public double FloorDb { get; init; } = -60.0;

    /// <summary>Steepest envelope slope, in dB per second, that still counts as a sustain plateau.</summary>
    public double PlateauMaxSlopeDbPerSecond { get; init; } = 20.0;

    /// <summary>Shortest plateau, in milliseconds, that is accepted as a sustain stage.</summary>
    public double MinimumPlateauMs { get; init; } = 40.0;

    /// <summary>Level drop, in dB, that defines the end of the release (and of the decay of a note without sustain).</summary>
    public double ReleaseDropDb { get; init; } = 20.0;
}

/// <summary>
/// Amplitude envelope of one note with robustly estimated stage timings. A stage that cannot be measured is null
/// and has confidence 0; nothing is invented.
/// </summary>
public sealed record EnvelopeProfile
{
    /// <summary>Time between points of <see cref="LevelsDb"/> in milliseconds.</summary>
    public double HopMs { get; init; }

    /// <summary>RMS window that was used, in milliseconds. Timings shorter than this are resolution limited.</summary>
    public double WindowMs { get; init; }

    /// <summary>
    /// Silence, in milliseconds, that was placed in front of the note before the envelope was computed. Point <c>i</c> of
    /// <see cref="LevelsDb"/> lies at <c>i * HopMs - LeadInMs</c>; all other times are relative to the start of the note.
    /// </summary>
    public double LeadInMs { get; init; }

    /// <summary>Envelope in dB relative to its peak, floored at <see cref="EnvelopeOptions.FloorDb"/>.</summary>
    [JsonIgnore]
    public float[] LevelsDb { get; init; } = [];

    /// <summary>Total duration of the analysed note in milliseconds.</summary>
    public double DurationMs { get; init; }

    /// <summary>Time of the onset (envelope first exceeds 10% of its peak) in milliseconds.</summary>
    public double OnsetMs { get; init; }

    /// <summary>Time at which the envelope first reaches its peak, in milliseconds.</summary>
    public double PeakMs { get; init; }

    /// <summary>Peak RMS level of the analysed signal in dB.</summary>
    public double PeakDb { get; init; }

    /// <summary>
    /// Attack time in milliseconds: the 10%..90% rise time divided by 0.8, which equals the full ramp time of a
    /// linear attack.
    /// </summary>
    public double? AttackMs { get; init; }

    /// <summary>Confidence (0..1) of <see cref="AttackMs"/>.</summary>
    public double AttackConfidence { get; init; }

    /// <summary>
    /// Decay time in milliseconds. With a sustain stage: time from the peak until the envelope is within 10% of the
    /// sustain level (0 when there is no overshoot). Without a sustain stage: time from the peak to fall by
    /// <see cref="EnvelopeOptions.ReleaseDropDb"/>.
    /// </summary>
    public double? DecayMs { get; init; }

    /// <summary>Confidence (0..1) of <see cref="DecayMs"/>.</summary>
    public double DecayConfidence { get; init; }

    /// <summary>True when a sustain plateau was found.</summary>
    public bool HasSustain { get; init; }

    /// <summary>Sustain level in dB relative to the envelope peak (0 or negative).</summary>
    public double? SustainDb { get; init; }

    /// <summary>Confidence (0..1) of <see cref="SustainDb"/>.</summary>
    public double SustainConfidence { get; init; }

    /// <summary>Time at which the sustain plateau starts, in milliseconds.</summary>
    public double? SustainStartMs { get; init; }

    /// <summary>Time at which the release starts, in milliseconds.</summary>
    public double? ReleaseStartMs { get; init; }

    /// <summary>
    /// Release time in milliseconds: time for the envelope to fall by <see cref="EnvelopeOptions.ReleaseDropDb"/>
    /// from the sustain level once the release has started.
    /// </summary>
    public double? ReleaseMs { get; init; }

    /// <summary>Confidence (0..1) of <see cref="ReleaseMs"/>.</summary>
    public double ReleaseConfidence { get; init; }
}
