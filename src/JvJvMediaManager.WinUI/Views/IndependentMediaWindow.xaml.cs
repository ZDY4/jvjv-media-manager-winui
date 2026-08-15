using System.Threading;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
using Windows.ApplicationModel.DataTransfer;
using JvJvMediaManager.Controllers.MainPage;
using JvJvMediaManager.Models;
using JvJvMediaManager.Services;
using JvJvMediaManager.Utilities;
using JvJvMediaManager.ViewModels;
using JvJvMediaManager.ViewModels.MainPage;
using WinRT.Interop;

namespace JvJvMediaManager.Views;

public sealed partial class IndependentMediaWindow : Window
{
    private const int InitialWindowWidth = 960;
    private const int InitialWindowHeight = 540;
    private const double PlayerEdgeNavigationRevealWidth = 96;
    private const double ZoomedImageNavigationHotspotWidth = 32;
    private const double NavigationGestureDragThreshold = 8;
    private static readonly UIntPtr KeyboardSubclassId = new(0x4A564A57);

    private enum PlayerNavigationEdge
    {
        None,
        Left,
        Right
    }

    private readonly IndependentMediaWindowController _controller;
    private readonly SelectionViewModel _selection = new();
    private readonly SettingsService _settings;
    private readonly LibraryShellViewModel _library;
    private readonly PlayerShellViewModel _player;
    private readonly VideoPlaybackController _videoPlaybackController;
    private readonly ImagePreviewController _imagePreviewController;
    private readonly WindowKeyboardInterceptor _keyboardInterceptor;
    private readonly IndependentMediaWindowShortcutHandler _shortcutHandler;
    private readonly KeyEventHandler _playerPaneKeyDownHandler;
    private readonly PointerEventHandler _playerRootPointerPressedHandler;
    private readonly PointerEventHandler _videoPlayerPointerPressedHandler;
    private readonly PointerEventHandler _playerNavigationHotspotPointerWheelHandler;

    private IntPtr _windowHandle;
    private bool _isImagePreviewControllerReady;
    private bool _isNavigationHotspotPressed;
    private bool _isNavigationHotspotTapCanceled;
    private bool _isNavigationHotspotDraggingImage;
    private bool _isDeletingCurrentMedia;
    private int _isNavigationHotspotWheelNavigating;
    private BossKeyController? _bossKeyController;
    private Windows.Foundation.Point _navigationHotspotPressPoint;
    private PlayerNavigationEdge _activePlayerNavigationEdge;
    private PlayerNavigationEdge _pressedNavigationHotspotEdge;
    private readonly double _initialPositionSeconds;
    private readonly bool _initialWasPlaying;

