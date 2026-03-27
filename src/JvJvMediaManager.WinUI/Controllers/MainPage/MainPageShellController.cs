using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using JvJvMediaManager.Coordinators.MainPage;
using JvJvMediaManager.Models;
using JvJvMediaManager.Services;
using JvJvMediaManager.Services.MainPage;
using JvJvMediaManager.Utilities;
using JvJvMediaManager.ViewModels;
using JvJvMediaManager.ViewModels.MainPage;
using JvJvMediaManager.Views.MainPageParts;
using WinRT.Interop;

namespace JvJvMediaManager.Controllers.MainPage;

public sealed class MainPageShellController
{
    private const double MinLibraryPaneWidth = 240;
    private const double MaxLibraryPaneWidth = 640;
    private const double GridViewWidthPadding = 24;
    private const double PlayerEdgeNavigationRevealWidth = 96;
    private const double ZoomedImageNavigationHotspotWidth = 32;
    private const double NavigationGestureDragThreshold = 8;

    private enum PlayerNavigationEdge
    {
        None,
        Left,
        Right
    }

    private readonly Views.MainPage _page;
    private readonly MainPageShellViewModel _shell;
    private readonly MainPageModuleFactory _modules;
    private readonly IContentDialogService _dialogService;
    private readonly DialogWorkflowCoordinator _dialogCoordinator;
    private readonly LibraryPaneController _libraryPaneController;
    private readonly PlaylistRailCoordinator _playlistRailCoordinator;
    private readonly MediaContextMenuCoordinator _mediaContextMenuCoordinator;
    private readonly MediaBrowserController _mediaBrowserController;
    private readonly ClipEditorController _clipEditorController;
    private readonly VideoPlaybackController _videoPlaybackController;
    private readonly ImagePreviewController _imagePreviewController;

    private AppWindow? _appWindow;

    private bool _isNavigationHotspotPressed;
    private bool _isNavigationHotspotTapCanceled;
    private bool _isNavigationHotspotDraggingImage;
    private Windows.Foundation.Point _navigationHotspotPressPoint;
    private PlayerNavigationEdge _activePlayerNavigationEdge;
    private PlayerNavigationEdge _pressedNavigationHotspotEdge;

    private Grid RootLayout => _page.RootLayout;
    private SplitView LibrarySplitView => _page.LibrarySplitView;
    private Grid LibraryPaneRoot => _libraryPane.PaneRoot;
    private Grid LibraryPaneExpandedContent => _libraryPane.ExpandedContent;
    private Border LibraryDropTarget => _libraryPane.DropTargetBorder;
    private Thumb LibraryPaneResizer => _libraryPane.PaneResizer;
    private Grid PlayerRoot => _playerPane.PlayerRoot;
    private Grid PlayerOverlay => _playerPane.PlayerOverlay;
    private Grid EmptyState => _playerPane.EmptyStateView.RootGrid;
    private MediaPlayerElement VideoPlayer => _playerPane.VideoViewport.VideoPlayer;
    private Grid ImageScrollViewer => _playerPane.ImageViewport.ImageScrollViewer;
    private Border PlayerInfoBadge => _playerPane.InfoOverlay.PlayerInfoBadge;
    private TextBlock PlayerFileNameText => _playerPane.InfoOverlay.FileNameText;
    private TextBlock PlayerResolutionText => _playerPane.InfoOverlay.ResolutionText;
    private Border PreviousMediaHotspot => _playerPane.NavigationOverlay.PreviousMediaHotspot;
    private Border NextMediaHotspot => _playerPane.NavigationOverlay.NextMediaHotspot;
    private Border PreviousMediaCue => _playerPane.NavigationOverlay.PreviousMediaCue;
    private Border NextMediaCue => _playerPane.NavigationOverlay.NextMediaCue;
    private Button ClipModeToggleButton => _playerPane.TransportBar.ClipModeToggleButton;
    private Button SetClipStartButton => _playerPane.ClipBarView.SetClipStartButton;
    private Button SetClipEndButton => _playerPane.ClipBarView.SetClipEndButton;
    private Button SplitClipButton => _playerPane.ClipBarView.SplitClipButton;
    private Button ClipPlanButton => _playerPane.ClipBarView.ClipPlanButton;
    private Button ClearClipButton => _playerPane.ClipBarView.ClearClipButton;
    private Button ExportClipButton => _playerPane.ClipBarView.ExportClipButton;
    private LibraryPaneView _libraryPane;
    private PlayerPaneView _playerPane;

