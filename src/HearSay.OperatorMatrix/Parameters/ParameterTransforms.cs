namespace HearSay.OperatorMatrix.Parameters;

/// <summary>How a parameter takes part in a search.</summary>
public enum ParameterSearchKind
{
    /// <summary>Smooth, searched on its native scale.</summary>
    Continuous,

    /// <summary>Smooth, searched through a transform that makes equal steps sound equally large.</summary>
    WarpedContinuous,

    /// <summary>A small ordered set of values; neighbours sound related.</summary>
    OrderedDiscrete,

    /// <summary>Changes what the patch is: an operator or edge that exists or not, a waveform, a mode, a coarse ratio. Never moved by gradients or small steps; it defines a basin.</summary>
    StructuralDiscrete,

    /// <summary>Unordered choice that does not change the structure of the patch.</summary>
    Categorical,
}

/// <summary>Maps between the native value of a parameter and the axis a search moves along.</summary>
public interface IParameterTransform
{
    /// <summary>Native value to search coordinate.</summary>
    /// <param name="nativeValue">Value in the unit of the patch.</param>
    double ToSearchSpace(double nativeValue);

    /// <summary>Search coordinate to native value.</summary>
    /// <param name="searchValue">Value on the search axis.</param>
    double ToNativeSpace(double searchValue);
}

/// <summary>Built-in transforms.</summary>
public static class ParameterTransforms
{
    /// <summary>No warping.</summary>
    public static IParameterTransform Linear { get; } = new LinearTransform();

    /// <summary>Logarithmic: equal steps are equal ratios. For strictly positive values such as times and frequencies.</summary>
    public static IParameterTransform Logarithmic { get; } = new LogTransform();

    /// <summary>
    /// Logarithmic above <paramref name="knee"/> and linear below it, so that zero is reachable. For levels and
    /// modulation amounts.
    /// </summary>
    /// <param name="knee">Native value around which the axis changes from linear to logarithmic.</param>
    public static IParameterTransform SoftLogarithmic(double knee) => new SoftLogTransform(knee);

    private sealed class LinearTransform : IParameterTransform
    {
        public double ToSearchSpace(double nativeValue) => nativeValue;

        public double ToNativeSpace(double searchValue) => searchValue;
    }

    private sealed class LogTransform : IParameterTransform
    {
        public double ToSearchSpace(double nativeValue) => Math.Log(Math.Max(nativeValue, 1e-12));

        public double ToNativeSpace(double searchValue) => Math.Exp(searchValue);
    }

    private sealed class SoftLogTransform(double knee) : IParameterTransform
    {
        public double ToSearchSpace(double nativeValue) => Math.Sign(nativeValue) * Math.Log(1.0 + (Math.Abs(nativeValue) / knee));

        public double ToNativeSpace(double searchValue) => Math.Sign(searchValue) * knee * (Math.Exp(Math.Abs(searchValue)) - 1.0);
    }
}
