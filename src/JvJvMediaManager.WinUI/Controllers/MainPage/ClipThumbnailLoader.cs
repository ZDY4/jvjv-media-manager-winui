using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using JvJvMediaManager.Services;
using JvJvMediaManager.ViewModels;

namespace JvJvMediaManager.Controllers.MainPage;

internal sealed class ClipThumbnailLoader : IDisposable
{
    private readonly TimelineThumbnailStripService _thumbnailStripService;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly double _tileWidth;
    private readonly double _tileHeight;
    private readonly int _minFrameCount;
    private readonly int _maxFrameCount;
    private readonly List<ImageSource?> _frames = new();
    private CancellationTokenSource? _loadCancellation;

    public ClipThumbnailLoader(
        DispatcherQueue dispatcherQueue,
        double tileWidth,
        double tileHeight,
        int minFrameCount,
        int maxFrameCount,
        PlaybackResourceCoordinator? playbackResources = null)
    {
        _thumbnailStripService = new TimelineThumbnailStripService(playbackResources: playbackResources);
        _dispatcherQueue = dispatcherQueue;
        _tileWidth = tileWidth;
        _tileHeight = tileHeight;
        _minFrameCount = minFrameCount;
        _maxFrameCount = maxFrameCount;
    }

    public IReadOnlyList<ImageSource?> Frames => _frames;

    public string? RequestKey { get; private set; }

    public string? RenderKey { get; private set; }

    public void MarkRendered(string renderKey)
    {
        RenderKey = renderKey;
    }

    public void InvalidateRender()
    {
        RenderKey = null;
    }

    public void Ensure(
        MediaItemViewModel? media,
        TimeSpan duration,
        double viewportWidth,
        double zoomFactor,
        Action<double> render,
        Func<double> resolveContentWidth)
    {
        if (media?.Type != Models.MediaType.Video || duration <= TimeSpan.Zero)
        {
            Clear();
            return;
        }

        if (viewportWidth <= 0)
        {
            return;
        }

        var contentWidth = Math.Max(viewportWidth, viewportWidth * zoomFactor);
        var frameCount = Math.Clamp((int)Math.Ceiling(contentWidth / _tileWidth), _minFrameCount, _maxFrameCount);
        var requestKey = $"{media.Id}|{media.Media.ModifiedAt}|{frameCount}|{(int)Math.Round(zoomFactor * 100)}|{(int)Math.Round(viewportWidth)}";
        var fallbackSource = media.Thumbnail;
        if (string.Equals(RequestKey, requestKey, StringComparison.Ordinal))
        {
            return;
        }

        RequestKey = requestKey;
        RenderKey = null;
        _frames.Clear();
        if (fallbackSource != null)
        {
            for (var i = 0; i < frameCount; i++)
            {
                _frames.Add(fallbackSource);
            }

            render(contentWidth);
        }

        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        var cancellationToken = _loadCancellation.Token;

        _ = LoadAsync(new TimelineThumbnailStripRequest
        {
            MediaId = media.Id,
            MediaPath = media.FileSystemPath,
            CacheToken = media.Media.ModifiedAt.ToString(),
            Duration = duration,
            FrameCount = frameCount,
            FrameWidth = (int)Math.Ceiling(_tileWidth),
            FrameHeight = (int)Math.Ceiling(_tileHeight)
        }, requestKey, fallbackSource, render, resolveContentWidth, cancellationToken);
    }

    public void Clear()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        RequestKey = null;
        RenderKey = null;
        _frames.Clear();
    }

    public void Dispose()
    {
        Clear();
    }

    private async Task LoadAsync(
        TimelineThumbnailStripRequest request,
        string requestKey,
        ImageSource? fallbackSource,
        Action<double> render,
        Func<double> resolveContentWidth,
        CancellationToken cancellationToken)
    {
        try
        {
            var frames = await _thumbnailStripService.GetStripAsync(
                request,
                fallbackSource,
                (index, source) => QueueFrameUpdate(requestKey, index, source, render, resolveContentWidth, cancellationToken),
                cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _dispatcherQueue.TryEnqueue(() =>
            {
                if (cancellationToken.IsCancellationRequested
                    || !string.Equals(RequestKey, requestKey, StringComparison.Ordinal))
                {
                    return;
                }

                RenderKey = null;
                _frames.Clear();
                _frames.AddRange(frames);
                render(resolveContentWidth());
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (!string.Equals(RequestKey, requestKey, StringComparison.Ordinal))
                {
                    return;
                }

                RenderKey = null;
                _frames.Clear();
                if (fallbackSource != null)
                {
                    for (var i = 0; i < request.FrameCount; i++)
                    {
                        _frames.Add(fallbackSource);
                    }
                }

                render(resolveContentWidth());
            });
        }
    }

    private void QueueFrameUpdate(
        string requestKey,
        int index,
        ImageSource? source,
        Action<double> render,
        Func<double> resolveContentWidth,
        CancellationToken cancellationToken)
    {
        if (source == null || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        _dispatcherQueue.TryEnqueue(() =>
        {
            if (cancellationToken.IsCancellationRequested
                || !string.Equals(RequestKey, requestKey, StringComparison.Ordinal)
                || index < 0
                || index >= _frames.Count)
            {
                return;
            }

            _frames[index] = source;
            RenderKey = null;
            render(resolveContentWidth());
        });
    }
}
