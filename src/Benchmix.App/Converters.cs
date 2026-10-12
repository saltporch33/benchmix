using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Benchmix.App;

/// <summary>Seconds to <c>m:ss.t</c>, the resolution a mashup is actually edited at.</summary>
public sealed class TimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double s = value is double d ? d : 0;
        if (double.IsNaN(s) || double.IsInfinity(s) || s < 0) s = 0;
        var ts = TimeSpan.FromSeconds(s);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 100}"
            : $"{ts.Minutes}:{ts.Seconds:00}.{ts.Milliseconds / 100}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Same, prefixed with a slash — for the "of total" half of a transport readout.</summary>
public sealed class TimeSlashConverter : IValueConverter
{
    private static readonly TimeConverter Inner = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => "/ " + Inner.Convert(value, targetType, parameter, culture);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class PlayGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "⏸" : "▶";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Collapsed when true: the other half of a pair that swaps with <see cref="BoolToVisibilityConverter"/>.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Where a tempo ramp's BPM box sits in its lane: just right of the ramp's end, or just left of it
/// when there is no room on the right. Inputs: the end in timeline seconds, the view's scroll, its
/// zoom, and the lane's width.
/// </summary>
public sealed class RampBoxPlacementConverter : IMultiValueConverter
{
    public const double Gap = 10, Width = 60;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        double At(int i) => values.Length > i && values[i] is double d && double.IsFinite(d) ? d : 0;
        double x = (At(0) - At(1)) * Math.Max(0.01, At(2));
        double width = At(3);
        double left = x + Gap + Width <= width ? x + Gap : x - Width - Gap;
        return new Thickness(Math.Max(2, left), 0, 0, 0);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Visible only when the bound count is zero — the empty-state prompt.</summary>
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int i && i == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
