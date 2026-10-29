using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using JvJvMediaManager.Models;
using JvJvMediaManager.Services;
using JvJvMediaManager.Utilities;
using JvJvMediaManager.ViewModels;
using JvJvMediaManager.ViewModels.MainPage;
using JvJvMediaManager.Views.MainPageParts;

namespace JvJvMediaManager.Controllers.MainPage;

public sealed class ImagePreviewController
{
    private const double MinImageZoomFactor = 1;
    private const double MaxImageZoomFactor = 6;

    private readonly LibraryShellViewModel _libraryViewModel;
    private readonly ImagePreviewViewModel _viewModel;
    private readonly ImageViewportView _imageViewportView;
    private readonly UIElement _playerOverlay;
    private readonly Action _refreshNavigationHotspots;
    private readonly VideoHoverPreviewFrameService _imageTranscoder = new(maxConcurrentFrames: 1);
    private readonly Dictionary<string, LinkedListNode<PreviewCacheEntry>> _previewCacheNodes = new(StringComparer.Ordinal);
    private readonly LinkedList<PreviewCacheEntry> _previewCacheLru = new();
    private long _previewCacheBytes;
    private CancellationTokenSource _previewCancellation = new();
    private readonly PointerEventHandler _pointerPressedHandler;
    private readonly PointerEventHandler _pointerMovedHandler;
    private readonly PointerEventHandler _pointerReleasedHandler;
    private readonly PointerEventHandler _pointerCaptureLostHandler;
    private readonly PointerEventHandler _pointerWheelChangedHandler;
    private readonly DoubleTappedEventHandler _doubleTappedHandler;

    private int _imageLoadVersion;
    private bool _isImageDragging;
    private string? _pendingImageFitMediaId;
    private Windows.Foundation.Point _imageDragStartPoint;
    private double _imageDragStartHorizontalOffset;
    private double _imageDragStartVerticalOffset;
    private double _imageSourceWidth;
    private double _imageSourceHeight;
    private double _imageZoomFactor = MinImageZoomFactor;
    private double _imageTranslationX;
    private double _imageTranslationY;
    private bool _isDisposed;
    private UIElement? _imageDragCaptureOwner;

    public ImagePreviewController(
        LibraryShellViewModel libraryViewModel,
        ImagePreviewViewModel viewModel,
        ImageViewportView imageViewportView,
        UIElement playerOverlay,
        Action refreshNavigationHotspots)
    {
        _libraryViewModel = libraryViewModel;
        _viewModel = viewModel;
        _imageViewportView = imageViewportView;
        _playerOverlay = playerOverlay;
        _refreshNavigationHotspots = refreshNavigationHotspots;
        _pointerPressedHandler = ImageScrollViewer_PointerPressed;
        _pointerMovedHandler = ImageScrollViewer_PointerMoved;
        _pointerReleasedHandler = ImageScrollViewer_PointerReleased;
        _pointerCaptureLostHandler = ImageScrollViewer_PointerCaptureLost;
        _pointerWheelChangedHandler = ImageScrollViewer_PointerWheelChanged;
        _doubleTappedHandler = ImageScrollViewer_DoubleTapped;

        _imageViewportView.ImageScrollViewer.SizeChanged += ImageScrollViewer_SizeChanged;
        _imageViewportView.ImageScrollViewer.AddHandler(UIElement.PointerPressedEvent, _pointerPressedHandler, true);
        _imageViewportView.ImageScrollViewer.AddHandler(UIElement.PointerMovedEvent, _pointerMovedHandler, true);
        _imageViewportView.ImageScrollViewer.AddHandler(UIElement.PointerReleasedEvent, _pointerReleasedHandler, true);
        _imageViewportView.ImageScrollViewer.AddHandler(UIElement.PointerCaptureLostEvent, _pointerCaptureLostHandler, true);
        _imageViewportView.ImageScrollViewer.AddHandler(UIElement.PointerWheelChangedEvent, _pointerWheelChangedHandler, true);
        _imageViewportView.ImageScrollViewer.AddHandler(UIElement.DoubleTappedEvent, _doubleTappedHandler, true);
        _imageViewportView.PreviewImageElement.ImageOpened += PreviewImageElement_ImageOpened;
        _viewModel.ZoomText = "100%";
    }

