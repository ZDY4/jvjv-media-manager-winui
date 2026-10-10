using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.Services.MainPage;

internal sealed class WatchedFolderMonitorService : IDisposable
{
    private const int WatcherInternalBufferSize = 64 * 1024;
    private static readonly TimeSpan WatcherDebounceInterval = TimeSpan.FromMilliseconds(900);
    private readonly object _sync = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly HashSet<string> _pendingImports = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingDeletes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingRelocations = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> _currentFolders = Array.Empty<string>();
    private DateTimeOffset? _pendingFirstEventAt;
    private CancellationTokenSource? _debounce;
    private long _batchSequence;
    private bool _isDisposed;

    public event EventHandler<WatchedFolderChangesEventArgs>? ChangesReady;

    public void SetWatchedFolders(IEnumerable<string> folderPaths, bool forceRecreate = false)
    {
        if (_isDisposed)
        {
            return;
        }

        var normalizedFolders = folderPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(PathHelpers.NormalizeFolderPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        lock (_sync)
        {
            if (_isDisposed)
            {
                return;
            }

            if (!forceRecreate && _currentFolders.SequenceEqual(normalizedFolders, StringComparer.OrdinalIgnoreCase))
            {
                AppTraceLogger.LogSampled(
                    "WatchedFolderMonitor",
                    "watchers-unchanged",
                    $"Skipped updating watchers because folders are unchanged. WatcherCount={_watchers.Count}.",
                    TimeSpan.FromSeconds(5));
                return;
            }

            _currentFolders = normalizedFolders;
            DisposeWatchersLocked();

            foreach (var folder in normalizedFolders.Select(PathHelpers.ToNativePath))
            {
                if (!Directory.Exists(folder))
                {
                    continue;
                }

                try
                {
                    var watcher = new FileSystemWatcher(folder)
                    {
                        IncludeSubdirectories = true,
                        InternalBufferSize = WatcherInternalBufferSize,
                        NotifyFilter = NotifyFilters.FileName
                            | NotifyFilters.DirectoryName
                            | NotifyFilters.LastWrite
                            | NotifyFilters.Size
                    };
                    watcher.Created += Watcher_CreatedOrChanged;
                    watcher.Changed += Watcher_CreatedOrChanged;
                    watcher.Deleted += Watcher_Deleted;
                    watcher.Renamed += Watcher_Renamed;
                    watcher.Error += Watcher_Error;
                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                }
                catch (Exception ex)
                {
                    AppTraceLogger.LogSampled(
                        "WatchedFolderMonitor",
                        "watcher-create-failed",
                        $"Could not watch folder. Folder='{folder}'. ErrorType={ex.GetType().Name}, Message='{ex.Message}'.",
                        TimeSpan.FromSeconds(5));
                }
            }

            AppTraceLogger.Log("WatchedFolderMonitor", $"Updated watchers. WatcherCount={_watchers.Count}.");
        }
    }

    public void Requeue(WatchedFolderChangesEventArgs changes)
    {
        QueueChange(changes.Imports, changes.Deletes, changes.Relocations);
    }

    public void Dispose()
    {
        _isDisposed = true;
        CancellationTokenSource? debounce;
        lock (_sync)
        {
            debounce = _debounce;
            _debounce = null;
            DisposeWatchersLocked();
            _pendingImports.Clear();
            _pendingDeletes.Clear();
            _pendingRelocations.Clear();
            _pendingFirstEventAt = null;
        }

        debounce?.Cancel();
        debounce?.Dispose();
    }

    private void DisposeWatchersLocked()
    {
        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Created -= Watcher_CreatedOrChanged;
                watcher.Changed -= Watcher_CreatedOrChanged;
                watcher.Deleted -= Watcher_Deleted;
                watcher.Renamed -= Watcher_Renamed;
                watcher.Error -= Watcher_Error;
                watcher.Dispose();
            }
            catch
            {
                // Best-effort watcher cleanup only.
            }
        }

