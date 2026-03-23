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
    private const double DefaultLibraryPaneWidth = 360;
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
    private readonly IContentDialogService _dialogService;
    private readonly DialogWorkflowCoordinator _dialogCoordinator;
    private readonly ClipEditorController _clipEditorController;
    private readonly VideoPlaybackController _videoPlaybackController;
    private readonly ImagePreviewController _imagePreviewController;

    private readonly DebounceDispatcher _debouncer = new();
    private AppWindow? _appWindow;

    private bool _isSyncingSelection;
    private ScrollViewer? _gridViewScrollViewer;
    private bool _isNavigationHotspotPressed;
    private bool _isNavigationHotspotTapCanceled;
    private bool _isNavigationHotspotDraggingImage;
    private bool _isResizingLibraryPane;
    private Windows.Foundation.Point _navigationHotspotPressPoint;
    private double _libraryPaneWidth = DefaultLibraryPaneWidth;
    private PlayerNavigationEdge _activePlayerNavigationEdge;
    private PlayerNavigationEdge _pressedNavigationHotspotEdge;

    private Grid RootLayout => _page.RootLayout;
    private SplitView LibrarySplitView => _page.LibrarySplitView;
    private Grid LibraryPaneRoot => _libraryPane.PaneRoot;
    private Grid LibraryPaneExpandedContent => _libraryPane.ExpandedContent;
    private Border LibraryDropTarget => _libraryPane.DropTargetBorder;
    private Thumb LibraryPaneResizer => _libraryPane.PaneResizer;
    private Button MediaTabButton => _libraryPane.PlaylistRail.MediaTabButton;
    private ListView PlaylistRailListView => _libraryPane.PlaylistRail.PlaylistRailListView;
    private Button CreatePlaylistRailButton => _libraryPane.PlaylistRail.CreatePlaylistRailButton;
    private Button SelectedPlaylistTitleButton => _libraryPane.HeaderView.SelectedPlaylistTitleButton;
    private StackPanel MediaActionsPanel => _libraryPane.HeaderView.MediaActionsPanel;
    private Button RefreshButton => _libraryPane.HeaderView.RefreshButton;
    private Button ViewModeToggleButton => _libraryPane.HeaderView.ViewModeToggleButton;
    private Button SortButton => _libraryPane.HeaderView.SortButton;
    private TextBlock StatusText => _libraryPane.HeaderView.StatusText;
    private ProgressBar ScanProgressBar => _libraryPane.HeaderView.ScanProgressBar;
    private TextBlock ScanPathText => _libraryPane.HeaderView.ScanPathText;
    private TextBox SearchBox => _libraryPane.FilterBarView.SearchBox;
    private ItemsControl SelectedTagsControl => _libraryPane.FilterBarView.SelectedTagsControl;
    private ListView ListView => _libraryPane.BrowserView.ListView;
    private GridView GridView => _libraryPane.BrowserView.GridView;
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
        IContentDialogService dialogService)
    {
        _page = page;
        _shell = shell;
        _libraryPane = libraryPane;
        _playerPane = playerPane;
        _dialogService = dialogService;
        _dialogCoordinator = new DialogWorkflowCoordinator(shell.Library, dialogService);
        _videoPlaybackController = new VideoPlaybackController(
            shell.Library,
            shell.Player.VideoPlayback,
            playerPane.TransportBar,
            playerPane.VideoViewport.VideoPlayer,
            _page.DispatcherQueue,
            GetAppWindow,
            NavigateRelativeAsync,
            () => ViewModel.SelectedMedia?.Type == MediaType.Video && EmptyState.Visibility != Visibility.Visible,
            () => _page.Focus(FocusState.Programmatic),
            RefreshPlayerNavigationHotspots,
            duration => _clipEditorController!.HandleMediaOpened(duration));
        _clipEditorController = new ClipEditorController(
            shell.Library,
            shell.Player.ClipEditor,
            ClipModeToggleButton,
            playerPane.ClipBarView,
            _dialogCoordinator,
            _videoPlaybackController.GetCurrentPlaybackPosition,
            _videoPlaybackController.GetCurrentVideoDuration,
            paths => UpdateWatchedFolders(paths),
            ShowControls);
        _imagePreviewController = new ImagePreviewController(
            shell.Library,
            shell.Player.ImagePreview,
            playerPane.ImageViewport,
            playerPane.PlayerOverlay,
            RefreshPlayerNavigationHotspots);
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.Playlists.CollectionChanged += Playlists_CollectionChanged;
        ViewModel.SelectedTags.CollectionChanged += SelectedTags_CollectionChanged;
        ViewModel.SetDispatcher(_page.DispatcherQueue);
        MediaTabButton.Click += MediaTabButton_Click;
        PlaylistRailListView.ItemClick += PlaylistRailListView_ItemClick;
        PlaylistRailListView.DragItemsCompleted += PlaylistRailListView_DragItemsCompleted;
        PlaylistRailListView.RightTapped += PlaylistRailListView_RightTapped;
        CreatePlaylistRailButton.Click += CreatePlaylist_Click;
        SelectedPlaylistTitleButton.Click += SelectedPlaylistTitleButton_Click;
        RefreshButton.Click += Refresh_Click;
        ViewModeToggleButton.Click += ToggleViewMode_Click;
        SortButton.Click += Sort_Click;
        SearchBox.TextChanged += SearchBox_TextChanged;
        SearchBox.KeyDown += SearchBox_KeyDown;
        _libraryPane.FilterBarView.TagRemoveRequested += (_, tag) => ViewModel.RemoveSelectedTagFilter(tag);
        ListView.ItemClick += Media_ItemClick;
        ListView.ContainerContentChanging += Media_ContainerContentChanging;
        ListView.SelectionChanged += Media_SelectionChanged;
        ListView.RightTapped += MediaView_RightTapped;
        GridView.ItemClick += Media_ItemClick;
        GridView.ContainerContentChanging += Media_ContainerContentChanging;
        GridView.SelectionChanged += Media_SelectionChanged;
        GridView.RightTapped += MediaView_RightTapped;
        GridView.Loaded += GridView_Loaded;
        GridView.SizeChanged += GridView_SizeChanged;
        LibraryDropTarget.DragOver += LibraryPanel_DragOver;
        LibraryDropTarget.Drop += LibraryPanel_Drop;
        LibraryPaneResizer.DragStarted += LibraryPaneResizer_DragStarted;
        LibraryPaneResizer.DragDelta += LibraryPaneResizer_DragDelta;
        LibraryPaneResizer.DragCompleted += LibraryPaneResizer_DragCompleted;
        LibraryPaneRoot.SizeChanged += LibraryPaneRoot_SizeChanged;
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
        ClipPlanButton.Click += ClipPlan_Click;
        ClearClipButton.Click += ClearClip_Click;
        ExportClipButton.Click += ExportClip_Click;
        ListView.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(MediaLibraryView_PointerWheelChanged), true);
        GridView.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(MediaLibraryView_PointerWheelChanged), true);
        PlaylistRailListView.ItemsSource = ViewModel.Playlists;
        SelectedTagsControl.ItemsSource = ViewModel.SelectedTags;
        ListView.ItemsSource = ViewModel.FilteredMediaItems;
        GridView.ItemsSource = ViewModel.FilteredMediaItems;
        StatusText.Text = ViewModel.StatusMessage;
        LibrarySplitView.OpenPaneLength = _libraryPaneWidth;
        RefreshPlaylistSelection();
        UpdateLibraryPaneUi();
        UpdateLibraryPanePresentation();

        UpdateViewModeButtonUi();
        UpdateSortButtonUi();
        _shell.Player.ImagePreview.ZoomBadgeVisibility = Visibility.Collapsed;
        _videoPlaybackController.Clear();
        _clipEditorController.Refresh();
        RefreshScanProgressVisibility();
    }

    public LibraryShellViewModel ViewModel => _shell.Library;

    public async Task InitializeAsync()
    {
        await ExecuteUiActionAsync(async () =>
        {
            await ViewModel.InitializeAsync();
            RefreshTagChips();
            RefreshPlaylistSelection();
            RefreshScanProgressVisibility();
            UpdateMediaItemSize();
            ConfigureGridViewScrolling();
            UpdateLibraryPaneState(preferOpen: true);
        }, "初始化失败");
    }

    public void Dispose()
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.Playlists.CollectionChanged -= Playlists_CollectionChanged;
        ViewModel.SelectedTags.CollectionChanged -= SelectedTags_CollectionChanged;
        GridView.Loaded -= GridView_Loaded;
        GridView.SizeChanged -= GridView_SizeChanged;
        LibraryPaneRoot.SizeChanged -= LibraryPaneRoot_SizeChanged;
        _videoPlaybackController.Dispose();
        _imagePreviewController.Dispose();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryShellViewModel.SelectedMedia))
        {
            _page.DispatcherQueue.TryEnqueue(SyncSelectionFromViewModel);
        }
        else if (e.PropertyName == nameof(LibraryShellViewModel.SelectedPlaylist))
        {
            _page.DispatcherQueue.TryEnqueue(() =>
            {
                RefreshPlaylistSelection();
                UpdateLibraryPaneUi();
            });
        }
        else if (e.PropertyName == nameof(LibraryShellViewModel.IsScanning)
            || e.PropertyName == nameof(LibraryShellViewModel.ScanCurrentPath)
            || e.PropertyName == nameof(LibraryShellViewModel.ScanProgressMaximum)
            || e.PropertyName == nameof(LibraryShellViewModel.ScanProgressValue))
        {
            _page.DispatcherQueue.TryEnqueue(RefreshScanProgressVisibility);
        }
        else if (e.PropertyName == nameof(LibraryShellViewModel.StatusMessage))
        {
            _page.DispatcherQueue.TryEnqueue(() => StatusText.Text = ViewModel.StatusMessage);
        }
    }

    private void SelectedTags_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshTagChips();
    }

    private void Playlists_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshPlaylistSelection();
        UpdateLibraryPaneUi();
    }

    public Task HandleAddFolderFromTitleBarAsync()
    {
        ActivateMediaLibrary(openPane: true);
        return AddFolderAsync();
    }

    public Task HandleAddFilesFromTitleBarAsync()
    {
        ActivateMediaLibrary(openPane: true);
        return AddFilesAsync();
    }

    public Task HandleOpenSettingsFromTitleBarAsync()
    {
        return ApplySettingsAsync();
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        await AddFilesAsync();
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        await AddFolderAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteUiActionAsync(async () =>
        {
            if (ViewModel.WatchedFolders.Count == 0)
            {
                return;
            }

            await ViewModel.RescanFoldersAsync();
        }, "刷新媒体库失败");
    }

    private async void EditTags_Click(object sender, RoutedEventArgs e)
    {
        await EditSelectedTagsAsync();
    }

    private async void AddToPlaylist_Click(object sender, RoutedEventArgs e)
    {
        await AddSelectionToPlaylistAsync();
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        await HandleOpenSettingsFromTitleBarAsync();
    }

    private async void FolderLock_Click(object sender, RoutedEventArgs e)
    {
        await _dialogCoordinator.ShowFolderLockDialogAsync();
    }

    private Task AddFolderAsync()
    {
        return ExecuteUiActionAsync(async () =>
        {
            var window = App.MainWindow;
            if (window == null)
            {
                return;
            }

            var folder = await PickerHelpers.PickFolderAsync(window);
            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            await ViewModel.AddFolderAsync(folder);
            UpdateWatchedFolders(new[] { folder }, refreshMedia: false);
        }, "导入文件夹失败");
    }

    private Task AddFilesAsync()
    {
        return ExecuteUiActionAsync(async () =>
        {
            var window = App.MainWindow;
            if (window == null)
            {
                return;
            }

            var paths = await PickerHelpers.PickFilesAsync(window);
            if (paths.Count == 0)
            {
                return;
            }

            await ViewModel.AddFilesAsync(paths);
            UpdateWatchedFolders(paths, refreshMedia: false);
        }, "导入文件失败");
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text ?? string.Empty;
        _debouncer.Debounce(TimeSpan.FromMilliseconds(250), () =>
        {
            _page.DispatcherQueue.TryEnqueue(() => ViewModel.SearchQuery = query);
        });
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
        {
            return;
        }

        var value = SearchBox.Text?.Trim() ?? string.Empty;
        if (!value.StartsWith("#", StringComparison.Ordinal) || value.Length <= 1)
        {
            return;
        }

        var tag = value[1..].Trim();
        if (tag.Length == 0)
        {
            return;
        }

        var needsImmediateRefresh = string.IsNullOrWhiteSpace(ViewModel.SearchQuery);

        if (!ViewModel.SelectedTags.Any(existing => string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase)))
        {
            ViewModel.SelectedTags.Add(tag);
            if (needsImmediateRefresh)
            {
                _ = ViewModel.RefreshMediaAsync(false);
            }
        }

        SearchBox.Text = string.Empty;
        ViewModel.SearchQuery = string.Empty;
        e.Handled = true;
    }

    private void SelectedTagRemove_Click(object sender, RoutedEventArgs e)
    {
        var tag = sender is Button button && button.Tag is string directTag
            ? directTag
            : e.OriginalSource is Button { Tag: string originalTag }
                ? originalTag
                : null;

        if (!string.IsNullOrWhiteSpace(tag))
        {
            ViewModel.RemoveSelectedTagFilter(tag);
        }
    }

    private void MediaTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (LibrarySplitView.IsPaneOpen && ViewModel.SelectedPlaylist == null)
        {
            SetLibraryPaneOpen(false);
            return;
        }

        ActivateMediaLibrary(openPane: true);
    }

    private void PlaylistRailListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not Playlist playlist)
        {
            return;
        }

        var isCurrentPlaylist = string.Equals(ViewModel.SelectedPlaylist?.Id, playlist.Id, StringComparison.Ordinal);
        if (LibrarySplitView.IsPaneOpen && isCurrentPlaylist)
        {
            SetLibraryPaneOpen(false);
            return;
        }

        ActivatePlaylist(playlist, openPane: true);
    }

    private void AllMedia_Click(object sender, RoutedEventArgs e)
    {
        ActivateMediaLibrary(openPane: true);
    }

    private async void CreatePlaylist_Click(object sender, RoutedEventArgs e)
    {
        var name = await _dialogService.ShowTextInputAsync("新建播放列表", "播放列表名称", string.Empty, "创建");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            ViewModel.CreatePlaylist(name);
            SetLibraryPaneOpen(true);
            UpdateLibraryPaneUi();
        }
        catch (Exception ex)
        {
            await ShowInfoDialogAsync("创建失败", ex.Message);
        }
    }

    private async void RenamePlaylist_Click(object sender, RoutedEventArgs e)
    {
        var playlist = ViewModel.SelectedPlaylist;
        if (playlist == null)
        {
            await ShowInfoDialogAsync("提示", "请先选择一个播放列表。");
            return;
        }

        await RenamePlaylistAsync(playlist);
    }

    private async void DeletePlaylist_Click(object sender, RoutedEventArgs e)
    {
        var playlist = ViewModel.SelectedPlaylist;
        if (playlist == null)
        {
            await ShowInfoDialogAsync("提示", "请先选择一个播放列表。");
            return;
        }

        await DeletePlaylistAsync(playlist);
    }

    private void PlaylistRailListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        ViewModel.UpdatePlaylistOrder(ViewModel.Playlists.ToList());
        RefreshPlaylistSelection();
    }

    private async void SelectedPlaylistTitleButton_Click(object sender, RoutedEventArgs e)
    {
        var playlist = ViewModel.SelectedPlaylist;
        if (playlist == null)
        {
            return;
        }

        await RenamePlaylistAsync(playlist);
    }

    private void PlaylistRailListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject origin)
        {
            return;
        }

        var container = FindAncestor<ListViewItem>(origin);
        if (container?.Content is not Playlist playlist)
        {
            return;
        }

        PlaylistRailItem_RightTapped(container, e);
    }

    private void PlaylistRailItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not Playlist playlist)
        {
            return;
        }

        e.Handled = true;

        var flyout = new MenuFlyout();

        var renameItem = new MenuFlyoutItem { Text = "重命名" };
        renameItem.Click += async (_, _) => await RenamePlaylistAsync(playlist);

        var colorItem = new MenuFlyoutItem { Text = "更改颜色" };
        colorItem.Click += async (_, _) => await ChangePlaylistColorAsync(playlist);

        var deleteItem = new MenuFlyoutItem { Text = "删除" };
        deleteItem.Click += async (_, _) => await DeletePlaylistAsync(playlist);

        flyout.Items.Add(renameItem);
        flyout.Items.Add(colorItem);
        flyout.Items.Add(deleteItem);
        flyout.ShowAt(element, e.GetPosition(element));
    }

    private void ToggleViewMode_Click(object sender, RoutedEventArgs e)
    {
        SetMediaViewMode(ViewModel.ViewMode == MediaViewMode.List
            ? MediaViewMode.Grid
            : MediaViewMode.List);
    }

    private void SetMediaViewMode(MediaViewMode viewMode)
    {
        ViewModel.ViewMode = viewMode;
        ListView.Visibility = viewMode == MediaViewMode.List ? Visibility.Visible : Visibility.Collapsed;
        GridView.Visibility = viewMode == MediaViewMode.Grid ? Visibility.Visible : Visibility.Collapsed;
        UpdateMediaItemSize();
        UpdateViewModeButtonUi();
        if (viewMode == MediaViewMode.Grid)
        {
            ConfigureGridViewScrolling();
            _page.DispatcherQueue.TryEnqueue(ConfigureGridViewScrolling);
        }
        _page.DispatcherQueue.TryEnqueue(SyncSelectionFromViewModel);
    }

    private void UpdateViewModeButtonUi()
    {
        if (ViewModeToggleButton == null)
        {
            return;
        }

        var switchToGrid = ViewModel.ViewMode == MediaViewMode.List;
        SetButtonGlyph(ViewModeToggleButton, switchToGrid ? "\uECA5" : "\uE8FD");
        ToolTipService.SetToolTip(ViewModeToggleButton, switchToGrid ? "切换到网格" : "切换到列表");
    }

    private void UpdateSortButtonUi()
    {
        if (SortButton == null)
        {
            return;
        }

        var fieldLabel = ViewModel.SortField == MediaSortField.FileName ? "名称" : "时间";
        var orderLabel = ViewModel.SortOrder == MediaSortOrder.Asc ? "升序" : "降序";
        ToolTipService.SetToolTip(SortButton, $"排序：{fieldLabel} {orderLabel}");
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SortField == MediaSortField.FileName && ViewModel.SortOrder == MediaSortOrder.Asc)
        {
            ViewModel.ToggleSort(MediaSortField.FileName);
            UpdateSortButtonUi();
            return;
        }

        if (ViewModel.SortField == MediaSortField.FileName && ViewModel.SortOrder == MediaSortOrder.Desc)
        {
            ViewModel.ToggleSort(MediaSortField.ModifiedAt);
            UpdateSortButtonUi();
            return;
        }

        if (ViewModel.SortField == MediaSortField.ModifiedAt && ViewModel.SortOrder == MediaSortOrder.Asc)
        {
            ViewModel.ToggleSort(MediaSortField.ModifiedAt);
            UpdateSortButtonUi();
            return;
        }

        ViewModel.ToggleSort(MediaSortField.FileName);
        UpdateSortButtonUi();
    }

    private void Media_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MediaItemViewModel media)
        {
            SelectMedia(media);
        }
    }

    private void Media_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingSelection)
        {
            return;
        }

        if (sender is ListView listView && listView.SelectedItem is MediaItemViewModel media)
        {
            SelectMedia(media);
        }
        else if (sender is GridView gridView && gridView.SelectedItem is MediaItemViewModel gridMedia)
        {
            SelectMedia(gridMedia);
        }
        else if (sender is ListView || sender is GridView)
        {
            ViewModel.SelectedMedia = null;
        }

        UpdateSelectedStateFlags();
    }

    private async void MediaView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not ListViewBase listViewBase)
        {
            return;
        }

        if (TryGetMediaFromElement(listViewBase, e.OriginalSource as DependencyObject, out var media) && media != null)
        {
            EnsureRightTappedSelection(listViewBase, media);
        }

        var selected = GetSelectedItems().ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var flyout = BuildMediaContextFlyout(selected);
        flyout.ShowAt((FrameworkElement)sender, e.GetPosition((FrameworkElement)sender));
        await Task.CompletedTask;
        e.Handled = true;
    }

    private void PlayerRoot_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (ViewModel.SelectedMedia == null)
        {
            return;
        }

        var selected = GetSelectedItems().ToList();
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
        var flyout = BuildMediaContextFlyout(selected);
        flyout.ShowAt(PlayerRoot, e.GetPosition(PlayerRoot));
        e.Handled = true;
    }

    private void SelectMedia(MediaItemViewModel media)
    {
        ViewModel.SelectedMedia = media;
    }

    private void UpdatePlayer(MediaItemViewModel media)
    {
        EmptyState.Visibility = Visibility.Collapsed;
        PlayerFileNameText.Text = media.FileName;
        PlayerResolutionText.Text = media.ResolutionText;
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

        UpdateLibraryPaneState();
    }

    private void ClearPlayerSelection()
    {
        EmptyState.Visibility = Visibility.Visible;
        PlayerFileNameText.Text = string.Empty;
        PlayerResolutionText.Text = string.Empty;
        _shell.Player.ImagePreview.ZoomBadgeVisibility = Visibility.Collapsed;
        _imagePreviewController.Clear();
        _clipEditorController.HandleMediaChanged(null);
        _videoPlaybackController.Clear();
        UpdateLibraryPaneState(preferOpen: true);
    }

    private void SyncSelectionFromViewModel()
    {
        _isSyncingSelection = true;
        try
        {
            var selected = ViewModel.SelectedMedia;
            ListView.SelectedItem = selected;
            GridView.SelectedItem = selected;
            UpdateSelectedStateFlags();

            if (selected == null)
            {
                ClearPlayerSelection();
                return;
            }

            UpdatePlayer(selected);
            RevealSelectedMedia(selected);
        }
        finally
        {
            _isSyncingSelection = false;
        }
    }

    private void RevealSelectedMedia(MediaItemViewModel media)
    {
        if (ViewModel.ViewMode == MediaViewMode.Grid)
        {
            GridView.ScrollIntoView(media);
            return;
        }

        ListView.ScrollIntoView(media);
    }

    public async Task HandleKeyDownAsync(KeyRoutedEventArgs e)
    {
        if ((InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0)
        {
            if (e.Key == Windows.System.VirtualKey.O)
            {
                await AddFolderAsync();
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.F)
            {
                SetLibraryPaneOpen(true);
                SearchBox.Focus(FocusState.Programmatic);
                SearchBox.SelectAll();
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
        else if (ViewModel.SelectedMedia.Type == MediaType.Video && _clipEditorController.IsClipModeActive && e.Key == Windows.System.VirtualKey.I)
        {
            _clipEditorController.SetClipStartToCurrent();
            e.Handled = true;
        }
        else if (ViewModel.SelectedMedia.Type == MediaType.Video && _clipEditorController.IsClipModeActive && e.Key == Windows.System.VirtualKey.O)
        {
            _clipEditorController.SetClipEndToCurrent();
            e.Handled = true;
        }
        else if (ViewModel.SelectedMedia.Type == MediaType.Video && _clipEditorController.IsClipModeActive && e.Key == Windows.System.VirtualKey.E)
        {
            await _clipEditorController.ExportCurrentClipAsync();
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

        await DeleteSelectedAsync();
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
        ShowControls();
        UpdatePlayerNavigationCue(e.GetCurrentPoint(PlayerRoot).Position);
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

    private void RefreshTagChips()
    {
        SelectedTagsControl.Visibility = ViewModel.SelectedTags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshPlaylistSelection()
    {
        _isSyncingSelection = true;
        try
        {
            PlaylistRailListView.SelectedItem = ViewModel.SelectedPlaylist;
        }
        finally
        {
            _isSyncingSelection = false;
        }
    }

    private void RefreshScanProgressVisibility()
    {
        var showProgress = ViewModel.IsScanning || ViewModel.ScanProgressValue > 0 || !string.IsNullOrWhiteSpace(ViewModel.ScanCurrentPath);
        StatusText.Text = ViewModel.StatusMessage;
        ScanProgressBar.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        ScanPathText.Visibility = !string.IsNullOrWhiteSpace(ViewModel.ScanCurrentPath) ? Visibility.Visible : Visibility.Collapsed;
        ScanProgressBar.Maximum = Math.Max(1, ViewModel.ScanProgressMaximum);
        ScanProgressBar.Value = ViewModel.ScanProgressValue;
    }

    private void ActivateMediaLibrary(bool openPane)
    {
        ViewModel.SelectedPlaylist = null;
        RefreshPlaylistSelection();
        UpdateLibraryPaneUi();
        if (openPane)
        {
            SetLibraryPaneOpen(true);
        }
    }

    private void ActivatePlaylist(Playlist playlist, bool openPane)
    {
        var targetPlaylist = ViewModel.Playlists.FirstOrDefault(item => string.Equals(item.Id, playlist.Id, StringComparison.Ordinal))
            ?? playlist;
        ViewModel.SelectedPlaylist = targetPlaylist;
        RefreshPlaylistSelection();
        UpdateLibraryPaneUi();
        if (openPane)
        {
            SetLibraryPaneOpen(true);
        }
    }

    private void UpdateLibraryPaneUi()
    {
        var hasSelectedPlaylist = ViewModel.SelectedPlaylist != null;
        SelectedPlaylistTitleButton.Visibility = hasSelectedPlaylist ? Visibility.Visible : Visibility.Collapsed;
        SelectedPlaylistTitleButton.Content = ViewModel.SelectedPlaylist?.Name ?? string.Empty;

        var activeBackground = Application.Current.Resources["SurfaceMutedBrush"] as Brush;
        var inactiveBackground = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var activeForeground = Application.Current.Resources["TextBrush"] as Brush;
        var inactiveForeground = Application.Current.Resources["MutedTextBrush"] as Brush;

        MediaTabButton.Background = hasSelectedPlaylist ? inactiveBackground : activeBackground;
        MediaTabButton.Foreground = hasSelectedPlaylist ? inactiveForeground : activeForeground;
    }

    private void UpdateMediaItemSize()
    {
        var gridItemSize = CalculateAdaptiveGridItemSize();
        GridView.Tag = gridItemSize;
        UpdateVisibleListItemSizes();
        if (GridView.ItemsPanelRoot is ItemsWrapGrid panel)
        {
            panel.Orientation = Orientation.Horizontal;
            panel.ItemWidth = gridItemSize;
            panel.ItemHeight = gridItemSize;
        }
    }

    private void MediaLibraryView_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var ctrlDown = (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (!ctrlDown)
        {
            return;
        }

        var source = sender as UIElement ?? ListView;
        var delta = e.GetCurrentPoint(source).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        ViewModel.IconSize = Math.Clamp(ViewModel.IconSize + (delta > 0 ? 12 : -12), 72, 260);
        UpdateMediaItemSize();
        ConfigureGridViewScrolling();
        e.Handled = true;
    }

    private void GridView_Loaded(object sender, RoutedEventArgs e)
    {
        ConfigureGridViewScrolling();
    }

    private void GridView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0)
        {
            return;
        }

        UpdateMediaItemSize();
    }

    private void LibraryPaneRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0)
        {
            return;
        }

        UpdateMediaItemSize();
    }

    private void LibraryPaneResizer_DragStarted(object sender, DragStartedEventArgs e)
    {
        _isResizingLibraryPane = true;
        SetLibraryPaneOpen(true);
    }

    private void LibraryPaneResizer_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var maxWidth = RootLayout.ActualWidth > 0
            ? Math.Min(MaxLibraryPaneWidth, Math.Max(MinLibraryPaneWidth, RootLayout.ActualWidth - 200))
            : MaxLibraryPaneWidth;

        _libraryPaneWidth = Math.Clamp(_libraryPaneWidth + e.HorizontalChange, MinLibraryPaneWidth, maxWidth);
        LibrarySplitView.OpenPaneLength = _libraryPaneWidth;
        UpdateMediaItemSize();
    }

    private void LibraryPaneResizer_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _isResizingLibraryPane = false;
        UpdateLibraryPanePresentation();
        UpdateMediaItemSize();
    }

    private void ConfigureGridViewScrolling()
    {
        ScrollViewer.SetHorizontalScrollMode(GridView, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(GridView, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollMode(GridView, ScrollMode.Enabled);
        ScrollViewer.SetVerticalScrollBarVisibility(GridView, ScrollBarVisibility.Visible);

        GridView.ApplyTemplate();
        GridView.UpdateLayout();

        if (GridView.ItemsPanelRoot is ItemsWrapGrid panel)
        {
            panel.Orientation = Orientation.Horizontal;
            var gridItemSize = CalculateAdaptiveGridItemSize();
            panel.ItemWidth = gridItemSize;
            panel.ItemHeight = gridItemSize;
        }

        var scrollViewer = FindDescendant<ScrollViewer>(GridView);
        if (scrollViewer == null)
        {
            return;
        }

        _gridViewScrollViewer = scrollViewer;
        _gridViewScrollViewer.VerticalScrollMode = ScrollMode.Enabled;
        _gridViewScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Visible;
        _gridViewScrollViewer.HorizontalScrollMode = ScrollMode.Disabled;
        _gridViewScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _gridViewScrollViewer.ZoomMode = ZoomMode.Disabled;
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

    private void Media_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            return;
        }

        if (sender == ListView)
        {
            ApplyListItemSize(args.ItemContainer as SelectorItem);
        }

        if (args.Item is MediaItemViewModel media)
        {
            _ = ViewModel.EnsureThumbnailAsync(media);
        }
    }

    private async Task DeleteSelectedAsync()
    {
        var selected = GetSelectedItems().ToList();
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

        UpdateSelectedStateFlags();
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
        if (ViewModel.ViewMode == MediaViewMode.Grid)
        {
            return GridView.SelectedItems.OfType<MediaItemViewModel>();
        }

        return ListView.SelectedItems.OfType<MediaItemViewModel>();
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
            && EmptyState.Visibility != Visibility.Visible;
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
        var selected = GetSelectedItems().ToList();
        if (selected.Count == 0 && ViewModel.SelectedMedia != null)
        {
            selected.Add(ViewModel.SelectedMedia);
        }

        if (selected.Count == 0)
        {
            await ShowInfoDialogAsync("提示", "请先选择一个或多个媒体。");
            return;
        }

        await ApplyTagEditorAsync(selected);
    }

    private async Task AddSelectionToPlaylistAsync()
    {
        var selected = GetSelectedItems().ToList();
        if (selected.Count == 0 && ViewModel.SelectedMedia != null)
        {
            selected.Add(ViewModel.SelectedMedia);
        }

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

    private MenuFlyout BuildMediaContextFlyout(IReadOnlyList<MediaItemViewModel> selected)
    {
        var flyout = new MenuFlyout();

        var openFolder = new MenuFlyoutItem { Text = "打开所在目录" };
        openFolder.Click += (_, _) => OpenMediaFolder(selected[0]);
        flyout.Items.Add(openFolder);

        var editTags = new MenuFlyoutItem { Text = selected.Count == 1 ? "编辑标签" : $"批量编辑标签 ({selected.Count})" };
        editTags.Click += async (_, _) => await ApplyTagEditorAsync(selected);
        flyout.Items.Add(editTags);

        var addToPlaylist = new MenuFlyoutSubItem { Text = "添加到播放列表" };
        if (ViewModel.Playlists.Count == 0)
        {
            addToPlaylist.Items.Add(new MenuFlyoutItem { Text = "暂无播放列表", IsEnabled = false });
        }
        else
        {
            foreach (var playlist in ViewModel.Playlists)
            {
                var playlistId = playlist.Id;
                var item = new MenuFlyoutItem { Text = playlist.Name };
                item.Click += async (_, _) => await ViewModel.AddMediaToPlaylistAsync(playlistId, selected);
                addToPlaylist.Items.Add(item);
            }
        }

        flyout.Items.Add(addToPlaylist);

        if (ViewModel.SelectedPlaylist != null)
        {
            var removeFromPlaylist = new MenuFlyoutItem { Text = $"从“{ViewModel.SelectedPlaylist.Name}”移除" };
            removeFromPlaylist.Click += async (_, _) => await ViewModel.RemoveMediaFromSelectedPlaylistAsync(selected);
            flyout.Items.Add(removeFromPlaylist);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        var delete = new MenuFlyoutItem { Text = "删除文件" };
        delete.Click += async (_, _) => await DeleteSelectedAsync();
        flyout.Items.Add(delete);

        return flyout;
    }

    private void OpenMediaFolder(MediaItemViewModel media)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{media.FileSystemPath}\"",
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    private bool TryGetMediaFromElement(ListViewBase listViewBase, DependencyObject? origin, out MediaItemViewModel? media)
    {
        media = null;
        if (origin == null)
        {
            return false;
        }

        var container = FindAncestor<SelectorItem>(origin);
        media = container?.Content as MediaItemViewModel;
        return media != null;
    }

    private static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        var current = start;
        while (current != null)
        {
            if (current is T typed)
            {
                return typed;
            }

            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root == null)
        {
            return null;
        }

        var childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T typed)
            {
                return typed;
            }

            var nested = FindDescendant<T>(child);
            if (nested != null)
            {
                return nested;
            }
        }

        return null;
    }

    private static FrameworkElement? FindDescendantByName(DependencyObject? root, string name)
    {
        if (root == null)
        {
            return null;
        }

        var childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, index);
            if (child is FrameworkElement element && string.Equals(element.Name, name, StringComparison.Ordinal))
            {
                return element;
            }

            var nested = FindDescendantByName(child, name);
            if (nested != null)
            {
                return nested;
            }
        }

        return null;
    }

    private void UpdateVisibleListItemSizes()
    {
        for (var index = 0; index < ListView.Items.Count; index++)
        {
            if (ListView.ContainerFromIndex(index) is SelectorItem container)
            {
                ApplyListItemSize(container);
            }
        }
    }

    private void ApplyListItemSize(SelectorItem? container)
    {
        if (container == null)
        {
            return;
        }

        if (FindDescendantByName(container, "ListThumbnailHost") is not FrameworkElement thumbnailHost)
        {
            return;
        }

        var size = Math.Clamp((int)Math.Round(ViewModel.IconSize * 0.6), 48, 180);
        thumbnailHost.Width = size;
        thumbnailHost.Height = size;
    }

    private double CalculateAdaptiveGridItemSize()
    {
        var desiredSize = Math.Clamp((double)ViewModel.IconSize, 72, 260);
        var availableWidth = GetGridViewAvailableWidth();
        if (availableWidth <= desiredSize)
        {
            return desiredSize;
        }

        var columns = Math.Max(1, (int)Math.Floor(availableWidth / desiredSize));
        var adjustedSize = Math.Floor(availableWidth / columns);
        return Math.Clamp(adjustedSize, 72, 320);
    }

    private double GetGridViewAvailableWidth()
    {
        var width = _gridViewScrollViewer?.ActualWidth ?? 0;
        if (width <= 0)
        {
            width = GridView.ActualWidth;
        }

        if (width <= 0)
        {
            width = _libraryPaneWidth;
        }

        return Math.Max(72, width - GridViewWidthPadding);
    }

    private void UpdateLibraryPaneState(bool preferOpen = false)
    {
        if (preferOpen)
        {
            SetLibraryPaneOpen(true);
            return;
        }

        UpdateLibraryPanePresentation();
    }

    private void SetLibraryPaneOpen(bool isOpen)
    {
        if (LibrarySplitView.IsPaneOpen == isOpen)
        {
            return;
        }

        LibrarySplitView.IsPaneOpen = isOpen;
        UpdateLibraryPanePresentation();
    }

    private void UpdateLibraryPanePresentation()
    {
        if (LibrarySplitView == null || LibraryPaneRoot == null || LibraryPaneExpandedContent == null || LibraryPaneResizer == null)
        {
            return;
        }

        LibrarySplitView.DisplayMode = SplitViewDisplayMode.CompactInline;
        LibrarySplitView.CompactPaneLength = 56;
        LibrarySplitView.OpenPaneLength = _libraryPaneWidth;
        LibraryPaneRoot.Background = Application.Current.Resources["SurfaceAltBrush"] as Brush
            ?? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 32, 32, 32));
        LibraryPaneExpandedContent.Visibility = LibrarySplitView.IsPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        LibraryPaneResizer.Visibility = LibrarySplitView.IsPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        LibraryPaneResizer.Opacity = _isResizingLibraryPane ? 1 : 0.65;
    }

    private async Task RenamePlaylistAsync(Playlist playlist)
    {
        var name = await _dialogService.ShowTextInputAsync("重命名播放列表", "播放列表名称", playlist.Name, "保存");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            ViewModel.RenamePlaylist(playlist.Id, name);
            RefreshPlaylistSelection();
            UpdateLibraryPaneUi();
        }
        catch (Exception ex)
        {
            await ShowInfoDialogAsync("重命名失败", ex.Message);
        }
    }

    private async Task DeletePlaylistAsync(Playlist playlist)
    {
        var confirmed = await ConfirmAsync("删除播放列表", $"确定要删除“{playlist.Name}”吗？\n媒体文件本身不会被删除。", "删除");
        if (!confirmed)
        {
            return;
        }

        await ViewModel.DeletePlaylistAsync(playlist.Id);
        RefreshPlaylistSelection();
        UpdateLibraryPaneUi();
    }

    private async Task ChangePlaylistColorAsync(Playlist playlist)
    {
        var colorHex = await _dialogService.ShowPlaylistColorDialogAsync($"更改“{playlist.Name}”颜色", playlist.ColorHex);
        if (colorHex == playlist.ColorHex)
        {
            return;
        }

        try
        {
            ViewModel.SetPlaylistColor(playlist.Id, colorHex);
            RefreshPlaylistSelection();
            UpdateLibraryPaneUi();
        }
        catch (Exception ex)
        {
            await ShowInfoDialogAsync("更改颜色失败", ex.Message);
        }
    }

    private void EnsureRightTappedSelection(ListViewBase listViewBase, MediaItemViewModel media)
    {
        var selected = GetSelectedItems().Any(item => string.Equals(item.Id, media.Id, StringComparison.Ordinal));
        if (selected)
        {
            return;
        }

        listViewBase.SelectedItems.Clear();
        listViewBase.SelectedItem = media;
    }

    private async Task ApplySettingsAsync()
    {
        var result = await _dialogCoordinator.ShowSettingsDialogAsync();
        if (result == null)
        {
            return;
        }

        ViewModel.SetThemeMode(result.ThemeMode);
        App.ApplyThemeMode(result.ThemeMode);
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
