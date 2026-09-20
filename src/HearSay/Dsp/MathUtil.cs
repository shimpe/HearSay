namespace HearSay.Dsp;

/// <summary>Small numeric helpers shared by the analyzers.</summary>
public static class MathUtil
{
    /// <summary>Lowest level, in dB, that conversions return; keeps silence finite and JSON-serializable.</summary>
    public const double FloorDb = -120.0;

    /// <summary>Converts a power (mean-square) value to dB, floored at <see cref="FloorDb"/>.</summary>
    /// <param name="power">Power value.</param>
    public static double PowerToDb(double power) =>
        power > 1e-12 ? 10.0 * Math.Log10(power) : FloorDb;

    /// <summary>Converts a linear amplitude to dB, floored at <see cref="FloorDb"/>.</summary>
    /// <param name="amplitude">Linear amplitude.</param>
    public static double AmplitudeToDb(double amplitude) =>
        amplitude > 1e-6 ? 20.0 * Math.Log10(amplitude) : FloorDb;

    /// <summary>Converts dB to a linear amplitude factor.</summary>
    /// <param name="db">Level in dB.</param>
    public static double DbToAmplitude(double db) => Math.Pow(10.0, db / 20.0);

    /// <summary>Converts dB to a power factor.</summary>
    /// <param name="db">Level in dB.</param>
    public static double DbToPower(double db) => Math.Pow(10.0, db / 10.0);

    /// <summary>Clamps a value to 0..1; NaN maps to 0.</summary>
    /// <param name="value">Value to clamp.</param>
    public static double Clamp01(double value) => double.IsNaN(value) ? 0.0 : Math.Clamp(value, 0.0, 1.0);

    /// <summary>Returns the smallest power of two that is at least <paramref name="value"/>.</summary>
    /// <param name="value">Minimum size.</param>
    public static int NextPowerOfTwo(int value)
    {
        int result = 1;
        while (result < value)
        {
            result <<= 1;
        }

        return result;
    }

    /// <summary>Interval between two frequencies in cents.</summary>
    /// <param name="frequencyHz">Frequency of interest.</param>
    /// <param name="referenceHz">Reference frequency.</param>
    public static double Cents(double frequencyHz, double referenceHz) =>
        frequencyHz > 0 && referenceHz > 0 ? 1200.0 * Math.Log2(frequencyHz / referenceHz) : 0.0;

    /// <summary>Error function (Abramowitz and Stegun 7.1.26, absolute error below 1.5e-7).</summary>
    /// <param name="x">Argument.</param>
    public static double Erf(double x)
    {
        double sign = x < 0 ? -1.0 : 1.0;
        x = Math.Abs(x);
        double t = 1.0 / (1.0 + (0.3275911 * x));
        double poly = ((((((((1.061405429 * t) - 1.453152027) * t) + 1.421413741) * t) - 0.284496736) * t) + 0.254829592) * t;
        return sign * (1.0 - (poly * Math.Exp(-x * x)));
    }

    /// <summary>Median of the values; the span is sorted in place. Returns 0 for an empty span.</summary>
    /// <param name="values">Values, reordered by the call.</param>
    public static double MedianInPlace(Span<double> values)
    {
        if (values.IsEmpty)
        {
            return 0.0;
        }

        values.Sort();
        int mid = values.Length / 2;
        return (values.Length & 1) == 1 ? values[mid] : 0.5 * (values[mid - 1] + values[mid]);
    }

    /// <summary>
    /// Refines the position of a local extremum from three equally spaced samples by fitting a parabola.
    /// Returns the offset of the vertex from the centre sample, in the range -0.5..+0.5.
    /// </summary>
    /// <param name="left">Sample before the extremum.</param>
    /// <param name="centre">Extremum sample.</param>
    /// <param name="right">Sample after the extremum.</param>
    public static double ParabolicOffset(double left, double centre, double right)
    {
        double denominator = left - (2.0 * centre) + right;
        if (Math.Abs(denominator) < 1e-300)
        {
            return 0.0;
        }

        return Math.Clamp(0.5 * (left - right) / denominator, -0.5, 0.5);
    }
}
