using Microsoft.UI.Dispatching;
using JvJvMediaManager.Data;
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
    // WP-08: latest forward keyset cursor per generation. Continuation pages
    // always use this (or a cursor rebuilt from the last loaded row) instead of
    // mixing OFFSET after a keyset prefix.
    private readonly Dictionary<int, MediaPageCursor> _latestCursorByGeneration = new();
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
            _latestCursorByGeneration.Clear();
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
        var queryState = GetCurrentQueryState();
        if (queryState == null)
        {
            AppTraceLogger.Log("MediaPageStore", $"EnsureMediaIdAvailableAsync skipped because query state has not been initialized. MediaId='{mediaId}'.");
            return false;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // H3: locate the target row with a single COUNT query and load one
        // window straight up to it, instead of serially paging 120 items at a
        // time (which meant dozens of queries to reach the tail of a large
        // library). Falls back to sequential paging for non-positionable sorts
        // or if the fast path does not confirm the row.
        var target = await _library.GetMediaByIdAsync(mediaId);
        var position = target == null
            ? null
            : await _library.QueryMediaPositionAsync(queryState.ToMediaQuery(0, 0), target);
        if (position is >= 0)
        {
            await _items.EnsureItemAvailableAsync(position.Value);
            if (_items.IndexOfMediaId(mediaId) >= 0)
            {
                AppTraceLogger.LogSampled(
                    "MediaPageStore",
                    "ensure-media-id-position",
                    $"Ensure media id available via position lookup. MediaId='{mediaId}', Position={position.Value}, LoadedCount={_items.Count}, HasMore={_items.HasMoreItems}, ElapsedMs={stopwatch.ElapsedMilliseconds}.",
                    TimeSpan.FromSeconds(1));
                return true;
            }
        }

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
            if (_queryStatesByGeneration.Count <= 2 && _latestCursorByGeneration.Count <= 2)
            {
                return;
            }

            var newestGenerations = _queryStatesByGeneration.Keys
                .Concat(_latestCursorByGeneration.Keys)
                .Distinct()
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

            foreach (var generation in _latestCursorByGeneration.Keys.ToList())
            {
                if (!newestGenerations.Contains(generation))
                {
                    _latestCursorByGeneration.Remove(generation);
                }
            }
        }
    }

    private async Task<MediaPageResult> LoadPageAsync(int offset, int limit, int generation, CancellationToken cancellationToken)
    {
        var queryState = ResolveQueryState(generation);
        var query = queryState.ToMediaQuery(offset, limit);
        var useKeyset = FeatureFlags.KeysetPaginationEnabled
            && !query.RandomOrder
            && MediaRepository.IsKeysetSupported(query.SortField);

        if (useKeyset)
        {
            if (offset <= 0)
            {
                ClearLatestCursor(generation);
            }
            else
            {
                var cursor = TryGetLatestCursor(generation) ?? TryRebuildCursorFromLoadedWindow(query);
                if (cursor != null)
                {
                    query = CloneQueryWithCursor(query, cursor);
                }
                else
                {
                    AppTraceLogger.LogSampled(
                        "MediaPageStore",
                        "keyset-cursor-missing",
                        $"Keyset continuation had no cursor; restarting from the first page instead of OFFSET. Generation={generation}, Offset={offset}.",
                        TimeSpan.FromSeconds(2));
                    // Preserve the requested offset when a cursor cannot be
                    // rebuilt. Restarting at offset 0 would append the first
                    // page again and can leave incremental loading stuck.
                    query = CloneQueryWithCursor(queryState.ToMediaQuery(offset, limit), cursor: null);
                }
            }
        }

        AppTraceLogger.LogSampled(
            "MediaPageStore",
            "load-page",
            $"Load page requested. Generation={generation}, Offset={offset}, Limit={limit}, Keyset={useKeyset}, HasCursor={query.Cursor != null}, VisibleFolders={queryState.VisibleFolderPaths.Count}, PlaylistId='{queryState.SelectedPlaylistId ?? "<null>"}'.",
            TimeSpan.FromSeconds(1));

        // WP-A: cancellation flows from IncrementalMediaCollection's generation
        // token; MediaLibraryService hops to the thread pool internally.
        var result = await _library.QueryPageAsync(query, cancellationToken);

        if (useKeyset && result.NextCursor != null)
        {
            RecordLatestCursor(generation, result.NextCursor);
        }

        return result;
    }

    private MediaPageCursor? TryRebuildCursorFromLoadedWindow(MediaQuery query)
    {
        if (_items.Count == 0)
        {
            return null;
        }

        return MediaRepository.TryCreateCursor(query, _items[^1].Media);
    }

    private static MediaQuery CloneQueryWithCursor(MediaQuery query, MediaPageCursor? cursor)
    {
        return new MediaQuery
        {
            SearchText = query.SearchText,
            SelectedTags = query.SelectedTags,
            PlaylistId = query.PlaylistId,
            IncludedFolderPaths = query.IncludedFolderPaths,
            ExactFolderPaths = query.ExactFolderPaths,
            ExcludedFolderPaths = query.ExcludedFolderPaths,
            TypeFilter = query.TypeFilter,
            ExcludedMediaIds = query.ExcludedMediaIds,
            RandomOrder = query.RandomOrder,
            SortField = query.SortField,
            SortOrder = query.SortOrder,
            AfterMediaId = query.AfterMediaId,
            Cursor = cursor,
            Offset = 0,
            Limit = query.Limit
        };
    }

    private MediaPageCursor? TryGetLatestCursor(int generation)
    {
        lock (_queryStateSync)
        {
            return _latestCursorByGeneration.TryGetValue(generation, out var cursor)
                ? cursor
                : null;
        }
    }

    private void RecordLatestCursor(int generation, MediaPageCursor cursor)
    {
        lock (_queryStateSync)
        {
            _latestCursorByGeneration[generation] = cursor;
        }
    }

    private void ClearLatestCursor(int generation)
    {
        lock (_queryStateSync)
        {
            _latestCursorByGeneration.Remove(generation);
        }
    }
}
