using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace JvJvMediaManager.Utilities;

internal static class ThemeResourceHelper
{
    public static Brush GetBrush(string key, Color fallbackColor)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(fallbackColor);
    }

    public static Color GetColor(string key, Color fallbackColor)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value))
        {
            if (value is Color color)
            {
                return color;
            }

            if (value is SolidColorBrush brush)
            {
                return brush.Color;
            }
        }

        return fallbackColor;
    }
}
