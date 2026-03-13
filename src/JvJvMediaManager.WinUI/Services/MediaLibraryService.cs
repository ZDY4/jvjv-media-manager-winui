using JvJvMediaManager.Data;
using JvJvMediaManager.Models;
using JvJvMediaManager.Utilities;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace JvJvMediaManager.Services;

public sealed class MediaLibraryService
{
    private readonly MediaDb _db;
    private const int BatchSize = 200;

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
        var normalized = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var files = new List<string>();
        foreach (var path in normalized)
        {
            if (Directory.Exists(path))
            {
                files.AddRange(EnumerateMediaFiles(path));
            }
            else if (File.Exists(path) && MediaExtensions.IsSupported(path))
            {
                files.Add(path);
            }
        }

        return await ProcessFilesAsync(files, progress);
    }

    public async Task<int> AddFolderAsync(string folderPath, IProgress<ScanProgress>? progress = null)
    {
        var files = EnumerateMediaFiles(folderPath);
        return await ProcessFilesAsync(files, progress);
    }

    public async Task<int> RescanFoldersAsync(IEnumerable<string> folders, IProgress<ScanProgress>? progress = null)
    {
        var files = new List<string>();
        foreach (var folder in folders)
        {
            files.AddRange(EnumerateMediaFiles(folder));
        }
        return await ProcessFilesAsync(files, progress);
    }

    private static List<string> EnumerateMediaFiles(string folderPath)
    {
        var results = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories))
            {
                if (MediaExtensions.IsSupported(file))
                {
                    results.Add(file);
                }
            }
        }
        catch
        {
            // Ignore folder scan errors
        }
        return results;
    }

    private async Task<int> ProcessFilesAsync(List<string> files, IProgress<ScanProgress>? progress)
    {
        if (files.Count == 0)
        {
            progress?.Report(new ScanProgress(0, 0, "", true));
            return 0;
        }

        var total = files.Count;
        var processed = 0;
        var persisted = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batch = new List<MediaFile>(BatchSize);

        foreach (var file in files)
        {
            processed++;
            if (!seen.Add(file))
            {
                progress?.Report(new ScanProgress(processed, total, file, processed == total));
                continue;
            }

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

            progress?.Report(new ScanProgress(processed, total, file, processed == total));
            await Task.Yield();
        }

        if (batch.Count > 0)
        {
            _db.UpsertMediaBatch(batch);
            persisted += batch.Count;
        }

        return persisted;
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

        if (type == MediaType.Image)
        {
            try
            {
                var storage = await StorageFile.GetFileFromPathAsync(filePath);
                using var stream = await storage.OpenReadAsync();
                var decoder = await BitmapDecoder.CreateAsync(stream);
                width = (int)decoder.PixelWidth;
                height = (int)decoder.PixelHeight;
            }
            catch
            {
                // Ignore metadata errors
            }
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
            CreatedAt = createdAt,
            ModifiedAt = modifiedAt
        };
    }
}

public readonly record struct ScanProgress(int Scanned, int Total, string CurrentPath, bool IsComplete);
