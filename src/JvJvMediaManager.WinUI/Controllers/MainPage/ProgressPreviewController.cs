using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Storage.Streams;
using JvJvMediaManager.Models;
using JvJvMediaManager.Services;
using JvJvMediaManager.Utilities;
using JvJvMediaManager.ViewModels;
using JvJvMediaManager.ViewModels.MainPage;
using JvJvMediaManager.Views.MainPageParts;

namespace JvJvMediaManager.Controllers.MainPage;

internal sealed class ProgressPreviewController : IDisposable
{
    private const int FrameWidth = 180;
    private const int FrameHeight = 102;
    private const int MaxCachedFrames = 60;
    private const int MaxConsecutiveFailures = 3;
    private const double TimeBucketSeconds = 0.5;
    private const double BubbleWidth = 188;
    private const double BubbleHeight = 132;
    private const long HeavyMediaSizeThresholdBytes = 4L * 1024 * 1024 * 1024;

    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan HeavyMediaDurationThreshold = TimeSpan.FromMinutes(60);

    private readonly LibraryShellViewModel _libraryViewModel;
    private readonly TransportControlBarView _transportBarView;
    private readonly VideoHoverPreviewFrameService _hoverPreviewFrameService = new();
    private readonly ProgressPreviewFrameCache _frameCache = new(MaxCachedFrames);
    private readonly Microsoft.UI.Xaml.DispatcherTimer _debounceTimer = new();
    private readonly Func<bool> _isDisposed;
    private readonly Func<bool> _isTransportSuppressed;
    private readonly Func<TimeSpan> _getDuration;
    private readonly Func<TimeSpan, TimeSpan> _mapDisplayToPlaybackPosition;
    private readonly Func<TimeSpan, string> _formatTime;
    private readonly Action<Func<Task>, string> _enqueueSafeAsync;
    private readonly PlaybackResourceCoordinator? _playbackResources;

    private CancellationTokenSource? _frameCts;
    private ProgressPreviewRequest? _pendingRequest;
    private ImageSource? _lastSource;
    private string? _cacheMediaId;
    private string? _targetCacheKey;
    private string? _inFlightCacheKey;
    private int _requestVersion;
    private int _consecutiveFailures;
    private long _failureCooldownUntilTicks;
    private string? _failureMediaId;
    private bool _disposed;

    public ProgressPreviewController(
        LibraryShellViewModel libraryViewModel,
        TransportControlBarView transportBarView,
        Func<bool> isDisposed,
        Func<bool> isTransportSuppressed,
        Func<TimeSpan> getDuration,
        Func<TimeSpan, TimeSpan> mapDisplayToPlaybackPosition,
        Func<TimeSpan, string> formatTime,
        Action<Func<Task>, string> enqueueSafeAsync,
        PlaybackResourceCoordinator? playbackResources = null)
    {
        _libraryViewModel = libraryViewModel;
        _transportBarView = transportBarView;
        _isDisposed = isDisposed;
        _isTransportSuppressed = isTransportSuppressed;
        _getDuration = getDuration;
        _mapDisplayToPlaybackPosition = mapDisplayToPlaybackPosition;
        _formatTime = formatTime;
        _enqueueSafeAsync = enqueueSafeAsync;
        _playbackResources = playbackResources;

        _debounceTimer.Interval = DebounceDelay;
        _debounceTimer.Tick += DebounceTimer_Tick;
    }

    private sealed record ProgressPreviewRequest(
        int Version,
        string CacheKey,
        string MediaId,
        string MediaPath,
        long ModifiedAt,
        TimeSpan PlaybackTime,
        TimeSpan BucketedPlaybackTime);

