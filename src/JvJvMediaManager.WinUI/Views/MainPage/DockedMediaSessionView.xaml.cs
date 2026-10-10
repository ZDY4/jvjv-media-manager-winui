using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using JvJvMediaManager.Controllers.MainPage;
using JvJvMediaManager.Models;
using JvJvMediaManager.Services;
using JvJvMediaManager.Utilities;
using JvJvMediaManager.ViewModels;
using JvJvMediaManager.ViewModels.MainPage;

namespace JvJvMediaManager.Views.MainPageParts;

public sealed partial class DockedMediaSessionView : UserControl, IDisposable
{
    private const double PlayerEdgeNavigationRevealWidth = 72;
    private const double ZoomedImageNavigationHotspotWidth = 24;
    private const double NavigationGestureDragThreshold = 8;

    private enum PlayerNavigationEdge
    {
        None,
        Left,
        Right
    }

    private readonly SelectionViewModel _selection = new();
    private readonly LibraryShellServices _libraryServices;
    private readonly SettingsService _settings;
    private readonly LibraryShellViewModel _library;
    private readonly PlayerShellViewModel _player;
    private readonly KeyEventHandler _playerPaneKeyDownHandler;
    private readonly PointerEventHandler _playerRootPointerPressedHandler;
    private readonly PointerEventHandler _videoPlayerPointerPressedHandler;
    private readonly PointerEventHandler _playerNavigationHotspotPointerWheelHandler;
    private readonly RightTappedEventHandler _playerRootRightTappedHandler;

    private IndependentMediaWindowController? _controller;
    private VideoPlaybackController? _videoPlaybackController;
    private ImagePreviewController? _imagePreviewController;
    private IndependentMediaWindowShortcutHandler? _shortcutHandler;
    private bool _isImagePreviewControllerReady;
    private bool _isNavigationHotspotPressed;
    private bool _isNavigationHotspotTapCanceled;
    private bool _isNavigationHotspotDraggingImage;
    private bool _isDeletingCurrentMedia;
    private bool _isDisposed;
    private CancellationTokenSource _restoreCancellation = new();
    private int _isNavigationHotspotWheelNavigating;
    private Windows.Foundation.Point _navigationHotspotPressPoint;
    private PlayerNavigationEdge _activePlayerNavigationEdge;
    private PlayerNavigationEdge _pressedNavigationHotspotEdge;

    public DockedMediaSessionView()
    {
        _libraryServices = LibraryShellServices.CreateDefault();
        _settings = _libraryServices.Settings;
        _library = new LibraryShellViewModel(
            _selection,
            _libraryServices,
            persistMediaLibraryViewState: false,
            enableWatchedFolderMonitoring: false);
        _player = new PlayerShellViewModel(_selection);
        _playerPaneKeyDownHandler = PlayerPane_KeyDown;
        _playerRootPointerPressedHandler = PlayerRoot_PointerPressed;
        _videoPlayerPointerPressedHandler = VideoPlayer_PointerPressed;
        _playerNavigationHotspotPointerWheelHandler = PlayerNavigationHotspot_PointerWheelChanged;
        _playerRootRightTappedHandler = PlayerRoot_RightTapped;

        InitializeComponent();
        PlayerPane.DataContext = _player;
        PlayerPane.ClipBarView.Visibility = Visibility.Collapsed;
        PlayerPane.TransportBar.ClipModeToggleButton.Visibility = Visibility.Collapsed;
        PlayerPane.EmptyStateView.RootGrid.Visibility = Visibility.Collapsed;
        PlayerPane.IsTabStop = true;
        AttachPlayerEvents();
        SetEmptyState();
    }

    internal event Action<MediaPlayerSessionTransfer>? DetachRequested;
    internal event Action? CloseRequested;
    internal event EventHandler? SessionActivated;
    internal event EventHandler<DockedSessionContextMenuRequestedEventArgs>? ContextMenuRequested;
    internal event EventHandler<IndependentMediaWindowTagsChangedEventArgs>? MediaTagsChanged;
    internal event EventHandler<IndependentMediaWindowDeletedEventArgs>? MediaDeleted;

    internal bool HasActiveSession => _controller != null && !_isDisposed;

    private Grid PlayerRoot => PlayerPane.PlayerRoot;
    private Grid PlayerOverlay => PlayerPane.PlayerOverlay;
    private Border PreviousMediaHotspot => PlayerPane.NavigationOverlay.PreviousMediaHotspot;
    private Border NextMediaHotspot => PlayerPane.NavigationOverlay.NextMediaHotspot;
    private Border PreviousMediaCue => PlayerPane.NavigationOverlay.PreviousMediaCue;
    private Border NextMediaCue => PlayerPane.NavigationOverlay.NextMediaCue;

