using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Data;
using JvJvMediaManager.Models;

namespace JvJvMediaManager.ViewModels;

public sealed class IncrementalMediaCollection : ObservableCollection<MediaItemViewModel>, ISupportIncrementalLoading
{
    private readonly Func<int, int, Task<MediaPageResult>> _pageLoader;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private readonly int _pageSize;
    private int _generation;
    private bool _hasMoreItems = true;
    private DispatcherQueue? _dispatcher;

    public IncrementalMediaCollection(Func<int, int, Task<MediaPageResult>> pageLoader, int pageSize = 200)
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

            await RunOnUiThreadAsync(() =>
            {
                foreach (var item in result.Items)
                {
                    Add(new MediaItemViewModel(item));
                }
            });

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
                    tcs.SetException(ex);
                }
            }))
        {
            tcs.SetException(new InvalidOperationException("无法切换到 UI 线程更新媒体集合。"));
        }

        return tcs.Task;
    }
}