    public double ZoomFactor => _imageZoomFactor;

    public bool CanPanZoomedImage()
    {
        return _libraryViewModel.SelectedMedia?.Type == MediaType.Image
            && _imageViewportView.ImageScrollViewer.Visibility == Visibility.Visible
            && _imageZoomFactor > 1.01;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        CancelPreviewLoad();
        EndImageDrag();
        Interlocked.Increment(ref _imageLoadVersion);
        _imageViewportView.ImageScrollViewer.SizeChanged -= ImageScrollViewer_SizeChanged;
        _imageViewportView.ImageScrollViewer.RemoveHandler(UIElement.PointerPressedEvent, _pointerPressedHandler);
        _imageViewportView.ImageScrollViewer.RemoveHandler(UIElement.PointerMovedEvent, _pointerMovedHandler);
        _imageViewportView.ImageScrollViewer.RemoveHandler(UIElement.PointerReleasedEvent, _pointerReleasedHandler);
        _imageViewportView.ImageScrollViewer.RemoveHandler(UIElement.PointerCaptureLostEvent, _pointerCaptureLostHandler);
        _imageViewportView.ImageScrollViewer.RemoveHandler(UIElement.PointerWheelChangedEvent, _pointerWheelChangedHandler);
        _imageViewportView.ImageScrollViewer.RemoveHandler(UIElement.DoubleTappedEvent, _doubleTappedHandler);
        _imageViewportView.PreviewImageElement.ImageOpened -= PreviewImageElement_ImageOpened;
        _imageViewportView.PreviewImageElement.Source = null;
    }

    public void ShowImage(MediaItemViewModel media)
    {
        if (_isDisposed)
        {
            return;
        }

        _imageViewportView.ImageScrollViewer.Visibility = Visibility.Visible;
        EndImageDrag();
        Interlocked.Increment(ref _imageLoadVersion);
        _pendingImageFitMediaId = media.Id;

        // Keep the current picture up until the next decode finishes. A blank
        // frame is what made each switch feel like a stall. If nothing is
        // showing yet, the grid thumbnail is an immediate stand-in.
        var preview = _imageViewportView.PreviewImageElement;
        if (preview.Source == null && media.Thumbnail != null)
        {
            preview.Source = media.Thumbnail;
            SetPreviewImageVisible(true);
        }
        else if (preview.Source == null)
        {
            SetPreviewImageVisible(false);
        }

        var loadVersion = Volatile.Read(ref _imageLoadVersion);
        var cancellationToken = ResetPreviewCancellation();
        _ = LoadImagePreviewAsync(media, loadVersion, cancellationToken);
    }

    public void Clear()
    {
        if (_isDisposed)
        {
            return;
        }

        EndImageDrag();
        CancelPreviewLoad();
        Interlocked.Increment(ref _imageLoadVersion);
        _pendingImageFitMediaId = null;
        _imageSourceWidth = 0;
        _imageSourceHeight = 0;
        _imageViewportView.ImageScrollViewer.Visibility = Visibility.Collapsed;
        _imageViewportView.PreviewImageElement.Width = double.NaN;
        _imageViewportView.PreviewImageElement.Height = double.NaN;
        _imageViewportView.PreviewImageElement.Source = null;
        SetPreviewImageVisible(true);
        ResetImageViewTransform();
        UpdateImageZoomUi(MinImageZoomFactor);
    }

    public void ZoomBy(double delta)
    {
        if (!TryGetImageViewportSize(out var viewportWidth, out var viewportHeight))
        {
            return;
        }

        ZoomImage(delta, new Windows.Foundation.Point(viewportWidth / 2, viewportHeight / 2));
    }