    internal IndependentMediaWindow(
        IReadOnlyList<IndependentMediaWindowMedia> mediaItems,
        int initialIndex,
        double initialPositionSeconds = 0,
        bool initialWasPlaying = true)
    {
        if (mediaItems.Count == 0)
        {
            throw new ArgumentException("At least one media item is required.", nameof(mediaItems));
        }

        _keyboardInterceptor = new WindowKeyboardInterceptor(KeyboardSubclassId, HandleKeyboardSubclassMessage, "IndependentMediaWindow");
        _playerPaneKeyDownHandler = PlayerPane_KeyDown;
        _playerRootPointerPressedHandler = PlayerRoot_PointerPressed;
        _videoPlayerPointerPressedHandler = VideoPlayer_PointerPressed;
        _playerNavigationHotspotPointerWheelHandler = PlayerNavigationHotspot_PointerWheelChanged;
        _controller = new IndependentMediaWindowController(mediaItems, initialIndex);
        _initialPositionSeconds = initialPositionSeconds;
        _initialWasPlaying = initialWasPlaying;
        var libraryServices = LibraryShellServices.CreateDefault();
        _settings = libraryServices.Settings;
        _library = new LibraryShellViewModel(
            _selection,
            libraryServices,
            persistMediaLibraryViewState: false,
            enableWatchedFolderMonitoring: false);
        _player = new PlayerShellViewModel(_selection);

        InitializeComponent();
        PlayerPane.DataContext = _player;
        PlayerPane.ClipBarView.Visibility = Visibility.Collapsed;
        PlayerPane.TransportBar.ClipModeToggleButton.Visibility = Visibility.Collapsed;
        PlayerPane.EmptyStateView.RootGrid.Visibility = Visibility.Collapsed;
        PlayerPane.IsTabStop = true;

        foreach (var media in _controller.MediaItems)
        {
            _library.FilteredMediaItems.Add(media);
        }

        _library.SelectedMedia = _controller.CurrentMedia;
        _videoPlaybackController = new VideoPlaybackController(
            _settings,
            _library,
            _player.VideoPlayback,
            PlayerPane.TransportBar,
            PlayerPane.NavigationOverlay,
            PlayerPane.VideoViewport,
            PlayerPane.VideoViewport.VideoPlayer,
            DispatcherQueue,
            GetAppWindow,
            NavigateRelativeAsync,
            CanAutoHideControls,
            FocusPlayer,
            RefreshPlayerNavigationHotspots,
            _ => { },
            () => { },
            libraryServices.PlaybackResources);
        _imagePreviewController = new ImagePreviewController(
            _library,
            _player.ImagePreview,
            PlayerPane.ImageViewport,
            PlayerPane.PlayerOverlay,
            RefreshPlayerNavigationHotspots);
        _isImagePreviewControllerReady = true;
        _bossKeyController = new BossKeyController(
            EnterBossMode,
            ExitBossMode,
            () => false);
        _shortcutHandler = new IndependentMediaWindowShortcutHandler(
            () => _library.SelectedMedia,
            Close,
            ToggleNumpadShortcutHint,
            TryApplyNumpadTagShortcutAsync,
            DeleteCurrentMediaAsync,
            NavigateRelativeAsync,
            _videoPlaybackController.TogglePlayPause,
            _videoPlaybackController.SeekRelative,
            _imagePreviewController.ZoomBy,
            _imagePreviewController.ResetZoom,
            _bossKeyController);

        Closed += IndependentMediaWindow_Closed;
        Activated += IndependentMediaWindow_Activated;
        PlayerPane.ShortcutHintInvoked += PlayerPane_ShortcutHintInvoked;
        PlayerPane.InfoOverlay.TagRemoveRequested += InfoOverlay_TagRemoveRequested;
        PlayerPane.NavigationOverlay.DeleteCurrentMediaButton.Click += DeleteCurrentMediaButton_Click;
        PlayerPane.AddHandler(UIElement.KeyDownEvent, _playerPaneKeyDownHandler, true);
        PlayerPane.PlayerRoot.PointerMoved += PlayerRoot_PointerMoved;
        PlayerPane.PlayerRoot.AddHandler(UIElement.PointerPressedEvent, _playerRootPointerPressedHandler, true);
        PlayerPane.PlayerRoot.PointerExited += PlayerRoot_PointerExited;
        PlayerPane.VideoViewport.VideoPlayer.AddHandler(UIElement.PointerPressedEvent, _videoPlayerPointerPressedHandler, true);
        PlayerPane.NavigationOverlay.PreviousMediaHotspot.PointerEntered += PreviousMediaHotspot_PointerEntered;
        PlayerPane.NavigationOverlay.PreviousMediaHotspot.PointerPressed += PlayerNavigationHotspot_PointerPressed;
        PlayerPane.NavigationOverlay.PreviousMediaHotspot.PointerMoved += PlayerNavigationHotspot_PointerMoved;
        PlayerPane.NavigationOverlay.PreviousMediaHotspot.PointerReleased += PlayerNavigationHotspot_PointerReleased;
        PlayerPane.NavigationOverlay.PreviousMediaHotspot.PointerCaptureLost += PlayerNavigationHotspot_PointerCaptureLost;
        PlayerPane.NavigationOverlay.PreviousMediaHotspot.AddHandler(UIElement.PointerWheelChangedEvent, _playerNavigationHotspotPointerWheelHandler, true);
        PlayerPane.ShortcutHintContainer.PointerEntered += ShortcutHintContainer_PointerEntered;
        PlayerPane.ShortcutHintContainer.AddHandler(UIElement.PointerWheelChangedEvent, _playerNavigationHotspotPointerWheelHandler, true);
        PlayerPane.NavigationOverlay.NextMediaHotspot.PointerEntered += NextMediaHotspot_PointerEntered;
        PlayerPane.NavigationOverlay.NextMediaHotspot.PointerPressed += PlayerNavigationHotspot_PointerPressed;
        PlayerPane.NavigationOverlay.NextMediaHotspot.PointerMoved += PlayerNavigationHotspot_PointerMoved;
        PlayerPane.NavigationOverlay.NextMediaHotspot.PointerReleased += PlayerNavigationHotspot_PointerReleased;
        PlayerPane.NavigationOverlay.NextMediaHotspot.PointerCaptureLost += PlayerNavigationHotspot_PointerCaptureLost;
        PlayerPane.NavigationOverlay.NextMediaHotspot.AddHandler(UIElement.PointerWheelChangedEvent, _playerNavigationHotspotPointerWheelHandler, true);

        ConfigureInitialWindowBounds();
        InstallKeyboardSubclass();
        OpenCurrentMedia();
        _ = RestoreInitialPlaybackStateAsync();
        NotifyPlaybackTargetActivated();
        FocusPlayer();
    }