    public void UpdateFromPointer(PointerRoutedEventArgs e)
    {
        if (!TryResolveTarget(e, out var playbackTime, out var sliderX))
        {
            Hide();
            CancelFrameRequest();
            return;
        }

        var media = _libraryViewModel.SelectedMedia;
        if (media == null)
        {
            Hide();
            CancelFrameRequest();
            return;
        }

        // Never fall back to the media cover: the bubble shows the last
        // generated frame for this media, or stays empty until a frame arrives.
        var fallbackSource = _lastSource;
        Show(playbackTime, sliderX, fallbackSource);

        if (ShouldSkipFrameGeneration(media, _getDuration()))
        {
            CancelFrameRequest();
            AppTraceLogger.LogSampled(
                "VideoHoverPreview",
                "preview-frame-skipped-heavy-media",
                $"Progress preview frame generation skipped for heavy media. MediaId='{media.Id}', Size={media.Size}, DurationSeconds={media.Media.Duration?.ToString("F3") ?? "<null>"}.",
                TimeSpan.FromSeconds(2));
            return;
        }

        var bucketedTime = GetBucket(playbackTime, _getDuration());
        var cacheKey = BuildCacheKey(media, bucketedTime);
        _targetCacheKey = cacheKey;
        if (_frameCache.TryGet(cacheKey, out var cachedSource))
        {
            _lastSource = cachedSource;
            _transportBarView.ProgressPreviewImage.Source = cachedSource;
            _pendingRequest = null;
            CancelFrameRequest();
            AppTraceLogger.LogSampled(
                "VideoHoverPreview",
                "preview-cache-hit",
                $"Progress preview cache hit. MediaId='{media.Id}', Bucket={bucketedTime.TotalSeconds:F3}, Playback={playbackTime.TotalSeconds:F3}, CacheCount={_frameCache.Count}.",
                TimeSpan.FromMilliseconds(500));
            return;
        }

        if (IsInFailureCooldown(media.Id, out var remainingCooldown))
        {
            AppTraceLogger.LogSampled(
                "VideoHoverPreview",
                "preview-generation-cooldown",
                $"Progress preview generation skipped during failure cooldown. MediaId='{media.Id}', RemainingMs={remainingCooldown.TotalMilliseconds:0}, ConsecutiveFailures={_consecutiveFailures}, FfmpegAvailable={_hoverPreviewFrameService.IsAvailable}.",
                TimeSpan.FromSeconds(1));
            return;
        }

        if (IsFrameAlreadyPending(cacheKey))
        {
            AppTraceLogger.LogSampled(
                "VideoHoverPreview",
                "preview-request-already-pending",
                $"Progress preview frame already pending. MediaId='{media.Id}', Bucket={bucketedTime.TotalSeconds:F3}, Playback={playbackTime.TotalSeconds:F3}, InFlight='{_inFlightCacheKey ?? "<null>"}'.",
                TimeSpan.FromMilliseconds(500));
            return;
        }

        // Latest-wins without cancelling in-flight work: moving the pointer
        // only records the newest target. The debounce starts a generation
        // when none is in flight, and a completed generation chases the
        // pending target (see DebounceTimer_Tick / LoadFrameAsync finally).
        _pendingRequest = new ProgressPreviewRequest(
            Volatile.Read(ref _requestVersion),
            cacheKey,
            media.Id,
            media.FileSystemPath,
            media.Media.ModifiedAt,
            playbackTime,
            bucketedTime);
        _debounceTimer.Stop();
        _debounceTimer.Start();
        AppTraceLogger.LogSampled(
            "VideoHoverPreview",
            "preview-request-scheduled",
            $"Progress preview request scheduled. MediaId='{media.Id}', Bucket={bucketedTime.TotalSeconds:F3}, Playback={playbackTime.TotalSeconds:F3}.",
            TimeSpan.FromMilliseconds(500));
    }

    public void Hide()
    {
        _debounceTimer.Stop();
        _pendingRequest = null;
        if (_transportBarView.ProgressPreviewPopup.IsOpen)
        {
            _transportBarView.ProgressPreviewPopup.IsOpen = false;
        }
    }

    public void CancelFrameRequest()
    {
        _debounceTimer.Stop();
        _pendingRequest = null;
        _frameCts?.Cancel();
        _frameCts?.Dispose();
        _frameCts = null;
        _inFlightCacheKey = null;
    }

    public void ResetForMedia(string mediaId)
    {
        Hide();
        CancelFrameRequest();
        ClearCache(mediaId);
    }

    public void Clear()
    {
        Hide();
        ClearCache(null);
        CancelFrameRequest();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _debounceTimer.Tick -= DebounceTimer_Tick;
        Clear();
    }

