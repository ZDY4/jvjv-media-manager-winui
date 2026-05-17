using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Data;
using JvJvMediaManager.Models;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.ViewModels;

public sealed class IncrementalMediaCollection : ObservableCollection<MediaItemViewModel>, ISupportIncrementalLoading
{
    private readonly Func<int, int, Task<MediaPageResult>> _pageLoader;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private readonly int _pageSize;
    private int _generation;
    private bool _hasMoreItems = true;
    private DispatcherQueue? _dispatcher;

    public IncrementalMediaCollection(Func<int, int, Task<MediaPageResult>> pageLoader, int pageSize = 120)
    {
        _pageLoader = pageLoader;
        _pageSize = pageSize;
    }

    public bool HasMoreItems => _hasMoreItems;

    public int PageSize => _pageSize;

    public void SetDispatcher(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public async Task RefreshAsync()
    {
        var generation = Interlocked.Increment(ref _generation);
        _hasMoreItems = true;
        await RunOnUiThreadAsync(base.ClearItems);
        await LoadMoreCoreAsync((uint)_pageSize, generation);
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

    public async Task RefreshLoadedWindowAsync()
    {
        await _loadLock.WaitAsync();
        try
        {
            var generation = Volatile.Read(ref _generation);
            var windowSize = Math.Max(Count, _pageSize);
            var result = await _pageLoader(0, windowSize);
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }

            var pageItems = result.Items
                .Select(item => new MediaItemViewModel(item))
                .ToList();
            await RunOnUiThreadAsync(() => ReplaceLoadedWindow(pageItems));
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
            var result = await _pageLoader(Count, limit);

            if (generation != Volatile.Read(ref _generation))
            {
                return 0;
            }

            var pageItems = result.Items
                .Select(item => new MediaItemViewModel(item))
                .ToList();
            await RunOnUiThreadAsync(() => AddRange(pageItems));

            _hasMoreItems = result.HasMore;
            return (uint)result.Items.Count;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private Task RunOnUiThreadAsync(Action action)
    {
        if (_dispatcher == null || _dispatcher.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() =>
            {
                try
                {
                    action();
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    AppTraceLogger.LogException("IncrementalMediaCollection", "RunOnUiThreadAsync action failed.", ex);
                    tcs.SetException(ex);
                }
            }))
        {
            AppTraceLogger.Log("IncrementalMediaCollection", "RunOnUiThreadAsync failed because DispatcherQueue rejected the callback.");
            tcs.SetException(new InvalidOperationException("无法切换到 UI 线程更新媒体集合。"));
        }

        return tcs.Task;
    }

    private void AddRange(IReadOnlyList<MediaItemViewModel> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        CheckReentrancy();

        var startIndex = Count;
        foreach (var item in items)
        {
            Add(item);
        }

        AppTraceLogger.LogSampled(
            "IncrementalMediaCollection",
            "range-add-incremental",
            $"Applied page append incrementally. StartIndex={startIndex}, Added={items.Count}, Total={Count}.",
            TimeSpan.FromSeconds(2));
    }

    public int RemoveByIds(ISet<string> ids)
    {
        if (ids.Count == 0)
        {
            return 0;
        }

        CheckReentrancy();

        var removed = 0;
        for (var index = Items.Count - 1; index >= 0; index--)
        {
            if (Items[index] is MediaItemViewModel item && ids.Contains(item.Id))
            {
                RemoveAt(index);
                removed++;
            }
        }

        if (removed == 0)
        {
            return 0;
        }

        AppTraceLogger.Log(
            "IncrementalMediaCollection",
            $"Removed media items incrementally. Removed={removed}, Remaining={Count}.");
        return removed;
    }

    private void ReplaceLoadedWindow(IReadOnlyList<MediaItemViewModel> desiredItems)
    {
        for (var index = 0; index < desiredItems.Count; index++)
        {
            var desired = desiredItems[index];
            if (index < Count && string.Equals(this[index].Id, desired.Id, StringComparison.Ordinal))
            {
                this[index].UpdateFrom(desired.Media);
                continue;
            }

            var existingIndex = IndexOfId(desired.Id, index + 1);
            if (existingIndex >= 0)
            {
                var existing = this[existingIndex];
                existing.UpdateFrom(desired.Media);
                Move(existingIndex, index);
                continue;
            }

            Insert(index, desired);
        }

        while (Count > desiredItems.Count)
        {
            RemoveAt(Count - 1);
        }
    }

    private int IndexOfId(string id, int startIndex)
    {
        for (var index = Math.Max(0, startIndex); index < Count; index++)
        {
            if (string.Equals(this[index].Id, id, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }
}
