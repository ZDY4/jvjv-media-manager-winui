using System.Runtime.InteropServices.WindowsRuntime;
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
    private const int ThumbnailCacheVersion = 3;
    private const int MaxMemoryCacheEntries = 1024;
    // 256 matches Explorer's large/extra-large thumbcache slot. 320 often
    // misses that cache and forces a custom-size decode.
    private const int ThumbnailSize = 256;
    private static readonly TimeSpan ThumbnailPerfLogInterval = TimeSpan.FromSeconds(2);

    // WP-06: disk cache quota. Configurable defaults; tune with real telemetry.
    private static readonly CacheQuota DiskQuota = new(
        MaxBytes: 2L * 1024 * 1024 * 1024,
        MaxFiles: 50_000,
        MaxAge: TimeSpan.FromDays(90),
        TargetBytesAfterTrim: (long)(1.5 * 1024 * 1024 * 1024));
    private const int TrimTriggerFileInterval = 500;

    private readonly SemaphoreSlim _cacheReadGate = new(8, 8);
    private readonly SemaphoreSlim _generateGate = new(2, 2);
    private readonly SemaphoreSlim _cacheMutationGate = new(1, 1);
    private readonly PlaybackResourceCoordinator _playbackResources;
    private readonly VideoHoverPreviewFrameService _videoFrameService = new(maxConcurrentFrames: 2);
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

            if (_inflight.TryGetValue(cacheKey, out request!)
                && IsRequestUnavailable(request))
            {
                // A just-cancelled prefetch can still be present in the map
                // while its producer unwinds. Do not attach a visible waiter
                // to that doomed task; replace it with a fresh request.
                _inflight.Remove(cacheKey);
                request = null!;
            }

            if (request == null)
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

    private static bool IsRequestUnavailable(InflightThumbnailRequest request)
    {
        if (request.Task.IsCompleted)
        {
            return true;
        }

        try
        {
            return request.Cancellation.IsCancellationRequested;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    public bool TryGetCachedThumbnail(MediaFile media, out ImageSource? source)
    {
        var cacheKey = BuildCacheKey(media);
        lock (_sync)
        {
            if (_memoryCache.TryGetValue(cacheKey, out source))
            {
                TouchMemoryCacheEntry(cacheKey);
                return true;
            }
        }

        source = null;
        return false;
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
                await TrimDiskCacheAsync();
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

        _ = Task.Run(async () =>
        {
            try
            {
                await TrimDiskCacheAsync();
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

    private async Task TrimDiskCacheAsync()
    {
        await _cacheMutationGate.WaitAsync();
        try
        {
            _diskTrimmer.Trim(DiskQuota, "*.thumb");
        }
        finally
        {
            _cacheMutationGate.Release();
        }
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

        var cacheDir = _cacheDir;
        var clearStartedAtUtc = DateTime.UtcNow;
        _ = Task.Run(() => DeleteAllFilesInDirectoryAsync(cacheDir, clearStartedAtUtc));
    }

    public void ClearCacheForMediaIds(IEnumerable<string> mediaIds)
    {
        var idSet = mediaIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (idSet.Count == 0)
        {
            return;
        }

        var requestsToCancel = new List<CancellationTokenSource>();
        var removedSources = new List<ImageSource>();
        lock (_sync)
        {
            foreach (var cacheKey in _memoryCache.Keys.ToList())
            {
                if (!CacheKeyMatchesMediaIds(cacheKey, idSet))
                {
                    continue;
                }

                if (_memoryCache.Remove(cacheKey, out var source))
                {
                    removedSources.Add(source);
                }

                if (_memoryCacheNodes.Remove(cacheKey, out var node))
                {
                    _memoryCacheLru.Remove(node);
                }
            }

            foreach (var inflightKey in _inflight.Keys.ToList())
            {
                if (CacheKeyMatchesMediaIds(inflightKey, idSet))
                {
                    if (_inflight.Remove(inflightKey, out var request))
                    {
                        requestsToCancel.Add(request.Cancellation);
                    }
                }
            }
        }

        foreach (var cancellation in requestsToCancel)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The request completed concurrently and disposed its CTS.
            }
        }
        foreach (var source in removedSources)
        {
            ReleaseImageSource(source);
        }

        var cacheDir = _cacheDir;
        var clearStartedAtUtc = DateTime.UtcNow;
        _ = Task.Run(() => DeleteThumbFilesForMediaIdsAsync(cacheDir, idSet, clearStartedAtUtc));
    }

    private static bool CacheKeyMatchesMediaIds(string cacheKey, HashSet<string> mediaIds)
    {
        var separator = cacheKey.IndexOf(':');
        var mediaId = separator > 0 ? cacheKey[..separator] : cacheKey;
        return mediaIds.Contains(mediaId);
    }

    private async Task DeleteAllFilesInDirectoryAsync(string directory, DateTime clearStartedAtUtc)
    {
        await _cacheMutationGate.WaitAsync();
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                TryDeleteCacheFileCreatedBefore(file, clearStartedAtUtc);
            }
        }
        finally
        {
            _cacheMutationGate.Release();
        }
    }

    private async Task DeleteThumbFilesForMediaIdsAsync(string directory, HashSet<string> mediaIds, DateTime clearStartedAtUtc)
    {
        await _cacheMutationGate.WaitAsync();
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.thumb", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var separator = name.IndexOf('_');
                var mediaId = separator > 0 ? name[..separator] : name;
                if (!mediaIds.Contains(mediaId))
                {
                    continue;
                }

                TryDeleteCacheFileCreatedBefore(file, clearStartedAtUtc);
            }
        }
        finally
        {
            _cacheMutationGate.Release();
        }
    }

    private static void TryDeleteCacheFileCreatedBefore(string file, DateTime cutoffUtc)
    {
        try
        {
            if (File.GetLastWriteTimeUtc(file) <= cutoffUtc)
            {
                File.Delete(file);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    // H2: consumes the page iterator and sweeps the cache directory on
    // background threads; the UI thread never materializes the full library
    // and never blocks on a tens-of-thousands-file directory enumeration.
    public async Task<int> ClearInvalidCacheAsync(IAsyncEnumerable<MediaFile> mediaItems, CancellationToken cancellationToken = default)
    {
        var clearStartedAtUtc = DateTime.UtcNow;
        var validFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var validCacheKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await Task.Run(async () =>
        {
            await foreach (var media in mediaItems.WithCancellation(cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(media.Id))
                {
                    continue;
                }

                validFileNames.Add(Path.GetFileName(GetCachePath(media)));
                validCacheKeys.Add(BuildCacheKey(media));
            }
        }, cancellationToken);

        return await ClearInvalidCacheFromValidSetsAsync(validFileNames, validCacheKeys, clearStartedAtUtc);
    }

    private async Task<int> ClearInvalidCacheFromValidSetsAsync(
        HashSet<string> validFileNames,
        HashSet<string> validCacheKeys,
        DateTime clearStartedAtUtc)
    {
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

        return await Task.Run(() => DeleteInvalidCacheFiles(validFileNames, clearStartedAtUtc));
    }

    private int DeleteInvalidCacheFiles(HashSet<string> validFileNames, DateTime clearStartedAtUtc)
    {
        _cacheMutationGate.Wait();
        try
        {
            if (!Directory.Exists(_cacheDir))
            {
                return 0;
            }

            var deleted = 0;
            foreach (var file in Directory.EnumerateFiles(_cacheDir, "*", SearchOption.TopDirectoryOnly))
            {
                if (validFileNames.Contains(Path.GetFileName(file)))
                {
                    continue;
                }

                // The media snapshot is inherently racy with imports. Keep
                // files written after the sweep began; they were not visible
                // to the snapshot and must not be treated as stale.
                try
                {
                    if (File.GetLastWriteTimeUtc(file) > clearStartedAtUtc)
                    {
                        continue;
                    }
                }
                catch
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
        finally
        {
            _cacheMutationGate.Release();
        }
    }

    private async Task<ImageSource?> LoadThumbnailCoreAsync(MediaFile media, string cacheKey, InflightThumbnailRequest request)
    {
        var cacheGeneration = request.CacheGeneration;
        var cancellationToken = request.Cancellation.Token;
        var gateWaitStartedAt = Environment.TickCount64;
        var gateWaitMs = 0L;
        var startedAt = 0L;
        var sourceStage = "none";
        var generateGateEntered = false;
        try
        {
            startedAt = Environment.TickCount64;
            var cachePath = GetCachePath(media);

            var source = await TryLoadFromDiskCacheAsync(cachePath, cancellationToken);
            if (source != null)
            {
                sourceStage = "disk-cache";
            }

            if (source == null && media.Type != MediaType.Image)
            {
                source = await TryGetWindowsThumbnailAsync(media, cachePath, cachedOnly: true, persist: false, cancellationToken);
                if (source != null)
                {
                    sourceStage = "windows-cached";
                }
            }

            if (source == null)
            {
                // Visible-library thumbnails keep their own generate gate so
                // playback does not serialize them behind scan/hover/timeline
                // on the single background-IO channel. Two concurrent decodes
                // is the contention cap while a video is playing.
                await _generateGate.WaitAsync(cancellationToken);
                generateGateEntered = true;
                gateWaitMs = Environment.TickCount64 - gateWaitStartedAt;

                if (media.Type == MediaType.Image)
                {
                    // StorageFile.GetThumbnailAsync / BitmapDecoder fail-fast
                    // in combase (E_UNEXPECTED) on some JPEG/JFIF files while
                    // the grouped GridView is realizing tiles.
                    source = await GenerateFallbackImageThumbnailAsync(media, cachePath, cancellationToken);
                    if (source != null)
                    {
                        sourceStage = "image-file";
                    }
                }
                else
                {
                    source = await TryGetWindowsThumbnailAsync(media, cachePath, cachedOnly: false, persist: true, cancellationToken);
                    if (source != null)
                    {
                        sourceStage = "windows-thumbnail";
                    }
                    else
                    {
                        source = await GenerateFallbackThumbnailAsync(media, cachePath, cancellationToken);
                        if (source != null)
                        {
                            sourceStage = "fallback";
                        }
                    }
                }
            }

            var acceptedSource = false;
            var requestInvalidated = false;
            lock (_sync)
            {
                if (source != null && !cancellationToken.IsCancellationRequested && cacheGeneration == _cacheGeneration)
                {
                    RememberInMemoryCache(cacheKey, source);
                    acceptedSource = true;
                }
                else
                {
                    requestInvalidated = cancellationToken.IsCancellationRequested || cacheGeneration != _cacheGeneration;
                    if (source != null)
                    {
                        ReleaseImageSource(source);
                    }
                }

                RemoveInflightIfCurrentLocked(cacheKey, request);
            }

            if (requestInvalidated)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            // A cache clear or request cancellation can invalidate the source
            // while the generation is running. Never return an ImageSource that
            // was just released from the cache in that case.
            return acceptedSource ? source : null;
        }
        catch (OperationCanceledException)
        {
            // Cancellation means the item left the realized/prefetch window or
            // the cache was cleared. Let the caller reset its in-flight marker;
            // it must not be recorded as a generation failure with backoff.
            throw;
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
            if (elapsedMs >= 750)
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
            else if (generateGateEntered && gateWaitMs >= 100)
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

            if (generateGateEntered)
            {
                _generateGate.Release();
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
                try
                {
                    request.Cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The producer completed its finalization concurrently.
                }
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

    private async Task<ImageSource?> TryLoadFromDiskCacheAsync(string cachePath, CancellationToken cancellationToken)
    {
        await _cacheReadGate.WaitAsync(cancellationToken);
        try
        {
            return await TryLoadBitmapAsync(cachePath, cancellationToken);
        }
        finally
        {
            _cacheReadGate.Release();
        }
    }

    private async Task<ImageSource?> TryGetWindowsThumbnailAsync(
        MediaFile media,
        string cachePath,
        bool cachedOnly,
        bool persist,
        CancellationToken cancellationToken)
    {
        var useCacheReadGate = cachedOnly;
        if (useCacheReadGate)
        {
            await _cacheReadGate.WaitAsync(cancellationToken);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = await StorageFile.GetFileFromPathAsync(PathHelpers.ToNativePath(media.Path));
            var thumbnailMode = media.Type == MediaType.Video ? ThumbnailMode.VideosView : ThumbnailMode.PicturesView;
            var options = ThumbnailOptions.UseCurrentScale;
            if (cachedOnly)
            {
                options |= ThumbnailOptions.ReturnOnlyIfCached;
            }

            using var thumbnail = await file.GetThumbnailAsync(thumbnailMode, ThumbnailSize, options);
            if (!IsUsableContentThumbnail(thumbnail))
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var source = await CreateBitmapFromStreamAsync(thumbnail, cancellationToken);
            if (source != null && persist)
            {
                try
                {
                    thumbnail.Seek(0);
                    await PersistStreamAsync(media, thumbnail, cachePath);
                }
                catch (Exception persistEx)
                {
                    AppTraceLogger.LogSampled(
                        "ThumbnailService",
                        "windows-thumbnail-persist-failed",
                        $"Windows thumbnail persist failed. MediaId='{media.Id}', File='{media.FileName}'. ErrorType={persistEx.GetType().Name}, Message='{persistEx.Message}'.",
                        TimeSpan.FromSeconds(5));
                }
            }

            if (source != null)
            {
                AppTraceLogger.LogSampled(
                    "ThumbnailService",
                    cachedOnly ? "windows-cached-thumbnail-used" : "windows-thumbnail-used",
                    $"Used Windows thumbnail. MediaId='{media.Id}', File='{media.FileName}', CachedOnly={cachedOnly}, Type={media.Type}.",
                    TimeSpan.FromSeconds(2));
            }

            return source;
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                throw;
            }

            AppTraceLogger.LogSampled(
                "ThumbnailService",
                cachedOnly ? "windows-cached-thumbnail-miss" : "generate-thumbnail-failed",
                $"TryGetWindowsThumbnailAsync failed. MediaId='{media.Id}', File='{media.FileName}', CachedOnly={cachedOnly}. ErrorType={ex.GetType().Name}, Message='{ex.Message}'.",
                TimeSpan.FromSeconds(5));
            return null;
        }
        finally
        {
            if (useCacheReadGate)
            {
                _cacheReadGate.Release();
            }
        }
    }

    private static bool IsUsableContentThumbnail(StorageItemThumbnail? thumbnail)
    {
        return thumbnail != null
            && thumbnail.Size > 0
            && thumbnail.Type != ThumbnailType.Icon;
    }

    private static async Task<ImageSource?> CreateBitmapFromStreamAsync(IRandomAccessStream stream, CancellationToken cancellationToken)
    {
        stream.Seek(0);
        var bitmap = new BitmapImage
        {
            DecodePixelWidth = ThumbnailSize
        };
        await bitmap.SetSourceAsync(stream);
        cancellationToken.ThrowIfCancellationRequested();
        return bitmap;
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

            var jpeg = await _videoFrameService.TranscodeStillImageAsync(nativePath, ThumbnailSize, cancellationToken);
            if (jpeg == null || jpeg.Length == 0)
            {
                return null;
            }

            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(jpeg.AsBuffer());
            stream.Seek(0);
            var bitmap = new BitmapImage
            {
                DecodePixelWidth = ThumbnailSize
            };
            await bitmap.SetSourceAsync(stream);
            AppTraceLogger.LogSampled(
                "ThumbnailService",
                "image-file-fallback-thumbnail-used",
                $"Generated image file fallback thumbnail. MediaId='{media.Id}', File='{media.FileName}'.",
                TimeSpan.FromSeconds(2));
            return bitmap;
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
            await _cacheMutationGate.WaitAsync(cancellationToken);
            try
            {
                DeleteStaleCacheFiles(media, cachePath);
                await File.WriteAllBytesAsync(cachePath, frameBytes, cancellationToken);
            }
            finally
            {
                _cacheMutationGate.Release();
            }
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
        await _cacheMutationGate.WaitAsync();
        try
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
        finally
        {
            _cacheMutationGate.Release();
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
            var bitmap = new BitmapImage
            {
                DecodePixelWidth = ThumbnailSize
            };
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
            await _cacheMutationGate.WaitAsync(cancellationToken);
            try
            {
                File.Delete(cachePath);
            }
            catch
            {
                // Ignore cleanup errors for broken cache entries.
            }
            finally
            {
                _cacheMutationGate.Release();
            }

            return null;
        }
    }

    // H2: stale-variant cleanup is batched. The previous implementation
    // enumerated the whole cache directory once per generated thumbnail
    // (O(ids x files) on large caches); requests now coalesce into one
    // background sweep that skips each media's current cache file.
    private static readonly TimeSpan StaleCleanupDebounce = TimeSpan.FromSeconds(2);
    private readonly object _staleCleanupSync = new();
    private readonly Dictionary<string, string> _pendingStaleCurrentPaths = new(StringComparer.OrdinalIgnoreCase);
    private int _staleCleanupScheduled;

    private void DeleteStaleCacheFiles(MediaFile media, string currentCachePath)
    {
        lock (_staleCleanupSync)
        {
            _pendingStaleCurrentPaths[media.Id] = currentCachePath;
        }

        ScheduleStaleCleanup();
    }

    private void ScheduleStaleCleanup()
    {
        if (Interlocked.Exchange(ref _staleCleanupScheduled, 1) == 1)
        {
            return;
        }

        var cacheDir = _cacheDir;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(StaleCleanupDebounce);
            }
            catch
            {
                Interlocked.Exchange(ref _staleCleanupScheduled, 0);
                return;
            }

            Dictionary<string, string> pending;
            lock (_staleCleanupSync)
            {
                pending = new Dictionary<string, string>(_pendingStaleCurrentPaths, StringComparer.OrdinalIgnoreCase);
                _pendingStaleCurrentPaths.Clear();
            }

            Interlocked.Exchange(ref _staleCleanupScheduled, 0);
            await DeleteStaleThumbFilesAsync(cacheDir, pending);

            // Requests that queued while this sweep ran still need a flush.
            lock (_staleCleanupSync)
            {
                if (_pendingStaleCurrentPaths.Count > 0)
                {
                    ScheduleStaleCleanup();
                }
            }
        });
    }

    private async Task DeleteStaleThumbFilesAsync(string directory, IReadOnlyDictionary<string, string> currentPathsByMediaId)
    {
        await _cacheMutationGate.WaitAsync();
        try
        {
            if (!Directory.Exists(directory) || currentPathsByMediaId.Count == 0)
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.thumb", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var separator = name.IndexOf('_');
                var mediaId = separator > 0 ? name[..separator] : name;
                if (!currentPathsByMediaId.TryGetValue(mediaId, out var currentPath)
                    || string.Equals(file, currentPath, StringComparison.OrdinalIgnoreCase))
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
        finally
        {
            _cacheMutationGate.Release();
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
