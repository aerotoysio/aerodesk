using System.Globalization;
using System.Windows.Data;

namespace AeroDesk.Converters;

/// <summary>Two-way radio-button ↔ enum binding: checked when the value's name
/// matches the ConverterParameter; checking parses the parameter back.</summary>
public sealed class EnumMatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string name ? Enum.Parse(targetType, name, ignoreCase: true) : Binding.DoNothing;
}
