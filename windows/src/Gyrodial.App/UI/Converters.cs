using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Gyrodial.App.UI;

/// <summary>true → Visible. ConverterParameter "not" inverts. Non-empty strings and non-null objects count as true.</summary>
public sealed class VisibleWhen : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value switch
        {
            bool v => v,
            string s => s.Length > 0,
            int i => i != 0,
            null => false,
            _ => true,
        };
        if (parameter as string == "not") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Negates a bool.</summary>
public sealed class Not : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>A toggle's label, as Windows 11 Settings shows it: "On" / "Off".</summary>
public sealed class OnOff : IValueConverter
{
    public static OnOff Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? "On" : "Off";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Formats a number with the ConverterParameter format, e.g. "0.00×".</summary>
public sealed class NumberFormat : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IFormattable f ? f.ToString(parameter as string ?? "0", CultureInfo.CurrentCulture) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
