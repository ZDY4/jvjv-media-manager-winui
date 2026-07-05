using JvJvMediaManager.Models;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.ViewModels.MainPage;

internal sealed class MediaFolderHierarchyManager
{
    private readonly FolderGroupingStore _folderGroupingStore = new();

    public IReadOnlyList<FolderTreeNode> BuildVisibleNodes(
        IReadOnlyDictionary<string, int> directCounts,
        IReadOnlyList<string> visibleRoots,
        IReadOnlyDictionary<string, bool> collapseStates)
    {
        return _folderGroupingStore.BuildVisibleNodes(directCounts, visibleRoots, collapseStates);
    }

    public IReadOnlyList<string> BuildCollapseStatePaths(
        IEnumerable<MediaFolderGroupViewModel> groups,
        IEnumerable<MediaFolderSummary> summaries)
    {
        return groups
            .Select(group => group.FolderPath)
            .Concat(summaries.Select(summary => PathHelpers.NormalizeFolderPath(summary.FolderPath)))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool IsCollapsedForMedia(
        string folderPath,
        IReadOnlyDictionary<string, bool> collapseStates,
        Func<string, IEnumerable<string>> getFolderPathAndVisibleAncestors)
    {
        foreach (var pathToCheck in getFolderPathAndVisibleAncestors(folderPath))
        {
            if (collapseStates.TryGetValue(pathToCheck, out var collapsed) && collapsed)
            {
                return true;
            }
        }

        return false;
    }
}
