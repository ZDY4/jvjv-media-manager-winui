using System.Diagnostics;
using JvJvMediaManager.Data;
using JvJvMediaManager.Models;
using JvJvMediaManager.Utilities;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace JvJvMediaManager.Services;

public sealed class MediaLibraryService : IDisposable
{
    private sealed class FolderEnumerationState
    {
        public bool HadErrors { get; set; }
    }

    private readonly MediaDb _db;
    private readonly PlaybackResourceCoordinator _playbackResources;
    private readonly SemaphoreSlim _fingerprintBackfillGate = new(1, 1);
    private readonly object _backfillTasksSync = new();
    private readonly HashSet<Task> _backfillTasks = new();
    private readonly CancellationTokenSource _disposeCancellation = new();
    private const int BatchSize = 80;
    private static readonly TimeSpan ProgressReportInterval = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan BackfillDisposeTimeout = TimeSpan.FromSeconds(5);
    private const int ProgressReportBatchSize = 24;
    private static readonly int ScanDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
    private bool _isDisposed;

    public MediaLibraryService(MediaDb db, PlaybackResourceCoordinator playbackResources)
    {
        _db = db;
        _playbackResources = playbackResources;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _disposeCancellation.Cancel();
        Task[] pendingTasks;
        lock (_backfillTasksSync)
        {
            pendingTasks = _backfillTasks.ToArray();
        }

        if (pendingTasks.Length > 0)
        {
            try
            {
                Task.WaitAll(pendingTasks, BackfillDisposeTimeout);
            }
            catch (Exception ex) when (ex is AggregateException or OperationCanceledException)
            {
                AppTraceLogger.LogSampled(
                    "MediaLibrary",
                    "media-details-backfill-dispose-wait-failed",
                    $"Media details backfill dispose wait ended with cancellation/error. Pending={pendingTasks.Length}, ErrorType={ex.GetType().Name}, Message='{ex.Message}'.",
                    TimeSpan.FromSeconds(5));
            }
        }

        _disposeCancellation.Dispose();
        _fingerprintBackfillGate.Dispose();
    }