    internal void LoadSession(MediaPlayerSessionTransfer session)
    {
        if (_isDisposed || session.MediaItems.Count == 0)
        {
            return;
        }

        _restoreCancellation.Cancel();
        _restoreCancellation.Dispose();
        _restoreCancellation = new CancellationTokenSource();
        ResetSessionState();
        _controller = new IndependentMediaWindowController(session.MediaItems, session.CurrentIndex);
        foreach (var media in _controller.MediaItems)
        {
            _library.FilteredMediaItems.Add(media);
        }

        _videoPlaybackController = new VideoPlaybackController(
            _settings,
            _library,
            _player.VideoPlayback,
            PlayerPane.TransportBar,
            PlayerPane.NavigationOverlay,
            PlayerPane.VideoViewport,
            PlayerPane.VideoViewport.VideoPlayer,
            DispatcherQueue,
            () => null,
            NavigateRelativeAsync,
            CanAutoHideControls,
            FocusPlayer,
            RefreshPlayerNavigationHotspots,
            _ => { },
            () => { },
            _libraryServices.PlaybackResources);
        _imagePreviewController = new ImagePreviewController(
            _library,
            _player.ImagePreview,
            PlayerPane.ImageViewport,
            PlayerPane.PlayerOverlay,
            RefreshPlayerNavigationHotspots);
        _isImagePreviewControllerReady = true;
        _shortcutHandler = new IndependentMediaWindowShortcutHandler(
            () => _library.SelectedMedia,
            () => CloseRequested?.Invoke(),
            ToggleNumpadShortcutHint,
            TryApplyNumpadTagShortcutAsync,
            DeleteCurrentMediaAsync,
            NavigateRelativeAsync,
            _videoPlaybackController.TogglePlayPause,
            _videoPlaybackController.SeekRelative,
            _imagePreviewController.ZoomBy,
            _imagePreviewController.ResetZoom,
            new BossKeyController(() => { }, () => { }, () => true));

        OpenCurrentMedia();
        var expectedMediaId = _controller.CurrentMedia.Id;
        _ = RestorePlaybackStateAsync(session.PositionSeconds, session.WasPlaying, expectedMediaId, _restoreCancellation.Token);
        _ = RestoreVideoRotationAsync(session.RotationDegrees, expectedMediaId, _restoreCancellation.Token);
        NotifySessionActivated();
        FocusPlayer();
    }

    internal void PlayMedia(IndependentMediaWindowMedia media)
    {
        CancelPendingRestore();
        if (_isDisposed || _controller == null)
        {
            return;
        }

        var beforeCount = _controller.Count;
        var played = _controller.PlayOrSelect(media);
        if (_library.FilteredMediaItems.All(item => !string.Equals(item.Id, played.Id, StringComparison.Ordinal)))
        {
            var insertIndex = Math.Min(_controller.CurrentIndex, _library.FilteredMediaItems.Count);
            _library.FilteredMediaItems.Insert(insertIndex, played);
        }

        AppTraceLogger.Log(
            "DockedMediaSession",
            $"PlayMedia. MediaId='{played.Id}', QueueCount={_controller.Count}, Added={_controller.Count > beforeCount}.");
        OpenCurrentMedia();
        NotifySessionActivated();
        FocusPlayer();
    }

    internal Task<bool> TryHandleShortcutAsync(VirtualKey key)
    {
        if (_isDisposed || _shortcutHandler == null)
        {
            return Task.FromResult(false);
        }

        return _shortcutHandler.HandleKeyAsync(key);
    }

    internal void ApplyExternalTags(string mediaId, IReadOnlyList<string> tags)
    {
        if (_controller == null || string.IsNullOrWhiteSpace(mediaId))
        {
            return;
        }

        var media = _controller.MediaItems.FirstOrDefault(item => string.Equals(item.Id, mediaId, StringComparison.Ordinal));
        if (media == null)
        {
            return;
        }

        media.UpdateTags(tags);
        if (string.Equals(_library.SelectedMedia?.Id, mediaId, StringComparison.Ordinal))
        {
            RefreshPlayerTags(media);
            RefreshNumpadShortcutHint();
        }
    }

    internal void RemoveDeletedMedia(IReadOnlyList<string> mediaIds)
    {
        if (_controller == null || mediaIds.Count == 0)
        {
            return;
        }

        var deletedIds = mediaIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (deletedIds.Count == 0)
        {
            return;
        }

        var currentMediaId = _controller.CurrentMedia.Id;
        var isDeletingCurrentMedia = deletedIds.Contains(currentMediaId);
        var removedAny = false;
        var orderedDeletedIds = deletedIds
            .Where(id => !string.Equals(id, currentMediaId, StringComparison.Ordinal))
            .Concat(deletedIds.Contains(currentMediaId) ? new[] { currentMediaId } : Array.Empty<string>());
        foreach (var mediaId in orderedDeletedIds)
        {
            var plan = _controller.BuildDeletePlan(mediaId);
            if (plan.RemovedIndex < 0)
            {
                continue;
            }

            var removed = _controller.MediaItems.ElementAtOrDefault(plan.RemovedIndex);
            _controller.RemoveDeletedMedia(
                plan,
                preserveCurrentMedia: !string.Equals(mediaId, currentMediaId, StringComparison.Ordinal));
            if (removed != null)
            {
                _ = _library.FilteredMediaItems.Remove(removed);
            }

            removedAny = true;
        }

        if (!removedAny)
        {
            return;
        }

        if (_controller.Count == 0)
        {
            CloseRequested?.Invoke();
            return;
        }

        if (isDeletingCurrentMedia)
        {
            OpenCurrentMedia();
            FocusPlayer();
        }
        else
        {
            RefreshPlayerNavigationHotspots();
            RefreshNumpadShortcutHint();
        }
    }