    public void ResetZoom()
    {
        _pendingImageFitMediaId = null;
        if (!FitImageToViewport())
        {
            ResetImageViewTransform();
            UpdateImageZoomUi(MinImageZoomFactor);
        }
    }

    public void BeginExternalDrag(UIElement captureOwner, Pointer pointer, Windows.Foundation.Point startPoint, bool capturePointer = true)
    {
        BeginImageDrag(captureOwner, pointer, startPoint, capturePointer);
    }

    public void UpdateExternalDrag(Windows.Foundation.Point position)
    {
        UpdateImageDrag(position);
    }

    public void EndExternalDrag()
    {
        EndImageDrag();
    }

    private async Task LoadImagePreviewAsync(
        MediaItemViewModel media,
        int loadVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!IsCurrentImageLoad(media, loadVersion))
            {
                return;
            }

            var path = media.FileSystemPath;
            var cacheKey = $"{media.Id}:{media.Size}:{MaxPreviewDecodeLongEdge}";
            var fromCache = TryTakePreviewCache(cacheKey, out var jpeg);
            AppTraceLogger.Log(
                "ImagePreview",
                $"LoadImagePreviewAsync start. MediaId='{media.Id}', File='{media.FileName}', CacheHit={fromCache}.");

            if (!fromCache)
            {
                // Decode with ffmpeg (libjpeg/libavcodec), not WIC. Some JFIF/JPEG
                // files fail-fast inside combase when BitmapImage/BitmapDecoder
                // touches them; that cannot be caught in managed code.
                jpeg = await _imageTranscoder.TranscodeStillImageAsync(
                    path,
                    MaxPreviewDecodeLongEdge,
                    cancellationToken);
                if (jpeg == null || jpeg.Length == 0)
                {
                    throw new InvalidOperationException("ffmpeg produced no preview image.");
                }

                if (!IsCurrentImageLoad(media, loadVersion))
                {
                    return;
                }

                RememberPreviewCache(cacheKey, jpeg);
            }

            var bitmap = await CreatePreviewBitmapAsync(jpeg!, cancellationToken);
            if (!IsCurrentImageLoad(media, loadVersion))
            {
                return;
            }