    private bool TryResolveTarget(PointerRoutedEventArgs e, out TimeSpan playbackTime, out double sliderX)
    {
        playbackTime = TimeSpan.Zero;
        sliderX = 0;

        if (_isDisposed()
            || _isTransportSuppressed()
            || _libraryViewModel.SelectedMedia?.Type != MediaType.Video
            || _transportBarView.ProgressSlider.ActualWidth <= 0)
        {
            return false;
        }

        var duration = _getDuration();
        if (duration <= TimeSpan.Zero)
        {
            return false;
        }

        var point = e.GetCurrentPoint(_transportBarView.ProgressSlider).Position;
        sliderX = Math.Clamp(point.X, 0, _transportBarView.ProgressSlider.ActualWidth);
        var progressMaximum = Math.Max(0, _transportBarView.ProgressSlider.Maximum);
        if (progressMaximum <= 0)
        {
            return false;
        }

        var ratio = sliderX / _transportBarView.ProgressSlider.ActualWidth;
        var displayTime = TimeSpan.FromSeconds(progressMaximum * ratio);
        playbackTime = ClampToDuration(_mapDisplayToPlaybackPosition(displayTime), duration);
        return true;
    }

    private void Show(TimeSpan playbackTime, double sliderX, ImageSource? previewSource)
    {
        _transportBarView.ProgressPreviewTimeText.Text = _formatTime(playbackTime);
        // Assign unconditionally: a null clears a stale source so the bubble
        // never shows the media cover or an outdated frame.
        _transportBarView.ProgressPreviewImage.Source = previewSource;
        UpdatePopupPosition(sliderX);
        if (!_transportBarView.ProgressPreviewPopup.IsOpen)
        {
            _transportBarView.ProgressPreviewPopup.IsOpen = true;
        }
    }

    private void UpdatePopupPosition(double sliderX)
    {
        try
        {
            var sliderOrigin = _transportBarView.ProgressSlider
                .TransformToVisual(_transportBarView)
                .TransformPoint(new Point(0, 0));
            var bubbleWidth = _transportBarView.ProgressPreviewBubble.ActualWidth > 0
                ? _transportBarView.ProgressPreviewBubble.ActualWidth
                : BubbleWidth;
            var bubbleHeight = _transportBarView.ProgressPreviewBubble.ActualHeight > 0
                ? _transportBarView.ProgressPreviewBubble.ActualHeight
                : BubbleHeight;
            var availableWidth = _transportBarView.ActualWidth > 0
                ? _transportBarView.ActualWidth
                : sliderOrigin.X + _transportBarView.ProgressSlider.ActualWidth;

            var left = sliderOrigin.X + sliderX - (bubbleWidth / 2);
            var maxLeft = Math.Max(0, availableWidth - bubbleWidth);
            _transportBarView.ProgressPreviewPopup.HorizontalOffset = Math.Clamp(left, 0, maxLeft);
            _transportBarView.ProgressPreviewPopup.VerticalOffset = Math.Max(0, sliderOrigin.Y - bubbleHeight - 10);
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("VideoPlayback", "UpdateProgressPreviewPopupPosition failed.", ex);
        }
    }

    private void DebounceTimer_Tick(object? sender, object e)
    {
        _debounceTimer.Stop();
        var request = _pendingRequest;
        if (request == null || !IsRequestCurrent(request))
        {
            return;
        }

        // Single-flight: never cancel an in-flight generation from a pointer
        // move. When it completes, LoadFrameAsync chases the newest pending
        // target, so frames keep appearing while scanning the timeline.
        if (_inFlightCacheKey != null)
        {
            return;
        }

        _ = LoadFrameAsync(request);
    }