    internal MediaPlayerSessionTransfer? ExportSession()
    {
        if (_controller == null)
        {
            return null;
        }

        var items = _controller.MediaItems
            .Select(IndependentMediaWindowService.CreateSnapshot)
            .ToList();

        return new MediaPlayerSessionTransfer(
            items,
            _controller.CurrentIndex,
            _videoPlaybackController?.GetCurrentPlaybackPosition().TotalSeconds ?? 0,
            _videoPlaybackController?.IsPlaying == true,
            _videoPlaybackController?.VideoRotationDegrees ?? 0);
    }

    internal void ClearSession()
    {
        _restoreCancellation.Cancel();
        ResetSessionState();
        SetEmptyState();
    }

    private void ResetSessionState()
    {
        _videoPlaybackController?.Dispose();
        _imagePreviewController?.Dispose();
        _videoPlaybackController = null;
        _imagePreviewController = null;
        _shortcutHandler = null;
        _controller = null;
        _library.FilteredMediaItems.Clear();
        _library.SelectedMedia = null;
        _isImagePreviewControllerReady = false;
        ResetNavigationHotspotGesture();
        _activePlayerNavigationEdge = PlayerNavigationEdge.None;
    }

    private async Task RestorePlaybackStateAsync(double positionSeconds, bool wasPlaying, string expectedMediaId, CancellationToken cancellationToken)
    {
        try
        {
            if (_videoPlaybackController == null || (positionSeconds <= 0 && wasPlaying))
            {
                return;
            }

            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(100, cancellationToken);
                if (!string.Equals(_library.SelectedMedia?.Id, expectedMediaId, StringComparison.Ordinal)
                    || _library.SelectedMedia?.Type != MediaType.Video || _videoPlaybackController == null)
                {
                    return;
                }

                if (_videoPlaybackController.GetCurrentVideoDuration() <= TimeSpan.Zero)
                {
                    continue;
                }

                if (positionSeconds > 0)
                {
                    _videoPlaybackController.SeekTo(TimeSpan.FromSeconds(positionSeconds));
                }

                if (!wasPlaying && _videoPlaybackController.IsPlaying)
                {
                    _videoPlaybackController.TogglePlayPause();
                }

                return;
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer docked session or teardown.
        }
    }

    private async Task RestoreVideoRotationAsync(double rotationDegrees, string expectedMediaId, CancellationToken cancellationToken)
    {
        try
        {
            if (_videoPlaybackController == null || rotationDegrees <= 0)
            {
                return;
            }

            // ShowVideo resets rotation per media switch, so wait until the source
            // is actually open before applying the transferred rotation.
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(100, cancellationToken);
                if (_videoPlaybackController == null)
                {
                    return;
                }

                if (!string.Equals(_library.SelectedMedia?.Id, expectedMediaId, StringComparison.Ordinal)
                    || _library.SelectedMedia?.Type != MediaType.Video)
                {
                    return;
                }

                if (_videoPlaybackController.GetCurrentVideoDuration() > TimeSpan.Zero)
                {
                    _videoPlaybackController.SetVideoRotation(rotationDegrees);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer docked session or teardown.
        }
    }

    private void OpenCurrentMedia()
    {
        if (_controller == null || _videoPlaybackController == null || _imagePreviewController == null)
        {
            SetEmptyState();
            return;
        }

        var media = _controller.CurrentMedia;
        _library.SelectedMedia = media;
        TitleText.Text = string.IsNullOrWhiteSpace(media.FileName) ? "分屏播放器" : media.FileName;
        _player.EmptyStateVisibility = Visibility.Collapsed;
        _player.PlayerInfoVisibility = Visibility.Visible;
        _player.PlayerFileName = media.FileName;
        _player.PlayerResolution = media.PlayerDetailsText;
        RefreshPlayerTags(media);
        RefreshNumpadShortcutHint();
        RefreshPlayerNavigationHotspots();

        if (string.IsNullOrWhiteSpace(media.FileSystemPath) || !File.Exists(media.FileSystemPath))
        {
            ShowMissingMediaState(media);
            return;
        }

        if (media.Type == MediaType.Video)
        {
            _player.ImagePreview.ZoomBadgeVisibility = Visibility.Collapsed;
            _imagePreviewController.Clear();
            _videoPlaybackController.ShowVideo(media);
            return;
        }

        _videoPlaybackController.ShowImageState();
        _player.ImagePreview.ZoomBadgeVisibility = Visibility.Visible;
        _imagePreviewController.ShowImage(media);
    }

    private void SetEmptyState()
    {
        TitleText.Text = "分屏播放器";
        _player.EmptyStateVisibility = Visibility.Collapsed;
        _player.PlayerInfoVisibility = Visibility.Collapsed;
        _player.PlayerFileName = string.Empty;
        _player.PlayerResolution = string.Empty;
        _player.PlayerTags.Clear();
        _player.ShortcutHintVisibility = Visibility.Collapsed;
        _player.ShortcutHintItems.Clear();
        _player.ImagePreview.ZoomBadgeVisibility = Visibility.Collapsed;
        PlayerPane.NavigationOverlay.SideActionButtons.Visibility = Visibility.Collapsed;
        PreviousMediaHotspot.Visibility = Visibility.Collapsed;
        NextMediaHotspot.Visibility = Visibility.Collapsed;
    }

    private void ShowMissingMediaState(MediaItemViewModel media)
    {
        _videoPlaybackController?.Clear();
        _imagePreviewController?.Clear();
        _player.PlayerFileName = $"文件不存在：{media.FileName}";
        _player.PlayerResolution = media.FileSystemPath;
        AppTraceLogger.Log(
            "DockedMediaSession",
            $"Missing media. MediaId='{media.Id}', File='{media.FileName}', Path='{media.FileSystemPath}'.");
    }

    private void RefreshPlayerTags(MediaItemViewModel media)
    {
        _player.PlayerTags.Clear();
        foreach (var tag in media.Tags
                     .Where(tag => !string.IsNullOrWhiteSpace(tag))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase))
        {
            _player.PlayerTags.Add(new PlayerMediaTagItem { Tag = tag });
        }
    }

