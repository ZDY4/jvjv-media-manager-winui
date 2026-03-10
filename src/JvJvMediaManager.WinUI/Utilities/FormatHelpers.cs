namespace JvJvMediaManager.Utilities;

public static class FormatHelpers
{
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    public static string FormatDuration(double? seconds)
    {
        if (!seconds.HasValue || seconds.Value <= 0) return "";
        var total = (int)Math.Round(seconds.Value);
        var minutes = total / 60;
        var secs = total % 60;
        return $"{minutes}:{secs:00}";
    }
}