    private async Task LoadFrameAsync(ProgressPreviewRequest request)
    {
        if (!IsRequestCurrent(request))
        {
            return;
        }

        _frameCts?.Cancel();
        _frameCts?.Dispose();
        var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        _frameCts = cancellation;
        _inFlightCacheKey = request.CacheKey;
        _pendingRequest = null;

        try
        {
            var frameStartTimestamp = Stopwatch.GetTimestamp();
            AppTraceLogger.LogSampled(
                "VideoHoverPreview",
                "preview-frame-generate-start",
                $"Progress preview frame generation started. MediaId='{request.MediaId}', Bucket={request.BucketedPlaybackTime.TotalSeconds:F3}, Playback={request.PlaybackTime.TotalSeconds:F3}.",
                TimeSpan.FromMilliseconds(500));
            IDisposable? backgroundIo = null;
            if (_playbackResources != null)
            {
                backgroundIo = await _playbackResources.EnterBackgroundIoAsync("hover-preview", cancellationToken);
            }

            byte[]? frameBytes;
            try
            {
                frameBytes = await _hoverPreviewFrameService.GenerateFrameAsync(
                    request.MediaPath,
                    request.BucketedPlaybackTime,
                    FrameWidth,
                    FrameHeight,
                    cancellationToken);
            }
            finally
            {
                backgroundIo?.Dispose();
            }
            var generationElapsed = Stopwatch.GetElapsedTime(frameStartTimestamp);
            if (frameBytes == null || cancellationToken.IsCancellationRequested)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    RegisterFailure(request.MediaId, "frame-empty");
                }

                AppTraceLogger.LogSampled(
                    "VideoHoverPreview",
                    "preview-frame-empty",
                    $"Progress preview frame generation returned no frame. MediaId='{request.MediaId}', Bucket={request.BucketedPlaybackTime.TotalSeconds:F3}, Canceled={cancellationToken.IsCancellationRequested}, FfmpegAvailable={_hoverPreviewFrameService.IsAvailable}, ElapsedMs={generationElapsed.TotalMilliseconds:0}.",
                    TimeSpan.FromSeconds(2));
                return;
            }

