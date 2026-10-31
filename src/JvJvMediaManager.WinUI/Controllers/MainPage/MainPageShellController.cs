using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using JvJvMediaManager.Coordinators.MainPage;
using JvJvMediaManager.Models;
using JvJvMediaManager.Services;
using JvJvMediaManager.Services.MainPage;
using JvJvMediaManager.Utilities;
using JvJvMediaManager.ViewModels;
using JvJvMediaManager.ViewModels.MainPage;
using JvJvMediaManager.Views;
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
#if DEBUG
    private const string SmokeOpenIndependentWindowIdsEnvironmentVariable = "JVJVMM_SMOKE_OPEN_INDEPENDENT_WINDOW_IDS";
    private const string SmokeShowContextMenuMediaIdEnvironmentVariable = "JVJVMM_SMOKE_SHOW_CONTEXT_MENU_MEDIA_ID";
#endif

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
    private readonly MediaDeletionWorkflow _mediaDeletionWorkflow;
    private readonly ActivePlaybackTarget _activePlaybackTarget;
    private readonly IndependentMediaWindowService _independentMediaWindows;
    private readonly IClipTimelineEditor _clipEditorController;
    private readonly VideoPlaybackController _videoPlaybackController;
    private readonly ImagePreviewController _imagePreviewController;
    private readonly MainPageShortcutHandler _shortcutHandler;
    private readonly PointerEventHandler _playerRootPointerPressedHandler;
    private readonly RightTappedEventHandler _playerRootRightTappedHandler;
    private readonly PointerEventHandler _videoPlayerPointerPressedHandler;
    private readonly PointerEventHandler _playerNavigationHotspotPointerWheelHandler;

    private AppWindow? _appWindow;

    private bool _isNavigationHotspotPressed;
    private bool _isNavigationHotspotTapCanceled;
    private bool _isNavigationHotspotDraggingImage;
    private bool _isDisposed;
    private int _selectionSyncQueued;
    private int _playerAvailabilityCheckVersion;
    private int _playerSwitchTraceId;
    private int _selectedMediaSwitchTraceId;
    private int _isNavigationHotspotWheelNavigating;
    private long _selectedMediaChangedTimestamp;
    private string? _selectedMediaSwitchMediaId;
    private readonly MissingMediaCleanupWorker _missingMediaCleanupWorker;
    private Windows.Foundation.Point _navigationHotspotPressPoint;
    private PlayerNavigationEdge _activePlayerNavigationEdge;
    private PlayerNavigationEdge _pressedNavigationHotspotEdge;
    private MediaItemViewModel? _activePlayerMedia;
    private string? _activePlayerMediaId;
    private MediaType? _activePlayerMediaType;
    private MediaPlayerSessionTransfer? _bossRecoverySession;
    private CancellationTokenSource _bossRecoveryCancellation = new();

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
    private FrameworkElement PlayerInfoBadge => _playerPane.InfoOverlay.PlayerInfoBadge;
    private TextBlock PlayerFileNameText => _playerPane.InfoOverlay.FileNameText;
    private TextBlock PlayerResolutionText => _playerPane.InfoOverlay.ResolutionText;
    private Border PreviousMediaHotspot => _playerPane.NavigationOverlay.PreviousMediaHotspot;
    private Border NextMediaHotspot => _playerPane.NavigationOverlay.NextMediaHotspot;
    private Border PreviousMediaCue => _playerPane.NavigationOverlay.PreviousMediaCue;
    private Border NextMediaCue => _playerPane.NavigationOverlay.NextMediaCue;
    private StackPanel PlayerSideActionButtons => _playerPane.NavigationOverlay.SideActionButtons;
    private Button DeleteCurrentMediaButton => _playerPane.NavigationOverlay.DeleteCurrentMediaButton;
    private Button ClipModeToggleButton => _playerPane.TransportBar.ClipModeToggleButton;
    private Button SplitClipButton => _playerPane.ClipBarView.SplitClipButton;
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
        _activePlaybackTarget = new ActivePlaybackTarget();
        _independentMediaWindows = new IndependentMediaWindowService(
            _activePlaybackTarget,
            SyncIndependentWindowTagsAsync,
            SyncIndependentWindowDeletedMediaAsync);
        _page.DockedSessionDetachRequested += Page_DockedSessionDetachRequested;
        _page.DockedSessionActivated += Page_DockedSessionActivated;
        _page.DockedSessionClosed += Page_DockedSessionClosed;
        _page.DockedSessionContextMenuRequested += Page_DockedSessionContextMenuRequested;
        _page.DockedSessionMediaTagsChanged += Page_DockedSessionMediaTagsChanged;
        _page.DockedSessionMediaDeleted += Page_DockedSessionMediaDeleted;
        _libraryPaneController = _modules.CreateLibraryPaneController(page, shell.Library, libraryPane);
        _mediaContextMenuCoordinator = _modules.CreateMediaContextMenuCoordinator(
            shell.Library,
            _dialogService,
            FocusMediaInLibraryAsync,
            ApplyTagEditorAsync,
            AddSelectionToPlaylistAsync,
            DeleteSelectionAsync,
            OpenSelectionInNewWindows);
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
            (paths, refreshMedia) => UpdateWatchedFoldersAsync(paths, refreshMedia),
            ShowInfoDialogAsync);
        _mediaDeletionWorkflow = _modules.CreateMediaDeletionWorkflow(
            shell.Library,
            count => ConfirmAsync(
                "确认删除",
                $"将 {count} 个文件移到回收站。此操作会同时从媒体库移除这些记录。",
                "移到回收站"),
            ShowInfoDialogAsync,
            ReleasePreviewHandlesForMedia,
            ClearPlayerSelection,
            ForceUpdatePlayer,
            selected => _mediaBrowserController.SyncSelectionFromViewModel(selected),
            selected => _mediaBrowserController.PreserveSelectionDuringCollectionMutation(selected));
        _missingMediaCleanupWorker = new MissingMediaCleanupWorker(
            () => ViewModel.FilteredMediaItems.ToList(),
            LogMissingMediaDetected);
        _mediaContextMenuCoordinator.PlaylistModified += MediaContextMenuCoordinator_PlaylistModified;
        _videoPlaybackController = _modules.CreateVideoPlaybackController(
            shell.Settings,
            shell.Library,
            shell.Player.VideoPlayback,
            playerPane.TransportBar,
            playerPane.NavigationOverlay,
            playerPane.VideoViewport,
            playerPane.VideoViewport.VideoPlayer,
            _page.DispatcherQueue,
            GetAppWindow,
            NavigateRelativeAsync,
            () => ViewModel.SelectedMedia?.Type == MediaType.Video && _shell.Player.EmptyStateVisibility != Visibility.Visible,
            RestoreKeyboardFocus,
            RefreshPlayerNavigationHotspots,
            duration => _clipEditorController!.HandleMediaOpened(duration),
            () => _clipEditorController!.HandlePlaybackProgressChanged());
        var clipEditorHost = new MainPageClipEditorHost(
            shell.Library,
            _videoPlaybackController,
            paths => UpdateWatchedFoldersAsync(paths),
            ShowControls);
        _clipEditorController = _modules.CreateClipEditorController(
            clipEditorHost,
            shell.Player.ClipEditor,
            ClipModeToggleButton,
            playerPane.ClipBarView);
        _imagePreviewController = _modules.CreateImagePreviewController(
            shell.Library,
            shell.Player.ImagePreview,
            playerPane.ImageViewport,
            playerPane.PlayerOverlay,
            RefreshPlayerNavigationHotspots);
        _playerRootPointerPressedHandler = PlayerRoot_PointerPressed;
        _playerRootRightTappedHandler = PlayerRoot_RightTapped;
        _videoPlayerPointerPressedHandler = VideoPlayer_PointerPressed;
        _playerNavigationHotspotPointerWheelHandler = PlayerNavigationHotspot_PointerWheelChanged;

        var bossKeyController = new BossKeyController(
            () =>
            {
                if (!_isDisposed)
                {
                    _bossRecoveryCancellation.Cancel();
                    _bossRecoveryCancellation.Dispose();
                    _bossRecoveryCancellation = new CancellationTokenSource();
                    var selected = ViewModel.SelectedMedia;
                    _bossRecoverySession = selected == null
                        ? null
                        : new MediaPlayerSessionTransfer(
                            new[] { IndependentMediaWindowService.CreateSnapshot(selected) },
                            0,
                            _videoPlaybackController.GetCurrentPlaybackPosition().TotalSeconds,
                            _videoPlaybackController.IsPlaying,
                            _videoPlaybackController.VideoRotationDegrees);
                    _videoPlaybackController.Clear();
                    _imagePreviewController.Clear();
                    _shell.Player.EmptyStateVisibility = Visibility.Visible;
                    _shell.Player.PlayerInfoVisibility = Visibility.Collapsed;
                    _shell.Library.IsBossMode = true;
                    _page.ShowBossRecoveryButton();
                    _independentMediaWindows.HideAllWindows();

                    if (GetAppWindow()?.Presenter is OverlappedPresenter presenter)
                    {
                        presenter.Minimize();
                    }
                }
            },
            () =>
            {
                if (!_isDisposed)
                {
                    _shell.Library.IsBossMode = false;
                    _page.ExitBossMode();
                    _independentMediaWindows.RestoreAllWindowsFromBossMode();
                    if (ViewModel.SelectedMedia != null)
                    {
                        ForceUpdatePlayer(ViewModel.SelectedMedia);
                        var recovery = _bossRecoverySession;
                        _bossRecoverySession = null;
                        if (recovery != null
                            && string.Equals(recovery.MediaItems[0].Id, ViewModel.SelectedMedia.Id, StringComparison.Ordinal))
                        {
                            _ = RestoreBossPlaybackStateAsync(recovery, _bossRecoveryCancellation.Token);
                        }
                    }
                }
            },
            () => _isDisposed);

        _shortcutHandler = new MainPageShortcutHandler(
            () => _isDisposed,
            IsTextInputFocused,
            GetCurrentPlayerMedia,
            () => _clipEditorController.IsClipModeActive,
            ToggleNumpadShortcutHint,
            TryOpenSelectedMediaFolderFromShortcut,
            TryAddSelectionToPlaylistFromShortcutAsync,
            TryRemoveSelectionFromPlaylistFromShortcutAsync,
            AddFolderAsync,
            FocusLibrarySearch,
            EditSelectedTagsAsync,
            TryApplyNumpadTagShortcutAsync,
            TryTogglePlaybackFromShortcut,
            TryDeleteSelectedFromShortcutAsync,
            NavigateRelativeAsync,
            SeekRelative,
            _clipEditorController.ExportCurrentClipAsync,
            TrySplitClipFromShortcut,
            _imagePreviewController.ZoomBy,
            _imagePreviewController.ResetZoom,
            key =>
            {
                var ctrlDown = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
                return bossKeyController.ShouldHandleBossKey(key, ctrlDown);
            },
            () => bossKeyController.HandleBossKeyTrigger());
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.SetDispatcher(_page.DispatcherQueue);
        _playerPane.ShortcutHintInvoked += PlayerPane_ShortcutHintInvoked;
        _playerPane.EmptyStateView.AddFolderRequested += EmptyStateView_AddFolderRequested;
        _playerPane.EmptyStateView.AddFilesRequested += EmptyStateView_AddFilesRequested;
        _playerPane.EmptyStateView.SettingsRequested += EmptyStateView_SettingsRequested;
        _playerPane.InfoOverlay.TagRemoveRequested += InfoOverlay_TagRemoveRequested;
        PlayerRoot.PointerMoved += PlayerRoot_PointerMoved;
        PlayerRoot.AddHandler(UIElement.PointerPressedEvent, _playerRootPointerPressedHandler, true);
        PlayerRoot.PointerExited += PlayerRoot_PointerExited;
        PlayerRoot.AddHandler(UIElement.RightTappedEvent, _playerRootRightTappedHandler, true);
        VideoPlayer.GotFocus += VideoPlayer_GotFocus;
        VideoPlayer.AddHandler(UIElement.PointerPressedEvent, _videoPlayerPointerPressedHandler, true);
        PreviousMediaHotspot.PointerEntered += PreviousMediaHotspot_PointerEntered;
        PreviousMediaHotspot.PointerPressed += PlayerNavigationHotspot_PointerPressed;
        PreviousMediaHotspot.PointerMoved += PlayerNavigationHotspot_PointerMoved;
        PreviousMediaHotspot.PointerReleased += PlayerNavigationHotspot_PointerReleased;
        PreviousMediaHotspot.PointerCaptureLost += PlayerNavigationHotspot_PointerCaptureLost;
        PreviousMediaHotspot.AddHandler(UIElement.PointerWheelChangedEvent, _playerNavigationHotspotPointerWheelHandler, true);
        _playerPane.ShortcutHintContainer.PointerEntered += ShortcutHintContainer_PointerEntered;
        _playerPane.ShortcutHintContainer.AddHandler(UIElement.PointerWheelChangedEvent, _playerNavigationHotspotPointerWheelHandler, true);
        NextMediaHotspot.PointerEntered += NextMediaHotspot_PointerEntered;
        NextMediaHotspot.PointerPressed += PlayerNavigationHotspot_PointerPressed;
        NextMediaHotspot.PointerMoved += PlayerNavigationHotspot_PointerMoved;
        NextMediaHotspot.PointerReleased += PlayerNavigationHotspot_PointerReleased;
        NextMediaHotspot.PointerCaptureLost += PlayerNavigationHotspot_PointerCaptureLost;
        NextMediaHotspot.AddHandler(UIElement.PointerWheelChangedEvent, _playerNavigationHotspotPointerWheelHandler, true);
        DeleteCurrentMediaButton.Click += DeleteCurrentMediaButton_Click;
        ClipModeToggleButton.Click += ToggleClipMode_Click;
        SplitClipButton.Click += SplitClip_Click;
        ClearClipButton.Click += ClearClip_Click;
        ExportClipButton.Click += ExportClip_Click;
        _shell.Player.EmptyStateVisibility = Visibility.Visible;
        _shell.Player.PlayerInfoVisibility = Visibility.Collapsed;
        _shell.Player.ImagePreview.ZoomBadgeVisibility = Visibility.Collapsed;
        _videoPlaybackController.Clear();
        _page.ConfigureBossKey(bossKeyController);

        _clipEditorController.Refresh();
    }

    public LibraryShellViewModel ViewModel => _shell.Library;

    public bool IsDisposed => _isDisposed;

    public async Task InitializeAsync()
    {
        await ExecuteUiActionAsync(async () =>
        {
            if (_isDisposed)
            {
                AppTraceLogger.Log("MainPageShell", "InitializeAsync skipped because controller is disposed.");
                return;
            }

            await _mediaBrowserController.InitializeAsync();
            if (_isDisposed)
            {
                AppTraceLogger.Log("MainPageShell", "InitializeAsync stopped after media browser initialization because controller is disposed.");
                return;
            }

            _libraryPaneController.EnsurePaneState();
#if DEBUG
            TryOpenIndependentWindowsFromSmokeHook();
            TryShowContextMenuFromSmokeHook();
#endif
        }, "初始化失败");
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Interlocked.Exchange(ref _selectionSyncQueued, 0);
        ResetNavigationHotspotGesture();
        AppTraceLogger.Log("MainPageShell", "Dispose start.");
        _mediaContextMenuCoordinator.PlaylistModified -= MediaContextMenuCoordinator_PlaylistModified;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _playerPane.ShortcutHintInvoked -= PlayerPane_ShortcutHintInvoked;
        _playerPane.EmptyStateView.AddFolderRequested -= EmptyStateView_AddFolderRequested;
        _playerPane.EmptyStateView.AddFilesRequested -= EmptyStateView_AddFilesRequested;
        _playerPane.EmptyStateView.SettingsRequested -= EmptyStateView_SettingsRequested;
        _playerPane.InfoOverlay.TagRemoveRequested -= InfoOverlay_TagRemoveRequested;
        PlayerRoot.PointerMoved -= PlayerRoot_PointerMoved;
        PlayerRoot.RemoveHandler(UIElement.PointerPressedEvent, _playerRootPointerPressedHandler);
        PlayerRoot.PointerExited -= PlayerRoot_PointerExited;
        PlayerRoot.RemoveHandler(UIElement.RightTappedEvent, _playerRootRightTappedHandler);
        VideoPlayer.GotFocus -= VideoPlayer_GotFocus;
        VideoPlayer.RemoveHandler(UIElement.PointerPressedEvent, _videoPlayerPointerPressedHandler);
        PreviousMediaHotspot.PointerEntered -= PreviousMediaHotspot_PointerEntered;
        PreviousMediaHotspot.PointerPressed -= PlayerNavigationHotspot_PointerPressed;
        PreviousMediaHotspot.PointerMoved -= PlayerNavigationHotspot_PointerMoved;
        PreviousMediaHotspot.PointerReleased -= PlayerNavigationHotspot_PointerReleased;
        PreviousMediaHotspot.PointerCaptureLost -= PlayerNavigationHotspot_PointerCaptureLost;
        PreviousMediaHotspot.RemoveHandler(UIElement.PointerWheelChangedEvent, _playerNavigationHotspotPointerWheelHandler);
        _playerPane.ShortcutHintContainer.PointerEntered -= ShortcutHintContainer_PointerEntered;
        _playerPane.ShortcutHintContainer.RemoveHandler(UIElement.PointerWheelChangedEvent, _playerNavigationHotspotPointerWheelHandler);
        NextMediaHotspot.PointerEntered -= NextMediaHotspot_PointerEntered;
        NextMediaHotspot.PointerPressed -= PlayerNavigationHotspot_PointerPressed;
        NextMediaHotspot.PointerMoved -= PlayerNavigationHotspot_PointerMoved;
        NextMediaHotspot.PointerReleased -= PlayerNavigationHotspot_PointerReleased;
        NextMediaHotspot.PointerCaptureLost -= PlayerNavigationHotspot_PointerCaptureLost;
        NextMediaHotspot.RemoveHandler(UIElement.PointerWheelChangedEvent, _playerNavigationHotspotPointerWheelHandler);
        DeleteCurrentMediaButton.Click -= DeleteCurrentMediaButton_Click;
        ClipModeToggleButton.Click -= ToggleClipMode_Click;
        SplitClipButton.Click -= SplitClip_Click;
        ClearClipButton.Click -= ClearClip_Click;
        ExportClipButton.Click -= ExportClip_Click;
        _mediaBrowserController.Dispose();
        _playlistRailCoordinator.Dispose();
        _libraryPaneController.Dispose();
        // Close extra playback windows before tearing down the main player so
        // nested Window.Closed does not run after the main MediaPlayer is gone.
        _independentMediaWindows.Dispose();
        _videoPlaybackController.Dispose();
        _bossRecoveryCancellation.Cancel();
        _bossRecoveryCancellation.Dispose();
        _clipEditorController.Dispose();
        _imagePreviewController.Dispose();
        _page.DockedSessionDetachRequested -= Page_DockedSessionDetachRequested;
        _page.DockedSessionActivated -= Page_DockedSessionActivated;
        _page.DockedSessionClosed -= Page_DockedSessionClosed;
        _page.DockedSessionContextMenuRequested -= Page_DockedSessionContextMenuRequested;
        _page.DockedSessionMediaTagsChanged -= Page_DockedSessionMediaTagsChanged;
        _page.DockedSessionMediaDeleted -= Page_DockedSessionMediaDeleted;
        ViewModel.Dispose();
        AppTraceLogger.Log("MainPageShell", "Dispose completed.");
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        if (e.PropertyName == nameof(LibraryShellViewModel.SelectedMedia))
        {
            var selected = ViewModel.SelectedMedia;
            var switchTraceId = Interlocked.Increment(ref _playerSwitchTraceId);
            _selectedMediaSwitchTraceId = switchTraceId;
            _selectedMediaChangedTimestamp = Stopwatch.GetTimestamp();
            _selectedMediaSwitchMediaId = selected?.Id;
            AppTraceLogger.Log(
                "MainPageShell",
                $"SelectedMedia changed. SelectedId='{selected?.Id ?? "<null>"}', Type='{selected?.Type.ToString() ?? "<null>"}', ActiveId='{_activePlayerMediaId ?? "<null>"}', ActiveType='{_activePlayerMediaType.ToString() ?? "<null>"}'.");
            LogPlayerSwitchTrace(
                switchTraceId,
                selected,
                "SelectedMediaChanged",
                $"ActiveId='{_activePlayerMediaId ?? "<null>"}', ActiveType='{_activePlayerMediaType.ToString() ?? "<null>"}'.");
            _videoPlaybackController.NotifySelectedMediaChanged(selected);
            QueueSelectionSync();
        }

        if (e.PropertyName == nameof(LibraryShellViewModel.IsLoading)
            && !ViewModel.IsLoading
            && ViewModel.SelectedMedia == null)
        {
            QueueSelectionSync();
        }

        if (e.PropertyName == nameof(LibraryShellViewModel.NumpadTagShortcuts))
        {
            RefreshNumpadShortcutHint();
        }
    }

    private void QueueSelectionSync()
    {
        if (_isDisposed)
        {
            AppTraceLogger.Log("MainPageShell", "QueueSelectionSync skipped because controller is disposed.");
            return;
        }

        if (Interlocked.Exchange(ref _selectionSyncQueued, 1) == 1)
        {
            return;
        }

        AppTraceLogger.Log("MainPageShell", "QueueSelectionSync queued.");
        var selected = ViewModel.SelectedMedia;
        var (switchTraceId, selectedChangedTimestamp) = GetSelectionSwitchTrace(selected);
        LogPlayerSwitchTrace(
            switchTraceId,
            selected,
            "SelectionSyncQueued",
            $"SelectedToQueueMs={FormatElapsedMilliseconds(selectedChangedTimestamp)}.");
        if (!_page.DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _selectionSyncQueued, 0);
            if (_isDisposed)
            {
                AppTraceLogger.Log("MainPageShell", "Queued selection sync skipped because controller is disposed.");
                return;
            }

            SyncSelectionFromViewModel();
        }))
        {
            Interlocked.Exchange(ref _selectionSyncQueued, 0);
            AppTraceLogger.Log("MainPageShell", "QueueSelectionSync failed because DispatcherQueue rejected the callback.");
        }
    }

    private void MediaContextMenuCoordinator_PlaylistModified(string playlistId)
    {
        _playlistRailCoordinator.HighlightPlaylist(playlistId);
    }

    private async void EmptyStateView_AddFolderRequested(object? sender, EventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        await ExecuteUiActionAsync(HandleAddFolderFromTitleBarAsync, "添加文件夹失败");
    }

    private async void EmptyStateView_AddFilesRequested(object? sender, EventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        await ExecuteUiActionAsync(HandleAddFilesFromTitleBarAsync, "添加文件失败");
    }

    private async void EmptyStateView_SettingsRequested(object? sender, EventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        await ExecuteUiActionAsync(HandleOpenSettingsFromTitleBarAsync, "打开设置失败");
    }

    public Task HandleAddFolderFromTitleBarAsync()
    {
        if (_isDisposed)
        {
            return Task.CompletedTask;
        }

        _libraryPaneController.ActivateMediaLibrary(openPane: true);
        return _mediaBrowserController.AddFolderAsync();
    }

    public Task HandleAddFilesFromTitleBarAsync()
    {
        if (_isDisposed)
        {
            return Task.CompletedTask;
        }

        _libraryPaneController.ActivateMediaLibrary(openPane: true);
        return _mediaBrowserController.AddFilesAsync();
    }

    public Task HandleOpenSettingsFromTitleBarAsync()
    {
        if (_isDisposed)
        {
            return Task.CompletedTask;
        }

        return ApplySettingsAsync();
    }

    private Task AddFolderAsync() => _mediaBrowserController.AddFolderAsync();

    private void PlayerRoot_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var currentPlayerMedia = GetCurrentPlayerMedia();
        if (currentPlayerMedia == null)
        {
            return;
        }

        var selected = _mediaBrowserController.GetSelectedItems().ToList();
        if (!selected.Any(item => string.Equals(item.Id, currentPlayerMedia.Id, StringComparison.Ordinal)))
        {
            selected = new List<MediaItemViewModel> { currentPlayerMedia };
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
        _mediaContextMenuCoordinator.ShowForTarget(
            PlayerRoot,
            e.GetPosition(PlayerRoot),
            selected,
            ToggleNumpadShortcutHint,
            GetNumpadShortcutHintMenuText(),
            rotateVideoAction: currentPlayerMedia.Type == MediaType.Video
                ? angle => _videoPlaybackController.RotateVideoBy(angle)
                : null);
        e.Handled = true;
    }

    private void SelectMedia(MediaItemViewModel media)
    {
        ViewModel.SelectedMedia = media;
    }

    private void OpenSelectionInNewWindows(IReadOnlyList<MediaItemViewModel> mediaItems)
    {
        if (mediaItems.Count == 0) return;
        var snapshots = IndependentMediaWindowService.CreateNavigationSnapshots(ViewModel.FilteredMediaItems.ToList(), mediaItems);
        var initialIndex = snapshots.ToList().FindIndex(item => string.Equals(item.Id, mediaItems[0].Id, StringComparison.Ordinal));
        _page.ShowDockedSession(new MediaPlayerSessionTransfer(snapshots, Math.Max(0, initialIndex), 0, true));
    }

    private void Page_DockedSessionDetachRequested(MediaPlayerSessionTransfer session)
    {
        _independentMediaWindows.OpenTransferredSession(session);
        _page.CloseDockedSession();
    }

    private void Page_DockedSessionActivated(object? sender, EventArgs e)
    {
        _activePlaybackTarget.SetDocked();
    }

    private void Page_DockedSessionClosed(object? sender, EventArgs e)
    {
        _activePlaybackTarget.ClearDockedIfActive();
    }

    private void Page_DockedSessionContextMenuRequested(object? sender, DockedSessionContextMenuRequestedEventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        var media = ResolveLibraryMedia(e.Media);
        _mediaContextMenuCoordinator.ShowForTarget(
            e.Target,
            e.Position,
            new[] { media },
            e.ToggleShortcutHint,
            e.ShortcutHintMenuText,
            includeOpenInSplit: false,
            rotateVideoAction: e.RotateVideo);
    }

    private MediaItemViewModel ResolveLibraryMedia(MediaItemViewModel media)
    {
        return ViewModel.FilteredMediaItems.FirstOrDefault(item => string.Equals(item.Id, media.Id, StringComparison.Ordinal))
            ?? GetSelectedItems().FirstOrDefault(item => string.Equals(item.Id, media.Id, StringComparison.Ordinal))
            ?? media;
    }

    private void Page_DockedSessionMediaTagsChanged(object? sender, IndependentMediaWindowTagsChangedEventArgs e)
    {
        _ = SyncIndependentWindowTagsAsync(e.MediaId, e.Tags);
    }

    private void Page_DockedSessionMediaDeleted(object? sender, IndependentMediaWindowDeletedEventArgs e)
    {
        _ = SyncIndependentWindowDeletedMediaAsync(e.MediaIds);
    }

