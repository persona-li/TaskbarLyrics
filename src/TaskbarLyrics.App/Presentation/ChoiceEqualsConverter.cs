using System.Globalization;
using System.Windows.Data;

namespace TaskbarLyrics.App.Presentation;

public sealed class ChoiceEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? targetType == typeof(double)
            ? double.Parse(parameter.ToString()!, CultureInfo.InvariantCulture) : parameter
            : System.Windows.Data.Binding.DoNothing;
}
