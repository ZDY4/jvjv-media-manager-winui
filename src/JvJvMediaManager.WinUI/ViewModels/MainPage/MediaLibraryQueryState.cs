using JvJvMediaManager.Models;

namespace JvJvMediaManager.ViewModels.MainPage;

internal sealed record MediaLibraryQueryState(
    string SearchQuery,
    IReadOnlyList<string> SelectedTags,
    string? SelectedPlaylistId,
    MediaType? MediaTypeFilter,
    MediaSortField SortField,
    MediaSortOrder SortOrder,
    IReadOnlyList<string> VisibleFolderPaths,
    IReadOnlyList<string> ExcludedFolderPaths,
    IReadOnlyList<string> ExcludedMediaIds)
{
    public MediaQuery ToMediaQuery(
        int offset,
        int limit,
        MediaType? typeFilterOverride = null,
        IReadOnlyCollection<string>? excludedMediaIds = null,
        bool randomOrder = false)
    {
        return new MediaQuery
        {
            SearchText = SearchQuery,
            SelectedTags = SelectedTags,
            PlaylistId = SelectedPlaylistId,
            IncludedFolderPaths = VisibleFolderPaths,
            ExcludedFolderPaths = ExcludedFolderPaths,
            TypeFilter = typeFilterOverride ?? MediaTypeFilter,
            ExcludedMediaIds = MergeExcludedMediaIds(ExcludedMediaIds, excludedMediaIds),
            RandomOrder = randomOrder,
            SortField = SortField,
            SortOrder = SortOrder,
            Offset = offset,
            Limit = limit
        };
    }

    private static IReadOnlyList<string> MergeExcludedMediaIds(
        IReadOnlyList<string> baseExcludedIds,
        IReadOnlyCollection<string>? extraExcludedIds)
    {
        if (baseExcludedIds.Count == 0 && extraExcludedIds is not { Count: > 0 })
        {
            return Array.Empty<string>();
        }

        return baseExcludedIds
            .Concat(extraExcludedIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
