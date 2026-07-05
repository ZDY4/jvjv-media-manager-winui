using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Data;
using JvJvMediaManager.Models;
using JvJvMediaManager.Services;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.ViewModels;

public sealed class IncrementalMediaCollection : ObservableCollection<MediaItemViewModel>, ISupportIncrementalLoading
{
    private readonly Func<int, int, int, Task<MediaPageResult>> _pageLoader;
    private readonly Func<IEnumerable<MediaFile>, IReadOnlyList<MediaItemViewModel>> _itemFactory;
    private readonly Action<int>? _generationStarted;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly Dictionary<string, int> _indexByMediaId = new(StringComparer.Ordinal);

    private readonly int _pageSize;
    private int _generation;
    private bool _hasMoreItems = true;
    private IUiThreadDispatcher _dispatcher = new UiThreadDispatcher(null, "IncrementalMediaCollection");

    public IncrementalMediaCollection(
        Func<int, int, Task<MediaPageResult>> pageLoader,
        Func<IEnumerable<MediaFile>, IReadOnlyList<MediaItemViewModel>>? itemFactory = null,
        int pageSize = 120)
        : this((offset, limit, _) => pageLoader(offset, limit), itemFactory, pageSize)
    {
    }

    public IncrementalMediaCollection(
        Func<int, int, int, Task<MediaPageResult>> pageLoader,
        Func<IEnumerable<MediaFile>, IReadOnlyList<MediaItemViewModel>>? itemFactory = null,
        int pageSize = 120,
        Action<int>? generationStarted = null)
    {
        _pageLoader = pageLoader;
        _itemFactory = itemFactory ?? (mediaFiles => mediaFiles.Select(item => new MediaItemViewModel(item)).ToList());
        _generationStarted = generationStarted;
        _pageSize = pageSize;
    }

    public bool HasMoreItems => _hasMoreItems;

    public int PageSize => _pageSize;

    public void SetDispatcher(DispatcherQueue dispatcher)
    {
        _dispatcher = new UiThreadDispatcher(dispatcher, "IncrementalMediaCollection");
    }

    public void InvalidatePendingLoads()
    {
        var generation = Interlocked.Increment(ref _generation);
        _hasMoreItems = false;
        AppTraceLogger.LogSampled(
            "IncrementalMediaCollection",
            "invalidate-pending-loads",
            $"Invalidated pending incremental loads. Generation={generation}, Count={Count}.",
            TimeSpan.FromSeconds(1));
    }

    public async Task RefreshAsync(Action<int>? generationStarted = null)
    {
        var generation = Interlocked.Increment(ref _generation);
        (generationStarted ?? _generationStarted)?.Invoke(generation);
        await _loadLock.WaitAsync();
        try
        {
            var result = await _pageLoader(0, _pageSize, generation);
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }

            var replaced = await RunOnUiThreadAsync(() =>
            {
                if (generation != Volatile.Read(ref _generation))
                {
                    return false;
                }

            var pageItems = DeduplicatePageItems(_itemFactory(result.Items), "refresh");
            ReplaceLoadedWindow(pageItems);
                return true;
            });
            if (!replaced || generation != Volatile.Read(ref _generation))
            {
                return;
            }

            _hasMoreItems = result.HasMore;
            AppTraceLogger.LogSampled(
                "IncrementalMediaCollection",
                "refresh-diff",
                $"Applied refresh with diff. Loaded={Count}, HasMore={_hasMoreItems}.",
                TimeSpan.FromSeconds(1));
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task EnsureItemAvailableAsync(int index)
    {
        if (index < 0)
        {
            return;
        }

        while (Count <= index && HasMoreItems)
        {
            var generation = Volatile.Read(ref _generation);
            var loaded = await LoadMoreCoreAsync((uint)Math.Max(_pageSize, index - Count + 1), generation);
            if (loaded == 0)
            {
                return;
            }
        }
    }

    public async Task<bool> EnsureMediaIdAvailableAsync(string mediaId)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return false;
        }