        _watchers.Clear();
    }

    private void Watcher_CreatedOrChanged(object sender, FileSystemEventArgs e)
    {
        if (!ShouldQueueImportEvent(e))
        {
            AppTraceLogger.LogSampled(
                "WatchedFolderMonitor",
                "ignored-import-event",
                $"Ignored watched folder import event. ChangeType={e.ChangeType}, Path='{PathHelpers.NormalizePath(e.FullPath)}'.",
                TimeSpan.FromSeconds(2));
            return;
        }

        QueueChange(new[] { e.FullPath }, Array.Empty<string>(), Array.Empty<string>());
    }

    private static bool ShouldQueueImportEvent(FileSystemEventArgs e)
    {
        var nativePath = e.FullPath;
        if (e.ChangeType == WatcherChangeTypes.Changed)
        {
            return File.Exists(nativePath) && MediaExtensions.IsSupported(nativePath);
        }

        if (e.ChangeType == WatcherChangeTypes.Created)
        {
            if (Directory.Exists(nativePath))
            {
                return true;
            }

            return File.Exists(nativePath) && MediaExtensions.IsSupported(nativePath);
        }

        return true;
    }

    private void Watcher_Deleted(object sender, FileSystemEventArgs e)
    {
        QueueChange(Array.Empty<string>(), new[] { e.FullPath }, Array.Empty<string>());
    }

    private void Watcher_Renamed(object sender, RenamedEventArgs e)
    {
        QueueChange(new[] { e.FullPath }, Array.Empty<string>(), new[] { e.OldFullPath });
    }

    private void Watcher_Error(object sender, ErrorEventArgs e)
    {
        var exception = e.GetException();
        AppTraceLogger.LogSampled(
            "WatchedFolderMonitor",
            "watcher-error",
            $"Watched folder watcher error. ErrorType={exception?.GetType().Name ?? "<unknown>"}, Message='{exception?.Message ?? string.Empty}'.",
            TimeSpan.FromSeconds(5));
        IReadOnlyList<string> currentFolders;
        lock (_sync)
        {
            currentFolders = _currentFolders;
        }

        if (IsBufferOverflow(exception) && currentFolders.Count > 0)
        {
            AppTraceLogger.LogSampled(
                "WatchedFolderMonitor",
                "watcher-overflow-rescan",
                $"Queued watched folder reconciliation after watcher overflow. FolderCount={currentFolders.Count}.",
                TimeSpan.FromSeconds(5));
            QueueChange(currentFolders, Array.Empty<string>(), Array.Empty<string>());
        }

        // An overflow invalidates the native watcher buffer even though the
        // configured folder list is unchanged, so force a fresh watcher.
        SetWatchedFolders(currentFolders, forceRecreate: IsBufferOverflow(exception));
    }

    private static bool IsBufferOverflow(Exception? exception)
    {
        return exception is InternalBufferOverflowException;
    }

    private void QueueChange(IEnumerable<string> imports, IEnumerable<string> deletes, IEnumerable<string> relocations)
    {
        if (_isDisposed)
        {
            return;
        }

        var normalizedImports = imports
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(PathHelpers.NormalizePath)
            .ToList();
        var normalizedDeletes = deletes
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(PathHelpers.NormalizePath)
            .ToList();
        var normalizedRelocations = relocations
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(PathHelpers.NormalizePath)
            .ToList();
        if (normalizedImports.Count == 0
            && normalizedDeletes.Count == 0
            && normalizedRelocations.Count == 0)
        {
            AppTraceLogger.LogSampled(
                "WatchedFolderMonitor",
                "empty-change-skipped",
                "Skipped queuing watched folder changes because all paths were empty.",
                TimeSpan.FromSeconds(5));
            return;
        }

        CancellationTokenSource debounce;
        lock (_sync)
        {
            if (_isDisposed)
            {
                return;
            }

            _pendingFirstEventAt ??= DateTimeOffset.UtcNow;
            foreach (var path in normalizedImports)
            {
                _pendingImports.Add(path);
            }

            foreach (var path in normalizedDeletes)
            {
                _pendingDeletes.Add(path);
            }

            foreach (var path in normalizedRelocations)
            {
                _pendingRelocations.Add(path);
            }

            _debounce?.Cancel();
            _debounce?.Dispose();
            debounce = new CancellationTokenSource();
            _debounce = debounce;
        }

        _ = RaiseChangesAfterDelayAsync(debounce.Token);
    }

    private async Task RaiseChangesAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(WatcherDebounceInterval, cancellationToken);
            if (_isDisposed || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var changes = TakePendingChanges();
            if (changes.Imports.Count == 0
                && changes.Deletes.Count == 0
                && changes.Relocations.Count == 0)
            {
                AppTraceLogger.LogSampled(
                    "WatchedFolderMonitor",
                    "empty-batch-skipped",
                    "Skipped watched folder batch because no pending changes remained.",
                    TimeSpan.FromSeconds(5));
                return;
            }

            var batchId = Interlocked.Increment(ref _batchSequence);
            var coalescedMs = changes.FirstEventAt.HasValue
                ? (DateTimeOffset.UtcNow - changes.FirstEventAt.Value).TotalMilliseconds
                : 0;
            AppTraceLogger.LogSampled(
                "WatchedFolderMonitor",
                "batch-ready",
                $"Watched folder batch ready. BatchId={batchId}, Imports={changes.Imports.Count}, Deletes={changes.Deletes.Count}, Relocations={changes.Relocations.Count}, CoalescedMs={coalescedMs:0}, FirstImport='{changes.Imports.FirstOrDefault() ?? "<none>"}', FirstDelete='{changes.Deletes.FirstOrDefault() ?? "<none>"}', FirstRelocation='{changes.Relocations.FirstOrDefault() ?? "<none>"}'.",
                TimeSpan.FromSeconds(1));
            if (!_isDisposed)
            {
                ChangesReady?.Invoke(this, new WatchedFolderChangesEventArgs(batchId, changes.Imports, changes.Deletes, changes.Relocations, changes.FirstEventAt));
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer watcher batch.
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                lock (_sync)
                {
                    if (_debounce?.Token == cancellationToken)
                    {
                        _debounce.Dispose();
                        _debounce = null;
                    }
                }
            }
        }
    }

    private (IReadOnlyList<string> Imports, IReadOnlyList<string> Deletes, IReadOnlyList<string> Relocations, DateTimeOffset? FirstEventAt) TakePendingChanges()
    {
        lock (_sync)
        {
            var imports = _pendingImports.ToList();
            var deletes = _pendingDeletes.ToList();
            var relocations = _pendingRelocations.ToList();
            var firstEventAt = _pendingFirstEventAt;
            _pendingImports.Clear();
            _pendingDeletes.Clear();
            _pendingRelocations.Clear();
            _pendingFirstEventAt = null;
            return (imports, deletes, relocations, firstEventAt);
        }
    }
}

internal sealed class WatchedFolderChangesEventArgs : EventArgs
{
    public WatchedFolderChangesEventArgs(
        long batchId,
        IReadOnlyList<string> imports,
        IReadOnlyList<string> deletes,
        IReadOnlyList<string> relocations,
        DateTimeOffset? firstEventAt)
    {
        BatchId = batchId;
        Imports = imports;
        Deletes = deletes;
        Relocations = relocations;
        FirstEventAt = firstEventAt;
    }

    public long BatchId { get; }

    public IReadOnlyList<string> Imports { get; }

    public IReadOnlyList<string> Deletes { get; }

    public IReadOnlyList<string> Relocations { get; }

    public DateTimeOffset? FirstEventAt { get; }
}
