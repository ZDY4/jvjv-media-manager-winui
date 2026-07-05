using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.ViewModels.MainPage;

internal sealed class FolderGroupingStore
{
    public IReadOnlyList<FolderTreeNode> BuildVisibleNodes(
        IReadOnlyDictionary<string, int> directCounts,
        IReadOnlyList<string> visibleRoots,
        IReadOnlyDictionary<string, bool> collapseStates)
    {
        var nodes = BuildNodes(directCounts, visibleRoots);
        if (nodes.Count == 0)
        {
            return nodes;
        }

        return FilterVisibleNodes(nodes, collapseStates);
    }

    public IReadOnlyList<FolderTreeNode> BuildNodes(
        IReadOnlyDictionary<string, int> directCounts,
        IReadOnlyList<string> visibleRoots)
    {
        if (directCounts.Count == 0)
        {
            return Array.Empty<FolderTreeNode>();
        }

        var normalizedRoots = visibleRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(PathHelpers.NormalizeFolderPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var ancestorsByPath = directCounts.Keys.ToDictionary(
            path => path,
            path => EnumerateFolderAncestors(path, normalizedRoots).ToList(),
            StringComparer.OrdinalIgnoreCase);
        var allPaths = new HashSet<string>(directCounts.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var ancestors in ancestorsByPath.Values)
        {
            foreach (var ancestor in ancestors)
            {
                allPaths.Add(ancestor);
            }
        }

        var totalCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in directCounts)
        {
            foreach (var ancestor in ancestorsByPath[pair.Key])
            {
                totalCounts.TryGetValue(ancestor, out var count);
                totalCounts[ancestor] = count + pair.Value;
            }
        }

        return allPaths
            .Select(path =>
            {
                var root = ResolveFolderTreeRoot(path, normalizedRoots);
                var depth = CalculateFolderTreeDepth(path, root);
                return new FolderTreeNode(
                    path,
                    GetFolderDisplayName(path),
                    GetFolderTreePath(path, root),
                    depth,
                    totalCounts.TryGetValue(path, out var count) ? count : 0);
            })
            .Where(node => node.TotalCount > 0)
            .OrderBy(node => ResolveFolderTreeRoot(node.FolderPath, normalizedRoots), StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.FolderPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<FolderTreeNode> FilterVisibleNodes(
        IReadOnlyList<FolderTreeNode> nodes,
        IReadOnlyDictionary<string, bool> collapseStates)
    {
        var nodePaths = nodes
            .Select(item => item.FolderPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visibleNodes = new List<FolderTreeNode>(nodes.Count);

        foreach (var node in nodes)
        {
            if (IsNodeVisible(node, nodePaths, collapseStates))
            {
                visibleNodes.Add(node);
            }
        }

        return visibleNodes;
    }

    private static bool IsNodeVisible(
        FolderTreeNode node,
        IReadOnlySet<string> nodePaths,
        IReadOnlyDictionary<string, bool> collapseStates)
    {
        var parent = GetParentFolderPath(node.FolderPath);
        while (!string.IsNullOrWhiteSpace(parent))
        {
            if (nodePaths.Contains(parent)
                && collapseStates.TryGetValue(parent, out var collapsed)
                && collapsed)
            {
                return false;
            }

            parent = GetParentFolderPath(parent);
        }

        return true;
    }

    private static IEnumerable<string> EnumerateFolderAncestors(string folderPath, IReadOnlyList<string> rootFolders)
    {
        var normalizedFolderPath = PathHelpers.NormalizeFolderPath(folderPath);
        var root = ResolveFolderTreeRoot(normalizedFolderPath, rootFolders);
        var ancestors = new Stack<string>();
        var parent = normalizedFolderPath;
        while (!string.IsNullOrWhiteSpace(parent))
        {
            ancestors.Push(parent);
            if (!string.IsNullOrWhiteSpace(root)
                && string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            parent = GetParentFolderPath(parent);
        }

        foreach (var ancestor in ancestors)
        {
            yield return ancestor;
        }
    }

    private static string ResolveFolderTreeRoot(string folderPath, IReadOnlyList<string> rootFolders)
    {
        var normalizedFolderPath = PathHelpers.NormalizeFolderPath(folderPath);
        return rootFolders
            .Where(root => string.Equals(normalizedFolderPath, root, StringComparison.OrdinalIgnoreCase)
                || PathHelpers.IsPathUnderFolder(normalizedFolderPath, root))
            .OrderByDescending(root => root.Length)
            .FirstOrDefault()
            ?? normalizedFolderPath;
    }

    private static int CalculateFolderTreeDepth(string folderPath, string rootFolder)
    {
        var relative = GetRelativeFolderTreePath(folderPath, rootFolder);
        return string.IsNullOrWhiteSpace(relative)
            ? 0
            : relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static string GetFolderTreePath(string folderPath, string rootFolder)
    {
        var relative = GetRelativeFolderTreePath(folderPath, rootFolder);
        return string.IsNullOrWhiteSpace(relative)
            ? GetFolderDisplayName(folderPath)
            : relative.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string GetRelativeFolderTreePath(string folderPath, string rootFolder)
    {
        if (string.IsNullOrWhiteSpace(rootFolder)
            || string.Equals(folderPath, rootFolder, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var normalizedRoot = PathHelpers.NormalizeFolderPath(rootFolder);
        var normalizedFolder = PathHelpers.NormalizeFolderPath(folderPath);
        return PathHelpers.IsPathUnderFolder(normalizedFolder, normalizedRoot)
            ? normalizedFolder[(normalizedRoot.Length + 1)..]
            : string.Empty;
    }

    private static string? GetParentFolderPath(string folderPath)
    {
        var normalized = PathHelpers.NormalizeFolderPath(folderPath);
        var slashIndex = normalized.LastIndexOf('/');
        if (slashIndex <= 0)
        {
            return null;
        }

        if (slashIndex == 2 && normalized.Length > 1 && normalized[1] == ':')
        {
            return null;
        }

        return normalized[..slashIndex];
    }

    private static string GetFolderDisplayName(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return "未知文件夹";
        }

        var fileName = Path.GetFileName(folderPath.TrimEnd('/', '\\'));
        return string.IsNullOrWhiteSpace(fileName) ? folderPath : fileName;
    }
}

internal sealed record FolderTreeNode(
    string FolderPath,
    string DisplayName,
    string TreePath,
    int Depth,
    int TotalCount);