    public Task<MediaPageResult> QueryPageAsync(MediaQuery query, CancellationToken cancellationToken = default)
    {
        // WP-A: Microsoft.Data.Sqlite executes its *Async APIs synchronously on
        // the calling thread. Page loads are triggered from the UI thread (list
        // virtualization / scroll preload), so hop to the thread pool here; all
        // callers of this choke point benefit.
        return Task.Run(() => _db.QueryMediaPageAsync(query, cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<MediaFolderSummary>> QueryFolderSummariesAsync(MediaQuery query, CancellationToken cancellationToken = default)
    {
        // WP-A: folder summaries aggregate the whole filtered table on every
        // refresh; keep that GROUP BY off the UI thread.
        return Task.Run(() => _db.QueryMediaFolderSummariesAsync(query, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// H3: position lookup for deep navigation. The COUNT scans the filtered
    /// table, so keep it off the UI thread like the page queries.
    /// </summary>
    public Task<int?> QueryMediaPositionAsync(MediaQuery query, MediaFile target, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => _db.QueryMediaPositionAsync(query, target, cancellationToken), cancellationToken);
    }

    public Task<MediaFile?> GetMediaByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        return string.IsNullOrWhiteSpace(id)
            ? Task.FromResult<MediaFile?>(null)
            : _db.GetMediaByIdAsync(id, cancellationToken);
    }

    public async Task<int> AddFilesAsync(IEnumerable<string> paths, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var requested = paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        AppTraceLogger.Log("MediaLibrary", $"AddFilesAsync start. RequestedPaths={requested.Count}.");
        return await ProcessFilesAsync("add-files", EnumerateImportFiles(requested, cancellationToken), progress, cancellationToken: cancellationToken);
    }

    public async Task<int> AddFolderAsync(string folderPath, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        AppTraceLogger.Log("MediaLibrary", $"AddFolderAsync start. Folder='{folderPath}', Exists={Directory.Exists(folderPath)}.");
        return await ProcessFilesAsync("add-folder", EnumerateFolderMediaFiles(folderPath, cancellationToken), progress, cancellationToken: cancellationToken);
    }

    public async Task<int> RescanFoldersAsync(IEnumerable<string> folders, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedFolders = folders
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Select(PathHelpers.NormalizeFolderPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedFolders.Count == 0)
        {
            AppTraceLogger.Log("MediaLibrary", "RescanFoldersAsync skipped. No visible folders.");
            progress?.Report(new ScanProgress(0, 0, string.Empty, true));
            return 0;
        }

        AppTraceLogger.Log("MediaLibrary", $"RescanFoldersAsync start. FolderCount={normalizedFolders.Count}.");
        var successfullyScannedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var discoveredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var persisted = await ProcessFilesAsync(
            "rescan-folders",
            EnumerateRescanFiles(normalizedFolders, successfullyScannedFolders, cancellationToken),
            progress,
            discoveredPaths,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (successfullyScannedFolders.Count > 0)
        {
            var existingEntries = await _db.GetMediaEntriesUnderFoldersAsync(successfullyScannedFolders.ToList(), cancellationToken);
            var staleIds = existingEntries
                .Where(entry => !discoveredPaths.Contains(entry.Path))
                .Select(entry => entry.Id)
                .ToList();

            if (staleIds.Count > 0)
            {
                AppTraceLogger.Log("MediaLibrary", $"RescanFoldersAsync removing stale media rows for relocation. ScannedFolders={successfullyScannedFolders.Count}, Discovered={discoveredPaths.Count}, StaleCount={staleIds.Count}.");
                await _db.RemoveMediaRecordsOnlyAsync(staleIds, cancellationToken);
            }
        }

        AppTraceLogger.Log("MediaLibrary", $"RescanFoldersAsync completed. Persisted={persisted}, ScannedFolders={successfullyScannedFolders.Count}, Discovered={discoveredPaths.Count}.");
        return persisted;
    }

    private static IEnumerable<string> EnumerateImportFiles(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        var normalized = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var path in normalized)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(path))
            {
                foreach (var file in EnumerateFolderMediaFiles(path, cancellationToken))
                {
                    yield return file;
                }
            }
            else if (File.Exists(path) && MediaExtensions.IsSupported(path))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> EnumerateFolderMediaFiles(string folderPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(folderPath))
        {
            yield break;
        }

        var state = new FolderEnumerationState();
        foreach (var file in EnumerateFolderMediaFiles(folderPath, state, cancellationToken))
        {
            yield return file;
        }
    }

    private static IEnumerable<string> EnumerateRescanFiles(
        IEnumerable<string> folders,
        ISet<string> successfullyScannedFolders,
        CancellationToken cancellationToken)
    {
        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(folder))
            {
                AppTraceLogger.LogSampled(
                    "MediaLibrary",
                    "rescan-folder-missing",
                    $"Skipped stale reconciliation for missing rescan folder. Folder='{folder}'.",
                    TimeSpan.FromSeconds(5));
                continue;
            }

            var state = new FolderEnumerationState();
            foreach (var file in EnumerateFolderMediaFiles(PathHelpers.ToNativePath(folder), state, cancellationToken))
            {
                yield return file;
            }

            if (!state.HadErrors)
            {
                successfullyScannedFolders.Add(folder);
            }
        }
    }

    private static IEnumerable<string> EnumerateFolderMediaFiles(string folderPath, FolderEnumerationState state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(folderPath))
        {
            yield break;
        }

        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(folderPath);
        var visitedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentDirectory = pendingDirectories.Pop();
            var normalizedDirectory = Path.GetFullPath(currentDirectory);
            if (!visitedDirectories.Add(normalizedDirectory))
            {
                continue;
            }

            try
            {
                var attributes = File.GetAttributes(currentDirectory);
                if ((attributes & System.IO.FileAttributes.ReparsePoint) != 0)
                {
                    // Junctions and symlinks can escape the watched root or
                    // create cycles; the root scan only follows real folders.
                    continue;
                }
            }
            catch (Exception ex)
            {
                state.HadErrors = true;
                AppTraceLogger.LogSampled(
                    "MediaLibrary",
                    "enumerate-directory-attributes-failed",
                    $"EnumerateFolderMediaFiles could not inspect directory attributes. Directory='{currentDirectory}'. ErrorType={ex.GetType().Name}, Message='{ex.Message}'.",
                    TimeSpan.FromSeconds(5));
                continue;
            }

            foreach (var childDirectory in EnumerateDirectoryEntries(currentDirectory, directories: true, state, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                pendingDirectories.Push(childDirectory);
            }

            foreach (var file in EnumerateDirectoryEntries(currentDirectory, directories: false, state, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (MediaExtensions.IsSupported(file))
                {
                    yield return file;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateDirectoryEntries(
        string directory,
        bool directories,
        FolderEnumerationState state,
        CancellationToken cancellationToken)
    {
        IEnumerator<string>? enumerator;
        try
        {
            enumerator = directories
                ? Directory.EnumerateDirectories(directory).GetEnumerator()
                : Directory.EnumerateFiles(directory).GetEnumerator();
        }
        catch (Exception ex)
        {
            state.HadErrors = true;
            AppTraceLogger.LogSampled(
                "MediaLibrary",
                directories ? "enumerate-directories-failed" : "enumerate-files-failed",
                $"EnumerateFolderMediaFiles could not list entries. Directory='{directory}', Directories={directories}. ErrorType={ex.GetType().Name}, Message='{ex.Message}'.",
                TimeSpan.FromSeconds(5));
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                string current;
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        yield break;
                    }

                    current = enumerator.Current;
                }
                catch (Exception ex)
                {
                    state.HadErrors = true;
                    AppTraceLogger.LogSampled(
                        "MediaLibrary",
                        directories ? "enumerate-directories-failed" : "enumerate-files-failed",
                        $"EnumerateFolderMediaFiles could not enumerate entries. Directory='{directory}', Directories={directories}. ErrorType={ex.GetType().Name}, Message='{ex.Message}'.",
                        TimeSpan.FromSeconds(5));
                    yield break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                yield return current;
            }
        }
    }

    private async Task<int> ProcessFilesAsync(
        string operationName,
        IEnumerable<string> files,
        IProgress<ScanProgress>? progress,
        ISet<string>? discoveredPaths = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var processed = 0;
        var persisted = 0;
        var duplicateCount = 0;
        var metadataSkippedCount = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batch = new List<MediaFile>(BatchSize);
        var fingerprintBackfill = new List<MediaFile>();
        var seenSync = new object();
        var batchSync = new object();
        var fingerprintBackfillSync = new object();
        var progressSync = new object();
        var writeSemaphore = new SemaphoreSlim(1, 1);
        var progressStopwatch = Stopwatch.StartNew();
        var lastReportedCount = 0;
        var sawAnyFile = false;

        var scanDegreeOfParallelism = _playbackResources.GetScanDegreeOfParallelism(ScanDegreeOfParallelism);
        AppTraceLogger.Log("MediaLibrary", $"ProcessFilesAsync started. Operation={operationName}, Parallelism={scanDegreeOfParallelism}, PlaybackActive={_playbackResources.IsPlaybackActive}.");

        void ReportProgressSafe(int processedCount, string currentPath, bool isComplete, bool isIndeterminate, bool shouldRefreshLibrary = false)
        {
            lock (progressSync)
            {
                ReportProgress(progress, processedCount, currentPath, isComplete, isIndeterminate, progressStopwatch, ref lastReportedCount, shouldRefreshLibrary);
            }
        }

        async Task FlushBatchAsync(List<MediaFile> items, int processedCount, string currentPath, bool isFinalBatch)
        {
            if (items.Count == 0)
            {
                return;
            }

            if (isFinalBatch)
            {
                AppTraceLogger.Log("MediaLibrary", $"ProcessFilesAsync flushing final batch. Operation={operationName}, BatchSize={items.Count}, Processed={processedCount}, PersistedBefore={Volatile.Read(ref persisted)}.");
            }
            else
            {
                AppTraceLogger.LogSampled(
                    "MediaLibrary",
                    $"{operationName}-batch",
                    $"ProcessFilesAsync flushing batch. Operation={operationName}, BatchSize={items.Count}, Processed={processedCount}, PersistedBefore={Volatile.Read(ref persisted)}.",
                    TimeSpan.FromSeconds(1));
            }

            var writeWaitStartedAt = Stopwatch.GetTimestamp();
            await writeSemaphore.WaitAsync(cancellationToken);
            var writeWaitElapsed = Stopwatch.GetElapsedTime(writeWaitStartedAt);
            var upsertStartedAt = Stopwatch.GetTimestamp();
            try
            {
                await _db.UpsertMediaBatchAsync(items, cancellationToken);
                lock (fingerprintBackfillSync)
                {
                    fingerprintBackfill.AddRange(items);
                }
            }
            finally
            {
                writeSemaphore.Release();
            }
            var upsertElapsed = Stopwatch.GetElapsedTime(upsertStartedAt);
            if (writeWaitElapsed.TotalMilliseconds >= 100 || upsertElapsed.TotalMilliseconds >= 100)
            {
                AppTraceLogger.LogSampled(
                    "MediaLibrary",
                    $"{operationName}-batch-write-perf",
                    $"ProcessFilesAsync batch write perf. Operation={operationName}, BatchSize={items.Count}, WriteWaitMs={writeWaitElapsed.TotalMilliseconds:0}, UpsertMs={upsertElapsed.TotalMilliseconds:0}, Final={isFinalBatch}, PlaybackActive={_playbackResources.IsPlaybackActive}.",
                    TimeSpan.FromSeconds(2));
            }

            Interlocked.Add(ref persisted, items.Count);
            ReportProgressSafe(processedCount, currentPath, isComplete: false, isIndeterminate: true, shouldRefreshLibrary: true);
        }

        await Parallel.ForEachAsync(
            files,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = scanDegreeOfParallelism
            },
            async (file, token) =>
            {
                token.ThrowIfCancellationRequested();
                using var backgroundIo = await _playbackResources.EnterBackgroundIoAsync($"scan:{operationName}", token);
                Volatile.Write(ref sawAnyFile, true);
                var currentProcessed = Interlocked.Increment(ref processed);

                var normalizedPath = PathHelpers.NormalizePath(file);
                var isDuplicate = false;
                lock (seenSync)
                {
                    if (!seen.Add(normalizedPath))
                    {
                        isDuplicate = true;
                    }
                    else
                    {
                        discoveredPaths?.Add(normalizedPath);
                    }
                }

                if (isDuplicate)
                {
                    Interlocked.Increment(ref duplicateCount);
                    ReportProgressSafe(currentProcessed, file, isComplete: false, isIndeterminate: true);
                    return;
                }

                var media = await ProcessFileAsync(file, token);
                if (media == null)
                {
                    Interlocked.Increment(ref metadataSkippedCount);
                    ReportProgressSafe(currentProcessed, file, isComplete: false, isIndeterminate: true);
                    return;
                }

                List<MediaFile>? batchToFlush = null;
                lock (batchSync)
                {
                    batch.Add(media);
                    if (batch.Count >= BatchSize)
                    {
                        batchToFlush = new List<MediaFile>(batch);
                        batch.Clear();
                    }
                }

                if (batchToFlush != null)
                {
                    await FlushBatchAsync(batchToFlush, currentProcessed, file, isFinalBatch: false);
                }

                ReportProgressSafe(currentProcessed, file, isComplete: false, isIndeterminate: true);
            });

        cancellationToken.ThrowIfCancellationRequested();
        List<MediaFile>? finalBatch = null;
        lock (batchSync)
        {
            if (batch.Count > 0)
            {
                finalBatch = new List<MediaFile>(batch);
                batch.Clear();
            }
        }

        var flushedFinalBatch = finalBatch != null;
        if (flushedFinalBatch)
        {
            await FlushBatchAsync(finalBatch!, Volatile.Read(ref processed), string.Empty, isFinalBatch: true);
        }

        if (!sawAnyFile)
        {
            progress?.Report(new ScanProgress(0, 0, string.Empty, true));
            AppTraceLogger.Log("MediaLibrary", $"ProcessFilesAsync completed with no files. Operation={operationName}, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
            return 0;
        }

        progress?.Report(new ScanProgress(processed, processed, string.Empty, true, ShouldRefreshLibrary: flushedFinalBatch));
        List<MediaFile> fingerprintBackfillSnapshot;
        lock (fingerprintBackfillSync)
        {
            fingerprintBackfillSnapshot = new List<MediaFile>(fingerprintBackfill);
            fingerprintBackfill.Clear();
        }

        QueueMediaDetailsBackfill(fingerprintBackfillSnapshot, operationName, progress);
        AppTraceLogger.Log("MediaLibrary", $"ProcessFilesAsync completed. Operation={operationName}, Processed={processed}, Persisted={persisted}, Duplicates={duplicateCount}, Skipped={metadataSkippedCount}, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
        return persisted;
    }

    private static void ReportProgress(
        IProgress<ScanProgress>? progress,
        int processed,
        string currentPath,
        bool isComplete,
        bool isIndeterminate,
        Stopwatch stopwatch,
        ref int lastReportedCount,
        bool shouldRefreshLibrary = false)
    {
        if (progress == null)
        {
            return;
        }

        if (!isComplete && !shouldRefreshLibrary)
        {
            var countDelta = processed - lastReportedCount;
            if (countDelta < ProgressReportBatchSize && stopwatch.Elapsed < ProgressReportInterval)
            {
                return;
            }
        }

        lastReportedCount = processed;
        stopwatch.Restart();
        progress.Report(new ScanProgress(processed, isIndeterminate ? 0 : processed, currentPath, isComplete, isIndeterminate, shouldRefreshLibrary));
    }

    private async Task<MediaFile?> ProcessFileAsync(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!MediaExtensions.IsSupported(filePath))
        {
            return null;
        }

        FileInfo info;
        try
        {
            info = new FileInfo(filePath);
            if (!info.Exists)
            {
                return null;
            }
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogSampled(
                "MediaLibrary",
                "file-info-failed",
                $"ProcessFileAsync could not read file info. Path='{filePath}'. ErrorType={ex.GetType().Name}, Message='{ex.Message}'.",
                TimeSpan.FromSeconds(5));
            return null;
        }

        var initialLength = info.Length;
        var initialModifiedUtc = info.LastWriteTimeUtc;
        var initialModifiedAt = new DateTimeOffset(initialModifiedUtc).ToUnixTimeSeconds();

        var ext = Path.GetExtension(filePath);
        var type = MediaExtensions.VideoExtensions.Contains(ext) ? MediaType.Video : MediaType.Image;
        string? contentHash = null;
        var fingerprintVersion = 0;

        int? width = null;
        int? height = null;
        double? duration = null;

        var normalizedPath = PathHelpers.NormalizePath(filePath);
        var existingMedia = await _db.GetMediaByPathAsync(normalizedPath, cancellationToken);
        if (existingMedia is { } existing
            && existing.Size == initialLength
            && existing.ModifiedAt == initialModifiedAt)
        {
            width = existing.Width;
            height = existing.Height;
            duration = existing.Duration;
            if (existing.FingerprintVersion == MediaContentFingerprint.Version
                && !string.IsNullOrWhiteSpace(existing.ContentHash))
            {
                contentHash = existing.ContentHash;
                fingerprintVersion = existing.FingerprintVersion;
                AppTraceLogger.LogSampled(
                    "MediaLibrary",
                    "fingerprint-reused",
                    $"ProcessFileAsync reused content fingerprint. Path='{filePath}', MediaId='{existing.Id}'.",
                    TimeSpan.FromSeconds(5));
            }
        }
        else if (await _db.HasReusableFingerprintCandidateAsync(normalizedPath, initialLength, cancellationToken))
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                contentHash = await MediaContentFingerprint.ComputeAsync(filePath, initialLength, cancellationToken);
                fingerprintVersion = MediaContentFingerprint.Version;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppTraceLogger.LogSampled(
                    "MediaLibrary",
                    "fingerprint-failed",
                    $"ProcessFileAsync fingerprint failed. Path='{filePath}'. ErrorType={ex.GetType().Name}, Message='{ex.Message}'.",
                    TimeSpan.FromSeconds(5));
            }
        }
        else
        {
            AppTraceLogger.LogSampled(
                "MediaLibrary",
                "fingerprint-deferred",
                $"ProcessFileAsync deferred content fingerprint. Path='{filePath}', Size={initialLength}.",
                TimeSpan.FromSeconds(5));
        }

        cancellationToken.ThrowIfCancellationRequested();
        info.Refresh();
        if (!info.Exists)
        {
            return null;
        }

        if (info.Length != initialLength || info.LastWriteTimeUtc != initialModifiedUtc)
        {
            AppTraceLogger.LogSampled(
                "MediaLibrary",
                "file-changed-during-scan",
                $"ProcessFileAsync skipped unstable file. Path='{filePath}', InitialLength={initialLength}, CurrentLength={info.Length}, InitialModifiedUtc='{initialModifiedUtc:O}', CurrentModifiedUtc='{info.LastWriteTimeUtc:O}'.",
                TimeSpan.FromSeconds(5));
            return null;
        }

        var createdAt = new DateTimeOffset(info.CreationTimeUtc).ToUnixTimeSeconds();
        var modifiedAt = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();

        return new MediaFile
        {
            Id = PathHelpers.ComputeStableId(filePath),
            Path = PathHelpers.NormalizePath(filePath),
            FileName = info.Name,
            Type = type,
            Size = initialLength,
            Width = width,
            Height = height,
            Duration = duration,
            CreatedAt = createdAt,
            ModifiedAt = modifiedAt,
            ContentHash = contentHash,
            FingerprintVersion = fingerprintVersion
        };
    }

    private void QueueMediaDetailsBackfill(IReadOnlyList<MediaFile> mediaItems, string operationName, IProgress<ScanProgress>? progress)
    {
        var pending = mediaItems
            .Where(media => media.Type is MediaType.Video or MediaType.Image)
            .Where(media => NeedsMetadataBackfill(media)
                || string.IsNullOrWhiteSpace(media.ContentHash)
                || media.FingerprintVersion != MediaContentFingerprint.Version)
            .Select(media => new MediaFile
            {
                Id = media.Id,
                Path = media.Path,
                FileName = media.FileName,
                Type = media.Type,
                Size = media.Size,
                Width = media.Width,
                Height = media.Height,
                Duration = media.Duration,
                CreatedAt = media.CreatedAt,
                ModifiedAt = media.ModifiedAt
            })
            .ToList();
        if (pending.Count == 0)
        {
            return;
        }

        if (_isDisposed)
        {
            return;
        }

        TrackBackfillTask(Task.Run(() => BackfillMediaDetailsAsync(pending, operationName, progress, _disposeCancellation.Token)));
    }

    private void TrackBackfillTask(Task task)
    {
        lock (_backfillTasksSync)
        {
            _backfillTasks.Add(task);
        }

        _ = task.ContinueWith(
            completed =>
            {
                lock (_backfillTasksSync)
                {
                    _backfillTasks.Remove(completed);
                }

                if (completed.IsFaulted && completed.Exception != null)
                {
                    AppTraceLogger.LogException(
                        "MediaLibrary",
                        "Media details backfill task failed.",
                        completed.Exception.GetBaseException());
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task BackfillMediaDetailsAsync(IReadOnlyList<MediaFile> mediaItems, string operationName, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        await _fingerprintBackfillGate.WaitAsync(cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        var updated = 0;
        var metadataUpdatesSinceLastSignal = 0;
        try
        {
            AppTraceLogger.LogSampled(
                "MediaLibrary",
                "media-details-backfill-start",
                $"Media details backfill started. Operation={operationName}, Count={mediaItems.Count}.",
                TimeSpan.FromSeconds(2));

            foreach (var media in mediaItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var nativePath = PathHelpers.ToNativePath(media.Path);
                try
                {
                    using var backgroundIo = await _playbackResources.EnterBackgroundIoAsync($"backfill:{operationName}", cancellationToken);
                    var info = new FileInfo(nativePath);
                    if (!info.Exists
                        || info.Length != media.Size
                        || new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds() != media.ModifiedAt)
                    {
                        continue;
                    }

                    var metadataUpdated = false;
                    if (NeedsMetadataBackfill(media))
                    {
                        var metadataStartedAt = Stopwatch.GetTimestamp();
                        var metadata = await ReadMediaMetadataAsync(nativePath, media.Type, cancellationToken);
                        var metadataElapsed = Stopwatch.GetElapsedTime(metadataStartedAt);
                        if (metadataElapsed.TotalMilliseconds >= 250)
                        {
                            AppTraceLogger.LogSampled(
                                "MediaLibrary",
                                "metadata-backfill-slow-read",
                                $"Media metadata backfill read was slow. Path='{media.Path}', MediaId='{media.Id}', ElapsedMs={metadataElapsed.TotalMilliseconds:0}, Type={media.Type}, PlaybackActive={_playbackResources.IsPlaybackActive}.",
                                TimeSpan.FromSeconds(2));
                        }

                        if (metadata.Width.HasValue || metadata.Height.HasValue || metadata.Duration.HasValue)
                        {
                            await _db.UpdateMediaMetadataByPathAsync(media.Path, media.Size, media.ModifiedAt, metadata.Width, metadata.Height, metadata.Duration, cancellationToken);
                            metadataUpdated = true;
                        }
                    }

                    var fingerprintUpdated = false;
                    if (string.IsNullOrWhiteSpace(media.ContentHash) || media.FingerprintVersion != MediaContentFingerprint.Version)
                    {
                        var fingerprintStartedAt = Stopwatch.GetTimestamp();
                        var contentHash = await MediaContentFingerprint.ComputeAsync(nativePath, media.Size, cancellationToken);
                        var fingerprintElapsed = Stopwatch.GetElapsedTime(fingerprintStartedAt);
                        if (fingerprintElapsed.TotalMilliseconds >= 250)
                        {
                            AppTraceLogger.LogSampled(
                                "MediaLibrary",
                                "fingerprint-backfill-slow-read",
                                $"Media fingerprint backfill read was slow. Path='{media.Path}', MediaId='{media.Id}', Size={media.Size}, ElapsedMs={fingerprintElapsed.TotalMilliseconds:0}, PlaybackActive={_playbackResources.IsPlaybackActive}.",
                                TimeSpan.FromSeconds(2));
                        }

                        await _db.UpdateMediaFingerprintByPathAsync(media.Path, media.Size, media.ModifiedAt, contentHash, MediaContentFingerprint.Version, cancellationToken);
                        fingerprintUpdated = true;
                    }

                    if (metadataUpdated || fingerprintUpdated)
                    {
                        updated++;
                        if (metadataUpdated)
                        {
                            metadataUpdatesSinceLastSignal++;
                            if (metadataUpdatesSinceLastSignal >= BatchSize)
                            {
                                progress?.Report(new ScanProgress(0, 0, string.Empty, true, ShouldRefreshLibrary: true, IsBackgroundUpdate: true));
                                metadataUpdatesSinceLastSignal = 0;
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppTraceLogger.LogSampled(
                        "MediaLibrary",
                        "media-details-backfill-failed",
                        $"Media details backfill failed. Path='{media.Path}'. ErrorType={ex.GetType().Name}, Message='{ex.Message}'.",
                        TimeSpan.FromSeconds(5));
                }
            }

            if (metadataUpdatesSinceLastSignal > 0)
            {
                progress?.Report(new ScanProgress(0, 0, string.Empty, true, ShouldRefreshLibrary: true, IsBackgroundUpdate: true));
            }

            AppTraceLogger.LogSampled(
                "MediaLibrary",
                "media-details-backfill-complete",
                $"Media details backfill completed. Operation={operationName}, Requested={mediaItems.Count}, Updated={updated}, ElapsedMs={stopwatch.ElapsedMilliseconds}.",
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            _fingerprintBackfillGate.Release();
        }
    }

    private static bool NeedsMetadataBackfill(MediaFile media)
    {
        return media.Type == MediaType.Image
            ? !media.Width.HasValue || !media.Height.HasValue
            : !media.Width.HasValue || !media.Height.HasValue || !media.Duration.HasValue;
    }

    private static async Task<(int? Width, int? Height, double? Duration)> ReadMediaMetadataAsync(string path, MediaType type, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var storage = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken);
        if (type == MediaType.Image)
        {
            var properties = await storage.Properties.GetImagePropertiesAsync().AsTask(cancellationToken);
            int? width = properties.Width > 0 ? (int)properties.Width : null;
            int? height = properties.Height > 0 ? (int)properties.Height : null;
            if (!width.HasValue || !height.HasValue)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var stream = await storage.OpenReadAsync().AsTask(cancellationToken);
                var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
                width = (int)decoder.PixelWidth;
                height = (int)decoder.PixelHeight;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return (width, height, null);
        }

        var videoProperties = await storage.Properties.GetVideoPropertiesAsync().AsTask(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return (
            videoProperties.Width > 0 ? (int)videoProperties.Width : null,
            videoProperties.Height > 0 ? (int)videoProperties.Height : null,
            videoProperties.Duration > TimeSpan.Zero ? videoProperties.Duration.TotalSeconds : null);
    }
}

public readonly record struct ScanProgress(
    int Scanned,
    int Total,
    string CurrentPath,
    bool IsComplete,
    bool IsIndeterminate = false,
    bool ShouldRefreshLibrary = false,
    bool IsBackgroundUpdate = false);
