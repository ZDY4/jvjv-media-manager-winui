using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using JvJvMediaManager.Models;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.Services;

public sealed class ThumbnailService
{
    private const int ThumbnailCacheVersion = 2;
    private const int MaxMemoryCacheEntries = 384;
    private const int ThumbnailSize = 320;
    private static readonly TimeSpan ThumbnailPerfLogInterval = TimeSpan.FromSeconds(2);

    // WP-06: disk cache quota. Configurable defaults; tune with real telemetry.
    private static readonly CacheQuota DiskQuota = new(
        MaxBytes: 2L * 1024 * 1024 * 1024,
        MaxFiles: 50_000,
        MaxAge: TimeSpan.FromDays(90),
        TargetBytesAfterTrim: (long)(1.5 * 1024 * 1024 * 1024));
    private const int TrimTriggerFileInterval = 500;

    private readonly SemaphoreSlim _loadGate = new(2, 2);
    private readonly PlaybackResourceCoordinator _playbackResources;
    private readonly VideoHoverPreviewFrameService _videoFrameService = new();
    private readonly Dictionary<string, ImageSource> _memoryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LinkedListNode<string>> _memoryCacheNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, InflightThumbnailRequest> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _memoryCacheLru = new();
    private readonly object _sync = new();
    private readonly string _cacheDir;
    private readonly DiskCacheTrimmer _diskTrimmer;
    private int _writesSinceTrim;
    private int _trimScheduled;
    private int _startupTrimDone;
    private CancellationTokenSource _cacheCancellation = new();
    private long _cacheGeneration;

    public ThumbnailService(string cacheDir, PlaybackResourceCoordinator playbackResources)
    {
        _cacheDir = cacheDir;
        _playbackResources = playbackResources;
        Directory.CreateDirectory(_cacheDir);
        _diskTrimmer = new DiskCacheTrimmer(_cacheDir, "thumbnail");
    }

    public async Task<ImageSource?> GetThumbnailAsync(MediaFile media, CancellationToken cancellationToken = default)
    {
        var cacheKey = BuildCacheKey(media);
        InflightThumbnailRequest request;

        lock (_sync)
        {
            if (_memoryCache.TryGetValue(cacheKey, out var cached))
            {
                TouchMemoryCacheEntry(cacheKey);
                AppTraceLogger.LogSampled(
                    "ThumbnailService",
                    "thumbnail-memory-hit",
                    $"Thumbnail memory cache hit. MediaId='{media.Id}', File='{media.FileName}', CacheCount={_memoryCache.Count}, InflightCount={_inflight.Count}.",
                    ThumbnailPerfLogInterval);
                return cached;
            }

            if (!_inflight.TryGetValue(cacheKey, out request!))
            {
                var cacheGeneration = _cacheGeneration;
                var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(_cacheCancellation.Token);
                request = new InflightThumbnailRequest(cacheGeneration, requestCancellation);
                _inflight[cacheKey] = request;
                request.Task = LoadThumbnailCoreAsync(media, cacheKey, request);
                AppTraceLogger.LogSampled(
                    "ThumbnailService",
                    "thumbnail-cache-miss",
                    () => $"Thumbnail cache miss queued. MediaId='{media.Id}', File='{media.FileName}', CacheCount={_memoryCache.Count}, InflightCount={_inflight.Count}, PlaybackActive={_playbackResources.IsPlaybackActive}.",
                    ThumbnailPerfLogInterval);
            }
            else
            {
                AppTraceLogger.LogSampled(
                    "ThumbnailService",
                    "thumbnail-inflight-hit",
                    $"Thumbnail in-flight request reused. MediaId='{media.Id}', File='{media.FileName}', InflightCount={_inflight.Count}.",
                    ThumbnailPerfLogInterval);
            }

            request.WaiterCount++;
        }

        try
        {
            return await request.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            ReleaseWaiter(request);
        }
    }

