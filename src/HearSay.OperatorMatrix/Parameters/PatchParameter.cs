using HearSay.Synthesis.Patches;

namespace HearSay.OperatorMatrix.Parameters;

/// <summary>
/// One searchable property of an <see cref="OperatorMatrixPatch"/>: where it lives in the hierarchy, how it is
/// searched, how it is read and written, and under which conditions it has any effect. Immutable and thread-safe.
/// </summary>
public sealed class PatchParameter
{
    private readonly Func<OperatorMatrixPatch, double> _get;
    private readonly Func<OperatorMatrixPatch, double, OperatorMatrixPatch> _set;
    private readonly Func<OperatorMatrixPatch, bool> _isActive;
    private readonly double _searchMin;
    private readonly double _searchMax;

    /// <summary>Creates a parameter.</summary>
    /// <param name="id">Stable identifier, for example <c>edge.2&gt;1.amount</c>. Operators are numbered from 1.</param>
    /// <param name="groupPath">Position in the hierarchy, for example <c>Patch/Operator 1/Amplitude Envelope</c>.</param>
    /// <param name="kind">How the parameter is searched.</param>
    /// <param name="minimum">Smallest native value.</param>
    /// <param name="maximum">Largest native value.</param>
    /// <param name="transform">Search-space transform.</param>
    /// <param name="get">Reads the native value from a patch.</param>
    /// <param name="set">Returns a patch with the native value replaced.</param>
    /// <param name="isActive">Whether the parameter can be heard in a patch.</param>
    /// <param name="choices">Allowed native values of a discrete parameter.</param>
    public PatchParameter(
        string id,
        string groupPath,
        ParameterSearchKind kind,
        double minimum,
        double maximum,
        IParameterTransform transform,
        Func<OperatorMatrixPatch, double> get,
        Func<OperatorMatrixPatch, double, OperatorMatrixPatch> set,
        Func<OperatorMatrixPatch, bool> isActive,
        IReadOnlyList<double>? choices = null)
    {
        Id = id;
        GroupPath = groupPath;
        Kind = kind;
        Minimum = minimum;
        Maximum = maximum;
        Transform = transform;
        Choices = choices;
        _get = get;
        _set = set;
        _isActive = isActive;
        _searchMin = transform.ToSearchSpace(minimum);
        _searchMax = transform.ToSearchSpace(maximum);
    }

    /// <summary>Stable identifier.</summary>
    public string Id { get; }

    /// <summary>Position in the parameter hierarchy, segments separated by a slash.</summary>
    public string GroupPath { get; }

    /// <summary>How the parameter is searched.</summary>
    public ParameterSearchKind Kind { get; }

    /// <summary>Smallest native value.</summary>
    public double Minimum { get; }

    /// <summary>Largest native value.</summary>
    public double Maximum { get; }

    /// <summary>Search-space transform.</summary>
    public IParameterTransform Transform { get; }

    /// <summary>Allowed native values of a discrete parameter; null for continuous parameters.</summary>
    public IReadOnlyList<double>? Choices { get; }

    /// <summary>True for parameters that gradients, local models and continuous optimizers may move.</summary>
    public bool IsSmooth => Kind is ParameterSearchKind.Continuous or ParameterSearchKind.WarpedContinuous;

    /// <summary>True for parameters that define the structure, and with it the basin, of a patch.</summary>
    public bool IsStructural => Kind == ParameterSearchKind.StructuralDiscrete;

    /// <summary>Reads the native value.</summary>
    /// <param name="patch">Patch to read.</param>
    public double Get(OperatorMatrixPatch patch) => _get(patch);

    /// <summary>Returns a patch with the native value replaced; the value is limited to the allowed range.</summary>
    /// <param name="patch">Patch to change.</param>
    /// <param name="nativeValue">New native value.</param>
    public OperatorMatrixPatch Set(OperatorMatrixPatch patch, double nativeValue) =>
        _set(patch, Math.Clamp(nativeValue, Minimum, Maximum));

    /// <summary>
    /// Conditional activity: false when the value cannot be heard in this patch, for example the amount of a disabled
    /// edge or anything that belongs to a disabled operator.
    /// </summary>
    /// <param name="patch">Patch to inspect.</param>
    public bool IsActive(OperatorMatrixPatch patch) => _isActive(patch);

    /// <summary>Reads the value as a position 0..1 on the search axis.</summary>
    /// <param name="patch">Patch to read.</param>
    public double GetNormalized(OperatorMatrixPatch patch) => Normalize(Get(patch));

    /// <summary>Returns a patch with the value set from a position 0..1 on the search axis.</summary>
    /// <param name="patch">Patch to change.</param>
    /// <param name="position">Position on the search axis; limited to 0..1.</param>
    public OperatorMatrixPatch SetNormalized(OperatorMatrixPatch patch, double position) => Set(patch, Denormalize(position));

    /// <summary>Native value to position 0..1 on the search axis.</summary>
    /// <param name="nativeValue">Native value.</param>
    public double Normalize(double nativeValue)
    {
        double range = _searchMax - _searchMin;
        return range > 0 ? Math.Clamp((Transform.ToSearchSpace(Math.Clamp(nativeValue, Minimum, Maximum)) - _searchMin) / range, 0.0, 1.0) : 0.0;
    }

    /// <summary>Position 0..1 on the search axis to native value.</summary>
    /// <param name="position">Position on the search axis.</param>
    public double Denormalize(double position) =>
        Math.Clamp(Transform.ToNativeSpace(_searchMin + (Math.Clamp(position, 0.0, 1.0) * (_searchMax - _searchMin))), Minimum, Maximum);

    /// <inheritdoc />
    public override string ToString() => $"{Id} ({Kind})";
}

/// <summary>A node of the parameter hierarchy.</summary>
/// <param name="Name">Name of this level, for example <c>Operator 1</c>.</param>
/// <param name="Path">Full path from the root.</param>
/// <param name="Parameters">Parameters directly at this level.</param>
/// <param name="Children">Sub-groups.</param>
public sealed record ParameterGroup(string Name, string Path, IReadOnlyList<PatchParameter> Parameters, IReadOnlyList<ParameterGroup> Children)
{
    /// <summary>All parameters of this group and its sub-groups.</summary>
    public IEnumerable<PatchParameter> All()
    {
        foreach (PatchParameter parameter in Parameters)
        {
            yield return parameter;
        }

        foreach (ParameterGroup child in Children)
        {
            foreach (PatchParameter parameter in child.All())
            {
                yield return parameter;
            }
        }
    }
}
