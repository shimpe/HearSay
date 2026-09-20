namespace HearSay.Optimization.History;

/// <summary>Thread-safe, append-only record of all observations of one optimization run.</summary>
public sealed class OptimizationHistory
{
    private readonly Lock gate = new();
    private readonly List<OptimizationObservation> observations = [];
    private OptimizationObservation? best;

    /// <summary>Creates an empty history.</summary>
    public OptimizationHistory()
    {
    }

    /// <summary>Creates a history from stored observations.</summary>
    /// <param name="observations">Observations in iteration order.</param>
    public OptimizationHistory(IEnumerable<OptimizationObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        foreach (OptimizationObservation observation in observations)
        {
            Add(observation);
        }
    }

    /// <summary>Snapshot of all observations in the order they were added.</summary>
    public IReadOnlyList<OptimizationObservation> Observations
    {
        get
        {
            lock (gate)
            {
                return observations.ToArray();
            }
        }
    }

    /// <summary>Number of observations.</summary>
    public int Count
    {
        get
        {
            lock (gate)
            {
                return observations.Count;
            }
        }
    }

    /// <summary>Observation with the lowest loss so far; the earliest one wins ties. Null while the history is empty.</summary>
    public OptimizationObservation? Best
    {
        get
        {
            lock (gate)
            {
                return best;
            }
        }
    }

    /// <summary>Most recently added observation, or null while the history is empty.</summary>
    public OptimizationObservation? Latest
    {
        get
        {
            lock (gate)
            {
                return observations.Count > 0 ? observations[^1] : null;
            }
        }
    }

    /// <summary>Appends an observation.</summary>
    /// <param name="observation">Observation to add.</param>
    public void Add(OptimizationObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        lock (gate)
        {
            observations.Add(observation);
            if (best is null || observation.OverallLoss < best.OverallLoss)
            {
                best = observation;
            }
        }
    }

    /// <summary>Serializes the observations to JSON.</summary>
    public string ToJson() => HearSayJson.Serialize(Observations);

    /// <summary>Restores a history from JSON produced by <see cref="ToJson"/>.</summary>
    /// <param name="json">JSON text.</param>
    public static OptimizationHistory FromJson(string json) =>
        new(HearSayJson.Deserialize<List<OptimizationObservation>>(json));
}
