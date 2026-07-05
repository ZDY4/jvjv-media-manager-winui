using Microsoft.UI.Dispatching;
using JvJvMediaManager.Models;
using JvJvMediaManager.Services;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.ViewModels.MainPage;

internal sealed class MediaPageStore
{
    private readonly MediaLibraryService _library;
    private readonly IncrementalMediaCollection _items;
    private readonly object _queryStateSync = new();
    private readonly Dictionary<int, MediaLibraryQueryState> _queryStatesByGeneration = new();
    private MediaLibraryQueryState? _queryState;

    public MediaPageStore(
        MediaLibraryService library,
        Func<IEnumerable<MediaFile>, IReadOnlyList<MediaItemViewModel>> itemFactory)
    {
        _library = library;
        _items = new IncrementalMediaCollection(LoadPageAsync, itemFactory, generationStarted: OnGenerationStarted);
    }

    public IncrementalMediaCollection Items => _items;

    public IReadOnlyList<MediaItemViewModel> LoadedItems => _items;

    public bool HasMoreItems => _items.HasMoreItems;

    public int PageSize => _items.PageSize;

    public int Count => _items.Count;

    public bool HasQuery => GetCurrentQueryState() != null;

    public void SetDispatcher(DispatcherQueue dispatcher)
    {
        _items.SetDispatcher(dispatcher);
    }

    public void InvalidatePendingLoads()
    {
        _items.InvalidatePendingLoads();
        lock (_queryStateSync)
        {
            _queryStatesByGeneration.Clear();
        }
    }

    public Task RefreshAsync(MediaLibraryQueryState queryState)
    {
        lock (_queryStateSync)
        {
            _queryState = queryState;
        }

        return RefreshCoreAsync(queryState);
    }