    internal IndependentMediaWindow(MediaPlayerSessionTransfer session)
        : this(session.MediaItems, session.CurrentIndex, session.PositionSeconds, session.WasPlaying)
    {
    }

    internal event EventHandler? PlaybackTargetActivated;
    internal event EventHandler<IndependentMediaWindowTagsChangedEventArgs>? MediaTagsChanged;
    internal event EventHandler<IndependentMediaWindowDeletedEventArgs>? MediaDeleted;

    internal void PlayMedia(IndependentMediaWindowMedia media)
    {
        var beforeCount = _controller.Count;
        var played = _controller.PlayOrSelect(media);
        if (_library.FilteredMediaItems.All(item => !string.Equals(item.Id, played.Id, StringComparison.Ordinal)))
        {
            var insertIndex = Math.Min(_controller.CurrentIndex, _library.FilteredMediaItems.Count);
            _library.FilteredMediaItems.Insert(insertIndex, played);
        }

        AppTraceLogger.Log(
            "IndependentMediaWindow",
            $"PlayMedia. MediaId='{played.Id}', QueueCount={_controller.Count}, Added={_controller.Count > beforeCount}.");
        OpenCurrentMedia();
        NotifyPlaybackTargetActivated();
        FocusPlayer();
    }

    internal Task<bool> TryHandleShortcutAsync(VirtualKey key)
    {
        return _shortcutHandler.HandleKeyAsync(key);
    }

    private Grid PlayerRoot => PlayerPane.PlayerRoot;

    private Grid PlayerOverlay => PlayerPane.PlayerOverlay;

    private Border PreviousMediaHotspot => PlayerPane.NavigationOverlay.PreviousMediaHotspot;

    private Border NextMediaHotspot => PlayerPane.NavigationOverlay.NextMediaHotspot;

    private Border PreviousMediaCue => PlayerPane.NavigationOverlay.PreviousMediaCue;

    private Border NextMediaCue => PlayerPane.NavigationOverlay.NextMediaCue;

    private AppWindow? GetAppWindow()
    {
        try
        {
            var hWnd = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            return AppWindow.GetFromWindowId(windowId);
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("IndependentMediaWindow", "GetAppWindow failed.", ex);
            return null;
        }
    }

