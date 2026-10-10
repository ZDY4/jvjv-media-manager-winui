namespace JvJvMediaManager.Services;

/// <summary>
/// Builds a bounded prev/next window around one or more focus items so
/// independent/docked player sessions do not snapshot the entire library.
/// </summary>
internal static class PlayerNavigationWindow
{
    public const int DefaultRadius = 64;

    public static List<T> SliceAround<T>(
        IReadOnlyList<T> navigationItems,
        IReadOnlyList<T> focusItems,
        Func<T, string> getId,
        int radius = DefaultRadius)
    {
        ArgumentNullException.ThrowIfNull(navigationItems);
        ArgumentNullException.ThrowIfNull(focusItems);
        ArgumentNullException.ThrowIfNull(getId);

        radius = Math.Max(0, radius);
        if (focusItems.Count == 0)
        {
            return new List<T>();
        }

        if (navigationItems.Count == 0)
        {
            return DedupPreserveOrder(focusItems, getId);
        }

        var indexById = new Dictionary<string, int>(navigationItems.Count, StringComparer.Ordinal);
        for (var index = 0; index < navigationItems.Count; index++)
        {
            indexById.TryAdd(getId(navigationItems[index]), index);
        }

        var include = new bool[navigationItems.Count];
        var extras = new List<T>();
        foreach (var focus in focusItems)
        {
            var id = getId(focus);
            if (indexById.TryGetValue(id, out var focusIndex))
            {
                var start = Math.Max(0, focusIndex - radius);
                var end = Math.Min(navigationItems.Count - 1, focusIndex + radius);
                for (var index = start; index <= end; index++)
                {
                    include[index] = true;
                }
            }
            else
            {
                extras.Add(focus);
            }
        }

        var result = new List<T>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < navigationItems.Count; index++)
        {
            if (!include[index])
            {
                continue;
            }

            var item = navigationItems[index];
            if (seen.Add(getId(item)))
            {
                result.Add(item);
            }
        }

        foreach (var extra in extras)
        {
            if (seen.Add(getId(extra)))
            {
                result.Add(extra);
            }
        }

        return result;
    }

    private static List<T> DedupPreserveOrder<T>(IReadOnlyList<T> items, Func<T, string> getId)
    {
        var result = new List<T>(items.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (seen.Add(getId(item)))
            {
                result.Add(item);
            }
        }

        return result;
    }
}
