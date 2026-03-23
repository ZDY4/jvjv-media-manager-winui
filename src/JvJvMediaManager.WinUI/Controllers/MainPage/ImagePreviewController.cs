using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using JvJvMediaManager.Models;
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

        _imageViewportView.ImageScrollViewer.SizeChanged += ImageScrollViewer_SizeChanged;
        _imageViewportView.ImageScrollViewer.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(ImageScrollViewer_PointerPressed), true);
        _imageViewportView.ImageScrollViewer.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(ImageScrollViewer_PointerMoved), true);
        _imageViewportView.ImageScrollViewer.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(ImageScrollViewer_PointerReleased), true);
        _imageViewportView.ImageScrollViewer.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(ImageScrollViewer_PointerCaptureLost), true);
        _imageViewportView.ImageScrollViewer.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(ImageScrollViewer_PointerWheelChanged), true);
        _imageViewportView.ImageScrollViewer.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler(ImageScrollViewer_DoubleTapped), true);
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
        _imageViewportView.ImageScrollViewer.SizeChanged -= ImageScrollViewer_SizeChanged;
        _imageViewportView.PreviewImageElement.ImageOpened -= PreviewImageElement_ImageOpened;
    }

    public void ShowImage(MediaItemViewModel media)
    {
        _imageViewportView.ImageScrollViewer.Visibility = Visibility.Visible;
        Interlocked.Increment(ref _imageLoadVersion);
        _imageViewportView.PreviewImageElement.Source = media.Thumbnail;
        BeginImagePreviewSession(media);
        _ = LoadImagePreviewAsync(media, Volatile.Read(ref _imageLoadVersion));
    }

    public void Clear()
    {
        EndImageDrag();
        Interlocked.Increment(ref _imageLoadVersion);
        _pendingImageFitMediaId = null;
        _imageSourceWidth = 0;
        _imageSourceHeight = 0;
        _imageViewportView.ImageScrollViewer.Visibility = Visibility.Collapsed;
        _imageViewportView.PreviewImageElement.Width = double.NaN;
        _imageViewportView.PreviewImageElement.Height = double.NaN;
        _imageViewportView.PreviewImageElement.Source = null;
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

    private void BeginImagePreviewSession(MediaItemViewModel media)
    {
        EndImageDrag();
        _pendingImageFitMediaId = media.Id;
        _imageViewportView.PreviewImageElement.Width = double.NaN;
        _imageViewportView.PreviewImageElement.Height = double.NaN;
        UpdatePreviewImageSourceSize(media.Media.Width, media.Media.Height);
        ResetImageViewTransform();
        UpdateImageZoomUi(MinImageZoomFactor);
        TryApplyPendingImageFit();
    }

    private async Task LoadImagePreviewAsync(MediaItemViewModel media, int loadVersion)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(media.FileSystemPath);
            using var stream = await file.OpenReadAsync();
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);

            if (loadVersion != Volatile.Read(ref _imageLoadVersion))
            {
                return;
            }

            if (!string.Equals(_libraryViewModel.SelectedMedia?.Id, media.Id, StringComparison.Ordinal))
            {
                return;
            }

            _imageViewportView.PreviewImageElement.Source = bitmap;
            UpdatePreviewImageSourceSize(bitmap.PixelWidth, bitmap.PixelHeight);
            if (media.Media.Width is not > 0 || media.Media.Height is not > 0)
            {
                _pendingImageFitMediaId = media.Id;
            }

            TryApplyPendingImageFit();
        }
        catch
        {
            if (loadVersion != Volatile.Read(ref _imageLoadVersion))
            {
                return;
            }

            if (!string.Equals(_libraryViewModel.SelectedMedia?.Id, media.Id, StringComparison.Ordinal))
            {
                return;
            }

            _imageViewportView.PreviewImageElement.Source = media.Thumbnail;
            TryApplyPendingImageFit();
        }
    }

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
}
