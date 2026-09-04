using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BlobTrap.App.Converters;

/// <summary>
/// Shows an element only while a collection is empty, for empty-state panels.
/// Pass ConverterParameter="invert" to show it only once the collection has items.
/// </summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value switch
        {
            int number => number,
            null => 0,
            _ => 1,
        };

        var invert = string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase);
        var visible = invert ? count > 0 : count == 0;

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