            _imageViewportView.PreviewImageElement.Source = bitmap;
            UpdatePreviewImageSourceSize(
                bitmap.PixelWidth > 0 ? bitmap.PixelWidth : null,
                bitmap.PixelHeight > 0 ? bitmap.PixelHeight : null);
            _pendingImageFitMediaId = media.Id;
            TryApplyPendingImageFit();
            AppTraceLogger.Log(
                "ImagePreview",
                $"LoadImagePreviewAsync completed. MediaId='{media.Id}', PixelSize={bitmap.PixelWidth}x{bitmap.PixelHeight}, JpegBytes={jpeg!.Length}, CacheHit={fromCache}.");
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection.
        }
        catch (Exception ex)
        {
            if (!IsCurrentImageLoad(media, loadVersion))
            {
                return;
            }

            AppTraceLogger.LogSampled(
                "ImagePreview",
                $"load-image-failed:{media.Id}",
                $"LoadImagePreviewAsync failed. MediaId='{media.Id}', File='{media.FileName}', Path='{media.FileSystemPath}', ErrorType={ex.GetType().Name}, Message='{ex.Message}'.",
                TimeSpan.FromSeconds(5));
            _pendingImageFitMediaId = media.Id;
            TryApplyPendingImageFit();
        }
    }

    private CancellationToken ResetPreviewCancellation()
    {
        CancelPreviewLoad();
        _previewCancellation = new CancellationTokenSource();
        return _previewCancellation.Token;
    }

    private void CancelPreviewLoad()
    {
        try
        {
            _previewCancellation.Cancel();
            _previewCancellation.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private bool IsCurrentImageLoad(MediaItemViewModel media, int loadVersion)
    {
        return !_isDisposed
            && loadVersion == Volatile.Read(ref _imageLoadVersion)
            && string.Equals(_libraryViewModel.SelectedMedia?.Id, media.Id, StringComparison.Ordinal);
    }

    // WP-F: decode at most this edge so a 50MP photo cannot spike memory.
    // 1600 is enough for the player pane and is cheaper to transcode than 2048.
    private const int MaxPreviewDecodeLongEdge = 1600;
    private const int PreviewCacheCapacity = 12;
    private const long PreviewCacheMaxBytes = 24L * 1024 * 1024;

    private async Task<BitmapImage> CreatePreviewBitmapAsync(byte[] jpeg, CancellationToken cancellationToken)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(jpeg.AsBuffer());
        cancellationToken.ThrowIfCancellationRequested();
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    private bool TryTakePreviewCache(string key, out byte[] jpeg)
    {
        if (_previewCacheNodes.TryGetValue(key, out var node))
        {
            _previewCacheLru.Remove(node);
            _previewCacheLru.AddFirst(node);
            jpeg = node.Value.Jpeg;
            return true;
        }

        jpeg = Array.Empty<byte>();
        return false;
    }

    private void RememberPreviewCache(string key, byte[] jpeg)
    {
        if (_previewCacheNodes.TryGetValue(key, out var existing))
        {
            _previewCacheBytes -= existing.Value.Jpeg.Length;
            _previewCacheLru.Remove(existing);
            _previewCacheNodes.Remove(key);
        }

        var entry = new PreviewCacheEntry(key, jpeg);
        var node = _previewCacheLru.AddFirst(entry);
        _previewCacheNodes[key] = node;
        _previewCacheBytes += jpeg.Length;

        while (_previewCacheLru.Count > PreviewCacheCapacity || _previewCacheBytes > PreviewCacheMaxBytes)
        {
            var last = _previewCacheLru.Last;
            if (last == null)
            {
                break;
            }

            _previewCacheLru.RemoveLast();
            _previewCacheNodes.Remove(last.Value.Key);
            _previewCacheBytes -= last.Value.Jpeg.Length;
        }
    }

    private sealed record PreviewCacheEntry(string Key, byte[] Jpeg);

    private void UpdatePreviewImageSourceSize(int? width, int? height)
    {
        if (width is > 0 && height is > 0)
        {
            SetPreviewImageSourceSize(width.Value, height.Value);
            return;
        }

        _imageSourceWidth = 0;
        _imageSourceHeight = 0;
        _imageViewportView.PreviewImageElement.Width = double.NaN;
        _imageViewportView.PreviewImageElement.Height = double.NaN;
    }

    private void SetPreviewImageSourceSize(double width, double height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        _imageSourceWidth = width;
        _imageSourceHeight = height;
    }

    private void ApplyPreviewImageDisplaySize(double width, double height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        _imageViewportView.PreviewImageElement.Width = width;
        _imageViewportView.PreviewImageElement.Height = height;
    }

    private void PreviewImageElement_ImageOpened(object sender, RoutedEventArgs e)
    {
        if ((_imageSourceWidth <= 0 || _imageSourceHeight <= 0)
            && _imageViewportView.PreviewImageElement.Source is BitmapImage bitmap
            && bitmap.PixelWidth > 0
            && bitmap.PixelHeight > 0)
        {
            SetPreviewImageSourceSize(bitmap.PixelWidth, bitmap.PixelHeight);
        }

        TryApplyPendingImageFit();
    }

    private void ImageScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0)
        {
            return;
        }

        UpdateImageViewportClip();
        TryApplyPendingImageFit();
    }

    private void TryApplyPendingImageFit()
    {
        var selectedMedia = _libraryViewModel.SelectedMedia;
        if (selectedMedia?.Type != MediaType.Image)
        {
            return;
        }

        if (!string.Equals(_pendingImageFitMediaId, selectedMedia.Id, StringComparison.Ordinal))
        {
            return;
        }

        if (FitImageToViewport())
        {
            _pendingImageFitMediaId = null;
        }
    }

    private bool FitImageToViewport()
    {
        if (_imageViewportView.ImageScrollViewer.Visibility != Visibility.Visible)
        {
            return false;
        }

        if (!TryGetImageViewportSize(out var viewportWidth, out var viewportHeight)
            || !TryGetImageSourceSize(out var imageWidth, out var imageHeight))
        {
            return false;
        }

        var fitScale = Math.Min(viewportWidth / imageWidth, viewportHeight / imageHeight);
        if (double.IsNaN(fitScale) || double.IsInfinity(fitScale) || fitScale <= 0)
        {
            return false;
        }

        ApplyPreviewImageDisplaySize(imageWidth * fitScale, imageHeight * fitScale);
        ResetImageViewTransform();
        UpdateImageZoomUi(MinImageZoomFactor);
        SetPreviewImageVisible(true);
        return true;
    }

    private bool TryGetImageViewportSize(out double viewportWidth, out double viewportHeight)
    {
        viewportWidth = _imageViewportView.ImageScrollViewer.ActualWidth;
        viewportHeight = _imageViewportView.ImageScrollViewer.ActualHeight;
        return viewportWidth > 0 && viewportHeight > 0;
    }

    private bool TryGetImageSourceSize(out double imageWidth, out double imageHeight)
    {
        imageWidth = 0;
        imageHeight = 0;

        var previewImage = _imageViewportView.PreviewImageElement;
        if (!double.IsNaN(previewImage.Width)
            && !double.IsNaN(previewImage.Height)
            && previewImage.Width > 0
            && previewImage.Height > 0)
        {
            imageWidth = previewImage.Width;
            imageHeight = previewImage.Height;
            return true;
        }

        imageWidth = _imageSourceWidth;
        imageHeight = _imageSourceHeight;
        if (imageWidth > 0 && imageHeight > 0)
        {
            return true;
        }

        if (previewImage.Source is BitmapImage bitmap && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
        {
            imageWidth = bitmap.PixelWidth;
            imageHeight = bitmap.PixelHeight;
            return true;
        }

        return false;
    }

    private void ZoomImage(double delta, Windows.Foundation.Point anchorPoint)
    {
        if (_imageViewportView.ImageScrollViewer.Visibility != Visibility.Visible)
        {
            return;
        }

        if (!TryGetImageViewportSize(out var viewportWidth, out var viewportHeight)
            || !TryGetImageSourceSize(out var imageWidth, out var imageHeight))
        {
            return;
        }

        _pendingImageFitMediaId = null;
        var currentZoom = Math.Max(_imageZoomFactor, 0.01);
        var targetZoom = Math.Clamp(currentZoom + delta, MinImageZoomFactor, MaxImageZoomFactor);
        if (Math.Abs(targetZoom - currentZoom) < 0.001)
        {
            return;
        }

        var viewportCenterX = viewportWidth / 2;
        var viewportCenterY = viewportHeight / 2;
        var imageLocalX = (anchorPoint.X - viewportCenterX - _imageTranslationX) / currentZoom;
        var imageLocalY = (anchorPoint.Y - viewportCenterY - _imageTranslationY) / currentZoom;

        _imageZoomFactor = targetZoom;
        _imageTranslationX = anchorPoint.X - viewportCenterX - (imageLocalX * targetZoom);
        _imageTranslationY = anchorPoint.Y - viewportCenterY - (imageLocalY * targetZoom);
        ClampImageTranslation(imageWidth, imageHeight, viewportWidth, viewportHeight);
        ApplyImageViewTransform();
        UpdateImageZoomUi(targetZoom);
        _refreshNavigationHotspots();
    }

    private void UpdateImageZoomUi(double zoomFactor)
    {
        var percentage = Math.Max(1, (int)Math.Round(zoomFactor * 100));
        _viewModel.ZoomText = $"{percentage}%";
    }

    private void ImageScrollViewer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(_imageViewportView.ImageScrollViewer).Properties.IsLeftButtonPressed)
        {
            return;
        }

        BeginImageDrag(_imageViewportView.ImageScrollViewer, e.Pointer, e.GetCurrentPoint(_playerOverlay).Position);
        e.Handled = true;
    }

    private void ImageScrollViewer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isImageDragging)
        {
            return;
        }

        UpdateImageDrag(e.GetCurrentPoint(_playerOverlay).Position);
        e.Handled = true;
    }

    private void ImageScrollViewer_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndImageDrag();
        e.Handled = true;
    }

    private void ImageScrollViewer_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        EndImageDrag();
    }

    private void ImageScrollViewer_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        ResetZoom();
        e.Handled = true;
    }

    private void ImageScrollViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_imageViewportView.ImageScrollViewer);
        var delta = point.Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        ZoomImage(delta > 0 ? 0.1 : -0.1, point.Position);
        e.Handled = true;
    }

    private void EndImageDrag()
    {
        if (!_isImageDragging)
        {
            return;
        }

        _isImageDragging = false;
        _imageDragCaptureOwner?.ReleasePointerCaptures();
        _imageDragCaptureOwner = null;
    }

    private void BeginImageDrag(UIElement captureOwner, Pointer pointer, Windows.Foundation.Point startPoint, bool capturePointer = true)
    {
        _pendingImageFitMediaId = null;
        _isImageDragging = true;
        _imageDragStartPoint = startPoint;
        _imageDragStartHorizontalOffset = _imageTranslationX;
        _imageDragStartVerticalOffset = _imageTranslationY;
        _imageDragCaptureOwner = captureOwner;

        if (capturePointer)
        {
            captureOwner.CapturePointer(pointer);
        }
    }

    private void UpdateImageDrag(Windows.Foundation.Point position)
    {
        if (!_isImageDragging)
        {
            return;
        }

        if (!TryGetImageViewportSize(out var viewportWidth, out var viewportHeight)
            || !TryGetImageSourceSize(out var imageWidth, out var imageHeight))
        {
            return;
        }

        _imageTranslationX = _imageDragStartHorizontalOffset + (position.X - _imageDragStartPoint.X);
        _imageTranslationY = _imageDragStartVerticalOffset + (position.Y - _imageDragStartPoint.Y);
        ClampImageTranslation(imageWidth, imageHeight, viewportWidth, viewportHeight);
        ApplyImageViewTransform();
    }

    private void ResetImageViewTransform()
    {
        _imageZoomFactor = MinImageZoomFactor;
        _imageTranslationX = 0;
        _imageTranslationY = 0;
        ApplyImageViewTransform();
        _refreshNavigationHotspots();
    }

    private void ApplyImageViewTransform()
    {
        _imageViewportView.PreviewImageTransform.ScaleX = _imageZoomFactor;
        _imageViewportView.PreviewImageTransform.ScaleY = _imageZoomFactor;
        _imageViewportView.PreviewImageTransform.TranslateX = _imageTranslationX;
        _imageViewportView.PreviewImageTransform.TranslateY = _imageTranslationY;
    }

    private void ClampImageTranslation(double imageWidth, double imageHeight, double viewportWidth, double viewportHeight)
    {
        var maxTranslationX = Math.Max(0, (imageWidth * _imageZoomFactor - viewportWidth) / 2);
        var maxTranslationY = Math.Max(0, (imageHeight * _imageZoomFactor - viewportHeight) / 2);
        _imageTranslationX = Math.Clamp(_imageTranslationX, -maxTranslationX, maxTranslationX);
        _imageTranslationY = Math.Clamp(_imageTranslationY, -maxTranslationY, maxTranslationY);
    }

    private void UpdateImageViewportClip()
    {
        _imageViewportView.ImageScrollViewer.Clip = new RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, _imageViewportView.ImageScrollViewer.ActualWidth, _imageViewportView.ImageScrollViewer.ActualHeight)
        };
    }

    private void SetPreviewImageVisible(bool visible)
    {
        _imageViewportView.PreviewImageElement.Opacity = visible ? 1 : 0;
    }
}
