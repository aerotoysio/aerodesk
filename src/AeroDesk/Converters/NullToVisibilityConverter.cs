using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AeroDesk.Converters;

/// <summary>Null / empty-string → Collapsed, otherwise Visible. Used to show inline
/// error and status lines only when they carry text.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || (value is string s && string.IsNullOrWhiteSpace(s))
            ? Visibility.Collapsed
            : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
