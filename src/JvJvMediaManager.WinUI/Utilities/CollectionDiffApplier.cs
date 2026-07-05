namespace JvJvMediaManager.Utilities;

public readonly record struct CollectionDiffResult(int Inserted, int Removed, int Moved, int Updated);

public static class CollectionDiffApplier
{
    public static CollectionDiffResult Apply<T>(
        IList<T> currentItems,
        IReadOnlyList<T> desiredItems,
        Func<T, string> keySelector,
        Action<int> removeAt,
        Action<int, int> move,
        Action<int, T> insert,
        Action<int, T> update,
        IEqualityComparer<string>? keyComparer = null)
    {
        ArgumentNullException.ThrowIfNull(currentItems);
        ArgumentNullException.ThrowIfNull(desiredItems);
        ArgumentNullException.ThrowIfNull(keySelector);
        ArgumentNullException.ThrowIfNull(removeAt);
        ArgumentNullException.ThrowIfNull(move);
        ArgumentNullException.ThrowIfNull(insert);
        ArgumentNullException.ThrowIfNull(update);

        keyComparer ??= StringComparer.Ordinal;

        if (currentItems.Count == desiredItems.Count
            && currentItems.Select(keySelector).SequenceEqual(desiredItems.Select(keySelector), keyComparer))
        {
            var updatedOnly = 0;
            for (var index = 0; index < currentItems.Count; index++)
            {
                update(index, desiredItems[index]);
                updatedOnly++;
            }

            return new CollectionDiffResult(0, 0, 0, updatedOnly);
        }

        var removed = 0;
        var moved = 0;
        var inserted = 0;
        var updated = 0;
        var desiredIds = desiredItems
            .Select(keySelector)
            .ToHashSet(keyComparer);

        for (var index = currentItems.Count - 1; index >= 0; index--)
        {
            if (!desiredIds.Contains(keySelector(currentItems[index])))
            {
                removeAt(index);
                removed++;
            }
        }

        for (var desiredIndex = 0; desiredIndex < desiredItems.Count; desiredIndex++)
        {
            var desiredItem = desiredItems[desiredIndex];
            var desiredId = keySelector(desiredItem);
            if (desiredIndex < currentItems.Count
                && keyComparer.Equals(keySelector(currentItems[desiredIndex]), desiredId))
            {
                update(desiredIndex, desiredItem);
                updated++;
                continue;
            }

            var existingIndex = IndexOf(currentItems, desiredId, desiredIndex + 1, keySelector, keyComparer);
            if (existingIndex >= 0)
            {
                move(existingIndex, desiredIndex);
                update(desiredIndex, desiredItem);
                moved++;
                updated++;
                continue;
            }

            insert(desiredIndex, desiredItem);
            inserted++;
        }

        return new CollectionDiffResult(inserted, removed, moved, updated);
    }

    private static int IndexOf<T>(
        IList<T> items,
        string key,
        int startIndex,
        Func<T, string> keySelector,
        IEqualityComparer<string> keyComparer)
    {
        for (var index = Math.Max(0, startIndex); index < items.Count; index++)
        {
            if (keyComparer.Equals(keySelector(items[index]), key))
            {
                return index;
            }
        }

        return -1;
    }
}
