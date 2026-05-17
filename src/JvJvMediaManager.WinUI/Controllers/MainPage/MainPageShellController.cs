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
    private const int MaxMissingMediaBatchSize = 48;

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
    private readonly IClipTimelineEditor _clipEditorController;
    private readonly VideoPlaybackController _videoPlaybackController;
    private readonly ImagePreviewController _imagePreviewController;

    private AppWindow? _appWindow;

    private bool _isNavigationHotspotPressed;
    private bool _isNavigationHotspotTapCanceled;
    private bool _isNavigationHotspotDraggingImage;
    private bool _isDisposed;
    private int _selectionSyncQueued;
    private int _missingMediaCleanupActive;
    private readonly object _missingMediaCleanupLock = new();
    private readonly HashSet<string> _missingMediaCleanupIds = new(StringComparer.Ordinal);
    private Windows.Foundation.Point _navigationHotspotPressPoint;
    private PlayerNavigationEdge _activePlayerNavigationEdge;
    private PlayerNavigationEdge _pressedNavigationHotspotEdge;
    private string? _activePlayerMediaId;
    private MediaType? _activePlayerMediaType;

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
        _libraryPaneController = _modules.CreateLibraryPaneController(page, shell.Library, libraryPane);
        _mediaContextMenuCoordinator = _modules.CreateMediaContextMenuCoordinator(
            shell.Library,
            _dialogService,
            FocusMediaInLibraryAsync,
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
        _mediaDeletionWorkflow = _modules.CreateMediaDeletionWorkflow(
            shell.Library,
            count => ConfirmAsync(
                "确认删除",
                $"将 {count} 个文件移到回收站。此操作会同时从媒体库移除这些记录。",
                "移到回收站"),
            ShowInfoDialogAsync,
            ReleasePreviewHandles,
            ClearPlayerSelection,
            UpdatePlayer,
            ForceUpdatePlayer,
            selected => _mediaBrowserController.SyncSelectionFromViewModel(selected),
            selected => _mediaBrowserController.PreserveSelectionDuringCollectionMutation(selected));
        _mediaContextMenuCoordinator.PlaylistModified += MediaContextMenuCoordinator_PlaylistModified;
        _videoPlaybackController = _modules.CreateVideoPlaybackController(
            shell.Settings,
            shell.Library,
            shell.Player.VideoPlayback,
            playerPane.TransportBar,
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
            paths => UpdateWatchedFolders(paths),
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
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.SetDispatcher(_page.DispatcherQueue);
        _playerPane.ShortcutHintInvoked += PlayerPane_ShortcutHintInvoked;
        _playerPane.EmptyStateView.AddFolderRequested += EmptyStateView_AddFolderRequested;
        _playerPane.EmptyStateView.AddFilesRequested += EmptyStateView_AddFilesRequested;
        _playerPane.EmptyStateView.SettingsRequested += EmptyStateView_SettingsRequested;
        _playerPane.InfoOverlay.TagRemoveRequested += InfoOverlay_TagRemoveRequested;
        PlayerRoot.PointerMoved += PlayerRoot_PointerMoved;
        PlayerRoot.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(PlayerRoot_PointerPressed), true);
        PlayerRoot.PointerExited += PlayerRoot_PointerExited;
        PlayerRoot.AddHandler(UIElement.RightTappedEvent, new RightTappedEventHandler(PlayerRoot_RightTapped), true);
        VideoPlayer.GotFocus += VideoPlayer_GotFocus;
        VideoPlayer.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(VideoPlayer_PointerPressed), true);
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
        DeleteCurrentMediaButton.Click += DeleteCurrentMediaButton_Click;
        ClipModeToggleButton.Click += ToggleClipMode_Click;
        SplitClipButton.Click += SplitClip_Click;
        ClearClipButton.Click += ClearClip_Click;
        ExportClipButton.Click += ExportClip_Click;
        _shell.Player.EmptyStateVisibility = Visibility.Visible;
        _shell.Player.PlayerInfoVisibility = Visibility.Collapsed;
        _shell.Player.ImagePreview.ZoomBadgeVisibility = Visibility.Collapsed;
        _videoPlaybackController.Clear();
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

            _libraryPaneController.EnsurePaneState(preferOpen: true);
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
        PlayerRoot.RemoveHandler(UIElement.PointerPressedEvent, new PointerEventHandler(PlayerRoot_PointerPressed));
        PlayerRoot.PointerExited -= PlayerRoot_PointerExited;
        PlayerRoot.RemoveHandler(UIElement.RightTappedEvent, new RightTappedEventHandler(PlayerRoot_RightTapped));
        VideoPlayer.GotFocus -= VideoPlayer_GotFocus;
        VideoPlayer.RemoveHandler(UIElement.PointerPressedEvent, new PointerEventHandler(VideoPlayer_PointerPressed));
        PreviousMediaHotspot.PointerEntered -= PreviousMediaHotspot_PointerEntered;
        PreviousMediaHotspot.PointerPressed -= PlayerNavigationHotspot_PointerPressed;
        PreviousMediaHotspot.PointerMoved -= PlayerNavigationHotspot_PointerMoved;
        PreviousMediaHotspot.PointerReleased -= PlayerNavigationHotspot_PointerReleased;
        PreviousMediaHotspot.PointerCaptureLost -= PlayerNavigationHotspot_PointerCaptureLost;
        NextMediaHotspot.PointerEntered -= NextMediaHotspot_PointerEntered;
        NextMediaHotspot.PointerPressed -= PlayerNavigationHotspot_PointerPressed;
        NextMediaHotspot.PointerMoved -= PlayerNavigationHotspot_PointerMoved;
        NextMediaHotspot.PointerReleased -= PlayerNavigationHotspot_PointerReleased;
        NextMediaHotspot.PointerCaptureLost -= PlayerNavigationHotspot_PointerCaptureLost;
        DeleteCurrentMediaButton.Click -= DeleteCurrentMediaButton_Click;
        ClipModeToggleButton.Click -= ToggleClipMode_Click;
        SplitClipButton.Click -= SplitClip_Click;
        ClearClipButton.Click -= ClearClip_Click;
        ExportClipButton.Click -= ExportClip_Click;
        _mediaBrowserController.Dispose();
        _playlistRailCoordinator.Dispose();
        _libraryPaneController.Dispose();
        _videoPlaybackController.Dispose();
        _clipEditorController.Dispose();
        _imagePreviewController.Dispose();
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
            AppTraceLogger.Log(
                "MainPageShell",
                $"SelectedMedia changed. SelectedId='{selected?.Id ?? "<null>"}', Type='{selected?.Type.ToString() ?? "<null>"}', ActiveId='{_activePlayerMediaId ?? "<null>"}', ActiveType='{_activePlayerMediaType.ToString() ?? "<null>"}'.");
            _videoPlaybackController.NotifySelectedMediaChanged(selected);
            QueueSelectionSync();
        }

        if (e.PropertyName == nameof(LibraryShellViewModel.IsLoading)
            && !ViewModel.IsLoading
            && ViewModel.SelectedMedia == null)
        {
            QueueSelectionSync();
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
        if (!IsMediaFileAvailable(media))
        {
            QueueMissingMediaCleanup(media, "update-player");
            return;
        }

        var isSameActiveMedia = string.Equals(_activePlayerMediaId, media.Id, StringComparison.Ordinal)
            && _activePlayerMediaType == media.Type;
        AppTraceLogger.Log(
            "MainPageShell",
            $"UpdatePlayer start. MediaId='{media.Id}', File='{media.FileName}', Type='{media.Type}', SameActive={isSameActiveMedia}, ActiveId='{_activePlayerMediaId ?? "<null>"}', ActiveType='{_activePlayerMediaType.ToString() ?? "<null>"}'.");
        _shell.Player.EmptyStateVisibility = Visibility.Collapsed;
        _shell.Player.PlayerInfoVisibility = Visibility.Visible;
        _shell.Player.PlayerFileName = media.FileName;
        _shell.Player.PlayerResolution = media.ResolutionText;
        RefreshPlayerTags(media);
        _clipEditorController.HandleMediaChanged(media);

        if (isSameActiveMedia)
        {
            return;
        }

        if (media.Type == MediaType.Video)
        {
            _shell.Player.ImagePreview.ZoomBadgeVisibility = Visibility.Collapsed;
            _imagePreviewController.Clear();
            _videoPlaybackController.ShowVideo(media);
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
    }

    private void ForceUpdatePlayer(MediaItemViewModel media)
    {
        AppTraceLogger.Log(
            "MainPageShell",
            $"ForceUpdatePlayer requested. MediaId='{media.Id}', PreviousActiveId='{_activePlayerMediaId ?? "<null>"}', PreviousActiveType='{_activePlayerMediaType.ToString() ?? "<null>"}'.");
        _activePlayerMediaId = null;
        _activePlayerMediaType = null;
        UpdatePlayer(media);
    }

    private bool IsMediaFileAvailable(MediaItemViewModel media)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(media.FileSystemPath) && File.Exists(media.FileSystemPath);
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("MainPageShell", $"IsMediaFileAvailable failed. MediaId='{media.Id}', Path='{media.FileSystemPath}'.", ex);
            return false;
        }
    }

    private void QueueMissingMediaCleanup(MediaItemViewModel media, string source)
    {
        lock (_missingMediaCleanupLock)
        {
            if (!_missingMediaCleanupIds.Add(media.Id))
            {
                return;
            }
        }

        AppTraceLogger.Log(
            "MainPageShell",
            $"Missing media detected. Source={source}, MediaId='{media.Id}', File='{media.FileName}', Path='{media.FileSystemPath}'.");
        ClearPlayerSelection();
        _ = HandleMissingMediaAsync(media);
    }

    private async Task HandleMissingMediaAsync(MediaItemViewModel media)
    {
        var cleanupIds = new HashSet<string>(StringComparer.Ordinal);
        MediaItemViewModel? nextSelection = media;
        var removedCount = 0;

        Interlocked.Increment(ref _missingMediaCleanupActive);
        try
        {
            for (var round = 0; round < 4 && nextSelection != null && !IsMediaFileAvailable(nextSelection); round++)
            {
                var batch = BuildMissingMediaCleanupBatch(nextSelection);
                if (batch.Count == 0)
                {
                    break;
                }

                lock (_missingMediaCleanupLock)
                {
                    foreach (var item in batch)
                    {
                        _missingMediaCleanupIds.Add(item.Id);
                        cleanupIds.Add(item.Id);
                    }
                }

                removedCount += batch.Count;
                AppTraceLogger.Log(
                    "MainPageShell",
                    $"Missing media cleanup batch deleting. Round={round + 1}, Count={batch.Count}, FirstId='{batch[0].Id}', FirstFile='{batch[0].FileName}'.");
                nextSelection = await ViewModel.DeleteMediaAsync(batch);
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
            lock (_missingMediaCleanupLock)
            {
                _missingMediaCleanupIds.Remove(media.Id);
                foreach (var id in cleanupIds)
                {
                    _missingMediaCleanupIds.Remove(id);
                }
            }

            Interlocked.Decrement(ref _missingMediaCleanupActive);
            SyncSelectionFromViewModel();
        }
    }

    private List<MediaItemViewModel> BuildMissingMediaCleanupBatch(MediaItemViewModel media)
    {
        var list = ViewModel.FilteredMediaItems;
        var index = list.IndexOf(media);
        if (index < 0)
        {
            return IsMediaFileAvailable(media) ? new List<MediaItemViewModel>() : new List<MediaItemViewModel> { media };
        }

        var batch = new List<MediaItemViewModel>(capacity: Math.Min(MaxMissingMediaBatchSize, list.Count - index));
        for (var currentIndex = index; currentIndex < list.Count && batch.Count < MaxMissingMediaBatchSize; currentIndex++)
        {
            var candidate = list[currentIndex];
            if (IsMediaFileAvailable(candidate))
            {
                break;
            }

            batch.Add(candidate);
        }

        return batch;
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
        AppTraceLogger.Log(
            "MainPageShell",
            $"SyncSelectionFromViewModel start. SelectedId='{selected?.Id ?? "<null>"}', SelectedType='{selected?.Type.ToString() ?? "<null>"}', ActiveId='{_activePlayerMediaId ?? "<null>"}', ActiveType='{_activePlayerMediaType.ToString() ?? "<null>"}', IsLoading={ViewModel.IsLoading}.");
        _mediaBrowserController.SyncSelectionFromViewModel(selected);
        RefreshNumpadShortcutHint();

        if (Volatile.Read(ref _missingMediaCleanupActive) > 0)
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

        var isSameActiveMedia = string.Equals(_activePlayerMediaId, selected.Id, StringComparison.Ordinal)
            && _activePlayerMediaType == selected.Type;
        UpdatePlayer(selected);
        if (!isSameActiveMedia)
        {
            _mediaBrowserController.RevealSelectedMedia(selected);
        }
    }

    public async Task HandleKeyDownAsync(KeyRoutedEventArgs e)
    {
        e.Handled = await HandleShortcutKeyAsync(e.Key);
        if (e.Handled)
        {
            ShowControls();
        }
    }

    public bool ShouldHandleWindowShortcutKey(VirtualKey key)
    {
        if (_isDisposed)
        {
            return false;
        }

        if (key == VirtualKey.F1)
        {
            return true;
        }

        var ctrlDown = IsCtrlKeyDown();
        if (ctrlDown)
        {
            return key is VirtualKey.O or VirtualKey.A or VirtualKey.R or VirtualKey.F or VirtualKey.T;
        }

        if (IsTextInputFocused())
        {
            return false;
        }

        if (IsNumpadTagShortcutKey(key))
        {
            return true;
        }

        return IsShortcutKeyForCurrentSelection(key);
    }

    public Task<bool> HandleVirtualKeyDownAsync(VirtualKey key)
    {
        return HandleShortcutKeyAsync(key);
    }

    private async void PlayerPane_ShortcutHintInvoked(object? sender, int digit)
    {
        if (_isDisposed)
        {
            return;
        }

        await ExecuteUiActionAsync(
            () => TryApplyNumpadTagShortcutAsync(digit),
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

        if (ViewModel.SelectedMedia?.Type != MediaType.Video)
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

        if (ViewModel.SelectedMedia?.Type != MediaType.Video || !_clipEditorController.IsClipModeActive)
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

    private async Task<bool> HandleShortcutKeyAsync(VirtualKey key)
    {
        if (_isDisposed)
        {
            return false;
        }

        if (key == VirtualKey.F1)
        {
            ToggleNumpadShortcutHint();
            return true;
        }

        var ctrlDown = IsCtrlKeyDown();
        var shiftDown = IsShiftKeyDown();

        if (ctrlDown)
        {
            if (shiftDown && key == VirtualKey.O)
            {
                return TryOpenSelectedMediaFolderFromShortcut();
            }

            if (shiftDown && key == VirtualKey.A)
            {
                return await TryAddSelectionToPlaylistFromShortcutAsync();
            }

            if (shiftDown && key == VirtualKey.R)
            {
                return await TryRemoveSelectionFromPlaylistFromShortcutAsync();
            }

            if (key == VirtualKey.O)
            {
                await AddFolderAsync();
                return true;
            }

            if (key == VirtualKey.F)
            {
                _libraryPaneController.SetPaneOpen(true);
                _mediaBrowserController.FocusSearchBox();
                return true;
            }

            if (key == VirtualKey.T)
            {
                await EditSelectedTagsAsync();
                return true;
            }
        }

        if (IsTextInputFocused())
        {
            return false;
        }

        if (await TryApplyNumpadTagShortcutAsync(key))
        {
            return true;
        }

        if (ViewModel.SelectedMedia == null)
        {
            return false;
        }

        if (key == VirtualKey.Space)
        {
            return TryTogglePlaybackFromShortcut();
        }

        if (key == VirtualKey.Delete)
        {
            return await TryDeleteSelectedFromShortcutAsync();
        }

        if (key == VirtualKey.Left)
        {
            await NavigateRelativeAsync(-1);
            return true;
        }

        if (key == VirtualKey.Right)
        {
            await NavigateRelativeAsync(1);
            return true;
        }

        if (key == VirtualKey.Up && ViewModel.SelectedMedia.Type == MediaType.Video)
        {
            SeekRelative(-5);
            return true;
        }

        if (key == VirtualKey.Down && ViewModel.SelectedMedia.Type == MediaType.Video)
        {
            SeekRelative(5);
            return true;
        }

        if (key == VirtualKey.PageUp)
        {
            await NavigateRelativeAsync(-1);
            return true;
        }

        if (key == VirtualKey.PageDown)
        {
            await NavigateRelativeAsync(1);
            return true;
        }

        if (ViewModel.SelectedMedia.Type == MediaType.Video && _clipEditorController.IsClipModeActive && key == VirtualKey.E)
        {
            await _clipEditorController.ExportCurrentClipAsync();
            return true;
        }

        if (ViewModel.SelectedMedia.Type == MediaType.Video && _clipEditorController.IsClipModeActive && key == VirtualKey.K)
        {
            return TrySplitClipFromShortcut();
        }

        if (ViewModel.SelectedMedia.Type == MediaType.Image)
        {
            if (key == VirtualKey.Add || key == (VirtualKey)187)
            {
                _imagePreviewController.ZoomBy(0.1);
                return true;
            }

            if (key == VirtualKey.Subtract || key == (VirtualKey)189)
            {
                _imagePreviewController.ZoomBy(-0.1);
                return true;
            }

            if (key == VirtualKey.Number0)
            {
                _imagePreviewController.ResetZoom();
                return true;
            }
        }

        return false;
    }

    private bool IsShortcutKeyForCurrentSelection(VirtualKey key)
    {
        var selected = ViewModel.SelectedMedia;
        if (selected == null)
        {
            return false;
        }

        if (key is VirtualKey.Space
            or VirtualKey.Delete
            or VirtualKey.Left
            or VirtualKey.Right
            or VirtualKey.PageUp
            or VirtualKey.PageDown)
        {
            return true;
        }

        if (selected.Type == MediaType.Video)
        {
            return key is VirtualKey.Up
                or VirtualKey.Down
                || (_clipEditorController.IsClipModeActive && key is VirtualKey.E or VirtualKey.K);
        }

        if (selected.Type == MediaType.Image)
        {
            return key is VirtualKey.Add
                or VirtualKey.Subtract
                or VirtualKey.Number0
                or (VirtualKey)187
                or (VirtualKey)189;
        }

        return false;
    }

    private static bool IsNumpadTagShortcutKey(VirtualKey key)
    {
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

    private bool IsTextInputFocused()
    {
        return FocusManager.GetFocusedElement(_page.XamlRoot) is TextBox or PasswordBox or RichEditBox or AutoSuggestBox or ComboBox;
    }

    private static bool IsCtrlKeyDown()
    {
        return (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
    }

    private static bool IsShiftKeyDown()
    {
        return (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
    }

    private void ToggleNumpadShortcutHint()
    {
        RefreshNumpadShortcutHint();
        _shell.Player.ShortcutHintVisibility = _shell.Player.ShortcutHintVisibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void RefreshNumpadShortcutHint()
    {
        var selectedTags = GetCurrentPlayerMedia()?.Tags ?? [];
        var items = ViewModel.NumpadTagShortcuts
            .Select((tag, index) => new { Tag = tag, Digit = index + 1 })
            .Where(item => !string.IsNullOrWhiteSpace(item.Tag))
            .ToList();

        _shell.Player.ShortcutHintItems.Clear();
        foreach (var item in items)
        {
            _shell.Player.ShortcutHintItems.Add(new NumpadTagShortcutHintItem
            {
                Digit = item.Digit,
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
        var isOverlayInteraction = IsPlayerOverlayInteractionSource(e.OriginalSource as DependencyObject);

        if (ViewModel.SelectedMedia?.Type == MediaType.Video
            && _clipEditorController.IsClipModeActive
            && !isOverlayInteraction)
        {
            _clipEditorController.HandlePreviewSurfaceInteraction();
        }

        if (ViewModel.SelectedMedia?.Type == MediaType.Video
            && _videoPlaybackController.AreControlsVisible
            && !isOverlayInteraction)
        {
            _videoPlaybackController.HideControlsImmediately();
        }
        else
        {
            ShowControls();
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
        if (list.Count == 0 || ViewModel.SelectedMedia == null)
        {
            return;
        }

        if (_videoPlaybackController.IsShuffleMode && list.Count > 1)
        {
            _videoPlaybackController.TryNavigateShuffle(offset);
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

        await _clipEditorController.ExportCurrentClipAsync();
    }

    private void SplitClip_Click(object sender, RoutedEventArgs e)
    {
        _clipEditorController.SplitSegmentAtCurrentPosition();
    }

    private Task DeleteSelectedAsync()
    {
        return DeleteSelectionAsync(_mediaBrowserController.GetSelectedItems());
    }

    private Task DeleteCurrentPlayerMediaAsync()
    {
        var media = GetCurrentPlayerMedia();
        if (media == null)
        {
            return Task.CompletedTask;
        }

        AppTraceLogger.Log(
            "MainPageShell",
            $"DeleteCurrentPlayerMediaAsync start. MediaId='{media.Id}', File='{media.FileName}', Type='{media.Type}'.");

        return _mediaDeletionWorkflow.DeleteAsync(new[] { media });
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
        return _mediaDeletionWorkflow.DeleteAsync(initialSelection);
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

    private MediaItemViewModel? GetCurrentPlayerMedia()
    {
        return ViewModel.SelectedMedia;
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

    private bool CanShowDeleteCurrentMediaButton()
    {
        return ViewModel.SelectedMedia != null
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

        var canShow = CanShowPlayerNavigationHotspots() && _videoPlaybackController.AreControlsVisible;
        var canShowDelete = CanShowDeleteCurrentMediaButton() && _videoPlaybackController.AreControlsVisible;
        PreviousMediaHotspot.Visibility = canShow ? Visibility.Visible : Visibility.Collapsed;
        NextMediaHotspot.Visibility = canShow ? Visibility.Visible : Visibility.Collapsed;
        DeleteCurrentMediaButton.Visibility = canShowDelete ? Visibility.Visible : Visibility.Collapsed;
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
        RefreshPlayerTags(GetCurrentPlayerMedia());
        RefreshNumpadShortcutHint();
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