        while (IndexOfMediaId(mediaId, 0) < 0 && HasMoreItems)
        {
            var generation = Volatile.Read(ref _generation);
            var loaded = await LoadMoreCoreAsync((uint)_pageSize, generation);
            if (loaded == 0)
            {
                break;
            }
        }

        return IndexOfMediaId(mediaId, 0) >= 0;
    }

    public async Task RefreshLoadedWindowAsync()
    {
        await _loadLock.WaitAsync();
        try
        {
            var generation = Volatile.Read(ref _generation);
            var windowSize = Math.Max(Count, _pageSize);
            var result = await _pageLoader(0, windowSize, generation);
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }

            var replaced = await RunOnUiThreadAsync(() =>
            {
                if (generation != Volatile.Read(ref _generation))
                {
                    return false;
                }

                var pageItems = DeduplicatePageItems(_itemFactory(result.Items), "refresh-loaded-window");
                ReplaceLoadedWindow(pageItems);
                return true;
            });
            if (!replaced || generation != Volatile.Read(ref _generation))
            {
                return;
            }

            _hasMoreItems = result.HasMore;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public Windows.Foundation.IAsyncOperation<LoadMoreItemsResult> LoadMoreItemsAsync(uint count)
    {
        return LoadMoreItemsCoreAsync(count).AsAsyncOperation();
    }

    private async Task<LoadMoreItemsResult> LoadMoreItemsCoreAsync(uint count)
    {
        var generation = Volatile.Read(ref _generation);
        var loaded = await LoadMoreCoreAsync(count, generation);
        return new LoadMoreItemsResult { Count = loaded };
    }

    private async Task<uint> LoadMoreCoreAsync(uint requestedCount, int generation)
    {
        await _loadLock.WaitAsync();
        try
        {
            if (generation != Volatile.Read(ref _generation) || !_hasMoreItems)
            {
                return 0;
            }

            var limit = (int)Math.Max((uint)_pageSize, requestedCount);
            var result = await _pageLoader(Count, limit, generation);

            if (generation != Volatile.Read(ref _generation))
            {
                return 0;
            }

            var added = await RunOnUiThreadAsync(() =>
            {
                if (generation != Volatile.Read(ref _generation))
                {
                    return 0;
                }

                var pageItems = DeduplicatePageItems(_itemFactory(result.Items), "load-more");
                return AddRange(pageItems);
            });
            if (generation != Volatile.Read(ref _generation))
            {
                return 0;
            }

            _hasMoreItems = result.HasMore && added > 0;
            return (uint)added;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private Task RunOnUiThreadAsync(Action action)
    {
        return _dispatcher.EnqueueAsync(action, failureContext: "RunOnUiThreadAsync action failed.");
    }

    private Task<T> RunOnUiThreadAsync<T>(Func<T> action)
    {
        return _dispatcher.EnqueueAsync(action, failureContext: "RunOnUiThreadAsync action failed.");
    }

    private int AddRange(IReadOnlyList<MediaItemViewModel> items)
    {
        if (items.Count == 0)
        {
            return 0;
        }

        var existingIds = Items
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var nextItems = items
            .Where(item => existingIds.Add(item.Id))
            .ToList();
        if (nextItems.Count == 0)
        {
            AppTraceLogger.LogSampled(
                "IncrementalMediaCollection",
                "range-add-duplicates-skipped",
                $"Skipped page append because all returned items were already loaded. Requested={items.Count}, Total={Count}.",
                TimeSpan.FromSeconds(2));
            return 0;
        }

        CheckReentrancy();

        var startIndex = Count;
        foreach (var item in nextItems)
        {
            Items.Add(item);
        }

        RebuildIndex();

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, nextItems, startIndex));

        AppTraceLogger.LogSampled(
            "IncrementalMediaCollection",
            "range-add-incremental",
            $"Applied page append incrementally. StartIndex={startIndex}, Added={nextItems.Count}, SkippedDuplicates={items.Count - nextItems.Count}, Total={Count}.",
            TimeSpan.FromSeconds(2));
        return nextItems.Count;
    }

    private static IReadOnlyList<MediaItemViewModel> DeduplicatePageItems(
        IReadOnlyList<MediaItemViewModel> items,
        string operation)
    {
        if (items.Count <= 1)
        {
            return items;
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        List<MediaItemViewModel>? deduplicatedItems = null;
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (seenIds.Add(item.Id))
            {
                deduplicatedItems?.Add(item);
                continue;
            }

            deduplicatedItems ??= items.Take(index).ToList();
        }

        if (deduplicatedItems == null)
        {
            return items;
        }

        AppTraceLogger.LogSampled(
            "IncrementalMediaCollection",
            "page-duplicates-skipped",
            $"Skipped duplicate media ids in page result. Operation={operation}, Input={items.Count}, Output={deduplicatedItems.Count}, Duplicates={items.Count - deduplicatedItems.Count}.",
            TimeSpan.FromSeconds(2));
        return deduplicatedItems;
    }

    public async Task<int> RemoveByIdsAsync(ISet<string> ids)
    {
        await _loadLock.WaitAsync();
        try
        {
            return await RunOnUiThreadAsync(() => RemoveByIdsCore(ids));
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private int RemoveByIdsCore(ISet<string> ids)
    {
        if (ids.Count == 0)
        {
            return 0;
        }

        CheckReentrancy();

        var removed = Items.Count(item => ids.Contains(item.Id));
        if (removed == 0)
        {
            return 0;
        }

        for (var index = Items.Count - 1; index >= 0; index--)
        {
            if (ids.Contains(Items[index].Id))
            {
                RemoveAt(index);
            }
        }

        RebuildIndex();

        AppTraceLogger.Log(
            "IncrementalMediaCollection",
            $"Removed media items incrementally. Removed={removed}, Remaining={Count}.");
        return removed;
    }

    private void ReplaceLoadedWindow(IReadOnlyList<MediaItemViewModel> desiredItems)
    {
        if (Items.Count == desiredItems.Count
            && Items.Select(item => item.Id).SequenceEqual(desiredItems.Select(item => item.Id), StringComparer.Ordinal))
        {
            for (var index = 0; index < Items.Count; index++)
            {
                if (!ReferenceEquals(Items[index], desiredItems[index]))
                {
                    Items[index].UpdateFrom(desiredItems[index].Media);
                }
            }

            return;
        }

        if (IsAppendOnly(desiredItems))
        {
            for (var index = 0; index < Items.Count; index++)
            {
                if (!ReferenceEquals(Items[index], desiredItems[index]))
                {
                    Items[index].UpdateFrom(desiredItems[index].Media);
                }
            }

            AddRange(desiredItems.Skip(Items.Count).ToList());
            return;
        }

        CheckReentrancy();
        var diff = CollectionDiffApplier.Apply(
            Items,
            desiredItems,
            item => item.Id,
            RemoveItem,
            MoveItem,
            InsertItem,
            (index, item) => Items[index].UpdateFrom(item.Media),
            StringComparer.Ordinal);
        RebuildIndex();

        AppTraceLogger.LogSampled(
            "IncrementalMediaCollection",
            "replace-loaded-window-diff",
            $"Replaced loaded window with diff. Count={Count}, Inserted={diff.Inserted}, Removed={diff.Removed}, Moved={diff.Moved}, Updated={diff.Updated}.",
            TimeSpan.FromSeconds(2));
    }

    private int IndexOfMediaId(string mediaId, int startIndex)
    {
        if (startIndex <= 0 && _indexByMediaId.TryGetValue(mediaId, out var cachedIndex))
        {
            return cachedIndex;
        }

        for (var index = Math.Max(0, startIndex); index < Items.Count; index++)
        {
            if (string.Equals(Items[index].Id, mediaId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private void RebuildIndex()
    {
        _indexByMediaId.Clear();
        for (var index = 0; index < Items.Count; index++)
        {
            _indexByMediaId[Items[index].Id] = index;
        }
    }

    private bool IsAppendOnly(IReadOnlyList<MediaItemViewModel> desiredItems)
    {
        if (desiredItems.Count < Items.Count)
        {
            return false;
        }

        for (var index = 0; index < Items.Count; index++)
        {
            if (!string.Equals(Items[index].Id, desiredItems[index].Id, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
