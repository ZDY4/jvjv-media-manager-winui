using JvJvMediaManager.Data;
using JvJvMediaManager.Services;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.ViewModels.MainPage;

// WP-10 (11.1): owns background persistence cleanup for deleted media (DB delete
// + thumbnail/timeline cache eviction) and the shutdown drain. Extracted from
// LibraryShellViewModel so the view model keeps only bindable state and command
// entry points. Behavior and logging are preserved.
internal sealed class LibraryPersistenceCoordinator
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    private readonly MediaDb _db;
    private readonly ThumbnailService _thumbnails;
    private readonly TimelineThumbnailStripService _timelineThumbnails;
    private readonly Action<IReadOnlyList<string>> _onCleanupFinished;
    private readonly object _tasksSync = new();
    private readonly List<Task> _cleanupTasks = new();

    public LibraryPersistenceCoordinator(
        MediaDb db,
        ThumbnailService thumbnails,
        TimelineThumbnailStripService timelineThumbnails,
        Action<IReadOnlyList<string>> onCleanupFinished)
    {
        _db = db;
        _thumbnails = thumbnails;
        _timelineThumbnails = timelineThumbnails;
        _onCleanupFinished = onCleanupFinished;
    }

    public void QueueDeletedMediaCleanup(IReadOnlyList<string> mediaIds)
    {
        if (mediaIds.Count == 0)
        {
            return;
        }

        var ids = mediaIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var cleanupTask = Task.Run(async () =>
        {
            try
            {
                await _db.DeleteMediaAsync(ids);
                _thumbnails.ClearCacheForMediaIds(ids);
                _timelineThumbnails.ClearCacheForMediaIds(ids);
                AppTraceLogger.Log("LibraryShell", $"Deleted media persistence cleanup completed. Count={ids.Count}.");
            }
            catch (Exception ex)
            {
                AppTraceLogger.LogException("LibraryShell", $"Deleted media persistence cleanup failed. Count={ids.Count}.", ex);
            }
            finally
            {
                _onCleanupFinished(ids);
            }
        });

        lock (_tasksSync)
        {
            _cleanupTasks.Add(cleanupTask);
        }

        _ = cleanupTask.ContinueWith(
            task =>
            {
                lock (_tasksSync)
                {
                    _cleanupTasks.Remove(task);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void Drain()
    {
        Task[] pendingTasks;
        lock (_tasksSync)
        {
            pendingTasks = _cleanupTasks
                .Where(task => !task.IsCompleted)
                .ToArray();
        }

        if (pendingTasks.Length == 0)
        {
            return;
        }

        AppTraceLogger.Log("LibraryShell", $"Draining deleted media persistence cleanup tasks. Count={pendingTasks.Length}.");
        try
        {
            if (!Task.WaitAll(pendingTasks, DrainTimeout))
            {
                AppTraceLogger.Log("LibraryShell", $"Deleted media persistence cleanup drain timed out. Pending={pendingTasks.Count(task => !task.IsCompleted)}.");
            }
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("LibraryShell", "DrainDeletedMediaPersistenceCleanupTasks failed.", ex);
        }
    }
}