    private async Task RefreshCoreAsync(MediaLibraryQueryState queryState)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _items.RefreshAsync(generation => RegisterGenerationQueryState(generation, queryState));
            AppTraceLogger.LogSampled(
                "MediaPageStore",
                "refresh",
                $"Refresh completed. LoadedCount={_items.Count}, HasMore={_items.HasMoreItems}, VisibleFolders={queryState.VisibleFolderPaths.Count}, PlaylistId='{queryState.SelectedPlaylistId ?? "<null>"}', ElapsedMs={stopwatch.ElapsedMilliseconds}.",
                TimeSpan.FromSeconds(1));
        }
        finally
        {
            PruneGenerationQueryStates();
        }
    }

    public async Task RefreshLoadedWindowAsync()
    {
        var queryState = GetCurrentQueryState();
        if (queryState == null)
        {
            AppTraceLogger.Log("MediaPageStore", "RefreshLoadedWindowAsync skipped because query state has not been initialized.");
            return;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await _items.RefreshLoadedWindowAsync();
        AppTraceLogger.LogSampled(
            "MediaPageStore",
            "refresh-loaded-window",
            $"Refresh loaded window completed. LoadedCount={_items.Count}, HasMore={_items.HasMoreItems}, VisibleFolders={queryState.VisibleFolderPaths.Count}, PlaylistId='{queryState.SelectedPlaylistId ?? "<null>"}', ElapsedMs={stopwatch.ElapsedMilliseconds}.",
            TimeSpan.FromSeconds(1));
    }

    public async Task EnsureItemAvailableAsync(int index)
    {
        if (GetCurrentQueryState() == null)
        {
            AppTraceLogger.Log("MediaPageStore", $"EnsureItemAvailableAsync skipped because query state has not been initialized. Index={index}.");
            return;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await _items.EnsureItemAvailableAsync(index);
        AppTraceLogger.LogSampled(
            "MediaPageStore",
            "ensure-item",
            $"Ensure item available completed. Index={index}, LoadedCount={_items.Count}, HasMore={_items.HasMoreItems}, ElapsedMs={stopwatch.ElapsedMilliseconds}.",
            TimeSpan.FromSeconds(1));
    }

    public async Task<bool> EnsureMediaIdAvailableAsync(string mediaId)
    {
        if (GetCurrentQueryState() == null)
        {
            AppTraceLogger.Log("MediaPageStore", $"EnsureMediaIdAvailableAsync skipped because query state has not been initialized. MediaId='{mediaId}'.");
            return false;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var found = await _items.EnsureMediaIdAvailableAsync(mediaId);
        AppTraceLogger.LogSampled(
            "MediaPageStore",
            "ensure-media-id",
            $"Ensure media id available completed. MediaId='{mediaId}', Found={found}, LoadedCount={_items.Count}, HasMore={_items.HasMoreItems}, ElapsedMs={stopwatch.ElapsedMilliseconds}.",
            TimeSpan.FromSeconds(1));
        return found;
    }

    public async Task EnsureNextPageLoadedAsync()
    {
        if (GetCurrentQueryState() == null)
        {
            AppTraceLogger.Log("MediaPageStore", "EnsureNextPageLoadedAsync skipped because query state has not been initialized.");
            return;
        }

        if (!_items.HasMoreItems)
        {
            return;
        }

        var targetIndex = _items.Count + _items.PageSize - 1;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await _items.EnsureItemAvailableAsync(targetIndex);
        AppTraceLogger.LogSampled(
            "MediaPageStore",
            "ensure-next-page",
            $"Ensure next page completed. TargetIndex={targetIndex}, LoadedCount={_items.Count}, HasMore={_items.HasMoreItems}, ElapsedMs={stopwatch.ElapsedMilliseconds}.",
            TimeSpan.FromSeconds(1));
    }

    public Task<int> RemoveByIdsAsync(ISet<string> ids)
    {
        return _items.RemoveByIdsAsync(ids);
    }

    private void OnGenerationStarted(int generation)
    {
        lock (_queryStateSync)
        {
            if (_queryState != null)
            {
                _queryStatesByGeneration[generation] = _queryState;
            }
        }
    }

    private void RegisterGenerationQueryState(int generation, MediaLibraryQueryState queryState)
    {
        lock (_queryStateSync)
        {
            _queryStatesByGeneration[generation] = queryState;
        }
    }

    private MediaLibraryQueryState? GetCurrentQueryState()
    {
        lock (_queryStateSync)
        {
            return _queryState;
        }
    }

    private MediaLibraryQueryState ResolveQueryState(int generation)
    {
        lock (_queryStateSync)
        {
            if (_queryStatesByGeneration.TryGetValue(generation, out var queryState))
            {
                return queryState;
            }

            return _queryState ?? throw new InvalidOperationException("媒体分页查询尚未初始化。");
        }
    }

    private void PruneGenerationQueryStates()
    {
        lock (_queryStateSync)
        {
            if (_queryStatesByGeneration.Count <= 2)
            {
                return;
            }

            var newestGenerations = _queryStatesByGeneration.Keys
                .OrderByDescending(generation => generation)
                .Take(2)
                .ToHashSet();
            foreach (var generation in _queryStatesByGeneration.Keys.ToList())
            {
                if (!newestGenerations.Contains(generation))
                {
                    _queryStatesByGeneration.Remove(generation);
                }
            }
        }
    }

    private Task<MediaPageResult> LoadPageAsync(int offset, int limit, int generation)
    {
        var queryState = ResolveQueryState(generation);
        AppTraceLogger.LogSampled(
            "MediaPageStore",
            "load-page",
            $"Load page requested. Generation={generation}, Offset={offset}, Limit={limit}, VisibleFolders={queryState.VisibleFolderPaths.Count}, PlaylistId='{queryState.SelectedPlaylistId ?? "<null>"}'.",
            TimeSpan.FromSeconds(1));
        return _library.QueryPageAsync(queryState.ToMediaQuery(offset, limit));
    }
}
