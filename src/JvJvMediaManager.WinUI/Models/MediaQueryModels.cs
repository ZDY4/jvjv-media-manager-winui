namespace JvJvMediaManager.Models;

public enum MediaSortField
{
    FileName,
    ModifiedAt,
    Type,
    Size,
    Duration,
    Resolution
}

public enum MediaSortOrder
{
    Asc,
    Desc
}

public sealed class MediaQuery
{
    public string SearchText { get; init; } = string.Empty;
    public IReadOnlyList<string> SelectedTags { get; init; } = Array.Empty<string>();
    public string? PlaylistId { get; init; }
    public IReadOnlyList<string>? IncludedFolderPaths { get; init; }
    public IReadOnlyList<string>? ExactFolderPaths { get; init; }
    public IReadOnlyList<string> ExcludedFolderPaths { get; init; } = Array.Empty<string>();
    public MediaType? TypeFilter { get; init; }
    public IReadOnlyList<string> ExcludedMediaIds { get; init; } = Array.Empty<string>();
    public bool RandomOrder { get; init; }
    public MediaSortField SortField { get; init; } = MediaSortField.ModifiedAt;
    public MediaSortOrder SortOrder { get; init; } = MediaSortOrder.Desc;
    public string? AfterMediaId { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; }
}

public sealed class MediaPageResult
{
    public IReadOnlyList<MediaFile> Items { get; init; } = Array.Empty<MediaFile>();
    public bool HasMore { get; init; }
}

public sealed class MediaFolderSummary
{
    public string FolderPath { get; init; } = string.Empty;
    public int Count { get; init; }
}