            _enqueueSafeAsync(async () =>
            {
                if (cancellationToken.IsCancellationRequested || !IsRequestCurrent(request))
                {
                    AppTraceLogger.LogSampled(
                        "VideoHoverPreview",
                        "preview-frame-stale",
                        $"Progress preview frame discarded as stale. MediaId='{request.MediaId}', Bucket={request.BucketedPlaybackTime.TotalSeconds:F3}, TargetMatches={string.Equals(_targetCacheKey, request.CacheKey, StringComparison.Ordinal)}, Canceled={cancellationToken.IsCancellationRequested}.",
                        TimeSpan.FromMilliseconds(500));
                    return;
                }

                var source = await CreateImageSourceAsync(frameBytes);
                if (source == null)
                {
                    RegisterFailure(request.MediaId, "decode-empty");
                    return;
                }

                _frameCache.Add(request.CacheKey, source);
                ResetFailures(request.MediaId);
                _lastSource = source;
                _transportBarView.ProgressPreviewImage.Source = source;
                AppTraceLogger.LogSampled(
                    "VideoHoverPreview",
                    "preview-frame-applied",
                    $"Progress preview frame applied. MediaId='{request.MediaId}', Bucket={request.BucketedPlaybackTime.TotalSeconds:F3}, Bytes={frameBytes.Length}, CacheCount={_frameCache.Count}, GenerateElapsedMs={generationElapsed.TotalMilliseconds:0}.",
                    TimeSpan.FromMilliseconds(500));
            },
            $"Progress preview frame UI apply failed. MediaId='{request.MediaId}', Bucket={request.BucketedPlaybackTime.TotalSeconds:F3}.");
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_frameCts, cancellation))
            {
                _frameCts = null;
            }

            if (string.Equals(_inFlightCacheKey, request.CacheKey, StringComparison.Ordinal))
            {
                _inFlightCacheKey = null;
            }

            cancellation.Dispose();

            // Single-flight chase: if the pointer moved while this frame was
            // generating, immediately start the latest pending target instead
            // of waiting for another hover event.
            var next = _pendingRequest;
            _pendingRequest = null;
            if (next != null
                && !string.Equals(next.CacheKey, request.CacheKey, StringComparison.Ordinal))
            {
                _ = LoadFrameAsync(next);
            }
        }
    }

    private bool IsFrameAlreadyPending(string cacheKey)
    {
        return string.Equals(_pendingRequest?.CacheKey, cacheKey, StringComparison.Ordinal)
            || string.Equals(_inFlightCacheKey, cacheKey, StringComparison.Ordinal);
    }

    private static bool ShouldSkipFrameGeneration(MediaItemViewModel media, TimeSpan currentDuration)
    {
        if (media.Size >= HeavyMediaSizeThresholdBytes)
        {
            return true;
        }

        var indexedDuration = media.Media.Duration is > 0
            ? TimeSpan.FromSeconds(media.Media.Duration.Value)
            : TimeSpan.Zero;
        var duration = currentDuration > TimeSpan.Zero ? currentDuration : indexedDuration;
        return duration >= HeavyMediaDurationThreshold;
    }

    private bool IsRequestCurrent(ProgressPreviewRequest request)
    {
        return !_isDisposed()
            && request.Version == Volatile.Read(ref _requestVersion)
            && string.Equals(_targetCacheKey, request.CacheKey, StringComparison.Ordinal)
            && _libraryViewModel.SelectedMedia is { } selected
            && string.Equals(selected.Id, request.MediaId, StringComparison.Ordinal)
            && selected.Media.ModifiedAt == request.ModifiedAt;
    }

    private bool IsInFailureCooldown(string mediaId, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        if (!string.Equals(_failureMediaId, mediaId, StringComparison.Ordinal))
        {
            return false;
        }

        var nowTicks = DateTime.UtcNow.Ticks;
        var cooldownTicks = Volatile.Read(ref _failureCooldownUntilTicks);
        if (cooldownTicks <= nowTicks)
        {
            return false;
        }

        remaining = TimeSpan.FromTicks(cooldownTicks - nowTicks);
        return true;
    }

    private void RegisterFailure(string mediaId, string reason)
    {
        if (!string.Equals(_failureMediaId, mediaId, StringComparison.Ordinal))
        {
            _failureMediaId = mediaId;
            _consecutiveFailures = 0;
        }

        _consecutiveFailures++;
        if (_consecutiveFailures < MaxConsecutiveFailures)
        {
            AppTraceLogger.LogSampled(
                "VideoHoverPreview",
                "preview-frame-failure",
                $"Progress preview frame failure recorded. MediaId='{mediaId}', Reason={reason}, ConsecutiveFailures={_consecutiveFailures}.",
                TimeSpan.FromSeconds(1));
            return;
        }

        var cooldownUntil = DateTime.UtcNow.Add(FailureCooldown);
        _failureCooldownUntilTicks = cooldownUntil.Ticks;
        AppTraceLogger.Log(
            "VideoHoverPreview",
            $"Progress preview generation entered failure cooldown. MediaId='{mediaId}', Reason={reason}, ConsecutiveFailures={_consecutiveFailures}, CooldownMs={FailureCooldown.TotalMilliseconds:0}, FfmpegAvailable={_hoverPreviewFrameService.IsAvailable}.");
    }

    private void ResetFailures(string mediaId)
    {
        if (!string.Equals(_failureMediaId, mediaId, StringComparison.Ordinal)
            || _consecutiveFailures == 0)
        {
            return;
        }

        AppTraceLogger.Log(
            "VideoHoverPreview",
            $"Progress preview failures reset after successful frame. MediaId='{mediaId}', PreviousFailures={_consecutiveFailures}.");
        _consecutiveFailures = 0;
        _failureCooldownUntilTicks = 0;
    }

    private void ClearCache(string? nextMediaId, bool invalidateRequests = true)
    {
        _frameCache.Clear();
        _lastSource = null;
        _cacheMediaId = nextMediaId;
        _targetCacheKey = null;
        _inFlightCacheKey = null;
        if (!string.Equals(_failureMediaId, nextMediaId, StringComparison.Ordinal))
        {
            _failureMediaId = nextMediaId;
            _consecutiveFailures = 0;
            _failureCooldownUntilTicks = 0;
        }

        if (invalidateRequests)
        {
            Interlocked.Increment(ref _requestVersion);
        }
    }

    private string BuildCacheKey(MediaItemViewModel media, TimeSpan bucketedTime)
    {
        if (!string.Equals(_cacheMediaId, media.Id, StringComparison.Ordinal))
        {
            ClearCache(media.Id, invalidateRequests: false);
        }

        var bucketMilliseconds = (long)Math.Round(bucketedTime.TotalMilliseconds);
        return $"{media.Id}|{media.Media.ModifiedAt}|{bucketMilliseconds}|{FrameWidth}x{FrameHeight}";
    }

    private static TimeSpan GetBucket(TimeSpan playbackTime, TimeSpan duration)
    {
        var bucketSeconds = Math.Round(playbackTime.TotalSeconds / TimeBucketSeconds) * TimeBucketSeconds;
        return ClampToDuration(TimeSpan.FromSeconds(bucketSeconds), duration);
    }

    private static TimeSpan ClampToDuration(TimeSpan value, TimeSpan duration)
    {
        if (value < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return duration > TimeSpan.Zero && value > duration
            ? duration
            : value;
    }

    private static async Task<ImageSource?> CreateImageSourceAsync(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return null;
        }

        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }
}
