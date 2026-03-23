using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace JvJvMediaManager.Utilities;

public sealed class PlaylistBackgroundBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (TryParseColor(value as string, out var color))
        {
            return new SolidColorBrush(color);
        }

        return Application.Current.Resources["PlaylistItemBrush"] as Brush
            ?? new SolidColorBrush(ColorHelper.FromArgb(255, 43, 43, 43));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }

    internal static bool TryParseColor(string? colorHex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(colorHex))
        {
            return false;
        }

        var normalized = colorHex.Trim().TrimStart('#');
        if (normalized.Length == 6
            && byte.TryParse(normalized[..2], System.Globalization.NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(normalized[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(normalized[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            color = ColorHelper.FromArgb(255, r, g, b);
            return true;
        }

        if (normalized.Length == 8
            && byte.TryParse(normalized[..2], System.Globalization.NumberStyles.HexNumber, null, out var a)
            && byte.TryParse(normalized[2..4], System.Globalization.NumberStyles.HexNumber, null, out var rr)
            && byte.TryParse(normalized[4..6], System.Globalization.NumberStyles.HexNumber, null, out var gg)
            && byte.TryParse(normalized[6..8], System.Globalization.NumberStyles.HexNumber, null, out var bb))
        {
            color = ColorHelper.FromArgb(a, rr, gg, bb);
            return true;
        }

        return false;
    }
}

public sealed class PlaylistForegroundBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (!PlaylistBackgroundBrushConverter.TryParseColor(value as string, out var color))
        {
            return Application.Current.Resources["TextBrush"] as Brush
                ?? new SolidColorBrush(Colors.White);
        }

        var luminance = (0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B);
        return new SolidColorBrush(luminance >= 160 ? Colors.Black : Colors.White);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