    public MainPageShellController(
        Views.MainPage page,
        MainPageShellViewModel shell,
        LibraryPaneView libraryPane,
        PlayerPaneView playerPane,
        IContentDialogService dialogService,
        MainPageModuleFactory modules)
    {
        _page = page;
        _shell = shell;
        _modules = modules;
        _libraryPane = libraryPane;
        _playerPane = playerPane;
        _dialogService = dialogService;
        _dialogCoordinator = _modules.CreateDialogWorkflowCoordinator(shell.Library, dialogService);
        _libraryPaneController = _modules.CreateLibraryPaneController(page, shell.Library, libraryPane);
        _mediaContextMenuCoordinator = _modules.CreateMediaContextMenuCoordinator(
            shell.Library,
            ApplyTagEditorAsync,
            AddSelectionToPlaylistAsync,
            DeleteSelectionAsync);
        _playlistRailCoordinator = _modules.CreatePlaylistRailCoordinator(
            shell.Library,
            libraryPane.PlaylistRail,
            libraryPane.HeaderView,
            _libraryPaneController,
            dialogService,
            ShowInfoDialogAsync,
            ConfirmAsync);
        _mediaBrowserController = _modules.CreateMediaBrowserController(
            page,
            shell.Library,
            libraryPane,
            _mediaContextMenuCoordinator,
            (paths, refreshMedia) => UpdateWatchedFolders(paths, refreshMedia),
            ShowInfoDialogAsync);
        _videoPlaybackController = _modules.CreateVideoPlaybackController(
            shell.Library,
            shell.Player.VideoPlayback,
            playerPane.TransportBar,
            playerPane.VideoViewport,
            playerPane.VideoViewport.VideoPlayer,
            _page.DispatcherQueue,
            GetAppWindow,
            NavigateRelativeAsync,
            () => ViewModel.SelectedMedia?.Type == MediaType.Video && _shell.Player.EmptyStateVisibility != Visibility.Visible,
            () => _page.Focus(FocusState.Programmatic),
            RefreshPlayerNavigationHotspots,
            duration => _clipEditorController!.HandleMediaOpened(duration),
            () => _clipEditorController!.Refresh());
        _clipEditorController = _modules.CreateClipEditorController(
            shell.Library,
            shell.Player.ClipEditor,
            ClipModeToggleButton,
            playerPane.ClipBarView,
            _dialogCoordinator,
            _videoPlaybackController.GetCurrentPlaybackPosition,
            _videoPlaybackController.GetCurrentVideoDuration,
            _videoPlaybackController.SeekTo,
            _videoPlaybackController.TogglePlayPause,
            () => _videoPlaybackController.IsPlaying,
            _videoPlaybackController.SetTransportSuppressed,
            paths => UpdateWatchedFolders(paths),
            ShowControls);
        _imagePreviewController = _modules.CreateImagePreviewController(
            shell.Library,
            shell.Player.ImagePreview,
            playerPane.ImageViewport,
            playerPane.PlayerOverlay,
            RefreshPlayerNavigationHotspots);
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.SetDispatcher(_page.DispatcherQueue);
        PlayerRoot.PointerMoved += PlayerRoot_PointerMoved;
        PlayerRoot.PointerPressed += PlayerRoot_PointerPressed;
        PlayerRoot.PointerExited += PlayerRoot_PointerExited;
        PlayerRoot.AddHandler(UIElement.RightTappedEvent, new RightTappedEventHandler(PlayerRoot_RightTapped), true);
        PreviousMediaHotspot.PointerEntered += PreviousMediaHotspot_PointerEntered;
        PreviousMediaHotspot.PointerPressed += PlayerNavigationHotspot_PointerPressed;
        PreviousMediaHotspot.PointerMoved += PlayerNavigationHotspot_PointerMoved;
        PreviousMediaHotspot.PointerReleased += PlayerNavigationHotspot_PointerReleased;
        PreviousMediaHotspot.PointerCaptureLost += PlayerNavigationHotspot_PointerCaptureLost;
        NextMediaHotspot.PointerEntered += NextMediaHotspot_PointerEntered;
        NextMediaHotspot.PointerPressed += PlayerNavigationHotspot_PointerPressed;
        NextMediaHotspot.PointerMoved += PlayerNavigationHotspot_PointerMoved;
        NextMediaHotspot.PointerReleased += PlayerNavigationHotspot_PointerReleased;
        NextMediaHotspot.PointerCaptureLost += PlayerNavigationHotspot_PointerCaptureLost;
        ClipModeToggleButton.Click += ToggleClipMode_Click;
        SetClipStartButton.Click += SetClipStart_Click;
        SetClipEndButton.Click += SetClipEnd_Click;
        SplitClipButton.Click += SplitClip_Click;
        ClipPlanButton.Click += ClipPlan_Click;
        ClearClipButton.Click += ClearClip_Click;
        ExportClipButton.Click += ExportClip_Click;
        _shell.Player.EmptyStateVisibility = Visibility.Visible;
        _shell.Player.PlayerInfoVisibility = Visibility.Collapsed;
        _shell.Player.ImagePreview.ZoomBadgeVisibility = Visibility.Collapsed;
        _videoPlaybackController.Clear();
        _clipEditorController.Refresh();
    }

