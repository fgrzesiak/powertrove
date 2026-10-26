using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace PowerTrove.Desktop;

using Microsoft.UI.Xaml.Media;
using Microsoft.UI;

public static class Converters
{
    // Function binding: Window roots have no resource lookup chain for x:Bind converters.
    public static Visibility ToVisibility(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;

    public static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);

    public static Microsoft.UI.Xaml.Controls.InfoBarSeverity ToSeverity(bool isSuccess) =>
        isSuccess ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error;

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility InvertedBoolToVisibility(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    public static double ToTextOpacity(bool isCompatible) =>
        isCompatible ? 1.0 : 0.5;

    public static Brush ToResultBrush(bool isSuccess) =>
        isSuccess ? new SolidColorBrush(Colors.Green) : new SolidColorBrush(Colors.Red);
}

public class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