    /// <summary>
    /// WP-06: runs a one-time low-priority quota check shortly after startup so
    /// large caches from previous sessions are bounded without blocking first paint.
    /// </summary>
    public void ScheduleStartupQuotaCheck()
    {
        if (Interlocked.Exchange(ref _startupTrimDone, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                _diskTrimmer.Trim(DiskQuota, "*.thumb");
            }
            catch (Exception ex)
            {
                AppTraceLogger.LogSampled(
                    "ThumbnailService",
                    "startup-trim-failed",
                    $"Startup thumbnail cache trim failed. ErrorType={ex.GetType().Name}, Message='{ex.Message}'.",
                    TimeSpan.FromSeconds(30));
            }
        });
    }

    private void NotifyDiskCacheWrite()
    {
        if (Interlocked.Increment(ref _writesSinceTrim) < TrimTriggerFileInterval)
        {
            return;
        }

        Interlocked.Exchange(ref _writesSinceTrim, 0);
        if (Interlocked.Exchange(ref _trimScheduled, 1) == 1)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                _diskTrimmer.Trim(DiskQuota, "*.thumb");
            }
            catch (Exception ex)
            {
                AppTraceLogger.LogSampled(
                    "ThumbnailService",
                    "incremental-trim-failed",
                    $"Incremental thumbnail cache trim failed. ErrorType={ex.GetType().Name}, Message='{ex.Message}'.",
                    TimeSpan.FromSeconds(30));
            }
            finally
            {
                Interlocked.Exchange(ref _trimScheduled, 0);
            }
        });
    }

    public void ClearCache()
    {
        CancellationTokenSource oldCancellation;
        List<ImageSource> cachedSources;
        lock (_sync)
        {
            oldCancellation = _cacheCancellation;
            _cacheCancellation = new CancellationTokenSource();
            cachedSources = _memoryCache.Values.ToList();
            _memoryCache.Clear();
            _memoryCacheNodes.Clear();
            _memoryCacheLru.Clear();
            _inflight.Clear();
            _cacheGeneration++;
        }

        CancelAndDisposeLater(oldCancellation);
        foreach (var source in cachedSources)
        {
            ReleaseImageSource(source);
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

        CancellationTokenSource oldCancellation;
        var removedSources = new List<ImageSource>();
        lock (_sync)
        {
            oldCancellation = _cacheCancellation;
            _cacheCancellation = new CancellationTokenSource();
            foreach (var cacheKey in _memoryCache.Keys.Where(key => ids.Any(id => key.StartsWith($"{id}:", StringComparison.OrdinalIgnoreCase))).ToList())
            {
                if (_memoryCache.Remove(cacheKey, out var source))
                {
                    removedSources.Add(source);
                }

                if (_memoryCacheNodes.Remove(cacheKey, out var node))
                {
                    _memoryCacheLru.Remove(node);
                }
            }

            foreach (var inflightKey in _inflight.Keys.Where(key => ids.Any(id => key.StartsWith($"{id}:", StringComparison.OrdinalIgnoreCase))).ToList())
            {
                _inflight.Remove(inflightKey);
            }

            _cacheGeneration++;
        }

        CancelAndDisposeLater(oldCancellation);
        foreach (var source in removedSources)
        {
            ReleaseImageSource(source);
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

    public int ClearInvalidCache(IEnumerable<MediaFile> mediaItems)
    {
        var validFileNames = mediaItems
            .Where(media => !string.IsNullOrWhiteSpace(media.Id))
            .Select(media => Path.GetFileName(GetCachePath(media)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var validCacheKeys = mediaItems
            .Where(media => !string.IsNullOrWhiteSpace(media.Id))
            .Select(BuildCacheKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        CancellationTokenSource oldCancellation;
        var removedSources = new List<ImageSource>();
        lock (_sync)
        {
            oldCancellation = _cacheCancellation;
            _cacheCancellation = new CancellationTokenSource();
            foreach (var cacheKey in _memoryCache.Keys.Where(key => !validCacheKeys.Contains(key)).ToList())
            {
                if (_memoryCache.Remove(cacheKey, out var source))
                {
                    removedSources.Add(source);
                }

                if (_memoryCacheNodes.Remove(cacheKey, out var node))
                {
                    _memoryCacheLru.Remove(node);
                }
            }

            foreach (var inflightKey in _inflight.Keys.Where(key => !validCacheKeys.Contains(key)).ToList())
            {
                _inflight.Remove(inflightKey);
            }

            _cacheGeneration++;
        }

        CancelAndDisposeLater(oldCancellation);
        foreach (var source in removedSources)
        {
            ReleaseImageSource(source);
        }

        if (!Directory.Exists(_cacheDir))
        {
            return 0;
        }

        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(_cacheDir, "*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(file);
            if (validFileNames.Contains(fileName))
            {
                continue;
            }

            try
            {
                File.Delete(file);
                deleted++;
            }
            catch
            {
                // Best-effort stale cache cleanup only.
            }
        }

        return deleted;
    }

    private async Task<ImageSource?> LoadThumbnailCoreAsync(MediaFile media, string cacheKey, InflightThumbnailRequest request)
    {
        var cacheGeneration = request.CacheGeneration;
        var cancellationToken = request.Cancellation.Token;
        var gateWaitStartedAt = Environment.TickCount64;
        var gateWaitMs = 0L;
        var startedAt = 0L;
        var sourceStage = "none";
        var gateEntered = false;
        try
        {
            using var backgroundIo = await _playbackResources.EnterBackgroundIoAsync("thumbnail", cancellationToken);
            await _loadGate.WaitAsync(cancellationToken);
            gateEntered = true;
            gateWaitMs = Environment.TickCount64 - gateWaitStartedAt;
            startedAt = Environment.TickCount64;

            var cachePath = GetCachePath(media);
            var source = await TryLoadBitmapAsync(cachePath, cancellationToken);
            if (source != null)
            {
                sourceStage = "disk-cache";
            }
            else if (media.Type == MediaType.Image)
            {
                source = await GenerateAndPersistThumbnailAsync(media, cachePath, cancellationToken);
                if (source != null)
                {
                    sourceStage = "windows-thumbnail";
                }
            }

            if (source == null)
            {
                source = await GenerateFallbackThumbnailAsync(media, cachePath, cancellationToken);
                if (source != null)
                {
                    sourceStage = "fallback";
                }
            }

            lock (_sync)
            {
                if (source != null && !cancellationToken.IsCancellationRequested && cacheGeneration == _cacheGeneration)
                {
                    RememberInMemoryCache(cacheKey, source);
                }
                else if (source != null)
                {
                    ReleaseImageSource(source);
                }

                RemoveInflightIfCurrentLocked(cacheKey, request);
            }

            return source;
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
            {
                AppTraceLogger.LogException("ThumbnailService", $"LoadThumbnailCoreAsync failed. MediaId='{media.Id}', File='{media.FileName}'.", ex);
            }

            return null;
        }
        finally
        {
            var elapsedMs = startedAt == 0 ? 0 : Environment.TickCount64 - startedAt;
            if (gateEntered && elapsedMs >= 750)
            {
                var inflightCount = 0;
                lock (_sync)
                {
                    inflightCount = _inflight.Count;
                }

                AppTraceLogger.LogSampled(
                    "ThumbnailService",
                    "slow-thumbnail",
                    $"Slow thumbnail load. MediaId='{media.Id}', File='{media.FileName}', Stage={sourceStage}, GateWaitMs={gateWaitMs}, ElapsedMs={elapsedMs}, InflightCount={inflightCount}, PlaybackActive={_playbackResources.IsPlaybackActive}.",
                    ThumbnailPerfLogInterval);
            }
            else if (gateEntered && gateWaitMs >= 100)
            {
                AppTraceLogger.LogSampled(
                    "ThumbnailService",
                    "thumbnail-gate-wait",
                    $"Thumbnail gate wait. MediaId='{media.Id}', File='{media.FileName}', Stage={sourceStage}, GateWaitMs={gateWaitMs}, ElapsedMs={elapsedMs}, PlaybackActive={_playbackResources.IsPlaybackActive}.",
                    ThumbnailPerfLogInterval);
            }

            lock (_sync)
            {
                RemoveInflightIfCurrentLocked(cacheKey, request);
            }

            if (gateEntered)
            {
                _loadGate.Release();
            }

            request.Cancellation.Dispose();
        }
    }

    private void ReleaseWaiter(InflightThumbnailRequest request)
    {
        lock (_sync)
        {
            request.WaiterCount = Math.Max(0, request.WaiterCount - 1);
            if (request.WaiterCount == 0 && !request.Task.IsCompleted)
            {
                request.Cancellation.Cancel();
            }
        }
    }

    private void RemoveInflightIfCurrentLocked(string cacheKey, InflightThumbnailRequest request)
    {
        if (_inflight.TryGetValue(cacheKey, out var current) && ReferenceEquals(current, request))
        {
            _inflight.Remove(cacheKey);
        }
    }

    private async Task<ImageSource?> GenerateAndPersistThumbnailAsync(MediaFile media, string cachePath, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = await StorageFile.GetFileFromPathAsync(PathHelpers.ToNativePath(media.Path));
            var thumbnailMode = media.Type == MediaType.Video ? ThumbnailMode.VideosView : ThumbnailMode.PicturesView;

            using var cachedThumbnail = await file.GetThumbnailAsync(
                thumbnailMode,
                ThumbnailSize,
                ThumbnailOptions.UseCurrentScale | ThumbnailOptions.ReturnOnlyIfCached);
            if (cachedThumbnail != null && cachedThumbnail.Size > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await PersistStreamAsync(media, cachedThumbnail, cachePath);
                AppTraceLogger.LogSampled(
                    "ThumbnailService",
                    "windows-cached-thumbnail-used",
                    $"Used cached Windows thumbnail. MediaId='{media.Id}', File='{media.FileName}'.",
                    TimeSpan.FromSeconds(2));
                return await TryLoadBitmapAsync(cachePath, cancellationToken);
            }

            using var thumbnail = await file.GetThumbnailAsync(thumbnailMode, ThumbnailSize, ThumbnailOptions.UseCurrentScale);
            if (thumbnail != null && thumbnail.Size > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await PersistStreamAsync(media, thumbnail, cachePath);
                return await TryLoadBitmapAsync(cachePath, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                throw;
            }

            AppTraceLogger.LogSampled(
                "ThumbnailService",
                "generate-thumbnail-failed",
                $"GenerateAndPersistThumbnailAsync failed. MediaId='{media.Id}', File='{media.FileName}'. ErrorType={ex.GetType().Name}, Message='{ex.Message}'.",
                TimeSpan.FromSeconds(5));
        }

        return null;
    }

    private async Task<ImageSource?> GenerateFallbackThumbnailAsync(MediaFile media, string cachePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return media.Type == MediaType.Video
            ? await GenerateFallbackVideoThumbnailAsync(media, cachePath, cancellationToken)
            : await GenerateFallbackImageThumbnailAsync(media, cachePath, cancellationToken);
    }

    private async Task<ImageSource?> GenerateFallbackImageThumbnailAsync(MediaFile media, string cachePath, CancellationToken cancellationToken)
    {
        try
        {
            var nativePath = PathHelpers.ToNativePath(media.Path);
            if (!File.Exists(nativePath))
            {
                return null;
            }

            var file = await StorageFile.GetFileFromPathAsync(nativePath);
            using var sourceStream = await file.OpenReadAsync();
            cancellationToken.ThrowIfCancellationRequested();

            var decoder = await BitmapDecoder.CreateAsync(sourceStream);
            var scale = Math.Min(1d, Math.Min(
                (double)ThumbnailSize / Math.Max(1, decoder.PixelWidth),
                (double)ThumbnailSize / Math.Max(1, decoder.PixelHeight)));
            var scaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale));
            var scaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale));

            using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                decoder.BitmapPixelFormat,
                decoder.BitmapAlphaMode,
                new BitmapTransform
                {
                    ScaledWidth = scaledWidth,
                    ScaledHeight = scaledHeight,
                    InterpolationMode = BitmapInterpolationMode.Fant
                },
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);
            cancellationToken.ThrowIfCancellationRequested();

            // WP-F: persist the scaled fallback so later sessions and scroll
            // passes hit the disk cache instead of re-decoding the full source
            // image every time the shell refused to thumbnail it. PNG keeps
            // alpha for transparent sources; JPEG keeps the cache quota small.
            var encoderId = decoder.BitmapAlphaMode == BitmapAlphaMode.Straight
                || decoder.BitmapAlphaMode == BitmapAlphaMode.Premultiplied
                    ? BitmapEncoder.PngEncoderId
                    : BitmapEncoder.JpegEncoderId;
            using var encodedStream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(encoderId, encodedStream);
            encoder.SetSoftwareBitmap(softwareBitmap);
            await encoder.FlushAsync();
            cancellationToken.ThrowIfCancellationRequested();

            await PersistStreamAsync(media, encodedStream, cachePath);
            AppTraceLogger.LogSampled(
                "ThumbnailService",
                "image-file-fallback-thumbnail-used",
                $"Generated image file fallback thumbnail. MediaId='{media.Id}', File='{media.FileName}', Scaled={scaledWidth}x{scaledHeight}.",
                TimeSpan.FromSeconds(2));
            return await TryLoadBitmapAsync(cachePath, cancellationToken);
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                throw;
            }

            AppTraceLogger.LogSampled(
                "ThumbnailService",
                "image-file-fallback-thumbnail-failed",
                $"Image fallback thumbnail failed. MediaId='{media.Id}', File='{media.FileName}'. ErrorType={ex.GetType().Name}, Message='{ex.Message}'.",
                TimeSpan.FromSeconds(5));
            return null;
        }
    }

    private async Task<ImageSource?> GenerateFallbackVideoThumbnailAsync(MediaFile media, string cachePath, CancellationToken cancellationToken)
    {
        try
        {
            var timestamp = ResolveVideoFallbackTimestamp(media);
            var frameSize = ResolveContainedThumbnailSize(media);
            var frameBytes = await _videoFrameService.GenerateFrameAsync(
                PathHelpers.ToNativePath(media.Path),
                timestamp,
                frameSize.Width,
                frameSize.Height,
                cancellationToken,
                cropToFill: false);
            if (frameBytes == null || frameBytes.Length == 0)
            {
                return null;
            }

            Directory.CreateDirectory(_cacheDir);
            DeleteStaleCacheFiles(media, cachePath);
            await File.WriteAllBytesAsync(cachePath, frameBytes, cancellationToken);
            NotifyDiskCacheWrite();

            AppTraceLogger.LogSampled(
                "ThumbnailService",
                "video-frame-fallback-thumbnail-used",
                $"Generated video frame fallback thumbnail. MediaId='{media.Id}', File='{media.FileName}', Timestamp={timestamp.TotalSeconds:F3}.",
                TimeSpan.FromSeconds(2));
            return await TryLoadBitmapAsync(cachePath, cancellationToken);
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                throw;
            }

            AppTraceLogger.LogSampled(
                "ThumbnailService",
                "video-frame-fallback-thumbnail-failed",
                $"Video fallback thumbnail failed. MediaId='{media.Id}', File='{media.FileName}'. ErrorType={ex.GetType().Name}, Message='{ex.Message}'.",
                TimeSpan.FromSeconds(5));
            return null;
        }
    }

    private static TimeSpan ResolveVideoFallbackTimestamp(MediaFile media)
    {
        if (media.Duration is > 3)
        {
            return TimeSpan.FromSeconds(Math.Min(5, Math.Max(1, media.Duration.Value * 0.1)));
        }

        return TimeSpan.FromSeconds(0.25);
    }

    private static (int Width, int Height) ResolveContainedThumbnailSize(MediaFile media)
    {
        if (media.Width is not > 0 || media.Height is not > 0)
        {
            return (ThumbnailSize, ThumbnailSize);
        }

        var scale = Math.Min(
            (double)ThumbnailSize / media.Width.Value,
            (double)ThumbnailSize / media.Height.Value);
        return (
            Math.Max(1, (int)Math.Round(media.Width.Value * scale)),
            Math.Max(1, (int)Math.Round(media.Height.Value * scale)));
    }

    private async Task PersistStreamAsync(MediaFile media, IRandomAccessStream stream, string cachePath)
    {
        Directory.CreateDirectory(_cacheDir);
        DeleteStaleCacheFiles(media, cachePath);

        // WP-06: write to a temp file then atomically rename so a cancelled or
        // crashed write never leaves a half-written file that reads as valid.
        var tempName = $"{Path.GetFileName(cachePath)}.{Guid.NewGuid():N}.tmp";
        var folder = await StorageFolder.GetFolderFromPathAsync(_cacheDir);
        var tempFile = await folder.CreateFileAsync(tempName, CreationCollisionOption.ReplaceExisting);
        try
        {
            using (var output = await tempFile.OpenAsync(FileAccessMode.ReadWrite))
            {
                stream.Seek(0);
                output.Seek(0);
                await RandomAccessStream.CopyAsync(stream, output);
                await output.FlushAsync();
            }

            await tempFile.MoveAndReplaceAsync(
                await folder.CreateFileAsync(Path.GetFileName(cachePath), CreationCollisionOption.OpenIfExists));
            NotifyDiskCacheWrite();
        }
        catch
        {
            try
            {
                await tempFile.DeleteAsync();
            }
            catch
            {
                // Best-effort temp cleanup only.
            }

            throw;
        }
    }

    private async Task<ImageSource?> TryLoadBitmapAsync(string cachePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(cachePath))
        {
            return null;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(cachePath, cancellationToken);
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            cancellationToken.ThrowIfCancellationRequested();
            return bitmap;
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogSampled(
                "ThumbnailService",
                "load-cache-failed",
                $"TryLoadBitmapAsync failed. CachePath='{cachePath}'. ErrorType={ex.GetType().Name}, Message='{ex.Message}'.",
                TimeSpan.FromSeconds(5));
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
        return Path.Combine(_cacheDir, $"{media.Id}_{media.ModifiedAt}_{media.Size}_{media.Type}_v{ThumbnailCacheVersion}.thumb");
    }

    private static string BuildCacheKey(MediaFile media)
    {
        return $"{media.Id}:{media.ModifiedAt}:{media.Size}:{media.Type}:v{ThumbnailCacheVersion}";
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
            if (_memoryCache.Remove(oldestKey, out var oldestSource))
            {
                ReleaseImageSource(oldestSource);
            }
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

    private static void ReleaseImageSource(ImageSource? source)
    {
        if (source is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static void CancelAndDisposeLater(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch
        {
            // Best-effort in-flight cancellation only.
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
            finally
            {
                cancellation.Dispose();
            }
        });
    }

    private sealed class InflightThumbnailRequest(long cacheGeneration, CancellationTokenSource cancellation)
    {
        public long CacheGeneration { get; } = cacheGeneration;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task<ImageSource?> Task { get; set; } = System.Threading.Tasks.Task.FromResult<ImageSource?>(null);
        public int WaiterCount { get; set; }
    }
}
