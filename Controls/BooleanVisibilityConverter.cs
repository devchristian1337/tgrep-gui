using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace TgrepGui.Controls;

public sealed class BooleanVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool visible = value is true;
        if (parameter is string flag && flag.Equals("Invert", StringComparison.OrdinalIgnoreCase))
            visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility.Visible;
}
