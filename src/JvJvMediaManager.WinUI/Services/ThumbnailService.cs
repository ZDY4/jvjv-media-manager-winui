using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using JvJvMediaManager.Models;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.Services;

public sealed class ThumbnailService
{
    private const int MaxMemoryCacheEntries = 256;

    private readonly SemaphoreSlim _loadGate = new(4, 4);
    private readonly Dictionary<string, ImageSource> _memoryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LinkedListNode<string>> _memoryCacheNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<ImageSource?>> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _memoryCacheLru = new();
    private readonly object _sync = new();
    private readonly string _cacheDir;

    public ThumbnailService(string cacheDir)
    {
        _cacheDir = cacheDir;
        Directory.CreateDirectory(_cacheDir);
    }

    public async Task<ImageSource?> GetThumbnailAsync(MediaFile media)
    {
        var cacheKey = BuildCacheKey(media);
        Task<ImageSource?> task;

        lock (_sync)
        {
            if (_memoryCache.TryGetValue(cacheKey, out var cached))
            {
                TouchMemoryCacheEntry(cacheKey);
                return cached;
            }

            if (!_inflight.TryGetValue(cacheKey, out var inflightTask) || inflightTask == null)
            {
                inflightTask = LoadThumbnailCoreAsync(media, cacheKey);
                _inflight[cacheKey] = inflightTask;
            }

            task = inflightTask;
        }

        return await task;
    }

    public void ClearCache()
    {
        lock (_sync)
        {
            _memoryCache.Clear();
            _memoryCacheNodes.Clear();
            _memoryCacheLru.Clear();
            _inflight.Clear();
        }

        if (!Directory.Exists(_cacheDir))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_cacheDir, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    public void ClearCacheForMediaIds(IEnumerable<string> mediaIds)
    {
        var ids = mediaIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0)
        {
            return;
        }

        lock (_sync)
        {
            foreach (var cacheKey in _memoryCache.Keys.Where(key => ids.Any(id => key.StartsWith($"{id}:", StringComparison.OrdinalIgnoreCase))).ToList())
            {
                _memoryCache.Remove(cacheKey);
                if (_memoryCacheNodes.Remove(cacheKey, out var node))
                {
                    _memoryCacheLru.Remove(node);
                }
            }

            foreach (var inflightKey in _inflight.Keys.Where(key => ids.Any(id => key.StartsWith($"{id}:", StringComparison.OrdinalIgnoreCase))).ToList())
            {
                _inflight.Remove(inflightKey);
            }
        }

        if (!Directory.Exists(_cacheDir))
        {
            return;
        }

        foreach (var id in ids)
        {
            var pattern = $"{id}_*.thumb";
            foreach (var file in Directory.EnumerateFiles(_cacheDir, pattern, SearchOption.TopDirectoryOnly))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // Best-effort cleanup only.
                }
            }
        }
    }

    private async Task<ImageSource?> LoadThumbnailCoreAsync(MediaFile media, string cacheKey)
    {
        await _loadGate.WaitAsync();
        try
        {
            var cachePath = GetCachePath(media);
            var source = await TryLoadBitmapAsync(cachePath);
            if (source == null)
            {
                source = await GenerateAndPersistThumbnailAsync(media, cachePath);
            }

            lock (_sync)
            {
                if (source != null)
                {
                    RememberInMemoryCache(cacheKey, source);
                }

                _inflight.Remove(cacheKey);
            }

            return source;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private async Task<ImageSource?> GenerateAndPersistThumbnailAsync(MediaFile media, string cachePath)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(PathHelpers.ToNativePath(media.Path));
            var thumbnailMode = media.Type == MediaType.Video ? ThumbnailMode.VideosView : ThumbnailMode.PicturesView;
            using var thumbnail = await file.GetThumbnailAsync(thumbnailMode, 320, ThumbnailOptions.UseCurrentScale);
            if (thumbnail != null && thumbnail.Size > 0)
            {
                await PersistStreamAsync(media, thumbnail, cachePath);
                return await TryLoadBitmapAsync(cachePath);
            }
        }
        catch
        {
            // Ignore Windows thumbnail generation failures and fall back below.
        }

        return null;
    }

    private async Task PersistStreamAsync(MediaFile media, IRandomAccessStream stream, string cachePath)
    {
        Directory.CreateDirectory(_cacheDir);
        DeleteStaleCacheFiles(media, cachePath);

        var folder = await StorageFolder.GetFolderFromPathAsync(_cacheDir);
        var file = await folder.CreateFileAsync(Path.GetFileName(cachePath), CreationCollisionOption.ReplaceExisting);
        using var output = await file.OpenAsync(FileAccessMode.ReadWrite);
        stream.Seek(0);
        output.Seek(0);
        await RandomAccessStream.CopyAsync(stream, output);
        await output.FlushAsync();
    }

    private async Task<ImageSource?> TryLoadBitmapAsync(string cachePath)
    {
        if (!File.Exists(cachePath))
        {
            return null;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(cachePath);
            using var stream = await file.OpenReadAsync();
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch
        {
            try
            {
                File.Delete(cachePath);
            }
            catch
            {
                // Ignore cleanup errors for broken cache entries.
            }

            return null;
        }
    }

    private void DeleteStaleCacheFiles(MediaFile media, string currentCachePath)
    {
        var pattern = $"{media.Id}_*.thumb";
        foreach (var file in Directory.EnumerateFiles(_cacheDir, pattern, SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(file, currentCachePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch
            {
                // Best-effort stale cache cleanup only.
            }
        }
    }

    private string GetCachePath(MediaFile media)
    {
        return Path.Combine(_cacheDir, $"{media.Id}_{media.ModifiedAt}.thumb");
    }

    private static string BuildCacheKey(MediaFile media)
    {
        return $"{media.Id}:{media.ModifiedAt}";
    }

    private void RememberInMemoryCache(string cacheKey, ImageSource source)
    {
        if (_memoryCache.TryGetValue(cacheKey, out _))
        {
            _memoryCache[cacheKey] = source;
            TouchMemoryCacheEntry(cacheKey);
            return;
        }

        _memoryCache[cacheKey] = source;
        var node = _memoryCacheLru.AddLast(cacheKey);
        _memoryCacheNodes[cacheKey] = node;

        while (_memoryCache.Count > MaxMemoryCacheEntries && _memoryCacheLru.First != null)
        {
            var oldestKey = _memoryCacheLru.First.Value;
            _memoryCacheLru.RemoveFirst();
            _memoryCacheNodes.Remove(oldestKey);
            _memoryCache.Remove(oldestKey);
        }
    }

    private void TouchMemoryCacheEntry(string cacheKey)
    {
        if (!_memoryCacheNodes.TryGetValue(cacheKey, out var node))
        {
            return;
        }

        _memoryCacheLru.Remove(node);
        _memoryCacheLru.AddLast(node);
    }
}
