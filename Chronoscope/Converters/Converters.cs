using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Chronoscope.Converters;

// Two-way "is this the selected option" for a group of RadioButtons bound to one string:
// checked when the value equals the parameter, and checking one writes its parameter back.
public class EqualsConverter : IValueConverter
{
    public static readonly EqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value?.ToString(), parameter?.ToString());

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? parameter : BindingOperations.DoNothing;
}

public static class ColorConverters
{
    // Swatches and dots bind a Color; controls want a brush
    public static readonly IValueConverter ToBrush =
        new FuncValueConverter<Color, IBrush>(c => new ImmutableSolidColorBrush(c));
}
