using JvJvMediaManager.Models;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.ViewModels.MainPage;

internal sealed class WatchedFolderManager
{
    public List<WatchedFolder> Normalize(IEnumerable<WatchedFolder> folders)
    {
        return folders
            .Where(folder => !string.IsNullOrWhiteSpace(folder.Path))
            .GroupBy(folder => PathHelpers.NormalizeFolderPath(folder.Path), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var last = group.Last();
                return new WatchedFolder
                {
                    Path = group.Key,
                    Locked = last.Locked,
                    Visible = last.Visible
                };
            })
            .OrderBy(folder => folder.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public List<string> GetVisibleFolderPaths(IEnumerable<WatchedFolder> folders)
    {
        return folders
            .Where(folder => folder.Visible)
            .Select(folder => PathHelpers.NormalizeFolderPath(folder.Path))
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public List<string> GetFolderPaths(IEnumerable<WatchedFolder> folders)
    {
        return folders
            .Select(folder => PathHelpers.NormalizeFolderPath(folder.Path))
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public List<string> GetNewVisibleFolders(IEnumerable<WatchedFolder> currentFolders, IEnumerable<WatchedFolder> requestedFolders)
    {
        var previousFolderPaths = GetFolderPaths(currentFolders).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Normalize(requestedFolders)
            .Where(folder => folder.Visible)
            .Select(folder => PathHelpers.NormalizeFolderPath(folder.Path))
            .Where(folder => !previousFolderPaths.Contains(folder))
            .ToList();
    }

    public HashSet<string> GetProtectedFolderPaths(IEnumerable<WatchedFolder> folders)
    {
        return folders
            .Where(folder => folder.Locked)
            .Select(folder => PathHelpers.NormalizeFolderPath(folder.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
