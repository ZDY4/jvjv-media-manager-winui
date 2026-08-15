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

    /// <summary>
    /// WP-08: optional composite keyset cursor for the next page. When set (and
    /// the sort is stable, i.e. not random), the query uses keyset pagination
    /// instead of OFFSET. Ignored for random order.
    /// </summary>
    public MediaPageCursor? Cursor { get; init; }

    public int Offset { get; init; }
    public int Limit { get; init; }
}

/// <summary>
/// WP-08: fully specifies the last row of a page so the next page can be fetched
/// with a keyset predicate that matches the exact ORDER BY tuple
/// (folderPath ASC, sort field, id ASC). Bound to the sort field/order and a
/// filter fingerprint so a stale cursor is never applied to a different query.
/// </summary>
public sealed record MediaPageCursor(
    string FolderPath,
    string? TextSortValue,
    long? IntegerSortValue,
    double? RealSortValue,
    string MediaId,
    MediaSortField SortField,
    MediaSortOrder SortOrder,
    string FilterFingerprint);

public sealed class MediaPageResult
{
    public IReadOnlyList<MediaFile> Items { get; init; } = Array.Empty<MediaFile>();
    public bool HasMore { get; init; }

    /// <summary>
    /// WP-08: cursor to fetch the page after this one, or null when keyset
    /// pagination was not used or there are no more rows.
    /// </summary>
    public MediaPageCursor? NextCursor { get; init; }
}

public sealed class MediaFolderSummary
{
    public string FolderPath { get; init; } = string.Empty;
    public int Count { get; init; }
}
