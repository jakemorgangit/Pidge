using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Pidge.Helpers;

public class InverseBoolToVisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class NullToVisConverter : IValueConverter
{
    /// <summary>Visible when not null. Pass "invert" to flip.</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool visible = value != null;
        if (parameter as string == "invert") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class CountToVisConverter : IValueConverter
{
    /// <summary>Visible when the number is greater than zero. Pass "invert" to flip.</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool visible = value is int i ? i > 0 : value is long l && l > 0;
        if (parameter as string == "invert") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class StringToVisConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Two-way binds a RadioButton's IsChecked to an enum value named by ConverterParameter.</summary>
public class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == parameter as string;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string name ? Enum.Parse(targetType, name) : Binding.DoNothing;
}

/// <summary>Bold when false (unread), normal when true.</summary>
public class ReadToWeightConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? FontWeights.Normal : FontWeights.SemiBold;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class FractionToWidthConverter : IValueConverter
{
    /// <summary>Multiplies a 0..1 fraction by the pixel width given as ConverterParameter.</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double f = value is double d ? d : 0;
        double max = double.TryParse(parameter as string, NumberStyles.Any, CultureInfo.InvariantCulture, out var m) ? m : 100;
        return Math.Max(2, f * max);
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Palette.FromHex(value as string);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public static class Palette
{
    private static readonly Dictionary<string, SolidColorBrush> Cache = new();

    public static SolidColorBrush FromHex(string? hex)
    {
        hex ??= "#7C6CFF";
        if (Cache.TryGetValue(hex, out var b)) return b;
        try
        {
            b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
        catch
        {
            b = new SolidColorBrush(Color.FromRgb(0x7C, 0x6C, 0xFF));
        }
        b.Freeze();
        Cache[hex] = b;
        return b;
    }

    public static readonly string[] AccountPalette =
        ["#7C6CFF", "#4CC9F0", "#36D399", "#FFB648", "#FF5D73", "#F472B6", "#A3E635", "#60A5FA", "#FB923C", "#2DD4BF"];

    private static readonly string[] AvatarPalette =
        ["#5B4FD6", "#2F7FA3", "#2A9A72", "#B5832F", "#B84457", "#A84F87", "#6E8F2C", "#3D6FB8", "#B8662E", "#258F86"];

    public static SolidColorBrush ForText(string text)
    {
        int h = 0;
        foreach (var ch in text.ToLowerInvariant()) h = unchecked(h * 31 + ch);
        return FromHex(AvatarPalette[Math.Abs(h % AvatarPalette.Length)]);
    }
}