    private MediaPlayerSessionTransfer ExportSession()
    {
        var items = _controller.MediaItems
            .Select(item => new IndependentMediaWindowMedia(
                item.Id,
                item.FileSystemPath,
                item.FileName,
                item.Type,
                item.ResolutionText,
                item.ContentHash,
                item.FingerprintVersion,
                item.Tags.ToList()))
            .ToList();
        return new MediaPlayerSessionTransfer(
            items,
            _controller.CurrentIndex,
            _videoPlaybackController.GetCurrentPlaybackPosition().TotalSeconds,
            _videoPlaybackController.IsPlaying);
    }

    private async Task RestoreInitialPlaybackStateAsync()
    {
        if (_initialPositionSeconds <= 0 && _initialWasPlaying) return;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(100);
            if (_library.SelectedMedia?.Type != MediaType.Video) return;
            if (_videoPlaybackController.GetCurrentVideoDuration() <= TimeSpan.Zero) continue;
            if (_initialPositionSeconds > 0) _videoPlaybackController.SeekTo(TimeSpan.FromSeconds(_initialPositionSeconds));
            if (!_initialWasPlaying && _videoPlaybackController.IsPlaying) _videoPlaybackController.TogglePlayPause();
            return;
        }
    }

    private void SessionDragHeader_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        args.Data.RequestedOperation = DataPackageOperation.Move;
        args.Data.SetData(MediaPlayerSessionTransfer.DataFormat, ExportSession().Serialize());
    }

    private void SessionDragHeader_DropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        if (args.DropResult == DataPackageOperation.Move)
        {
            Close();
        }
    }

    private void ConfigureInitialWindowBounds()
    {
        try
        {
            var hWnd = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
            var workArea = displayArea.WorkArea;
            var width = Math.Min(InitialWindowWidth, Math.Max(640, workArea.Width));
            var height = Math.Min(InitialWindowHeight, Math.Max(360, workArea.Height));
            var x = workArea.X + Math.Max(0, (workArea.Width - width) / 2);
            var y = workArea.Y + Math.Max(0, (workArea.Height - height) / 2);

            appWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException(
                "IndependentMediaWindow",
                $"ConfigureInitialWindowBounds failed. MediaId='{_library.SelectedMedia?.Id ?? "<null>"}'.",
                ex);
        }
    }

    private void OpenCurrentMedia()
    {
        var media = _controller.CurrentMedia;
        _library.SelectedMedia = media;
        Title = string.IsNullOrWhiteSpace(media.FileName) ? "媒体播放器" : media.FileName;
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

    private void ShowMissingMediaState(MediaItemViewModel media)
    {
        _videoPlaybackController.Clear();
        _imagePreviewController.Clear();
        _player.PlayerFileName = $"文件不存在：{media.FileName}";
        _player.PlayerResolution = media.FileSystemPath;
        AppTraceLogger.Log(
            "IndependentMediaWindow",
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
        var media = _controller.NavigateRelative(offset);
        if (media == null)
        {
            return Task.CompletedTask;
        }

        AppTraceLogger.Log(
            "IndependentMediaWindow",
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

    private void NotifyPlaybackTargetActivated()
    {
        PlaybackTargetActivated?.Invoke(this, EventArgs.Empty);
    }

    private void IndependentMediaWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            return;
        }

        NotifyPlaybackTargetActivated();
    }

    private void ShowControls()
    {
        _videoPlaybackController.ShowControls();
    }

    private void PlayerRoot_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pointerPosition = e.GetCurrentPoint(PlayerRoot).Position;
        _videoPlaybackController.HandlePlayerPointerMoved(pointerPosition.Y, PlayerRoot.ActualHeight);
        UpdatePlayerNavigationCue(pointerPosition);
    }

    private void PlayerRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pointerPosition = e.GetCurrentPoint(PlayerRoot).Position;
        UpdatePlayerNavigationCue(pointerPosition);
        NotifyPlaybackTargetActivated();
        var isOverlayInteraction = IsPlayerOverlayInteractionSource(e.OriginalSource as DependencyObject);

        if (!isOverlayInteraction)
        {
            FocusPlayer();
        }
    }

    private void VideoPlayer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        NotifyPlaybackTargetActivated();
        FocusPlayer();
    }

    private void PlayerRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        SetPlayerNavigationEdge(PlayerNavigationEdge.None);
        _videoPlaybackController.HandlePointerExited();
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
        if (!_isNavigationHotspotPressed || sender is not UIElement hotspot)
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
            _imagePreviewController.EndExternalDrag();
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
            AppTraceLogger.LogException("IndependentMediaWindow", "Player navigation hotspot release failed.", ex);
        }
    }

    private void PlayerNavigationHotspot_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        ResetNavigationHotspotGesture();
        _imagePreviewController.EndExternalDrag();
    }

    private async void PlayerNavigationHotspot_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            if (!CanShowPlayerNavigationHotspots() || sender is not UIElement hotspot)
            {
                return;
            }

            var point = e.GetCurrentPoint(hotspot);
            if (point.Properties.MouseWheelDelta == 0)
            {
                return;
            }

            e.Handled = true;
            if (Interlocked.Exchange(ref _isNavigationHotspotWheelNavigating, 1) == 1)
            {
                return;
            }

            var offset = point.Properties.MouseWheelDelta < 0 ? 1 : -1;
            var edge = offset < 0 ? PlayerNavigationEdge.Left : PlayerNavigationEdge.Right;
            await NavigateRelativeAsync(offset);
            ShowControls();
            SetPlayerNavigationEdge(edge);
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("IndependentMediaWindow", "Player navigation hotspot wheel navigation failed.", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _isNavigationHotspotWheelNavigating, 0);
        }
    }

    private bool CanShowPlayerNavigationHotspots()
    {
        return _controller.Count > 1
            && _player.EmptyStateVisibility != Visibility.Visible;
    }

    private void UpdatePlayerNavigationHotspotLayout()
    {
        var imageZoomFactor = _isImagePreviewControllerReady
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
        var canShowSideActions = _controller.Count > 0
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

        PreviousMediaCue.Opacity = canShow && _activePlayerNavigationEdge == PlayerNavigationEdge.Left ? 1 : 0;
        NextMediaCue.Opacity = canShow && _activePlayerNavigationEdge == PlayerNavigationEdge.Right ? 1 : 0;
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
                "IndependentMediaWindow",
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
                "IndependentMediaWindow",
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
                "IndependentMediaWindow",
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
            e.Handled = await _shortcutHandler.HandleKeyAsync(e.Key);
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("IndependentMediaWindow", $"Key handling failed. Key={e.Key}.", ex);
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
            AppTraceLogger.Log(
                "IndependentMediaWindow",
                $"Shortcut tag applied. Digit={digit}, MediaId='{currentMedia.Id}', File='{currentMedia.FileName}'.");
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
                "IndependentMediaWindow",
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
        var media = _library.SelectedMedia;
        if (media == null)
        {
            return;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var deletePlan = _controller.BuildDeletePlan(media.Id);
        var replacement = deletePlan.Replacement;
        AppTraceLogger.Log(
            "IndependentMediaWindow",
            $"DeleteCurrentMediaAsync start. MediaId='{media.Id}', File='{media.FileName}', Path='{media.FileSystemPath}', ReplacementId='{replacement?.Id ?? "<null>"}'.");

        _videoPlaybackController.PauseAndClearSource();
        _imagePreviewController.Clear();

        try
        {
            await Task.Run(() =>
            {
                if (File.Exists(media.FileSystemPath))
                {
                    RecycleBinHelper.SendToRecycleBin(media.FileSystemPath);
                }

            });
        }
        catch
        {
            OpenCurrentMedia();
            FocusPlayer();
            throw;
        }

        await _library.DeleteMediaAsync(new[] { media });
        _controller.RemoveDeletedMedia(deletePlan);

        MediaDeleted?.Invoke(this, new IndependentMediaWindowDeletedEventArgs(new[] { media.Id }));
        if (_controller.Count == 0)
        {
            AppTraceLogger.Log("IndependentMediaWindow", $"DeleteCurrentMediaAsync closing empty window. DeletedId='{media.Id}'.");
            Close();
            return;
        }

        OpenCurrentMedia();
        FocusPlayer();
        AppTraceLogger.Log(
            "IndependentMediaWindow",
            $"DeleteCurrentMediaAsync completed. DeletedId='{media.Id}', NextId='{_controller.CurrentMedia.Id}', Remaining={_controller.Count}, TotalElapsedMs={stopwatch.ElapsedMilliseconds}.");
    }

    private void IndependentMediaWindow_Closed(object sender, WindowEventArgs args)
    {
        UninstallKeyboardSubclass();
        Closed -= IndependentMediaWindow_Closed;
        Activated -= IndependentMediaWindow_Activated;
        PlayerPane.ShortcutHintInvoked -= PlayerPane_ShortcutHintInvoked;
        PlayerPane.InfoOverlay.TagRemoveRequested -= InfoOverlay_TagRemoveRequested;
        PlayerPane.NavigationOverlay.DeleteCurrentMediaButton.Click -= DeleteCurrentMediaButton_Click;
        PlayerPane.RemoveHandler(UIElement.KeyDownEvent, _playerPaneKeyDownHandler);
        PlayerPane.PlayerRoot.PointerMoved -= PlayerRoot_PointerMoved;
        PlayerPane.PlayerRoot.RemoveHandler(UIElement.PointerPressedEvent, _playerRootPointerPressedHandler);
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
        _videoPlaybackController.Dispose();
        _imagePreviewController.Dispose();
        _library.Dispose();
    }

    private void RaiseMediaTagsChanged(MediaItemViewModel media)
    {
        MediaTagsChanged?.Invoke(
            this,
            new IndependentMediaWindowTagsChangedEventArgs(media.Id, media.Tags.ToList()));
    }

    private void InstallKeyboardSubclass()
    {
        try
        {
            _windowHandle = WindowNative.GetWindowHandle(this);
            _keyboardInterceptor.Install(_windowHandle);
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("IndependentMediaWindow", "InstallKeyboardSubclass failed.", ex);
        }
    }

    private void UninstallKeyboardSubclass()
    {
        _keyboardInterceptor.Uninstall();
        _windowHandle = IntPtr.Zero;
    }

    internal void MinimizeForBossMode()
    {
        _bossKeyController?.HandleBossKeyTrigger();
    }

    private void EnterBossMode()
    {
        _videoPlaybackController.Clear();
        _imagePreviewController.Clear();
        _player.EmptyStateVisibility = Visibility.Visible;
        _player.PlayerInfoVisibility = Visibility.Collapsed;
        BossKeyRecoveryButton.Visibility = Visibility.Visible;

        if (GetAppWindow()?.Presenter is OverlappedPresenter presenter)
        {
            presenter.Minimize();
        }
    }

    private void ExitBossMode()
    {
        BossKeyRecoveryButton.Visibility = Visibility.Collapsed;
    }

    private void BossKeyRecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        _bossKeyController?.ExitBossMode();
    }

    private bool HandleKeyboardSubclassMessage(
        IntPtr hWnd,
        uint msg,
        UIntPtr wParam,
        IntPtr lParam)
    {
        if ((msg == WindowKeyboardInterceptor.WmKeyDown || msg == WindowKeyboardInterceptor.WmSysKeyDown)
            && (int)wParam == (int)VirtualKey.Escape)
        {
            AppTraceLogger.Log(
                "IndependentMediaWindow",
                $"Escape received by window subclass. MediaId='{_library.SelectedMedia?.Id ?? "<null>"}'.");
            _ = DispatcherQueue.TryEnqueue(Close);
            return true;
        }

        return false;
    }
}

internal sealed record IndependentMediaWindowTagsChangedEventArgs(
    string MediaId,
    IReadOnlyList<string> Tags);

internal sealed record IndependentMediaWindowDeletedEventArgs(
    IReadOnlyList<string> MediaIds);