    public LibraryShellViewModel ViewModel => _shell.Library;

    public async Task InitializeAsync()
    {
        await ExecuteUiActionAsync(async () =>
        {
            await _mediaBrowserController.InitializeAsync();
            _libraryPaneController.EnsurePaneState(preferOpen: true);
        }, "初始化失败");
    }

    public void Dispose()
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _mediaBrowserController.Dispose();
        _playlistRailCoordinator.Dispose();
        _libraryPaneController.Dispose();
        _videoPlaybackController.Dispose();
        _clipEditorController.Dispose();
        _imagePreviewController.Dispose();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryShellViewModel.SelectedMedia))
        {
            _page.DispatcherQueue.TryEnqueue(SyncSelectionFromViewModel);
        }
    }

    public Task HandleAddFolderFromTitleBarAsync()
    {
        _libraryPaneController.ActivateMediaLibrary(openPane: true);
        return _mediaBrowserController.AddFolderAsync();
    }

    public Task HandleAddFilesFromTitleBarAsync()
    {
        _libraryPaneController.ActivateMediaLibrary(openPane: true);
        return _mediaBrowserController.AddFilesAsync();
    }

    public Task HandleOpenSettingsFromTitleBarAsync()
    {
        return ApplySettingsAsync();
    }

    private Task AddFolderAsync() => _mediaBrowserController.AddFolderAsync();

    private void PlayerRoot_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (ViewModel.SelectedMedia == null)
        {
            return;
        }

        var selected = _mediaBrowserController.GetSelectedItems().ToList();
        if (selected.Count == 0)
        {
            selected.Add(ViewModel.SelectedMedia);
        }

        selected = selected
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        if (selected.Count == 0)
        {
            return;
        }

        ShowControls();
        _mediaContextMenuCoordinator.ShowForTarget(PlayerRoot, e.GetPosition(PlayerRoot), selected);
        e.Handled = true;
    }

    private void SelectMedia(MediaItemViewModel media)
    {
        ViewModel.SelectedMedia = media;
    }

    private void UpdatePlayer(MediaItemViewModel media)
    {
        _shell.Player.EmptyStateVisibility = Visibility.Collapsed;
        _shell.Player.PlayerInfoVisibility = Visibility.Visible;
        _shell.Player.PlayerFileName = media.FileName;
        _shell.Player.PlayerResolution = media.ResolutionText;
        _clipEditorController.HandleMediaChanged(media);

        if (media.Type == MediaType.Video)
        {
            _shell.Player.ImagePreview.ZoomBadgeVisibility = Visibility.Collapsed;
            _imagePreviewController.Clear();
            _videoPlaybackController.ShowVideo(media);
        }
        else
        {
            _shell.Player.ImagePreview.ZoomBadgeVisibility = Visibility.Visible;
            _videoPlaybackController.ShowImageState();
            _imagePreviewController.ShowImage(media);
        }
    }

    private void ClearPlayerSelection()
    {
        _shell.Player.EmptyStateVisibility = Visibility.Visible;
        _shell.Player.PlayerInfoVisibility = Visibility.Collapsed;
        _shell.Player.PlayerFileName = string.Empty;
        _shell.Player.PlayerResolution = string.Empty;
        _shell.Player.ImagePreview.ZoomBadgeVisibility = Visibility.Collapsed;
        _imagePreviewController.Clear();
        _clipEditorController.HandleMediaChanged(null);
        _videoPlaybackController.Clear();
        _libraryPaneController.EnsurePaneState(preferOpen: true);
    }

    private void SyncSelectionFromViewModel()
    {
        var selected = ViewModel.SelectedMedia;
        _mediaBrowserController.SyncSelectionFromViewModel(selected);

        if (selected == null)
        {
            ClearPlayerSelection();
            return;
        }

        UpdatePlayer(selected);
        _mediaBrowserController.RevealSelectedMedia(selected);
    }

    public async Task HandleKeyDownAsync(KeyRoutedEventArgs e)
    {
        var ctrlDown = (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        var shiftDown = (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

        if (ctrlDown)
        {
            if (shiftDown && e.Key == Windows.System.VirtualKey.O)
            {
                e.Handled = TryOpenSelectedMediaFolderFromShortcut();
            }
            else if (shiftDown && e.Key == Windows.System.VirtualKey.A)
            {
                e.Handled = await TryAddSelectionToPlaylistFromShortcutAsync();
            }
            else if (shiftDown && e.Key == Windows.System.VirtualKey.R)
            {
                e.Handled = await TryRemoveSelectionFromPlaylistFromShortcutAsync();
            }
            else if (e.Key == Windows.System.VirtualKey.O)
            {
                await AddFolderAsync();
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.F)
            {
                _libraryPaneController.SetPaneOpen(true);
                _mediaBrowserController.FocusSearchBox();
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.T)
            {
                await EditSelectedTagsAsync();
                e.Handled = true;
            }
        }

        if (e.Handled)
        {
            return;
        }

        if (IsTextInputFocused())
        {
            return;
        }

        if (ViewModel.SelectedMedia == null)
        {
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Left && ViewModel.SelectedMedia.Type == MediaType.Video)
        {
            SeekRelative(-5);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Right && ViewModel.SelectedMedia.Type == MediaType.Video)
        {
            SeekRelative(5);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.PageUp)
        {
            await NavigateRelativeAsync(-1);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.PageDown)
        {
            await NavigateRelativeAsync(1);
            e.Handled = true;
        }
        else if (ViewModel.SelectedMedia.Type == MediaType.Video && _clipEditorController.IsClipModeActive && e.Key == Windows.System.VirtualKey.E)
        {
            await _clipEditorController.ExportCurrentClipAsync();
            e.Handled = true;
        }
        else if (ViewModel.SelectedMedia.Type == MediaType.Video && _clipEditorController.IsClipModeActive && e.Key == Windows.System.VirtualKey.K)
        {
            _clipEditorController.SplitSegmentAtCurrentPosition();
            e.Handled = true;
        }
        else if (ViewModel.SelectedMedia.Type == MediaType.Image)
        {
            if (e.Key == Windows.System.VirtualKey.Add || e.Key == (Windows.System.VirtualKey)187)
            {
                _imagePreviewController.ZoomBy(0.1);
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Subtract || e.Key == (Windows.System.VirtualKey)189)
            {
                _imagePreviewController.ZoomBy(-0.1);
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Number0)
            {
                _imagePreviewController.ResetZoom();
                e.Handled = true;
            }
        }

        if (e.Handled)
        {
            ShowControls();
        }
    }

    public bool HandlePlayPauseAccelerator()
    {
        if (!TryTogglePlaybackFromShortcut())
        {
            return false;
        }

        return true;
    }

    public Task<bool> HandleDeleteAcceleratorAsync()
    {
        return TryDeleteSelectedFromShortcutAsync();
    }

    private bool TryTogglePlaybackFromShortcut()
    {
        if (IsTextInputFocused())
        {
            return false;
        }

        if (ViewModel.SelectedMedia?.Type != MediaType.Video)
        {
            return false;
        }

        TogglePlayPause();
        return true;
    }

    private async Task<bool> TryDeleteSelectedFromShortcutAsync()
    {
        if (IsTextInputFocused())
        {
            return false;
        }

        if (ViewModel.SelectedMedia == null)
        {
            return false;
        }

        if (ViewModel.SelectedMedia.Type == MediaType.Video && _clipEditorController.IsClipModeActive)
        {
            return _clipEditorController.DeleteSelectedSegment();
        }

        await DeleteSelectedAsync();
        return true;
    }

    private bool TryOpenSelectedMediaFolderFromShortcut()
    {
        if (IsTextInputFocused())
        {
            return false;
        }

        var media = GetPrimarySelectedMedia();
        if (media == null)
        {
            return false;
        }

        MediaContextMenuCoordinator.OpenMediaFolder(media);
        return true;
    }

    private async Task<bool> TryAddSelectionToPlaylistFromShortcutAsync()
    {
        if (IsTextInputFocused())
        {
            return false;
        }

        var selected = GetCommandSelection();
        if (selected.Count == 0 || ViewModel.Playlists.Count == 0)
        {
            return false;
        }

        await AddSelectionToPlaylistAsync(selected);
        return true;
    }

    private async Task<bool> TryRemoveSelectionFromPlaylistFromShortcutAsync()
    {
        if (IsTextInputFocused())
        {
            return false;
        }

        if (ViewModel.SelectedPlaylist == null)
        {
            return false;
        }

        var selected = GetCommandSelection();
        if (selected.Count == 0)
        {
            return false;
        }

        await ViewModel.RemoveMediaFromSelectedPlaylistAsync(selected);
        return true;
    }

    private bool IsTextInputFocused()
    {
        return FocusManager.GetFocusedElement(_page.XamlRoot) is TextBox or PasswordBox or RichEditBox or AutoSuggestBox or ComboBox;
    }

    private void TogglePlayPause()
    {
        _videoPlaybackController.TogglePlayPause();
    }

    private void ToggleClipMode_Click(object sender, RoutedEventArgs e)
    {
        _clipEditorController.ToggleClipMode();
    }

    private void PlayerRoot_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ShowControls();
        UpdatePlayerNavigationCue(e.GetCurrentPoint(PlayerRoot).Position);
    }

    private void PlayerRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pointerPosition = e.GetCurrentPoint(PlayerRoot).Position;
        UpdatePlayerNavigationCue(pointerPosition);

        if (ViewModel.SelectedMedia?.Type == MediaType.Video
            && _videoPlaybackController.AreControlsVisible
            && !IsPlayerOverlayInteractionSource(e.OriginalSource as DependencyObject))
        {
            _videoPlaybackController.HideControlsImmediately();
        }
        else
        {
            ShowControls();
        }

        _page.Focus(FocusState.Programmatic);
    }

    private void PlayerRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        SetPlayerNavigationEdge(PlayerNavigationEdge.None);
        _videoPlaybackController.HandlePointerExited();
    }

    private void SeekRelative(double seconds)
    {
        _videoPlaybackController.SeekRelative(seconds);
    }

    private async Task NavigateRelativeAsync(int offset)
    {
        if (_clipEditorController.IsClipModeActive)
        {
            return;
        }

        var list = ViewModel.FilteredMediaItems;
        if (list.Count == 0 || ViewModel.SelectedMedia == null)
        {
            return;
        }

        var index = list.IndexOf(ViewModel.SelectedMedia);
        if (index < 0)
        {
            return;
        }

        var nextIndex = index + offset;
        if (offset > 0)
        {
            await ViewModel.EnsureMediaItemLoadedAsync(nextIndex);
        }

        if (list.Count == 0)
        {
            return;
        }

        nextIndex = ((nextIndex % list.Count) + list.Count) % list.Count;
        ViewModel.SelectedMedia = list[nextIndex];
    }

    private void PreviousMediaHotspot_PointerEntered(object sender, PointerRoutedEventArgs e)
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

    private void PlayerNavigationHotspot_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        ResetNavigationHotspotGesture();
        _imagePreviewController.EndExternalDrag();
    }

    private void UpdateWatchedFolders(IEnumerable<string> paths, bool refreshMedia = true)
    {
        var folderPaths = paths
            .Select(path => Directory.Exists(path) ? path : Path.GetDirectoryName(path))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => path!)
            .ToList();

        if (folderPaths.Count == 0)
        {
            return;
        }

        var current = ViewModel.WatchedFolders.ToList();
        foreach (var folder in folderPaths)
        {
            if (current.All(item => !string.Equals(item.Path, folder, StringComparison.OrdinalIgnoreCase)))
            {
                current.Add(new WatchedFolder { Path = folder, Locked = false });
            }
        }

        ViewModel.UpdateWatchedFolders(current, refreshMedia);
    }

    private void SetClipStart_Click(object sender, RoutedEventArgs e)
    {
        _clipEditorController.SetClipStartToCurrent();
    }

    private void SetClipEnd_Click(object sender, RoutedEventArgs e)
    {
        _clipEditorController.SetClipEndToCurrent();
    }

    private void ClearClip_Click(object sender, RoutedEventArgs e)
    {
        _clipEditorController.Clear();
    }

    private async void ExportClip_Click(object sender, RoutedEventArgs e)
    {
        await _clipEditorController.ExportCurrentClipAsync();
    }

    private async void ClipPlan_Click(object sender, RoutedEventArgs e)
    {
        await _clipEditorController.ShowClipPlanDialogAsync();
    }

    private void SplitClip_Click(object sender, RoutedEventArgs e)
    {
        _clipEditorController.SplitSegmentAtCurrentPosition();
    }

    private Task DeleteSelectedAsync()
    {
        return DeleteSelectionAsync(_mediaBrowserController.GetSelectedItems());
    }

    private async Task DeleteSelectionAsync(IReadOnlyList<MediaItemViewModel> initialSelection)
    {
        var selected = initialSelection.ToList();
        if (selected.Count == 0 && ViewModel.SelectedMedia != null)
        {
            selected.Add(ViewModel.SelectedMedia);
        }

        selected = selected
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        if (selected.Count == 0)
        {
            return;
        }

        var currentMediaId = ViewModel.SelectedMedia?.Id;
        var isDeletingCurrent = !string.IsNullOrWhiteSpace(currentMediaId)
            && selected.Any(item => string.Equals(item.Id, currentMediaId, StringComparison.Ordinal));

        if (isDeletingCurrent)
        {
            ReleasePreviewHandles();
        }

        var deleted = new List<MediaItemViewModel>();
        var failed = new List<string>();

        foreach (var media in selected)
        {
            try
            {
                MoveMediaFileToRecycleBin(media);
                deleted.Add(media);
            }
            catch (Exception ex)
            {
                failed.Add($"{media.FileName}: {ex.Message}");
            }
        }

        MediaItemViewModel? nextSelection = null;
        if (deleted.Count > 0)
        {
            nextSelection = await ViewModel.DeleteMediaAsync(deleted);
            ViewModel.StatusMessage = $"已将 {deleted.Count} 个文件移到回收站。";
        }

        if (nextSelection == null && deleted.Count > 0)
        {
            ClearPlayerSelection();
        }

        var currentDeleteFailed = !string.IsNullOrWhiteSpace(currentMediaId)
            && selected.Any(item => string.Equals(item.Id, currentMediaId, StringComparison.Ordinal))
            && deleted.All(item => !string.Equals(item.Id, currentMediaId, StringComparison.Ordinal));
        if (currentDeleteFailed && ViewModel.SelectedMedia != null)
        {
            UpdatePlayer(ViewModel.SelectedMedia);
        }

        if (failed.Count > 0)
        {
            var detail = string.Join(Environment.NewLine, failed.Take(5));
            var suffix = failed.Count > 5 ? $"{Environment.NewLine}... 另有 {failed.Count - 5} 个文件移到回收站失败。" : string.Empty;
            await ShowInfoDialogAsync("部分文件移到回收站失败", $"{detail}{suffix}");
        }

        _mediaBrowserController.SyncSelectionFromViewModel(ViewModel.SelectedMedia);
    }

    private void MoveMediaFileToRecycleBin(MediaItemViewModel media)
    {
        var path = media.FileSystemPath;
        if (!File.Exists(path))
        {
            return;
        }

        RecycleBinHelper.SendToRecycleBin(path);
    }

    private void ReleasePreviewHandles()
    {
        _imagePreviewController.Clear();
        _videoPlaybackController.PauseAndClearSource();
    }

    private IEnumerable<MediaItemViewModel> GetSelectedItems()
    {
        return _mediaBrowserController.GetSelectedItems();
    }

    private MediaItemViewModel? GetPrimarySelectedMedia()
    {
        return GetSelectedItems().FirstOrDefault() ?? ViewModel.SelectedMedia;
    }

    private IReadOnlyList<MediaItemViewModel> GetCommandSelection()
    {
        var selected = GetSelectedItems().ToList();
        if (selected.Count == 0 && ViewModel.SelectedMedia != null)
        {
            selected.Add(ViewModel.SelectedMedia);
        }

        return selected;
    }

    private void UpdateSelectedStateFlags()
    {
        var selectedIds = new HashSet<string>(GetSelectedItems().Select(item => item.Id), StringComparer.Ordinal);
        if (ViewModel.SelectedMedia != null)
        {
            selectedIds.Add(ViewModel.SelectedMedia.Id);
        }

        foreach (var item in ViewModel.FilteredMediaItems)
        {
            item.IsSelected = selectedIds.Contains(item.Id);
        }
    }

    private void ShowControls()
    {
        _videoPlaybackController.ShowControls();
    }

    private bool CanShowPlayerNavigationHotspots()
    {
        return ViewModel.SelectedMedia != null
            && ViewModel.FilteredMediaItems.Count > 1
            && !_clipEditorController.IsClipModeActive
            && _shell.Player.EmptyStateVisibility != Visibility.Visible;
    }

    private void UpdatePlayerNavigationHotspotLayout()
    {
        var hotspotWidth = ViewModel.SelectedMedia?.Type == MediaType.Image
            && _imagePreviewController.ZoomFactor > 1.01
            ? ZoomedImageNavigationHotspotWidth
            : PlayerEdgeNavigationRevealWidth;

        PreviousMediaHotspot.Width = hotspotWidth;
        NextMediaHotspot.Width = hotspotWidth;
    }

    private void RefreshPlayerNavigationHotspots()
    {
        UpdatePlayerNavigationHotspotLayout();

        var canShow = CanShowPlayerNavigationHotspots() && _videoPlaybackController.AreControlsVisible;
        PreviousMediaHotspot.Visibility = canShow ? Visibility.Visible : Visibility.Collapsed;
        NextMediaHotspot.Visibility = canShow ? Visibility.Visible : Visibility.Collapsed;
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
            if (ReferenceEquals(source, _playerPane.TransportBar.ControlBar)
                || ReferenceEquals(source, _playerPane.TransportBar.VolumeButtonHost)
                || ReferenceEquals(source, _playerPane.TransportBar.VolumeFlyoutContent)
                || ReferenceEquals(source, _playerPane.TransportBar.VolumeSlider)
                || ReferenceEquals(source, _playerPane.TransportBar.VolumeButton)
                || ReferenceEquals(source, _playerPane.TransportBar.PlayPauseButton)
                || ReferenceEquals(source, _playerPane.TransportBar.PlaybackModeButton)
                || ReferenceEquals(source, _playerPane.TransportBar.ClipModeToggleButton)
                || ReferenceEquals(source, _playerPane.TransportBar.FullScreenButton)
                || ReferenceEquals(source, _playerPane.TransportBar.ProgressSlider)
                || ReferenceEquals(source, PreviousMediaHotspot)
                || ReferenceEquals(source, NextMediaHotspot)
                || ReferenceEquals(source, PreviousMediaCue)
                || ReferenceEquals(source, NextMediaCue)
                || ReferenceEquals(source, _playerPane.ClipBarView.ClipBar))
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private static void SetButtonGlyph(Button? button, string glyph)
    {
        if (button == null)
        {
            return;
        }

        if (button.Content is FontIcon icon)
        {
            icon.Glyph = glyph;
            return;
        }

        button.Content = new FontIcon
        {
            Glyph = glyph
        };
    }

    private TimeSpan GetCurrentPlaybackPosition() => _videoPlaybackController.GetCurrentPlaybackPosition();

    private TimeSpan GetCurrentVideoDuration() => _videoPlaybackController.GetCurrentVideoDuration();

    private static string FormatTime(TimeSpan value)
    {
        var totalSeconds = (int)Math.Max(0, value.TotalSeconds);
        var hours = totalSeconds / 3600;
        var minutes = (totalSeconds % 3600) / 60;
        var seconds = totalSeconds % 60;

        return hours > 0
            ? $"{hours}:{minutes:00}:{seconds:00}"
            : $"{minutes}:{seconds:00}";
    }

    private void LibraryPanel_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "导入媒体文件或文件夹";
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }

        e.Handled = true;
    }

    private async void LibraryPanel_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items
                .OfType<IStorageItem>()
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToList();

            if (paths.Count == 0)
            {
                return;
            }

            await ViewModel.AddFilesAsync(paths);
            UpdateWatchedFolders(paths);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task EditSelectedTagsAsync()
    {
        var selected = GetCommandSelection();
        if (selected.Count == 0)
        {
            await ShowInfoDialogAsync("提示", "请先选择一个或多个媒体。");
            return;
        }

        await ApplyTagEditorAsync(selected);
    }

    private Task AddSelectionToPlaylistAsync()
    {
        return AddSelectionToPlaylistAsync(GetCommandSelection());
    }

    private async Task AddSelectionToPlaylistAsync(IReadOnlyList<MediaItemViewModel> selected)
    {
        if (selected.Count == 0)
        {
            await ShowInfoDialogAsync("提示", "请先选择一个或多个媒体。");
            return;
        }

        if (ViewModel.Playlists.Count == 0)
        {
            await ShowInfoDialogAsync("提示", "请先创建播放列表。");
            return;
        }

        var result = await _dialogCoordinator.ShowPlaylistPickerDialogAsync("加入播放列表", ViewModel.Playlists.ToList());
        if (result == null)
        {
            return;
        }

        await ViewModel.AddMediaToPlaylistAsync(result.Playlist.Id, selected);
    }

    private async Task ApplyTagEditorAsync(IReadOnlyList<MediaItemViewModel> items)
    {
        var result = await _dialogCoordinator.ShowTagEditorDialogAsync(items);
        if (result == null)
        {
            return;
        }

        await ViewModel.UpdateTagsAsync(items, result.Tags, result.Mode);
    }


    private async Task ApplySettingsAsync()
    {
        var result = await _dialogCoordinator.ShowSettingsDialogAsync();
        if (result == null)
        {
            return;
        }

        ViewModel.SetPortableMode(result.PortableModeEnabled);
        if (!string.IsNullOrWhiteSpace(result.DataDirectory))
        {
            ViewModel.SetDataDir(result.DataDirectory);
        }

        ViewModel.SetLockPassword(result.GlobalPassword);
        ViewModel.UpdateWatchedFolders(result.WatchedFolders);
        await ShowInfoDialogAsync("设置已保存", "设置已写入。若切换了数据目录或便携模式，重启后会使用新的数据位置。");
    }

    private async Task<bool> ConfirmAsync(string title, string message, string primaryButtonText)
    {
        return await _dialogService.ConfirmAsync(title, message, primaryButtonText);
    }

    private async Task ShowInfoDialogAsync(string title, string message)
    {
        await _dialogService.ShowInfoAsync(title, message);
    }

    private async Task ExecuteUiActionAsync(Func<Task> action, string failureTitle)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            await ShowInfoDialogAsync(failureTitle, ex.Message);
        }
    }

    private AppWindow? GetAppWindow()
    {
        if (_appWindow != null)
        {
            return _appWindow;
        }

        var window = App.MainWindow;
        if (window == null)
        {
            return null;
        }

        var hWnd = WindowNative.GetWindowHandle(window);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        return _appWindow;
    }
}
