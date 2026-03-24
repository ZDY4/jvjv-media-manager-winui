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
    private const int BatchSize = 200;
    private static readonly TimeSpan ProgressReportInterval = TimeSpan.FromMilliseconds(120);
    private const int ProgressReportBatchSize = 24;

    public MediaLibraryService(MediaDb db)
    {
        _db = db;
    }

    public Task<MediaPageResult> QueryPageAsync(MediaQuery query)
    {
        return Task.Run(() => _db.QueryMediaPage(query));
    }

    public async Task<int> AddFilesAsync(IEnumerable<string> paths, IProgress<ScanProgress>? progress = null)
    {
        return await ProcessFilesAsync(EnumerateImportFiles(paths), progress);
    }

    public async Task<int> AddFolderAsync(string folderPath, IProgress<ScanProgress>? progress = null)
    {
        return await ProcessFilesAsync(EnumerateFolderMediaFiles(folderPath), progress);
    }

    public async Task<int> RescanFoldersAsync(IEnumerable<string> folders, IProgress<ScanProgress>? progress = null)
    {
        var normalizedFolders = folders
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Select(PathHelpers.NormalizeFolderPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedFolders.Count == 0)
        {
            progress?.Report(new ScanProgress(0, 0, string.Empty, true));
            return 0;
        }

        var successfullyScannedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var discoveredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var persisted = await ProcessFilesAsync(
            EnumerateRescanFiles(normalizedFolders, successfullyScannedFolders),
            progress,
            discoveredPaths);

        if (successfullyScannedFolders.Count > 0)
        {
            var existingEntries = _db.GetMediaEntriesUnderFolders(successfullyScannedFolders.ToList());
            var staleIds = existingEntries
                .Where(entry => !discoveredPaths.Contains(entry.Path))
                .Select(entry => entry.Id)
                .ToList();

            if (staleIds.Count > 0)
            {
                _db.DeleteMedia(staleIds);
            }
        }

        return persisted;
    }

    private static IEnumerable<string> EnumerateImportFiles(IEnumerable<string> paths)
    {
        var normalized = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var path in normalized)
        {
            if (Directory.Exists(path))
            {
                foreach (var file in EnumerateFolderMediaFiles(path))
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

    private static IEnumerable<string> EnumerateFolderMediaFiles(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            yield break;
        }

        var state = new FolderEnumerationState();
        foreach (var file in EnumerateFolderMediaFiles(folderPath, state))
        {
            yield return file;
        }
    }

    private static IEnumerable<string> EnumerateRescanFiles(
        IEnumerable<string> folders,
        ISet<string> successfullyScannedFolders)
    {
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder))
            {
                successfullyScannedFolders.Add(folder);
                continue;
            }

            var state = new FolderEnumerationState();
            foreach (var file in EnumerateFolderMediaFiles(PathHelpers.ToNativePath(folder), state))
            {
                yield return file;
            }

            if (!state.HadErrors)
            {
                successfullyScannedFolders.Add(folder);
            }
        }
    }

    private static IEnumerable<string> EnumerateFolderMediaFiles(string folderPath, FolderEnumerationState state)
    {
        if (!Directory.Exists(folderPath))
        {
            yield break;
        }

        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(folderPath);

        while (pendingDirectories.Count > 0)
        {
            var currentDirectory = pendingDirectories.Pop();

            string[] childDirectories;
            try
            {
                childDirectories = Directory.GetDirectories(currentDirectory);
            }
            catch
            {
                state.HadErrors = true;
                continue;
            }

            foreach (var childDirectory in childDirectories)
            {
                pendingDirectories.Push(childDirectory);
            }

            string[] childFiles;
            try
            {
                childFiles = Directory.GetFiles(currentDirectory);
            }
            catch
            {
                state.HadErrors = true;
                continue;
            }

            foreach (var file in childFiles)
            {
                if (MediaExtensions.IsSupported(file))
                {
                    yield return file;
                }
            }
        }
    }

    private async Task<int> ProcessFilesAsync(
        IEnumerable<string> files,
        IProgress<ScanProgress>? progress,
        ISet<string>? discoveredPaths = null)
    {
        var processed = 0;
        var persisted = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batch = new List<MediaFile>(BatchSize);
        var progressStopwatch = Stopwatch.StartNew();
        var lastReportedCount = 0;
        var sawAnyFile = false;

        foreach (var file in files)
        {
            sawAnyFile = true;
            processed++;

            var normalizedPath = PathHelpers.NormalizePath(file);
            if (!seen.Add(normalizedPath))
            {
                ReportProgress(progress, processed, file, isComplete: false, isIndeterminate: true, progressStopwatch, ref lastReportedCount);
                continue;
            }

            discoveredPaths?.Add(normalizedPath);

            var media = await ProcessFileAsync(file);
            if (media != null)
            {
                batch.Add(media);
                if (batch.Count >= BatchSize)
                {
                    _db.UpsertMediaBatch(batch);
                    persisted += batch.Count;
                    batch.Clear();
                }
            }

            ReportProgress(progress, processed, file, isComplete: false, isIndeterminate: true, progressStopwatch, ref lastReportedCount);
            await Task.Yield();
        }

        if (batch.Count > 0)
        {
            _db.UpsertMediaBatch(batch);
            persisted += batch.Count;
        }

        if (!sawAnyFile)
        {
            progress?.Report(new ScanProgress(0, 0, string.Empty, true));
            return 0;
        }

        progress?.Report(new ScanProgress(processed, processed, string.Empty, true));
        return persisted;
    }

    private static void ReportProgress(
        IProgress<ScanProgress>? progress,
        int processed,
        string currentPath,
        bool isComplete,
        bool isIndeterminate,
        Stopwatch stopwatch,
        ref int lastReportedCount)
    {
        if (progress == null)
        {
            return;
        }

        if (!isComplete)
        {
            var countDelta = processed - lastReportedCount;
            if (countDelta < ProgressReportBatchSize && stopwatch.Elapsed < ProgressReportInterval)
            {
                return;
            }
        }

        lastReportedCount = processed;
        stopwatch.Restart();
        progress.Report(new ScanProgress(processed, isIndeterminate ? 0 : processed, currentPath, isComplete, isIndeterminate));
    }

    private async Task<MediaFile?> ProcessFileAsync(string filePath)
    {
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
        catch
        {
            return null;
        }

        var ext = Path.GetExtension(filePath);
        var type = MediaExtensions.VideoExtensions.Contains(ext) ? MediaType.Video : MediaType.Image;

        int? width = null;
        int? height = null;
        double? duration = null;

        try
        {
            var storage = await StorageFile.GetFileFromPathAsync(filePath);
            if (type == MediaType.Image)
            {
                using var stream = await storage.OpenReadAsync();
                var decoder = await BitmapDecoder.CreateAsync(stream);
                width = (int)decoder.PixelWidth;
                height = (int)decoder.PixelHeight;
            }
            else
            {
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
        catch
        {
            // Ignore metadata errors.
        }

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

public readonly record struct ScanProgress(int Scanned, int Total, string CurrentPath, bool IsComplete, bool IsIndeterminate = false);
