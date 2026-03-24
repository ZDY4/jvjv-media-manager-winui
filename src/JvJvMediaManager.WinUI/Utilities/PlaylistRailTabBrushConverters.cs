using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace JvJvMediaManager.Utilities;

public sealed class MediaTabBackgroundBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value == null
            ? Application.Current.Resources["SurfaceMutedBrush"] as Brush
                ?? new SolidColorBrush(ColorHelper.FromArgb(255, 46, 46, 46))
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}

public sealed class MediaTabForegroundBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value == null
            ? Application.Current.Resources["TextBrush"] as Brush
                ?? new SolidColorBrush(Microsoft.UI.Colors.White)
            : Application.Current.Resources["MutedTextBrush"] as Brush
                ?? new SolidColorBrush(ColorHelper.FromArgb(255, 170, 170, 170));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
