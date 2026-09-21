namespace HearSay.Modeling.Parameters;

/// <summary>Mathematical nature of a synth parameter.</summary>
public enum SynthParameterKind
{
    /// <summary>Real-valued; finite differences and local linear models apply.</summary>
    Continuous,

    /// <summary>Ordered whole numbers; treated like a continuous parameter whose values are rounded.</summary>
    Integer,

    /// <summary>On/off switch (0 or 1). Never differentiated; every change is a separate experiment.</summary>
    Boolean,

    /// <summary>Unordered selector such as a waveform or filter type. Never differentiated; every change is a separate experiment.</summary>
    Categorical,
}

/// <summary>
/// Synth-independent description of one parameter. The library never talks to a synthesizer; it only needs ranges to
/// normalize values to 0..1 so that parameters become comparable.
/// </summary>
/// <param name="Id">Stable identifier used in states, histories and learned relations.</param>
/// <param name="Name">Display name.</param>
/// <param name="Value">Current or default value in native units.</param>
/// <param name="MinValue">Smallest allowed value in native units.</param>
/// <param name="MaxValue">Largest allowed value in native units.</param>
/// <param name="Kind">Mathematical nature of the parameter.</param>
/// <param name="RecommendedMinStep">Smallest change, in native units, that is worth trying; smaller changes are lost in measurement noise.</param>
/// <param name="RecommendedMaxStep">Largest change, in native units, that guidance may suggest in one step.</param>
/// <param name="Categories">
/// Optional generic categories (see <see cref="Comparison.SynthParameterCategories"/>). They enable low-confidence,
/// prior-based suggestions before anything has been learned about the parameter.
/// </param>
public sealed record SynthParameter(
    string Id,
    string Name,
    double Value,
    double MinValue,
    double MaxValue,
    SynthParameterKind Kind = SynthParameterKind.Continuous,
    double? RecommendedMinStep = null,
    double? RecommendedMaxStep = null,
    IReadOnlyList<string>? Categories = null)
{
    /// <summary>True for every kind except <see cref="SynthParameterKind.Continuous"/>.</summary>
    public bool IsDiscrete => Kind != SynthParameterKind.Continuous;

    /// <summary>True when values have a meaningful order, so that sensitivities (d feature / d parameter) make sense.</summary>
    public bool IsOrdered => Kind is SynthParameterKind.Continuous or SynthParameterKind.Integer;

    /// <summary>Width of the value range in native units.</summary>
    public double Range => MaxValue - MinValue;

    /// <summary>Maps a native value to 0..1.</summary>
    /// <param name="value">Value in native units.</param>
    public double Normalize(double value) => Range > 0 ? Math.Clamp((value - MinValue) / Range, 0.0, 1.0) : 0.0;

    /// <summary>Maps a normalized value back to native units, clamped to the range and rounded for discrete kinds.</summary>
    /// <param name="normalizedValue">Value in 0..1.</param>
    public double Denormalize(double normalizedValue) => Coerce(MinValue + (Math.Clamp(normalizedValue, 0.0, 1.0) * Range));

    /// <summary>Clamps a native value to the range and rounds it for discrete kinds.</summary>
    /// <param name="value">Value in native units.</param>
    public double Coerce(double value)
    {
        double clamped = Math.Clamp(value, MinValue, MaxValue);
        return IsDiscrete ? Math.Round(clamped) : clamped;
    }
}

/// <summary>A complete set of parameter values in native units.</summary>
/// <param name="Values">Value per parameter id.</param>
public sealed record SynthParameterState(IReadOnlyDictionary<string, double> Values)
{
    /// <summary>Returns a copy with one value replaced.</summary>
    /// <param name="parameterId">Parameter to change.</param>
    /// <param name="value">New value in native units.</param>
    public SynthParameterState With(string parameterId, double value)
    {
        var copy = new Dictionary<string, double>(Values, StringComparer.Ordinal) { [parameterId] = value };
        return new SynthParameterState(copy);
    }

    /// <summary>Creates a state from the default values of the parameter definitions.</summary>
    /// <param name="parameters">Parameter definitions.</param>
    public static SynthParameterState FromDefaults(IEnumerable<SynthParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (SynthParameter parameter in parameters)
        {
            values[parameter.Id] = parameter.Value;
        }

        return new SynthParameterState(values);
    }
}

/// <summary>The ordered set of parameter definitions of one synth. Immutable.</summary>
public sealed class SynthParameterSpace
{
    private readonly Dictionary<string, int> indexById = new(StringComparer.Ordinal);

    /// <summary>Creates a parameter space.</summary>
    /// <param name="parameters">Parameter definitions; ids must be unique.</param>
    public SynthParameterSpace(IEnumerable<SynthParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var list = new List<SynthParameter>();
        foreach (SynthParameter parameter in parameters)
        {
            if (parameter.MaxValue < parameter.MinValue)
            {
                throw new ArgumentException($"Parameter '{parameter.Id}' has an inverted range.", nameof(parameters));
            }

            if (!indexById.TryAdd(parameter.Id, list.Count))
            {
                throw new ArgumentException($"Duplicate parameter id '{parameter.Id}'.", nameof(parameters));
            }

            list.Add(parameter);
        }

        Parameters = list;
    }

    /// <summary>Parameter definitions in their fixed order.</summary>
    public IReadOnlyList<SynthParameter> Parameters { get; }

    /// <summary>Returns a definition by id, or null when unknown.</summary>
    /// <param name="parameterId">Parameter id.</param>
    public SynthParameter? Find(string parameterId) =>
        indexById.TryGetValue(parameterId, out int index) ? Parameters[index] : null;

    /// <summary>Normalized value (0..1) of one parameter in a state; parameters missing from the state use their default.</summary>
    /// <param name="state">Parameter state.</param>
    /// <param name="parameter">Parameter definition.</param>
    public static double NormalizedValue(SynthParameterState state, SynthParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(parameter);
        return parameter.Normalize(state.Values.TryGetValue(parameter.Id, out double value) ? value : parameter.Value);
    }

    /// <summary>Normalized values of all parameters, in definition order.</summary>
    /// <param name="state">Parameter state.</param>
    public double[] Normalize(SynthParameterState state)
    {
        var result = new double[Parameters.Count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = NormalizedValue(state, Parameters[i]);
        }

        return result;
    }
}
