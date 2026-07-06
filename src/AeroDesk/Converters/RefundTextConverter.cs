using System.Globalization;
using System.Windows.Data;

namespace AeroDesk.Converters;

/// <summary>bool Refundable → agent-friendly label.</summary>
public sealed class RefundTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Refundable" : "Non-refundable";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
