using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace HearSay.Studio;

/// <summary>Maps a modulation intensity 0..1 to a cell fill: dark for nothing, cyan for strong modulation.</summary>
public sealed class IntensityToBrushConverter : IValueConverter
{
    private static readonly Color Empty = Color.Parse("#0B1220");
    private static readonly Color Full = Color.Parse("#22D3EE");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double t = value is double d ? Math.Clamp(d, 0.0, 1.0) : 0.0;
        if (t == 0.0)
        {
            return new SolidColorBrush(Empty);
        }

        // Perceived brightness grows fast at first so that small amounts stay visible.
        double k = 0.25 + (0.75 * Math.Sqrt(t));
        return new SolidColorBrush(Color.FromRgb(
            (byte)(Empty.R + ((Full.R - Empty.R) * k)),
            (byte)(Empty.G + ((Full.G - Empty.G) * k)),
            (byte)(Empty.B + ((Full.B - Empty.B) * k))));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Text colour that stays readable on a cell of any intensity.</summary>
public sealed class IntensityToForegroundConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d && d > 0.45 ? new SolidColorBrush(Color.Parse("#0F172A")) : new SolidColorBrush(Color.Parse("#E2E8F0"));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
