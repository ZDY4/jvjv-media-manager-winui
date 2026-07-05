using JvJvMediaManager.Utilities;
using JvJvMediaManager.ViewModels;

namespace JvJvMediaManager.Controllers.MainPage;

internal sealed class MissingMediaCleanupWorker
{
    private const int MaxBatchSize = 48;
    private readonly object _sync = new();
    private readonly HashSet<string> _queuedIds = new(StringComparer.Ordinal);
    private readonly Func<IReadOnlyList<MediaItemViewModel>> _getLoadedItems;
    private readonly Action<MediaItemViewModel, string> _logMissingMedia;
    private int _activeCount;

    public MissingMediaCleanupWorker(
        Func<IReadOnlyList<MediaItemViewModel>> getLoadedItems,
        Action<MediaItemViewModel, string> logMissingMedia)
    {
        _getLoadedItems = getLoadedItems;
        _logMissingMedia = logMissingMedia;
    }

    public bool IsActive => Volatile.Read(ref _activeCount) > 0;

    public bool TryQueue(MediaItemViewModel media, string source)
    {
        lock (_sync)
        {
            if (!_queuedIds.Add(media.Id))
            {
                return false;
            }
        }

        _logMissingMedia(media, source);
        return true;
    }

    public async Task<IReadOnlyList<MediaItemViewModel>> BuildCleanupBatchAsync(MediaItemViewModel media)
    {
        var items = _getLoadedItems();
        return await Task.Run(() => BuildCleanupBatch(items, media));
    }

    public async Task<bool> IsMediaFileAvailableAsync(MediaItemViewModel media)
    {
        return await Task.Run(() => IsMediaFileAvailable(media));
    }

    public IDisposable EnterCleanup()
    {
        Interlocked.Increment(ref _activeCount);
        return new CleanupScope(this);
    }

    public void MarkBatchQueued(IEnumerable<MediaItemViewModel> mediaItems, ISet<string> cleanupIds)
    {
        lock (_sync)
        {
            foreach (var item in mediaItems)
            {
                _queuedIds.Add(item.Id);
                cleanupIds.Add(item.Id);
            }
        }
    }

    public void ReleaseQueued(MediaItemViewModel requestedMedia, IEnumerable<string> cleanupIds)
    {
        lock (_sync)
        {
            _queuedIds.Remove(requestedMedia.Id);
            foreach (var id in cleanupIds)
            {
                _queuedIds.Remove(id);
            }
        }
    }

    private static IReadOnlyList<MediaItemViewModel> BuildCleanupBatch(IReadOnlyList<MediaItemViewModel> items, MediaItemViewModel media)
    {
        var index = IndexOfMediaId(items, media.Id);
        if (index < 0)
        {
            return IsMediaFileAvailable(media)
                ? Array.Empty<MediaItemViewModel>()
                : new[] { media };
        }

        var batch = new List<MediaItemViewModel>(capacity: Math.Min(MaxBatchSize, items.Count - index));
        for (var currentIndex = index; currentIndex < items.Count && batch.Count < MaxBatchSize; currentIndex++)
        {
            var candidate = items[currentIndex];
            if (IsMediaFileAvailable(candidate))
            {
                break;
            }

            batch.Add(candidate);
        }

        return batch;
    }

    private static bool IsMediaFileAvailable(MediaItemViewModel media)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(media.FileSystemPath) && File.Exists(media.FileSystemPath);
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("MainPageShell", $"IsMediaFileAvailable failed. MediaId='{media.Id}', Path='{media.FileSystemPath}'.", ex);
            return false;
        }
    }

    private static int IndexOfMediaId(IReadOnlyList<MediaItemViewModel> items, string mediaId)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i].Id, mediaId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private sealed class CleanupScope : IDisposable
    {
        private MissingMediaCleanupWorker? _owner;

        public CleanupScope(MissingMediaCleanupWorker owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner != null)
            {
                Interlocked.Decrement(ref owner._activeCount);
            }
        }
    }
}
