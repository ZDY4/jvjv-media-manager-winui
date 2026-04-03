using System.Globalization;

namespace JvJvMediaManager.Models;

public enum MediaType
{
    Video,
    Image
}

public enum MediaViewMode
{
    List,
    Grid
}

public enum TagUpdateMode
{
    Replace,
    Append
}

public sealed class WatchedFolder
{
    public string Path { get; set; } = string.Empty;
    public bool Locked { get; set; }
    public bool Visible { get; set; } = true;
}

public sealed class MediaFile
{
    public string Id { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public MediaType Type { get; set; }
    public long Size { get; set; }
    public double? Duration { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? Thumbnail { get; set; }
    public long CreatedAt { get; set; }
    public long ModifiedAt { get; set; }
    public long? LastPlayed { get; set; }
    public int? PlayCount { get; set; }
    public List<string> Tags { get; set; } = new();
}

public sealed class Tag
{
    public int Id { get; set; }
    public string MediaId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long CreatedAt { get; set; }
}

public sealed class TrimSegment
{
    public double Start { get; set; }
    public double End { get; set; }
}

public class Playlist
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ColorHex { get; set; }
    public int SortOrder { get; set; }
    public long CreatedAt { get; set; }

    public string RailDisplayText
    {
        get
        {
            var normalized = Name?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(normalized))
            {
                return "?";
            }

            var enumerator = StringInfo.GetTextElementEnumerator(normalized);
            while (enumerator.MoveNext())
            {
                var element = enumerator.GetTextElement();
                if (element.Length > 0 && char.IsLetter(element, 0))
                {
                    return element;
                }
            }

            return GetLeadingTextElements(normalized, 2);
        }
    }

    private static string GetLeadingTextElements(string value, int count)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        var buffer = string.Empty;
        while (count > 0 && enumerator.MoveNext())
        {
            buffer += enumerator.GetTextElement();
            count--;
        }

        return string.IsNullOrEmpty(buffer) ? "?" : buffer;
    }
}

public sealed class PlaylistWithMedia : Playlist
{
    public List<string> MediaIds { get; set; } = new();
}
