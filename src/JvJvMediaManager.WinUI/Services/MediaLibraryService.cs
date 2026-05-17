using System.Diagnostics;
using JvJvMediaManager.Data;
using JvJvMediaManager.Models;
using JvJvMediaManager.Utilities;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace JvJvMediaManager.Services;

public sealed class MediaLibraryService
{
    private sealed class FolderEnumerationState
    {
        public bool HadErrors { get; set; }
    }

    private readonly MediaDb _db;
    private const int BatchSize = 80;
    private static readonly TimeSpan ProgressReportInterval = TimeSpan.FromMilliseconds(120);
    private const int ProgressReportBatchSize = 24;
    private static readonly int ScanDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);

    public MediaLibraryService(MediaDb db)
    {
        _db = db;
    }

    public Task<MediaPageResult> QueryPageAsync(MediaQuery query, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _db.QueryMediaPage(query);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<MediaFolderSummary>> QueryFolderSummariesAsync(MediaQuery query, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _db.QueryMediaFolderSummaries(query);
        }, cancellationToken);
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
            var existingEntries = _db.GetMediaEntriesUnderFolders(successfullyScannedFolders.ToList());
            var staleIds = existingEntries
                .Where(entry => !discoveredPaths.Contains(entry.Path))
                .Select(entry => entry.Id)
                .ToList();

            if (staleIds.Count > 0)
            {
                AppTraceLogger.Log("MediaLibrary", $"RescanFoldersAsync deleting stale records. ScannedFolders={successfullyScannedFolders.Count}, Discovered={discoveredPaths.Count}, StaleCount={staleIds.Count}.");
                _db.DeleteMedia(staleIds);
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
                successfullyScannedFolders.Add(folder);
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

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentDirectory = pendingDirectories.Pop();

            string[] childDirectories;
            try
            {
                childDirectories = Directory.GetDirectories(currentDirectory);
            }
            catch (Exception ex)
            {
                state.HadErrors = true;
                AppTraceLogger.LogSampled(
                    "MediaLibrary",
                    "enumerate-directories-failed",
                    $"EnumerateFolderMediaFiles could not list child directories. Directory='{currentDirectory}'. ErrorType={ex.GetType().Name}, Message='{ex.Message}'.",
                    TimeSpan.FromSeconds(5));
                continue;
            }

            foreach (var childDirectory in childDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pendingDirectories.Push(childDirectory);
            }

            string[] childFiles;
            try
            {
                childFiles = Directory.GetFiles(currentDirectory);
            }
            catch (Exception ex)
            {
                state.HadErrors = true;
                AppTraceLogger.LogSampled(
                    "MediaLibrary",
                    "enumerate-files-failed",
                    $"EnumerateFolderMediaFiles could not list files. Directory='{currentDirectory}'. ErrorType={ex.GetType().Name}, Message='{ex.Message}'.",
                    TimeSpan.FromSeconds(5));
                continue;
            }

            foreach (var file in childFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (MediaExtensions.IsSupported(file))
                {
                    yield return file;
                }
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
        var seenSync = new object();
        var batchSync = new object();
        var progressSync = new object();
        var writeSemaphore = new SemaphoreSlim(1, 1);
        var progressStopwatch = Stopwatch.StartNew();
        var lastReportedCount = 0;
        var sawAnyFile = false;

        AppTraceLogger.Log("MediaLibrary", $"ProcessFilesAsync started. Operation={operationName}, Parallelism={ScanDegreeOfParallelism}.");

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

            await writeSemaphore.WaitAsync(cancellationToken);
            try
            {
                _db.UpsertMediaBatch(items);
            }
            finally
            {
                writeSemaphore.Release();
            }

            Interlocked.Add(ref persisted, items.Count);
            ReportProgressSafe(processedCount, currentPath, isComplete: false, isIndeterminate: true, shouldRefreshLibrary: true);
        }

        await Parallel.ForEachAsync(
            files,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = ScanDegreeOfParallelism
            },
            async (file, token) =>
            {
                token.ThrowIfCancellationRequested();
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

        var ext = Path.GetExtension(filePath);
        var type = MediaExtensions.VideoExtensions.Contains(ext) ? MediaType.Video : MediaType.Image;

        int? width = null;
        int? height = null;
        double? duration = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var storage = await StorageFile.GetFileFromPathAsync(filePath);
            if (type == MediaType.Image)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var stream = await storage.OpenReadAsync();
                var decoder = await BitmapDecoder.CreateAsync(stream);
                width = (int)decoder.PixelWidth;
                height = (int)decoder.PixelHeight;
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                var properties = await storage.Properties.GetVideoPropertiesAsync();
                if (properties.Width > 0)
                {
                    width = (int)properties.Width;
                }

                if (properties.Height > 0)
                {
                    height = (int)properties.Height;
                }

                if (properties.Duration > TimeSpan.Zero)
                {
                    duration = properties.Duration.TotalSeconds;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogSampled(
                "MediaLibrary",
                "metadata-read-failed",
                $"ProcessFileAsync metadata read failed. Path='{filePath}', Type={type}. ErrorType={ex.GetType().Name}, Message='{ex.Message}'.",
                TimeSpan.FromSeconds(5));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var createdAt = new DateTimeOffset(info.CreationTimeUtc).ToUnixTimeSeconds();
        var modifiedAt = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();

        return new MediaFile
        {
            Id = PathHelpers.ComputeStableId(filePath),
            Path = PathHelpers.NormalizePath(filePath),
            FileName = info.Name,
            Type = type,
            Size = info.Length,
            Width = width,
            Height = height,
            Duration = duration,
            CreatedAt = createdAt,
            ModifiedAt = modifiedAt
        };
    }
}

public readonly record struct ScanProgress(
    int Scanned,
    int Total,
    string CurrentPath,
    bool IsComplete,
    bool IsIndeterminate = false,
    bool ShouldRefreshLibrary = false);
