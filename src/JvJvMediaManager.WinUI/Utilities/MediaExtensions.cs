namespace JvJvMediaManager.Utilities;

public static class MediaExtensions
{
    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".avi",
        ".mkv",
        ".mov",
        ".wmv",
        ".flv",
        ".webm",
        ".m4v"
    };

    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".gif",
        ".bmp",
        ".webp"
    };

    public static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path);
        return VideoExtensions.Contains(ext) || ImageExtensions.Contains(ext);
    }
}