#if DEBUG
    private void TryOpenIndependentWindowsFromSmokeHook()
    {
        var rawIds = Environment.GetEnvironmentVariable(SmokeOpenIndependentWindowIdsEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(rawIds))
        {
            return;
        }

        var requestedIds = rawIds
            .Split(new[] { ',', ';', '|' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (requestedIds.Count == 0)
        {
            return;
        }

        var loadedById = ViewModel.FilteredMediaItems.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var mediaItems = requestedIds
            .Select(id => loadedById.TryGetValue(id, out var media) ? media : null)
            .OfType<MediaItemViewModel>()
            .ToList();
        if (mediaItems.Count == 0)
        {
            AppTraceLogger.Log(
                "IndependentMediaWindow",
                $"Smoke hook skipped because none of the requested ids are loaded. Requested='{rawIds}', LoadedCount={ViewModel.FilteredMediaItems.Count}.");
            return;
        }

        AppTraceLogger.Log(
            "IndependentMediaWindow",
            $"Smoke hook opening independent windows. Requested={requestedIds.Count}, Opening={mediaItems.Count}, LoadedCount={ViewModel.FilteredMediaItems.Count}.");
        OpenSelectionInNewWindows(mediaItems);
    }

    private void TryShowContextMenuFromSmokeHook()
    {
        var mediaId = Environment.GetEnvironmentVariable(SmokeShowContextMenuMediaIdEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return;
        }

        mediaId = mediaId.Trim();
        var media = ViewModel.FilteredMediaItems.FirstOrDefault(
            item => string.Equals(item.Id, mediaId, StringComparison.Ordinal));
        if (media == null)
        {
            AppTraceLogger.Log(
                "MediaContextMenu",
                $"Smoke hook skipped because requested media is not loaded. MediaId='{mediaId}', LoadedCount={ViewModel.FilteredMediaItems.Count}.");
            return;
        }

        ViewModel.SelectedMedia = media;
        _mediaBrowserController.SyncSelectionFromViewModel(media);
        var target = _libraryPane.BrowserView.GridView.Visibility == Visibility.Visible
            ? (FrameworkElement)_libraryPane.BrowserView.GridView
            : _libraryPane.BrowserView.ListView;
        var position = new Windows.Foundation.Point(
            Math.Max(8, Math.Min(target.ActualWidth / 2, 180)),
            Math.Max(8, Math.Min(target.ActualHeight / 2, 180)));

        AppTraceLogger.Log(
            "MediaContextMenu",
            $"Smoke hook showing media context menu. MediaId='{media.Id}', Target={target.GetType().Name}, Position={position.X:0.#},{position.Y:0.#}.");
        _mediaContextMenuCoordinator.ShowForTarget(target, position, new[] { media });
    }
#endif

    private async Task SyncIndependentWindowTagsAsync(string mediaId, IReadOnlyList<string> tags)
    {
        if (_isDisposed || string.IsNullOrWhiteSpace(mediaId))
        {
            return;
        }

        var loadedMedia = ViewModel.FilteredMediaItems
            .FirstOrDefault(item => string.Equals(item.Id, mediaId, StringComparison.Ordinal));
        if (loadedMedia != null)
        {
            loadedMedia.UpdateTags(tags);
            if (string.Equals(_activePlayerMediaId, mediaId, StringComparison.Ordinal))
            {
                RefreshPlayerTags(loadedMedia);
            }
        }

        if (!string.IsNullOrWhiteSpace(ViewModel.SearchQuery) || ViewModel.SelectedTags.Count > 0)
        {
            await ViewModel.RefreshMediaAsync(preserveSelection: true, updateLoadingState: false);
        }

        BroadcastMediaTagsChanged(mediaId, tags);

        AppTraceLogger.Log(
            "IndependentMediaWindow",
            $"Synced tag changes to main window. MediaId='{mediaId}', Loaded={loadedMedia != null}, TagCount={tags.Count}.");
    }

    private async Task SyncIndependentWindowDeletedMediaAsync(IReadOnlyList<string> mediaIds)
    {
        if (_isDisposed || mediaIds.Count == 0)
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

        var loadedDeletedMedia = ViewModel.FilteredMediaItems
            .Where(item => deletedIds.Contains(item.Id))
            .ToList();
        var deletedCurrentMediaId = _activePlayerMediaId != null && deletedIds.Contains(_activePlayerMediaId)
            ? _activePlayerMediaId
            : null;

        if (deletedCurrentMediaId != null)
        {
            ReleasePreviewHandlesForMedia(deletedCurrentMediaId);
        }

        MediaItemViewModel? nextSelection = null;
        if (loadedDeletedMedia.Count > 0)
        {
            using var selectionMutation = _mediaBrowserController.PreserveSelectionDuringCollectionMutation(ViewModel.SelectedMedia);
            nextSelection = await ViewModel.DeleteMediaAsync(loadedDeletedMedia);
        }
        else
        {
            ViewModel.MarkMediaDeletedExternally(deletedIds);
            await ViewModel.RefreshMediaAsync(preserveSelection: true, updateLoadingState: false);
        }

        if (deletedCurrentMediaId != null)
        {
            if (nextSelection != null)
            {
                _mediaBrowserController.SyncSelectionFromViewModel(nextSelection);
                ForceUpdatePlayer(nextSelection);
            }
            else
            {
                ClearPlayerSelection();
            }
        }
        else
        {
            _mediaBrowserController.SyncSelectionFromViewModel(ViewModel.SelectedMedia);
        }

        BroadcastMediaDeleted(mediaIds);

        AppTraceLogger.Log(
            "IndependentMediaWindow",
            $"Synced deleted media to main window. Requested={deletedIds.Count}, LoadedDeleted={loadedDeletedMedia.Count}, DeletedCurrent={deletedCurrentMediaId != null}, NextSelection='{nextSelection?.Id ?? "<null>"}'.");
    }

    private void UpdatePlayer(MediaItemViewModel media)
    {
        var updateStartTimestamp = Stopwatch.GetTimestamp();
        var (switchTraceId, selectedChangedTimestamp) = GetSelectionSwitchTrace(media);
        LogPlayerSwitchTrace(
            switchTraceId,
            media,
            "UpdatePlayerStart",
            $"SelectedToUpdatePlayerMs={FormatElapsedMilliseconds(selectedChangedTimestamp)}.");

        var isSameActiveMedia = string.Equals(_activePlayerMediaId, media.Id, StringComparison.Ordinal)
            && _activePlayerMediaType == media.Type;
        AppTraceLogger.Log(
            "MainPageShell",
            $"UpdatePlayer start. MediaId='{media.Id}', File='{media.FileName}', Type='{media.Type}', SameActive={isSameActiveMedia}, ActiveId='{_activePlayerMediaId ?? "<null>"}', ActiveType='{_activePlayerMediaType.ToString() ?? "<null>"}'.");
        _shell.Player.EmptyStateVisibility = Visibility.Collapsed;
        _shell.Player.PlayerInfoVisibility = Visibility.Visible;
        _shell.Player.PlayerFileName = media.FileName;
        _shell.Player.PlayerResolution = media.PlayerDetailsText;
        RefreshPlayerTags(media);
        _clipEditorController.HandleMediaChanged(media);
        _activePlayerMedia = media;

        if (isSameActiveMedia)
        {
            return;
        }

        if (media.Type == MediaType.Video)
        {
            _shell.Player.ImagePreview.ZoomBadgeVisibility = Visibility.Collapsed;
            _imagePreviewController.Clear();
            LogPlayerSwitchTrace(
                switchTraceId,
                media,
                "PlayerUiPrepared",
                $"SelectedToPlayerUiPreparedMs={FormatElapsedMilliseconds(selectedChangedTimestamp)}, UpdatePlayerUiPrepMs={Stopwatch.GetElapsedTime(updateStartTimestamp).TotalMilliseconds:0.###}.");
            _videoPlaybackController.ShowVideo(media, selectedChangedTimestamp, switchTraceId);
        }
        else
        {
            _shell.Player.ImagePreview.ZoomBadgeVisibility = Visibility.Visible;
            if (_activePlayerMediaType != MediaType.Image)
            {
                AppTraceLogger.Log(
                    "MainPageShell",
                    $"UpdatePlayer switching video pipeline to image mode. PreviousActiveType='{_activePlayerMediaType.ToString() ?? "<null>"}', MediaId='{media.Id}'.");
                _videoPlaybackController.ShowImageState();
            }

            _imagePreviewController.ShowImage(media);
        }

        _activePlayerMediaId = media.Id;
        _activePlayerMediaType = media.Type;
        _activePlayerMedia = media;
    }

    private bool TryRouteSelectedMediaToActiveTarget(MediaItemViewModel media)
    {
        var snapshot = IndependentMediaWindowService.CreateSnapshot(media);
        switch (_activePlaybackTarget.Kind)
        {
            case ActivePlaybackTargetKind.Docked:
                if (_page.TryPlayInDockedSession(snapshot))
                {
                    AppTraceLogger.Log(
                        "MainPageShell",
                        $"Routed library selection to docked session. MediaId='{media.Id}'.");
                    return true;
                }

                _activePlaybackTarget.ClearDockedIfActive();
                return false;

            case ActivePlaybackTargetKind.Independent:
                if (_independentMediaWindows.TryPlayInActiveIndependentWindow(snapshot))
                {
                    AppTraceLogger.Log(
                        "MainPageShell",
                        $"Routed library selection to independent window. MediaId='{media.Id}'.");
                    return true;
                }

                if (_activePlaybackTarget.IndependentTarget is { } staleWindow)
                {
                    _activePlaybackTarget.ClearIndependentIfActive(staleWindow);
                }

                return false;

            default:
                return false;
        }
    }

    private void QueueVerifiedPlayerUpdate(MediaItemViewModel media)
    {
        var version = Interlocked.Increment(ref _playerAvailabilityCheckVersion);
        _ = VerifyPlayerMediaAvailableAndUpdateAsync(media, version);
    }

    private async Task VerifyPlayerMediaAvailableAndUpdateAsync(MediaItemViewModel media, int version)
    {
        var availabilityStartTimestamp = Stopwatch.GetTimestamp();
        var isAvailable = await _missingMediaCleanupWorker.IsMediaFileAvailableAsync(media);
        var availabilityElapsedMs = Stopwatch.GetElapsedTime(availabilityStartTimestamp).TotalMilliseconds;

        if (_isDisposed || version != Volatile.Read(ref _playerAvailabilityCheckVersion))
        {
            return;
        }

        if (!isAvailable)
        {
            LogPlayerSwitchTrace(
                0,
                media,
                "UpdatePlayerMissing",
                $"FileAvailabilityMs={availabilityElapsedMs:0.###}.");
            QueueMissingMediaCleanup(media, "update-player");
            return;
        }

        if (!_page.DispatcherQueue.TryEnqueue(() =>
        {
            if (_isDisposed || version != Volatile.Read(ref _playerAvailabilityCheckVersion))
            {
                return;
            }

            var selected = ViewModel.SelectedMedia;
            if (selected == null || !string.Equals(selected.Id, media.Id, StringComparison.Ordinal))
            {
                return;
            }

            var isSameActiveMedia = string.Equals(_activePlayerMediaId, selected.Id, StringComparison.Ordinal)
                && _activePlayerMediaType == selected.Type;
            UpdatePlayer(selected);
            if (!isSameActiveMedia)
            {
                _mediaBrowserController.RevealSelectedMedia(selected);
            }
        }))
        {
            AppTraceLogger.Log("MainPageShell", $"Verified player update dispatcher rejected. MediaId='{media.Id}'.");
        }
    }

    private void ForceUpdatePlayer(MediaItemViewModel media)
    {
        AppTraceLogger.Log(
            "MainPageShell",
            $"ForceUpdatePlayer requested. MediaId='{media.Id}', PreviousActiveId='{_activePlayerMediaId ?? "<null>"}', PreviousActiveType='{_activePlayerMediaType.ToString() ?? "<null>"}'.");
        Interlocked.Increment(ref _playerAvailabilityCheckVersion);
        _activePlayerMediaId = null;
        _activePlayerMediaType = null;
        _activePlayerMedia = null;

        void Apply()
        {
            if (_isDisposed)
            {
                return;
            }

            UpdatePlayer(media);
            _mediaBrowserController.RevealSelectedMedia(media);
        }

        if (_page.DispatcherQueue.HasThreadAccess)
        {
            Apply();
            return;
        }

        if (!_page.DispatcherQueue.TryEnqueue(Apply))
        {
            AppTraceLogger.Log("MainPageShell", $"ForceUpdatePlayer dispatcher rejected. MediaId='{media.Id}'.");
        }
    }

    private void LogMissingMediaDetected(MediaItemViewModel media, string source)
    {
        AppTraceLogger.Log(
            "MainPageShell",
            $"Missing media detected. Source={source}, MediaId='{media.Id}', File='{media.FileName}', Path='{media.FileSystemPath}'.");
    }

    private void QueueMissingMediaCleanup(MediaItemViewModel media, string source)
    {
        if (!_missingMediaCleanupWorker.TryQueue(media, source))
        {
            return;
        }

        ClearPlayerSelection();
        _ = HandleMissingMediaAsync(media);
    }

    private async Task HandleMissingMediaAsync(MediaItemViewModel media)
    {
        var cleanupIds = new HashSet<string>(StringComparer.Ordinal);
        MediaItemViewModel? nextSelection = media;
        var removedCount = 0;

        using var cleanupScope = _missingMediaCleanupWorker.EnterCleanup();
        try
        {
            for (var round = 0; round < 4 && nextSelection != null && !await _missingMediaCleanupWorker.IsMediaFileAvailableAsync(nextSelection); round++)
            {
                var batch = (await _missingMediaCleanupWorker.BuildCleanupBatchAsync(nextSelection)).ToList();
                if (batch.Count == 0)
                {
                    break;
                }

                _missingMediaCleanupWorker.MarkBatchQueued(batch, cleanupIds);

                removedCount += batch.Count;
                AppTraceLogger.Log(
                    "MainPageShell",
                    $"Missing media cleanup batch deleting. Round={round + 1}, Count={batch.Count}, FirstId='{batch[0].Id}', FirstFile='{batch[0].FileName}'.");
                nextSelection = await ViewModel.DeleteMediaAsync(batch);
                _mediaBrowserController.SuppressTransientEmptySelectionFor(nextSelection, "missing-media-cleanup");
            }

            ViewModel.StatusMessage = removedCount <= 1
                ? $"媒体文件已不存在，已从媒体库移除：{media.FileName}"
                : $"已从媒体库移除 {removedCount} 个不存在的媒体文件";

            if (nextSelection == null)
            {
                ClearPlayerSelection();
            }

            AppTraceLogger.Log(
                "MainPageShell",
                $"Missing media cleanup completed. RequestedId='{media.Id}', Removed={removedCount}, NextSelection='{ViewModel.SelectedMedia?.Id ?? "<null>"}'.");
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("MainPageShell", $"Missing media cleanup failed. MediaId='{media.Id}'.", ex);
            await ShowInfoDialogAsync("媒体已失效", $"文件不存在，且从媒体库移除失败：{media.FileName}{Environment.NewLine}{ex.Message}");
        }
        finally
        {
            _missingMediaCleanupWorker.ReleaseQueued(media, cleanupIds);
            SyncSelectionFromViewModel();
        }
    }

    private void ClearPlayerSelection()
    {
        if (_isDisposed)
        {
            AppTraceLogger.Log("MainPageShell", "ClearPlayerSelection skipped because controller is disposed.");
            return;
        }

        AppTraceLogger.Log(
            "MainPageShell",
            $"ClearPlayerSelection start. ActiveId='{_activePlayerMediaId ?? "<null>"}', ActiveType='{_activePlayerMediaType.ToString() ?? "<null>"}', SelectedId='{ViewModel.SelectedMedia?.Id ?? "<null>"}'.");

        if (_activePlayerMediaId == null
            && _activePlayerMediaType == null
            && ViewModel.SelectedMedia == null
            && _shell.Player.EmptyStateVisibility == Visibility.Visible)
        {
            AppTraceLogger.Log("MainPageShell", "ClearPlayerSelection skipped because player is already empty.");
            return;
        }

        _shell.Player.EmptyStateVisibility = Visibility.Visible;
        _shell.Player.PlayerInfoVisibility = Visibility.Collapsed;
        _shell.Player.PlayerFileName = string.Empty;
        _shell.Player.PlayerResolution = string.Empty;
        _shell.Player.PlayerTags.Clear();
        _shell.Player.ImagePreview.ZoomBadgeVisibility = Visibility.Collapsed;
        _imagePreviewController.Clear();
        _clipEditorController.HandleMediaChanged(null);
        _videoPlaybackController.Clear();
        _activePlayerMediaId = null;
        _activePlayerMediaType = null;
        _libraryPaneController.EnsurePaneState(preferOpen: true);
        AppTraceLogger.Log("MainPageShell", "ClearPlayerSelection completed.");
    }

    private void SyncSelectionFromViewModel()
    {
        if (_isDisposed)
        {
            AppTraceLogger.Log("MainPageShell", "SyncSelectionFromViewModel skipped because controller is disposed.");
            return;
        }

        var selected = ViewModel.SelectedMedia;
        var (switchTraceId, selectedChangedTimestamp) = GetSelectionSwitchTrace(selected);
        AppTraceLogger.Log(
            "MainPageShell",
            $"SyncSelectionFromViewModel start. SelectedId='{selected?.Id ?? "<null>"}', SelectedType='{selected?.Type.ToString() ?? "<null>"}', ActiveId='{_activePlayerMediaId ?? "<null>"}', ActiveType='{_activePlayerMediaType.ToString() ?? "<null>"}', IsLoading={ViewModel.IsLoading}.");
        LogPlayerSwitchTrace(
            switchTraceId,
            selected,
            "SelectionSyncStart",
            $"SelectedToSyncStartMs={FormatElapsedMilliseconds(selectedChangedTimestamp)}, IsLoading={ViewModel.IsLoading}.");
        _mediaBrowserController.SyncSelectionFromViewModel(selected);
        RefreshNumpadShortcutHint();

        if (_missingMediaCleanupWorker.IsActive)
        {
            AppTraceLogger.LogSampled(
                "MainPageShell",
                "selection-sync-during-missing-cleanup",
                $"SyncSelectionFromViewModel deferred player update during missing-media cleanup. SelectedId='{selected?.Id ?? "<null>"}'.",
                TimeSpan.FromSeconds(1));
            return;
        }

        if (selected == null)
        {
            if ((_activePlayerMediaId != null || _activePlayerMediaType != null) && ViewModel.IsLoading)
            {
                return;
            }

            ClearPlayerSelection();
            return;
        }

        if (TryRouteSelectedMediaToActiveTarget(selected))
        {
            _mediaBrowserController.RevealSelectedMedia(selected);
            return;
        }

        QueueVerifiedPlayerUpdate(selected);
    }

    private (int SwitchTraceId, long SelectedChangedTimestamp) GetSelectionSwitchTrace(MediaItemViewModel? media)
    {
        if (media == null)
        {
            return string.IsNullOrWhiteSpace(_selectedMediaSwitchMediaId)
                ? (_selectedMediaSwitchTraceId, _selectedMediaChangedTimestamp)
                : (0, 0);
        }

        return string.Equals(_selectedMediaSwitchMediaId, media.Id, StringComparison.Ordinal)
            ? (_selectedMediaSwitchTraceId, _selectedMediaChangedTimestamp)
            : (0, 0);
    }

    private static string FormatElapsedMilliseconds(long startTimestamp)
    {
        return startTimestamp <= 0
            ? "<none>"
            : Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds.ToString("0.###");
    }

    private static void LogPlayerSwitchTrace(
        int switchTraceId,
        MediaItemViewModel? media,
        string stage,
        string details)
    {
        AppTraceLogger.Log(
            "PlayerSwitch",
            $"TraceId={switchTraceId} Stage={stage} MediaId='{media?.Id ?? "<null>"}', Type='{media?.Type.ToString() ?? "<null>"}', File='{media?.FileName ?? "<null>"}'. {details}");
    }

    public async Task HandleKeyDownAsync(KeyRoutedEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        if (await TryHandleRoutedPlaybackShortcutAsync(e.Key))
        {
            e.Handled = true;
            return;
        }

        e.Handled = await _shortcutHandler.HandleShortcutKeyAsync(e.Key);
        if (e.Handled)
        {
            ShowControls();
        }
    }

    public bool ShouldHandleWindowShortcutKey(VirtualKey key)
    {
        if (_shortcutHandler.ShouldHandleWindowShortcutKey(key))
        {
            return true;
        }

        return ShouldRoutePlaybackShortcut(key);
    }

    public async Task<bool> HandleVirtualKeyDownAsync(VirtualKey key)
    {
        if (await TryHandleRoutedPlaybackShortcutAsync(key))
        {
            return true;
        }

        return await _shortcutHandler.HandleShortcutKeyAsync(key);
    }

    private bool ShouldRoutePlaybackShortcut(VirtualKey key)
    {
        if (IsLibraryOrGlobalShortcut(key) || IsTextInputFocused())
        {
            return false;
        }

        if (_activePlaybackTarget.Kind is not (ActivePlaybackTargetKind.Docked or ActivePlaybackTargetKind.Independent))
        {
            return false;
        }

        return IsRoutablePlaybackShortcutKey(key);
    }

    private static bool IsLibraryOrGlobalShortcut(VirtualKey key)
    {
        var ctrlDown = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (ctrlDown)
        {
            return key is VirtualKey.O or VirtualKey.A or VirtualKey.R or VirtualKey.F or VirtualKey.T or (VirtualKey)87;
        }

        return false;
    }

    private static bool IsRoutablePlaybackShortcutKey(VirtualKey key)
    {
        if (key is VirtualKey.Escape
            or VirtualKey.F1
            or VirtualKey.Delete
            or VirtualKey.Space
            or VirtualKey.Left
            or VirtualKey.Right
            or VirtualKey.PageUp
            or VirtualKey.PageDown
            or VirtualKey.Up
            or VirtualKey.Down
            or VirtualKey.Add
            or VirtualKey.Subtract
            or VirtualKey.Number0
            or (VirtualKey)187
            or (VirtualKey)189)
        {
            return true;
        }

        return key is VirtualKey.NumberPad1
            or VirtualKey.NumberPad2
            or VirtualKey.NumberPad3
            or VirtualKey.NumberPad4
            or VirtualKey.NumberPad5
            or VirtualKey.NumberPad6
            or VirtualKey.NumberPad7
            or VirtualKey.NumberPad8
            or VirtualKey.NumberPad9;
    }

    private async Task<bool> TryHandleRoutedPlaybackShortcutAsync(VirtualKey key)
    {
        if (!ShouldRoutePlaybackShortcut(key))
        {
            return false;
        }

        switch (_activePlaybackTarget.Kind)
        {
            case ActivePlaybackTargetKind.Docked:
                if (await _page.TryHandleDockedShortcutAsync(key))
                {
                    return true;
                }

                _activePlaybackTarget.ClearDockedIfActive();
                return false;

            case ActivePlaybackTargetKind.Independent:
                if (await _independentMediaWindows.TryHandleActiveIndependentShortcutAsync(key))
                {
                    return true;
                }

                if (_activePlaybackTarget.IndependentTarget is { } staleWindow)
                {
                    _activePlaybackTarget.ClearIndependentIfActive(staleWindow);
                }

                return false;

            default:
                return false;
        }
    }

    private async void PlayerPane_ShortcutHintInvoked(object? sender, int index)
    {
        if (_isDisposed)
        {
            return;
        }

        await ExecuteUiActionAsync(
            () => TryApplyShortcutTagAsync(index),
            "快捷标签添加失败");
    }

    private async void InfoOverlay_TagRemoveRequested(object? sender, string tag)
    {
        if (_isDisposed)
        {
            return;
        }

        await ExecuteUiActionAsync(
            () => RemoveCurrentPlayerTagAsync(tag),
            "标签删除失败");
    }

    private bool TryTogglePlaybackFromShortcut()
    {
        if (IsTextInputFocused())
        {
            return false;
        }

        if (GetCurrentPlayerMedia()?.Type != MediaType.Video)
        {
            return false;
        }

        TogglePlayPause();
        return true;
    }

    private bool TrySplitClipFromShortcut()
    {
        if (IsTextInputFocused())
        {
            return false;
        }

        if (GetCurrentPlayerMedia()?.Type != MediaType.Video || !_clipEditorController.IsClipModeActive)
        {
            return false;
        }

        _clipEditorController.SplitSegmentAtCurrentPosition();
        ShowControls();
        return true;
    }

    private async Task<bool> TryDeleteSelectedFromShortcutAsync()
    {
        if (IsTextInputFocused())
        {
            return false;
        }

        var currentMedia = GetCurrentPlayerMedia();
        if (currentMedia == null)
        {
            return false;
        }

        if (currentMedia.Type == MediaType.Video && _clipEditorController.IsClipModeActive)
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

    private void FocusLibrarySearch()
    {
        _libraryPaneController.SetPaneOpen(true);
        _mediaBrowserController.FocusSearchBox();
    }

    private void ToggleNumpadShortcutHint()
    {
        RefreshNumpadShortcutHint();
        _shell.Player.ShortcutHintVisibility = _shell.Player.ShortcutHintVisibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private string GetNumpadShortcutHintMenuText()
    {
        return _shell.Player.ShortcutHintVisibility == Visibility.Visible
            ? "隐藏快捷加tag"
            : "显示快捷加tag";
    }

    private void ShowNumpadShortcutHint()
    {
        RefreshNumpadShortcutHint();
        _shell.Player.ShortcutHintVisibility = Visibility.Visible;
    }

    private void RefreshNumpadShortcutHint()
    {
        var selectedTags = GetCurrentPlayerMedia()?.Tags ?? [];
        var items = ViewModel.NumpadTagShortcuts
            .Select((tag, index) => new { Tag = tag, Index = index + 1 })
            .Where(item => !string.IsNullOrWhiteSpace(item.Tag))
            .ToList();

        _shell.Player.ShortcutHintItems.Clear();
        foreach (var item in items)
        {
            _shell.Player.ShortcutHintItems.Add(new NumpadTagShortcutHintItem
            {
                Index = item.Index,
                Tag = item.Tag,
                IsApplied = selectedTags.Any(existing => string.Equals(existing, item.Tag, StringComparison.OrdinalIgnoreCase))
            });
        }
    }

    private void RefreshPlayerTags(MediaItemViewModel? media)
    {
        _shell.Player.PlayerTags.Clear();
        if (media == null)
        {
            return;
        }

        foreach (var tag in media.Tags
                     .Where(tag => !string.IsNullOrWhiteSpace(tag))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase))
        {
            _shell.Player.PlayerTags.Add(new PlayerMediaTagItem
            {
                Tag = tag
            });
        }
    }

    private async Task<bool> TryApplyNumpadTagShortcutAsync(Windows.System.VirtualKey key)
    {
        var digit = key switch
        {
            Windows.System.VirtualKey.NumberPad1 => 1,
            Windows.System.VirtualKey.NumberPad2 => 2,
            Windows.System.VirtualKey.NumberPad3 => 3,
            Windows.System.VirtualKey.NumberPad4 => 4,
            Windows.System.VirtualKey.NumberPad5 => 5,
            Windows.System.VirtualKey.NumberPad6 => 6,
            Windows.System.VirtualKey.NumberPad7 => 7,
            Windows.System.VirtualKey.NumberPad8 => 8,
            Windows.System.VirtualKey.NumberPad9 => 9,
            _ => 0
        };
        if (digit == 0)
        {
            return false;
        }

        return await TryApplyNumpadTagShortcutAsync(digit);
    }

    private async Task<bool> TryApplyNumpadTagShortcutAsync(int digit)
    {
        var currentMedia = GetCurrentPlayerMedia();
        if (currentMedia == null)
        {
            return false;
        }

        var applied = await ViewModel.TryApplyNumpadTagShortcutAsync(digit, new[] { currentMedia });
        if (applied)
        {
            RefreshPlayerTags(currentMedia);
            RefreshNumpadShortcutHint();
            BroadcastMediaTagsChanged(currentMedia.Id, currentMedia.Tags.ToList());
        }

        return applied;
    }

    private async Task<bool> TryApplyShortcutTagAsync(int index)
    {
        var currentMedia = GetCurrentPlayerMedia();
        if (currentMedia == null)
        {
            return false;
        }

        var applied = await ViewModel.TryApplyTagShortcutAsync(index, new[] { currentMedia });
        if (applied)
        {
            RefreshPlayerTags(currentMedia);
            RefreshNumpadShortcutHint();
            BroadcastMediaTagsChanged(currentMedia.Id, currentMedia.Tags.ToList());
        }

        return applied;
    }

    private async Task RemoveCurrentPlayerTagAsync(string tag)
    {
        var currentMedia = GetCurrentPlayerMedia();
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

        await ViewModel.UpdateTagsAsync(new[] { currentMedia }, nextTags, TagUpdateMode.Replace);
        RefreshPlayerTags(currentMedia);
        RefreshNumpadShortcutHint();
        BroadcastMediaTagsChanged(currentMedia.Id, currentMedia.Tags.ToList());
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
        var pointerPosition = e.GetCurrentPoint(PlayerRoot).Position;
        _videoPlaybackController.HandlePlayerPointerMoved(pointerPosition.Y, PlayerRoot.ActualHeight);
        UpdatePlayerNavigationCue(pointerPosition);
    }

    private void PlayerRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pointerPosition = e.GetCurrentPoint(PlayerRoot).Position;
        UpdatePlayerNavigationCue(pointerPosition);
        var isOverlayInteraction = IsPlayerOverlayInteractionSource(e.OriginalSource as DependencyObject);
        _activePlaybackTarget.SetMain();

        if (ViewModel.SelectedMedia?.Type == MediaType.Video
            && _clipEditorController.IsClipModeActive
            && !isOverlayInteraction)
        {
            _clipEditorController.HandlePreviewSurfaceInteraction();
        }

        if (!isOverlayInteraction)
        {
            RestoreKeyboardFocus();
        }
    }

    private void VideoPlayer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        _activePlaybackTarget.SetMain();
        AppTraceLogger.LogSampled(
            "MainPageShell",
            "video-player-pointer",
            $"Video player pointer pressed. FocusedElement='{FocusManager.GetFocusedElement(_page.XamlRoot)?.GetType().Name ?? "<null>"}'.",
            TimeSpan.FromSeconds(2));
        RestoreKeyboardFocus();
    }

    private void VideoPlayer_GotFocus(object sender, RoutedEventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        AppTraceLogger.LogSampled(
            "MainPageShell",
            "video-player-focus",
            $"Video player got focus. FocusedElement='{FocusManager.GetFocusedElement(_page.XamlRoot)?.GetType().Name ?? "<null>"}'.",
            TimeSpan.FromSeconds(2));
        RestoreKeyboardFocus();
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
        var currentMedia = GetCurrentPlayerMedia();
        if (list.Count == 0 || currentMedia == null)
        {
            return;
        }

        if (_videoPlaybackController.IsShuffleMode)
        {
            await _videoPlaybackController.TryNavigateShuffleAsync(offset);
            return;
        }

        var index = IndexOfMediaId(list, currentMedia.Id);
        if (index < 0)
        {
            var found = await ViewModel.EnsureMediaItemLoadedAsync(currentMedia.Id);
            index = IndexOfMediaId(list, currentMedia.Id);
            if (!found || index < 0)
            {
                index = ResolveLoadedNavigationAnchorIndex(list, currentMedia);
                if (index < 0)
                {
                    index = offset > 0 ? -1 : list.Count;
                    AppTraceLogger.LogSampled(
                        "MainPageShell",
                        "navigate-relative-current-missing-boundary-fallback",
                        $"NavigateRelative using boundary fallback because current media is not in the loaded list. Offset={offset}, CurrentId='{currentMedia.Id}', LoadedCount={list.Count}, FoundAfterLoad={found}, FallbackIndex={index}.",
                        TimeSpan.FromSeconds(1));
                }
                else
                {
                    AppTraceLogger.LogSampled(
                        "MainPageShell",
                        "navigate-relative-current-missing-anchor-fallback",
                        $"NavigateRelative using loaded anchor fallback because current media is not in the loaded list. Offset={offset}, CurrentId='{currentMedia.Id}', LoadedCount={list.Count}, FoundAfterLoad={found}, AnchorIndex={index}.",
                        TimeSpan.FromSeconds(1));
                }
            }
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

    private int ResolveLoadedNavigationAnchorIndex(IReadOnlyList<MediaItemViewModel> items, MediaItemViewModel currentMedia)
    {
        if (ViewModel.SelectedMedia is { } selected
            && !string.Equals(selected.Id, currentMedia.Id, StringComparison.Ordinal))
        {
            var selectedIndex = IndexOfMediaId(items, selected.Id);
            if (selectedIndex >= 0)
            {
                return selectedIndex;
            }
        }

        if (_activePlayerMedia is { } active
            && !string.Equals(active.Id, currentMedia.Id, StringComparison.Ordinal))
        {
            var activeIndex = IndexOfMediaId(items, active.Id);
            if (activeIndex >= 0)
            {
                return activeIndex;
            }
        }

        foreach (var selectedItem in GetSelectedItems())
        {
            var selectedItemIndex = IndexOfMediaId(items, selectedItem.Id);
            if (selectedItemIndex >= 0)
            {
                return selectedItemIndex;
            }
        }

        return -1;
    }
    private static int IndexOfMediaId(IReadOnlyList<MediaItemViewModel> items, string mediaId)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return -1;
        }

        for (var index = 0; index < items.Count; index++)
        {
            if (string.Equals(items[index].Id, mediaId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
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
            if (_isDisposed)
            {
                return;
            }

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
                if (_isDisposed)
                {
                    return;
                }

                ShowControls();
                SetPlayerNavigationEdge(edge);
            }

            e.Handled = true;
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("MainPageShell", "Player navigation hotspot release failed.", ex);
        }
    }

    private async void PlayerNavigationHotspot_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            if (_isDisposed || !CanShowPlayerNavigationHotspots())
            {
                return;
            }

            var point = e.GetCurrentPoint(sender as UIElement);
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
            if (_isDisposed)
            {
                return;
            }

            ShowControls();
            SetPlayerNavigationEdge(edge);
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("MainPageShell", "Player navigation hotspot wheel navigation failed.", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _isNavigationHotspotWheelNavigating, 0);
        }
    }

    private void PlayerNavigationHotspot_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        ResetNavigationHotspotGesture();
        _imagePreviewController.EndExternalDrag();
    }

    private async Task UpdateWatchedFoldersAsync(IEnumerable<string> paths, bool refreshMedia = true)
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

        await ViewModel.UpdateWatchedFoldersAsync(current, refreshMedia);
    }

    private void ClearClip_Click(object sender, RoutedEventArgs e)
    {
        _clipEditorController.Clear();
    }

    private async void DeleteCurrentMediaButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        await ExecuteUiActionAsync(DeleteCurrentPlayerMediaAsync, "删除当前媒体失败");
    }

    private async void ExportClip_Click(object sender, RoutedEventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        await ExecuteUiActionAsync(_clipEditorController.ExportCurrentClipAsync, "导出剪辑失败");
    }

    private void SplitClip_Click(object sender, RoutedEventArgs e)
    {
        _clipEditorController.SplitSegmentAtCurrentPosition();
    }

    private Task DeleteSelectedAsync()
    {
        return DeleteSelectionAsync(_mediaBrowserController.GetSelectedItems());
    }

    private async Task DeleteCurrentPlayerMediaAsync()
    {
        var media = GetCurrentPlayerMedia();
        if (media == null)
        {
            return;
        }

        AppTraceLogger.Log(
            "MainPageShell",
            $"DeleteCurrentPlayerMediaAsync start. MediaId='{media.Id}', File='{media.FileName}', Type='{media.Type}'.");

        await DeleteSelectionAndSyncDockedAsync(new[] { media });
    }

    private Task FocusMediaInLibraryAsync(IReadOnlyList<MediaItemViewModel> selection)
    {
        var media = selection.FirstOrDefault();
        if (media == null)
        {
            return Task.CompletedTask;
        }

        AppTraceLogger.Log(
            "MainPageShell",
            $"FocusMediaInLibraryAsync requested. MediaId='{media.Id}', File='{media.FileName}', SelectionCount={selection.Count}.");
        _mediaBrowserController.FocusMediaInLibrary(media);
        return Task.CompletedTask;
    }

    private Task DeleteSelectionAsync(IReadOnlyList<MediaItemViewModel> initialSelection)
    {
        return DeleteSelectionAndSyncDockedAsync(initialSelection);
    }

    private async Task DeleteSelectionAndSyncDockedAsync(IReadOnlyList<MediaItemViewModel> initialSelection)
    {
        var deletedIds = await _mediaDeletionWorkflow.DeleteAsync(initialSelection);
        BroadcastMediaDeleted(deletedIds);
    }

    private void ReleasePreviewHandlesForMedia(string? mediaId)
    {
        var current = GetCurrentPlayerMedia();
        if (string.IsNullOrWhiteSpace(mediaId)
            || current == null
            || !string.Equals(current.Id, mediaId, StringComparison.Ordinal))
        {
            AppTraceLogger.Log(
                "MainPageShell",
                $"ReleasePreviewHandlesForMedia skipped. RequestedId='{mediaId ?? "<null>"}', CurrentId='{current?.Id ?? "<null>"}'.");
            return;
        }

        AppTraceLogger.Log("MainPageShell", $"ReleasePreviewHandlesForMedia releasing. MediaId='{mediaId}'.");
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

    private MediaItemViewModel? GetCurrentPlayerMedia()
    {
        return ViewModel.SelectedMedia ?? _activePlayerMedia;
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

    private void ShowControls()
    {
        _videoPlaybackController.ShowControls();
    }

    private bool CanShowPlayerNavigationHotspots()
    {
        return GetCurrentPlayerMedia() != null
            && ViewModel.FilteredMediaItems.Count > 1
            && !_clipEditorController.IsClipModeActive
            && _shell.Player.EmptyStateVisibility != Visibility.Visible;
    }

    private bool CanShowPlayerSideActions()
    {
        return GetCurrentPlayerMedia() != null
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
        if (_isDisposed || _clipEditorController == null)
        {
            return;
        }

        UpdatePlayerNavigationHotspotLayout();

        var canShow = CanShowPlayerNavigationHotspots();
        var canShowSideActions = CanShowPlayerSideActions();
        PreviousMediaHotspot.Visibility = canShow ? Visibility.Visible : Visibility.Collapsed;
        NextMediaHotspot.Visibility = canShow ? Visibility.Visible : Visibility.Collapsed;
        PlayerSideActionButtons.Visibility = canShowSideActions ? Visibility.Visible : Visibility.Collapsed;
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
            || ReferenceEquals(sender, _playerPane.ShortcutHintContainer)
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
                || ReferenceEquals(source, _playerPane.TransportBar.ClipModeToggleButton)
                || ReferenceEquals(source, _playerPane.TransportBar.ProgressSlider)
                || ReferenceEquals(source, PlayerSideActionButtons)
                || ReferenceEquals(source, _playerPane.NavigationOverlay.PlaybackModeButton)
                || ReferenceEquals(source, _playerPane.NavigationOverlay.FullScreenButton)
                || ReferenceEquals(source, PreviousMediaHotspot)
                || ReferenceEquals(source, NextMediaHotspot)
                || ReferenceEquals(source, PreviousMediaCue)
                || ReferenceEquals(source, NextMediaCue)
                || ReferenceEquals(source, DeleteCurrentMediaButton)
                || ReferenceEquals(source, _playerPane.ShortcutHintContainer)
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

    private void RestoreKeyboardFocus()
    {
        if (_isDisposed)
        {
            return;
        }

        _page.FocusKeyboardHost();
        _page.DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isDisposed)
            {
                _page.FocusKeyboardHost();
            }
        });
    }

    private TimeSpan GetCurrentPlaybackPosition() => _videoPlaybackController.GetCurrentPlaybackPosition();

    private TimeSpan GetCurrentVideoDuration() => _videoPlaybackController.GetCurrentVideoDuration();

    private async Task RestoreBossPlaybackStateAsync(
        MediaPlayerSessionTransfer recovery,
        CancellationToken cancellationToken)
    {
        try
        {
            var expectedMediaId = recovery.MediaItems[0].Id;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(100, cancellationToken);
                if (!string.Equals(ViewModel.SelectedMedia?.Id, expectedMediaId, StringComparison.Ordinal)
                    || ViewModel.SelectedMedia?.Type != MediaType.Video)
                {
                    return;
                }

                if (_videoPlaybackController.GetCurrentVideoDuration() <= TimeSpan.Zero)
                {
                    continue;
                }

                if (recovery.PositionSeconds > 0)
                {
                    _videoPlaybackController.SeekTo(TimeSpan.FromSeconds(recovery.PositionSeconds));
                }

                if (!recovery.WasPlaying && _videoPlaybackController.IsPlaying)
                {
                    _videoPlaybackController.TogglePlayPause();
                }

                if (recovery.RotationDegrees > 0)
                {
                    _videoPlaybackController.SetVideoRotation(recovery.RotationDegrees);
                }

                return;
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by another boss-mode transition or disposal.
        }
    }

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
        if (_isDisposed)
        {
            return;
        }

        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            if (_isDisposed)
            {
                return;
            }

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
            await UpdateWatchedFoldersAsync(paths);
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("MainPageShell", "LibraryPanel_Drop failed.", ex);
            if (!_isDisposed)
            {
                await ShowInfoDialogAsync("导入失败", ex.Message);
            }
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
        RefreshPlayerTags(GetCurrentPlayerMedia());
        RefreshNumpadShortcutHint();
        foreach (var media in items)
        {
            BroadcastMediaTagsChanged(media.Id, media.Tags.ToList());
        }
    }

    private void BroadcastMediaTagsChanged(string mediaId, IReadOnlyList<string> tags)
    {
        if (_isDisposed || string.IsNullOrWhiteSpace(mediaId))
        {
            return;
        }

        _page.ApplyTagsToDockedSession(mediaId, tags);
        _independentMediaWindows.ApplyExternalTags(mediaId, tags);
    }

    private void BroadcastMediaDeleted(IReadOnlyList<string> mediaIds)
    {
        if (_isDisposed || mediaIds.Count == 0)
        {
            return;
        }

        _page.RemoveDeletedMediaFromDockedSession(mediaIds);
        _independentMediaWindows.RemoveDeletedMedia(mediaIds);
    }


    private async Task ApplySettingsAsync()
    {
        await _dialogCoordinator.ShowSettingsDialogAsync();
        RefreshNumpadShortcutHint();
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
        if (_isDisposed)
        {
            return;
        }

        try
        {
            await action();
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("MainPageShell", $"ExecuteUiActionAsync failed. FailureTitle='{failureTitle}'.", ex);
            if (_isDisposed)
            {
                AppTraceLogger.Log("MainPageShell", $"ExecuteUiActionAsync suppressed dialog after disposal. FailureTitle='{failureTitle}', Error='{ex.Message}'.");
                return;
            }

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