    private void RefreshNumpadShortcutHint()
    {
        var selectedTags = _library.SelectedMedia?.Tags ?? [];
        var items = _library.NumpadTagShortcuts
            .Select((tag, index) => new { Tag = tag, Index = index + 1 })
            .Where(item => !string.IsNullOrWhiteSpace(item.Tag))
            .ToList();

        _player.ShortcutHintItems.Clear();
        foreach (var item in items)
        {
            _player.ShortcutHintItems.Add(new NumpadTagShortcutHintItem
            {
                Index = item.Index,
                Tag = item.Tag,
                IsApplied = selectedTags.Any(existing => string.Equals(existing, item.Tag, StringComparison.OrdinalIgnoreCase))
            });
        }
    }

    private Task NavigateRelativeAsync(int offset)
    {
        CancelPendingRestore();
        if (_controller == null)
        {
            return Task.CompletedTask;
        }

        var media = _controller.NavigateRelative(offset);
        if (media == null)
        {
            return Task.CompletedTask;
        }

        AppTraceLogger.Log(
            "DockedMediaSession",
            $"NavigateRelative. Offset={offset}, NewIndex={_controller.CurrentIndex}, MediaId='{media.Id}', QueueCount={_controller.Count}.");
        OpenCurrentMedia();
        return Task.CompletedTask;
    }

    private bool CanAutoHideControls()
    {
        return _library.SelectedMedia?.Type == MediaType.Video
            && _player.EmptyStateVisibility != Visibility.Visible;
    }

    private void FocusPlayer()
    {
        PlayerPane.Focus(FocusState.Programmatic);
    }

    private void NotifySessionActivated()
    {
        if (_controller == null || _isDisposed)
        {
            return;
        }

        SessionActivated?.Invoke(this, EventArgs.Empty);
    }

    private void ShowControls()
    {
        _videoPlaybackController?.ShowControls();
    }

