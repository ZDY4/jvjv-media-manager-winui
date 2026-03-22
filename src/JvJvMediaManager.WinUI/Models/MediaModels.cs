namespace JvJvMediaManager.Models;

public enum MediaType
{
    Video,
    Image
}

public enum AppThemeMode
{
    System,
    Light,
    Dark
}

public sealed class WatchedFolder
{
    public string Path { get; set; } = string.Empty;
    public bool Locked { get; set; }
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
    public int SortOrder { get; set; }
    public long CreatedAt { get; set; }
}

public sealed class PlaylistWithMedia : Playlist
{
    public List<string> MediaIds { get; set; } = new();
}
