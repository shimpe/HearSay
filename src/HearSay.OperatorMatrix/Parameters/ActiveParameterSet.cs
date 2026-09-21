using HearSay.Synthesis.Patches;

namespace HearSay.OperatorMatrix.Parameters;

/// <summary>
/// Temporary macro parameter: one search axis that moves several parameters together, for example all modulation
/// decay times. Early search stages optimize a few macros; later stages split them into their members.
/// </summary>
public static class ParameterTie
{
    /// <summary>Creates a macro that shifts all members by the same distance along their own search axes.</summary>
    /// <param name="id">Identifier of the macro.</param>
    /// <param name="members">Smooth parameters that move together.</param>
    /// <param name="basePatch">Patch whose values are the neutral position of the macro.</param>
    /// <param name="range">Largest shift in either direction, as a fraction of the search axis of each member.</param>
    /// <exception cref="ArgumentException">A member is not smooth, or there are no members.</exception>
    public static PatchParameter Create(string id, IReadOnlyList<PatchParameter> members, OperatorMatrixPatch basePatch, double range = 0.35)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(basePatch);
        if (members.Count == 0)
        {
            throw new ArgumentException("A tie needs at least one member.", nameof(members));
        }

        var neutral = new double[members.Count];
        for (int i = 0; i < members.Count; i++)
        {
            if (!members[i].IsSmooth)
            {
                throw new ArgumentException($"'{members[i].Id}' is not smooth and cannot be tied.", nameof(members));
            }

            neutral[i] = members[i].GetNormalized(basePatch);
        }

        return new PatchParameter(
            id,
            "Patch/Macros",
            ParameterSearchKind.Continuous,
            -range,
            range,
            ParameterTransforms.Linear,
            patch =>
            {
                double sum = 0;
                for (int i = 0; i < members.Count; i++)
                {
                    sum += members[i].GetNormalized(patch) - neutral[i];
                }

                return sum / members.Count;
            },
            (patch, shift) =>
            {
                for (int i = 0; i < members.Count; i++)
                {
                    patch = members[i].SetNormalized(patch, neutral[i] + shift);
                }

                return patch;
            },
            patch => members.Any(m => m.IsActive(patch)));
    }
}

/// <summary>
/// The parameters a continuous optimizer is allowed to move right now; everything else is frozen. It converts
/// between a patch and a point in the unit cube. A search normally keeps this between 5 and 25 parameters.
/// </summary>
public sealed class ActiveParameterSet
{
    /// <summary>Creates a set.</summary>
    /// <param name="parameters">Smooth parameters to optimize together.</param>
    /// <exception cref="ArgumentException">A parameter is structural or discrete.</exception>
    public ActiveParameterSet(IReadOnlyList<PatchParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        foreach (PatchParameter parameter in parameters)
        {
            if (!parameter.IsSmooth)
            {
                throw new ArgumentException($"'{parameter.Id}' is {parameter.Kind}; only smooth parameters can be optimized continuously.", nameof(parameters));
            }
        }

        Parameters = parameters;
    }

    /// <summary>Parameters of the set, in the order of the vector.</summary>
    public IReadOnlyList<PatchParameter> Parameters { get; }

    /// <summary>Number of dimensions.</summary>
    public int Count => Parameters.Count;

    /// <summary>Position of a patch in the unit cube.</summary>
    /// <param name="patch">Patch to read.</param>
    public double[] ToVector(OperatorMatrixPatch patch)
    {
        var vector = new double[Parameters.Count];
        for (int i = 0; i < vector.Length; i++)
        {
            vector[i] = Parameters[i].GetNormalized(patch);
        }

        return vector;
    }

    /// <summary>Returns the patch at a position in the unit cube; all frozen parameters keep their values.</summary>
    /// <param name="patch">Patch that supplies the frozen values.</param>
    /// <param name="vector">Position; every coordinate is limited to 0..1.</param>
    public OperatorMatrixPatch Apply(OperatorMatrixPatch patch, ReadOnlySpan<double> vector)
    {
        for (int i = 0; i < Parameters.Count; i++)
        {
            patch = Parameters[i].SetNormalized(patch, vector[i]);
        }

        return patch;
    }
}