    private void PlayerRoot_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pointerPosition = e.GetCurrentPoint(PlayerRoot).Position;
        _videoPlaybackController?.HandlePlayerPointerMoved(pointerPosition.Y, PlayerRoot.ActualHeight);
        UpdatePlayerNavigationCue(pointerPosition);
    }

    private void PlayerRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pointerPosition = e.GetCurrentPoint(PlayerRoot).Position;
        UpdatePlayerNavigationCue(pointerPosition);
        NotifySessionActivated();

        if (!IsPlayerOverlayInteractionSource(e.OriginalSource as DependencyObject))
        {
            FocusPlayer();
        }
    }

    private void VideoPlayer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        NotifySessionActivated();
        FocusPlayer();
    }

    private void PlayerRoot_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var media = _library.SelectedMedia;
        if (media == null || _controller == null)
        {
            return;
        }

        NotifySessionActivated();
        ShowControls();
        ContextMenuRequested?.Invoke(
            this,
            new DockedSessionContextMenuRequestedEventArgs(
                PlayerRoot,
                e.GetPosition(PlayerRoot),
                media,
                ToggleNumpadShortcutHint,
                GetNumpadShortcutHintMenuText(),
                media.Type == MediaType.Video && _videoPlaybackController is { } playback
                    ? angle => playback.RotateVideoBy(angle)
                    : null));
        e.Handled = true;
    }

    private string GetNumpadShortcutHintMenuText()
    {
        return _player.ShortcutHintVisibility == Visibility.Visible
            ? "隐藏快捷加tag"
            : "显示快捷加tag";
    }

    private void PlayerRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        SetPlayerNavigationEdge(PlayerNavigationEdge.None);
        _videoPlaybackController?.HandlePointerExited();
    }

    private void PreviousMediaHotspot_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SetPlayerNavigationEdge(PlayerNavigationEdge.Left);
    }

    private void ShortcutHintContainer_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SetPlayerNavigationEdge(PlayerNavigationEdge.Left);
    }

    private void NextMediaHotspot_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SetPlayerNavigationEdge(PlayerNavigationEdge.Right);
    }

    private void PlayerNavigationHotspot_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!CanShowPlayerNavigationHotspots() || sender is not UIElement hotspot)
        {
            return;
        }

        var point = e.GetCurrentPoint(PlayerOverlay);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isNavigationHotspotPressed = true;
        _isNavigationHotspotTapCanceled = false;
        _isNavigationHotspotDraggingImage = false;
        _pressedNavigationHotspotEdge = GetNavigationHotspotEdge(sender);
        _navigationHotspotPressPoint = point.Position;
        hotspot.CapturePointer(e.Pointer);
        ShowControls();
        SetPlayerNavigationEdge(_pressedNavigationHotspotEdge);
        e.Handled = true;
    }

    private void PlayerNavigationHotspot_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isNavigationHotspotPressed || sender is not UIElement hotspot || _imagePreviewController == null)
        {
            return;
        }

        var position = e.GetCurrentPoint(PlayerOverlay).Position;
        if (!_isNavigationHotspotDraggingImage)
        {
            var deltaX = position.X - _navigationHotspotPressPoint.X;
            var deltaY = position.Y - _navigationHotspotPressPoint.Y;
            if (Math.Abs(deltaX) >= NavigationGestureDragThreshold
                || Math.Abs(deltaY) >= NavigationGestureDragThreshold)
            {
                _isNavigationHotspotTapCanceled = true;
                if (_imagePreviewController.CanPanZoomedImage())
                {
                    _isNavigationHotspotDraggingImage = true;
                    _imagePreviewController.BeginExternalDrag(hotspot, e.Pointer, _navigationHotspotPressPoint, capturePointer: false);
                }
            }
        }

        if (_isNavigationHotspotDraggingImage)
        {
            _imagePreviewController.UpdateExternalDrag(position);
        }

        e.Handled = true;
    }

    private async void PlayerNavigationHotspot_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            if (!_isNavigationHotspotPressed || sender is not UIElement hotspot)
            {
                return;
            }

            var edge = _pressedNavigationHotspotEdge;
            var shouldNavigate = !_isNavigationHotspotDraggingImage
                && !_isNavigationHotspotTapCanceled
                && CanShowPlayerNavigationHotspots()
                && edge != PlayerNavigationEdge.None;

            ResetNavigationHotspotGesture();
            _imagePreviewController?.EndExternalDrag();
            hotspot.ReleasePointerCaptures();

            if (shouldNavigate)
            {
                await NavigateRelativeAsync(edge == PlayerNavigationEdge.Left ? -1 : 1);
                ShowControls();
                SetPlayerNavigationEdge(edge);
            }

            e.Handled = true;
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("DockedMediaSession", "Player navigation hotspot release failed.", ex);
        }
    }

    private void PlayerNavigationHotspot_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        ResetNavigationHotspotGesture();
        _imagePreviewController?.EndExternalDrag();
    }

    private async void PlayerNavigationHotspot_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!CanShowPlayerNavigationHotspots())
        {
            return;
        }

        if (Interlocked.Exchange(ref _isNavigationHotspotWheelNavigating, 1) == 1)
        {
            e.Handled = true;
            return;
        }

        try
        {
            var delta = e.GetCurrentPoint(PlayerOverlay).Properties.MouseWheelDelta;
            if (delta == 0)
            {
                return;
            }

            var offset = delta > 0 ? -1 : 1;
            await NavigateRelativeAsync(offset);
            ShowControls();
            e.Handled = true;
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("DockedMediaSession", "Player navigation hotspot wheel navigation failed.", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _isNavigationHotspotWheelNavigating, 0);
        }
    }

    private bool CanShowPlayerNavigationHotspots()
    {
        return _controller is { Count: > 1 }
            && _player.EmptyStateVisibility != Visibility.Visible;
    }

    private void UpdatePlayerNavigationHotspotLayout()
    {
        var imageZoomFactor = _isImagePreviewControllerReady && _imagePreviewController != null
            ? _imagePreviewController.ZoomFactor
            : 1;
        var hotspotWidth = _library.SelectedMedia?.Type == MediaType.Image
            && imageZoomFactor > 1.01
            ? ZoomedImageNavigationHotspotWidth
            : PlayerEdgeNavigationRevealWidth;

        PreviousMediaHotspot.Width = hotspotWidth;
        NextMediaHotspot.Width = hotspotWidth;
    }

    private void RefreshPlayerNavigationHotspots()
    {
        UpdatePlayerNavigationHotspotLayout();

        var canShow = CanShowPlayerNavigationHotspots();
        var canShowSideActions = _controller is { Count: > 0 }
            && _player.EmptyStateVisibility != Visibility.Visible;
        PreviousMediaHotspot.Visibility = canShow ? Visibility.Visible : Visibility.Collapsed;
        NextMediaHotspot.Visibility = canShow ? Visibility.Visible : Visibility.Collapsed;
        PlayerPane.NavigationOverlay.SideActionButtons.Visibility = canShowSideActions
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!canShow)
        {
            _activePlayerNavigationEdge = PlayerNavigationEdge.None;
        }

        PreviousMediaCue.Opacity = canShow ? (_activePlayerNavigationEdge == PlayerNavigationEdge.Left ? 1 : 0.55) : 0;
        NextMediaCue.Opacity = canShow ? (_activePlayerNavigationEdge == PlayerNavigationEdge.Right ? 1 : 0.55) : 0;
    }

    private void UpdatePlayerNavigationCue(Windows.Foundation.Point pointerPosition)
    {
        if (!CanShowPlayerNavigationHotspots())
        {
            SetPlayerNavigationEdge(PlayerNavigationEdge.None);
            return;
        }

        var playerWidth = PlayerRoot.ActualWidth;
        if (playerWidth <= 0)
        {
            SetPlayerNavigationEdge(PlayerNavigationEdge.None);
            return;
        }

        if (pointerPosition.X <= PlayerEdgeNavigationRevealWidth)
        {
            SetPlayerNavigationEdge(PlayerNavigationEdge.Left);
        }
        else if (pointerPosition.X >= playerWidth - PlayerEdgeNavigationRevealWidth)
        {
            SetPlayerNavigationEdge(PlayerNavigationEdge.Right);
        }
        else
        {
            SetPlayerNavigationEdge(PlayerNavigationEdge.None);
        }
    }

    private void SetPlayerNavigationEdge(PlayerNavigationEdge edge)
    {
        _activePlayerNavigationEdge = edge;
        RefreshPlayerNavigationHotspots();
    }

    private PlayerNavigationEdge GetNavigationHotspotEdge(object sender)
    {
        return ReferenceEquals(sender, PreviousMediaHotspot)
            || ReferenceEquals(sender, PlayerPane.ShortcutHintContainer)
            ? PlayerNavigationEdge.Left
            : PlayerNavigationEdge.Right;
    }

    private void ResetNavigationHotspotGesture()
    {
        _isNavigationHotspotPressed = false;
        _isNavigationHotspotTapCanceled = false;
        _isNavigationHotspotDraggingImage = false;
        _pressedNavigationHotspotEdge = PlayerNavigationEdge.None;
    }

    private bool IsPlayerOverlayInteractionSource(DependencyObject? source)
    {
        while (source != null)
        {
            if (ReferenceEquals(source, PlayerPane.TransportBar.ControlBar)
                || ReferenceEquals(source, PlayerPane.TransportBar.VolumeButtonHost)
                || ReferenceEquals(source, PlayerPane.TransportBar.VolumeFlyoutContent)
                || ReferenceEquals(source, PlayerPane.TransportBar.VolumeSlider)
                || ReferenceEquals(source, PlayerPane.TransportBar.VolumeButton)
                || ReferenceEquals(source, PlayerPane.TransportBar.PlayPauseButton)
                || ReferenceEquals(source, PlayerPane.TransportBar.ProgressSlider)
                || ReferenceEquals(source, PlayerPane.NavigationOverlay.SideActionButtons)
                || ReferenceEquals(source, PlayerPane.ShortcutHintContainer)
                || ReferenceEquals(source, PlayerPane.NavigationOverlay.PlaybackModeButton)
                || ReferenceEquals(source, PlayerPane.NavigationOverlay.FullScreenButton)
                || ReferenceEquals(source, PlayerPane.NavigationOverlay.DeleteCurrentMediaButton)
                || ReferenceEquals(source, PreviousMediaHotspot)
                || ReferenceEquals(source, NextMediaHotspot)
                || ReferenceEquals(source, PreviousMediaCue)
                || ReferenceEquals(source, NextMediaCue))
            {
                return true;
            }

            source = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private async void PlayerPane_ShortcutHintInvoked(object? sender, int index)
    {
        try
        {
            await TryApplyShortcutTagAsync(index);
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException(
                "DockedMediaSession",
                $"Apply shortcut tag from hint failed. Index={index}, MediaId='{_library.SelectedMedia?.Id ?? "<null>"}'.",
                ex);
        }
    }

    private async void InfoOverlay_TagRemoveRequested(object? sender, string tag)
    {
        try
        {
            await RemoveCurrentPlayerTagAsync(tag);
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException(
                "DockedMediaSession",
                $"Remove player tag failed. Tag='{tag}', MediaId='{_library.SelectedMedia?.Id ?? "<null>"}'.",
                ex);
        }
    }

    private async void DeleteCurrentMediaButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await DeleteCurrentMediaAsync();
        }
        catch (Exception ex)
        {
            var media = _library.SelectedMedia;
            AppTraceLogger.LogException(
                "DockedMediaSession",
                $"Delete current media failed. MediaId='{media?.Id ?? "<null>"}', File='{media?.FileName ?? "<null>"}'.",
                ex);
            _player.PlayerFileName = $"删除失败：{media?.FileName ?? "当前媒体"}";
            _player.PlayerResolution = ex.Message;
        }
    }

    private async void PlayerPane_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        try
        {
            e.Handled = _shortcutHandler != null && await _shortcutHandler.HandleKeyAsync(e.Key);
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("DockedMediaSession", $"Key handling failed. Key={e.Key}.", ex);
        }
    }

    private void ToggleNumpadShortcutHint()
    {
        RefreshNumpadShortcutHint();
        _player.ShortcutHintVisibility = _player.ShortcutHintVisibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private async Task<bool> TryApplyNumpadTagShortcutAsync(VirtualKey key)
    {
        var digit = key switch
        {
            VirtualKey.NumberPad1 => 1,
            VirtualKey.NumberPad2 => 2,
            VirtualKey.NumberPad3 => 3,
            VirtualKey.NumberPad4 => 4,
            VirtualKey.NumberPad5 => 5,
            VirtualKey.NumberPad6 => 6,
            VirtualKey.NumberPad7 => 7,
            VirtualKey.NumberPad8 => 8,
            VirtualKey.NumberPad9 => 9,
            _ => 0
        };

        return digit != 0 && await TryApplyNumpadTagShortcutAsync(digit);
    }

    private async Task<bool> TryApplyNumpadTagShortcutAsync(int digit)
    {
        var currentMedia = _library.SelectedMedia;
        if (currentMedia == null)
        {
            return false;
        }

        var applied = await _library.TryApplyNumpadTagShortcutAsync(digit, new[] { currentMedia });
        if (applied)
        {
            RefreshPlayerTags(currentMedia);
            RefreshNumpadShortcutHint();
            RaiseMediaTagsChanged(currentMedia);
        }

        return applied;
    }

    private async Task<bool> TryApplyShortcutTagAsync(int index)
    {
        var currentMedia = _library.SelectedMedia;
        if (currentMedia == null)
        {
            return false;
        }

        var applied = await _library.TryApplyTagShortcutAsync(index, new[] { currentMedia });
        if (applied)
        {
            RefreshPlayerTags(currentMedia);
            RefreshNumpadShortcutHint();
            RaiseMediaTagsChanged(currentMedia);
        }

        return applied;
    }

    private async Task RemoveCurrentPlayerTagAsync(string tag)
    {
        var currentMedia = _library.SelectedMedia;
        if (currentMedia == null || string.IsNullOrWhiteSpace(tag))
        {
            return;
        }

        var nextTags = currentMedia.Tags
            .Where(existing => !string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (nextTags.Count == currentMedia.Tags.Count)
        {
            return;
        }

        await _library.UpdateTagsAsync(new[] { currentMedia }, nextTags, TagUpdateMode.Replace);
        RefreshPlayerTags(currentMedia);
        RefreshNumpadShortcutHint();
        RaiseMediaTagsChanged(currentMedia);
    }

    private async Task DeleteCurrentMediaAsync()
    {
        if (_isDeletingCurrentMedia)
        {
            AppTraceLogger.LogSampled(
                "DockedMediaSession",
                "delete-current-reentrant",
                $"Ignored reentrant delete request. MediaId='{_library.SelectedMedia?.Id ?? "<null>"}'.",
                TimeSpan.FromSeconds(2));
            return;
        }

        _isDeletingCurrentMedia = true;
        try
        {
            await DeleteCurrentMediaCoreAsync();
        }
        finally
        {
            _isDeletingCurrentMedia = false;
        }
    }

    private async Task DeleteCurrentMediaCoreAsync()
    {
        if (_controller == null)
        {
            return;
        }

        var media = _library.SelectedMedia;
        if (media == null)
        {
            return;
        }

        var deletePlan = _controller.BuildDeletePlan(media.Id);
        var replacement = deletePlan.Replacement;
        var deletedPath = media.FileSystemPath;
        AppTraceLogger.Log(
            "DockedMediaSession",
            $"DeleteCurrentMediaAsync start. MediaId='{media.Id}', File='{media.FileName}', Path='{deletedPath}', ReplacementId='{replacement?.Id ?? "<null>"}'.");

        var switchedToReplacement = replacement != null && _controller.TrySelect(replacement.Id);
        if (switchedToReplacement)
        {
            AppTraceLogger.Log(
                "DockedMediaSession",
                $"Selected replacement before file delete. CurrentMediaId='{media.Id}', ReplacementId='{replacement!.Id}'.");
            OpenCurrentMedia();
            FocusPlayer();
            await UiThreadYield.NextPassAsync();
        }
        else
        {
            _videoPlaybackController?.PauseAndClearSource();
            _imagePreviewController?.Clear();
        }

        try
        {
            await Task.Run(() =>
            {
                if (File.Exists(deletedPath))
                {
                    RecycleBinHelper.SendToRecycleBin(deletedPath);
                }
            });
        }
        catch
        {
            if (_controller.TrySelect(media.Id))
            {
                OpenCurrentMedia();
            }

            FocusPlayer();
            throw;
        }

        await _library.DeleteMediaAsync(new[] { media });
        _controller.RemoveDeletedMedia(deletePlan);
        MediaDeleted?.Invoke(this, new IndependentMediaWindowDeletedEventArgs(new[] { media.Id }));

        if (_controller.Count == 0)
        {
            CloseRequested?.Invoke();
            return;
        }

        if (!switchedToReplacement
            || !string.Equals(_controller.CurrentMedia.Id, replacement?.Id, StringComparison.Ordinal))
        {
            OpenCurrentMedia();
        }

        FocusPlayer();
    }

    private void DragHeader_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        var session = ExportSession();
        if (session == null)
        {
            return;
        }

        args.Data.RequestedOperation = DataPackageOperation.Move;
        args.Data.SetData(MediaPlayerSessionTransfer.DataFormat, session.Serialize());
    }

    private void DragHeader_DropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        if (args.DropResult != DataPackageOperation.None)
        {
            return;
        }

        var session = ExportSession();
        if (session != null)
        {
            DetachRequested?.Invoke(session);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke();
    }

    private void AttachPlayerEvents()
    {
        PlayerPane.ShortcutHintInvoked += PlayerPane_ShortcutHintInvoked;
        PlayerPane.InfoOverlay.TagRemoveRequested += InfoOverlay_TagRemoveRequested;
        PlayerPane.NavigationOverlay.DeleteCurrentMediaButton.Click += DeleteCurrentMediaButton_Click;
        PlayerPane.AddHandler(UIElement.KeyDownEvent, _playerPaneKeyDownHandler, true);
        PlayerPane.PlayerRoot.PointerMoved += PlayerRoot_PointerMoved;
        PlayerPane.PlayerRoot.AddHandler(UIElement.PointerPressedEvent, _playerRootPointerPressedHandler, true);
        PlayerPane.PlayerRoot.AddHandler(UIElement.RightTappedEvent, _playerRootRightTappedHandler, true);
        PlayerPane.PlayerRoot.PointerExited += PlayerRoot_PointerExited;
        PlayerPane.VideoViewport.VideoPlayer.AddHandler(UIElement.PointerPressedEvent, _videoPlayerPointerPressedHandler, true);
        PreviousMediaHotspot.PointerEntered += PreviousMediaHotspot_PointerEntered;
        PreviousMediaHotspot.PointerPressed += PlayerNavigationHotspot_PointerPressed;
        PreviousMediaHotspot.PointerMoved += PlayerNavigationHotspot_PointerMoved;
        PreviousMediaHotspot.PointerReleased += PlayerNavigationHotspot_PointerReleased;
        PreviousMediaHotspot.PointerCaptureLost += PlayerNavigationHotspot_PointerCaptureLost;
        PreviousMediaHotspot.AddHandler(UIElement.PointerWheelChangedEvent, _playerNavigationHotspotPointerWheelHandler, true);
        PlayerPane.ShortcutHintContainer.PointerEntered += ShortcutHintContainer_PointerEntered;
        PlayerPane.ShortcutHintContainer.AddHandler(UIElement.PointerWheelChangedEvent, _playerNavigationHotspotPointerWheelHandler, true);
        NextMediaHotspot.PointerEntered += NextMediaHotspot_PointerEntered;
        NextMediaHotspot.PointerPressed += PlayerNavigationHotspot_PointerPressed;
        NextMediaHotspot.PointerMoved += PlayerNavigationHotspot_PointerMoved;
        NextMediaHotspot.PointerReleased += PlayerNavigationHotspot_PointerReleased;
        NextMediaHotspot.PointerCaptureLost += PlayerNavigationHotspot_PointerCaptureLost;
        NextMediaHotspot.AddHandler(UIElement.PointerWheelChangedEvent, _playerNavigationHotspotPointerWheelHandler, true);
    }

    private void DetachPlayerEvents()
    {
        PlayerPane.ShortcutHintInvoked -= PlayerPane_ShortcutHintInvoked;
        PlayerPane.InfoOverlay.TagRemoveRequested -= InfoOverlay_TagRemoveRequested;
        PlayerPane.NavigationOverlay.DeleteCurrentMediaButton.Click -= DeleteCurrentMediaButton_Click;
        PlayerPane.RemoveHandler(UIElement.KeyDownEvent, _playerPaneKeyDownHandler);
        PlayerPane.PlayerRoot.PointerMoved -= PlayerRoot_PointerMoved;
        PlayerPane.PlayerRoot.RemoveHandler(UIElement.PointerPressedEvent, _playerRootPointerPressedHandler);
        PlayerPane.PlayerRoot.RemoveHandler(UIElement.RightTappedEvent, _playerRootRightTappedHandler);
        PlayerPane.PlayerRoot.PointerExited -= PlayerRoot_PointerExited;
        PlayerPane.VideoViewport.VideoPlayer.RemoveHandler(UIElement.PointerPressedEvent, _videoPlayerPointerPressedHandler);
        PreviousMediaHotspot.PointerEntered -= PreviousMediaHotspot_PointerEntered;
        PreviousMediaHotspot.PointerPressed -= PlayerNavigationHotspot_PointerPressed;
        PreviousMediaHotspot.PointerMoved -= PlayerNavigationHotspot_PointerMoved;
        PreviousMediaHotspot.PointerReleased -= PlayerNavigationHotspot_PointerReleased;
        PreviousMediaHotspot.PointerCaptureLost -= PlayerNavigationHotspot_PointerCaptureLost;
        PreviousMediaHotspot.RemoveHandler(UIElement.PointerWheelChangedEvent, _playerNavigationHotspotPointerWheelHandler);
        PlayerPane.ShortcutHintContainer.PointerEntered -= ShortcutHintContainer_PointerEntered;
        PlayerPane.ShortcutHintContainer.RemoveHandler(UIElement.PointerWheelChangedEvent, _playerNavigationHotspotPointerWheelHandler);
        NextMediaHotspot.PointerEntered -= NextMediaHotspot_PointerEntered;
        NextMediaHotspot.PointerPressed -= PlayerNavigationHotspot_PointerPressed;
        NextMediaHotspot.PointerMoved -= PlayerNavigationHotspot_PointerMoved;
        NextMediaHotspot.PointerReleased -= PlayerNavigationHotspot_PointerReleased;
        NextMediaHotspot.PointerCaptureLost -= PlayerNavigationHotspot_PointerCaptureLost;
        NextMediaHotspot.RemoveHandler(UIElement.PointerWheelChangedEvent, _playerNavigationHotspotPointerWheelHandler);
    }

    private void RaiseMediaTagsChanged(MediaItemViewModel media)
    {
        MediaTagsChanged?.Invoke(
            this,
            new IndependentMediaWindowTagsChangedEventArgs(media.Id, media.Tags.ToList()));
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _restoreCancellation.Cancel();
        _restoreCancellation.Dispose();
        DetachPlayerEvents();
        ResetSessionState();
        _library.Dispose();
    }

    private void CancelPendingRestore()
    {
        if (_restoreCancellation.IsCancellationRequested)
        {
            _restoreCancellation.Dispose();
            _restoreCancellation = new CancellationTokenSource();
            return;
        }

        _restoreCancellation.Cancel();
        _restoreCancellation.Dispose();
        _restoreCancellation = new CancellationTokenSource();
    }
}

internal sealed record DockedSessionContextMenuRequestedEventArgs(
    FrameworkElement Target,
    Windows.Foundation.Point Position,
    MediaItemViewModel Media,
    Action ToggleShortcutHint,
    string ShortcutHintMenuText,
    Action<double>? RotateVideo);
