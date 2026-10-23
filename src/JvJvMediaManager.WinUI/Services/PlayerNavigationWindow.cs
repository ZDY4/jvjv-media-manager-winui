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

        // Track only the focus IDs, not every item in a potentially huge library.
        // Removing matches also preserves the first-occurrence behavior for duplicates.
        var missingIds = new HashSet<string>(focusItems.Select(getId), StringComparer.Ordinal);
        var ranges = new List<(int Start, int End)>();
        for (var index = 0; index < navigationItems.Count && missingIds.Count > 0; index++)
        {
            if (!missingIds.Remove(getId(navigationItems[index])))
            {
                continue;
            }

            var start = Math.Max(0, index - radius);
            var end = (int)Math.Min(navigationItems.Count - 1L, (long)index + radius);
            if (ranges.Count > 0 && start <= (long)ranges[^1].End + 1)
            {
                ranges[^1] = (ranges[^1].Start, end);
            }
            else
            {
                ranges.Add((start, end));
            }
        }

        var result = new List<T>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var range in ranges)
        {
            for (var index = range.Start; index <= range.End; index++)
            {
                var item = navigationItems[index];
                if (seen.Add(getId(item)))
                {
                    result.Add(item);
                }
            }
        }

        foreach (var focus in focusItems)
        {
            var id = getId(focus);
            if (missingIds.Contains(id) && seen.Add(id))
            {
                result.Add(focus);
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
