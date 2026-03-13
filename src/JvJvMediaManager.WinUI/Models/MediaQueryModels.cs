namespace JvJvMediaManager.Models;

public enum MediaSortField
{
    FileName,
    ModifiedAt
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
    public IReadOnlyList<string> ExcludedFolderPaths { get; init; } = Array.Empty<string>();
    public MediaSortField SortField { get; init; } = MediaSortField.ModifiedAt;
    public MediaSortOrder SortOrder { get; init; } = MediaSortOrder.Desc;
    public int Offset { get; init; }
    public int Limit { get; init; }
}

public sealed class MediaPageResult
{
    public IReadOnlyList<MediaFile> Items { get; init; } = Array.Empty<MediaFile>();
    public bool HasMore { get; init; }
}
