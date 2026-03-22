using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using JvJvMediaManager.Models;
using JvJvMediaManager.Services;
using JvJvMediaManager.Utilities;
using JvJvMediaManager.ViewModels;
using JvJvMediaManager.Views.Controls;
using WinRT.Interop;

namespace JvJvMediaManager.Views;

public sealed partial class MainPage : Page
{
    private const double DefaultLibraryPaneWidth = 420;
    private const double MinLibraryPaneWidth = 280;
    private const double MaxLibraryPaneWidth = 760;
    private const double LibraryRevealHotZoneWidth = 28;
    private const double LibraryHideBufferWidth = 24;
    private const double GridViewWidthPadding = 24;
    private const double PlayerEdgeNavigationRevealWidth = 96;
    private enum PlaybackMode
    {
        ListLoop,
        SingleLoop,
        Shuffle
    }

    private enum PlayerNavigationEdge
    {
        None,
        Left,
        Right
    }

    public MainViewModel ViewModel { get; } = new();

    private readonly DebounceDispatcher _debouncer = new();
    private readonly DispatcherTimer _playbackTimer = new();
    private readonly DispatcherTimer _controlsHideTimer = new();
    private readonly Random _random = new();
    private readonly VideoClipService _clipService = new();

    private MediaPlayer? _player;
    private AppWindow? _appWindow;
    private int _imageLoadVersion;

    private bool _isSeeking;
    private bool _controlsVisible = true;
    private bool _isFullScreen;
    private bool _isSyncingSelection;
    private PlaybackMode _playbackMode = PlaybackMode.ListLoop;
    private string? _clipMediaId;
    private TimeSpan? _clipStart;
    private TimeSpan? _clipEnd;
    private bool _isExportingClip;
    private string _clipStatusMessage = string.Empty;
    private readonly ObservableCollection<VideoClipSegment> _clipSegments = new();
    private VideoClipMode _clipMode = VideoClipMode.Keep;
    private string? _clipOutputDirectory;
    private ScrollViewer? _gridViewScrollViewer;
    private bool _progressSliderHandlersAttached;
    private bool _isImageDragging;
    private bool _isLibraryPinned = true;
    private bool _isResizingLibraryPane;
    private string? _pendingImageFitMediaId;
    private Windows.Foundation.Point _imageDragStartPoint;
    private double _imageDragStartHorizontalOffset;
    private double _imageDragStartVerticalOffset;
    private double _imageSourceWidth;
    private double _imageSourceHeight;
    private double _libraryPaneWidth = DefaultLibraryPaneWidth;
    private PlayerNavigationEdge _activePlayerNavigationEdge;

    private Image? PreviewImage => PreviewImageElement;
    private SymbolIcon? LibraryPinSymbolIcon => LibraryPinButton.Content as SymbolIcon;
    private SymbolIcon? PlayPauseSymbolIcon => PlayPauseButton.Icon as SymbolIcon;

    public MainPage()
    {
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.SelectedTags.CollectionChanged += SelectedTags_CollectionChanged;
        ViewModel.SetDispatcher(DispatcherQueue);
        Loaded += MainPage_Loaded;
        Unloaded += MainPage_Unloaded;
        GridView.Loaded += GridView_Loaded;
        GridView.SizeChanged += GridView_SizeChanged;
        ImageScrollViewer.SizeChanged += ImageScrollViewer_SizeChanged;
        ImageScrollViewer.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(ImageScrollViewer_PointerPressed), true);
        ImageScrollViewer.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(ImageScrollViewer_PointerMoved), true);
        ImageScrollViewer.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(ImageScrollViewer_PointerReleased), true);
        ImageScrollViewer.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(ImageScrollViewer_PointerCaptureLost), true);
        ImageScrollViewer.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(ImageScrollViewer_PointerWheelChanged), true);
        ImageScrollViewer.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler(ImageScrollViewer_DoubleTapped), true);
        ImageScrollViewer.ViewChanged += ImageScrollViewer_ViewChanged;
        ProgressSlider.Loaded += ProgressSlider_Loaded;
        LibraryPaneRoot.SizeChanged += LibraryPaneRoot_SizeChanged;
        PlayerRoot.AddHandler(UIElement.RightTappedEvent, new RightTappedEventHandler(PlayerRoot_RightTapped), true);
        ListView.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(MediaLibraryView_PointerWheelChanged), true);
        GridView.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(MediaLibraryView_PointerWheelChanged), true);
        SelectedTagsControl.ItemsSource = ViewModel.SelectedTags;
        KeyDown += MainPage_KeyDown;
        LibrarySplitView.OpenPaneLength = _libraryPaneWidth;
        UpdateLibraryPinButtonUi();
        UpdateLibraryPanePresentation();

        _playbackTimer.Interval = TimeSpan.FromMilliseconds(250);
        _playbackTimer.Tick += PlaybackTimer_Tick;

        _controlsHideTimer.Interval = TimeSpan.FromSeconds(2.5);
        _controlsHideTimer.Tick += ControlsHideTimer_Tick;

        UpdatePlaybackModeUi();
        UpdateViewModeButtonUi();
        UpdateControlBarState(false);
        UpdateClipUi();
        RefreshScanProgressVisibility();
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        await ExecuteUiActionAsync(async () =>
        {
            await ViewModel.InitializeAsync();
            RefreshTagChips();
            RefreshPlaylistSelection();
            RefreshScanProgressVisibility();
            UpdateMediaItemSize();
            ConfigureGridViewScrolling();
            EnsureProgressSliderHandlers();
            UpdateLibraryPaneState(preferOpen: true);
        }, "初始化失败");
    }

    private void MainPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.SelectedTags.CollectionChanged -= SelectedTags_CollectionChanged;
        GridView.Loaded -= GridView_Loaded;
        GridView.SizeChanged -= GridView_SizeChanged;
        ImageScrollViewer.SizeChanged -= ImageScrollViewer_SizeChanged;
        ImageScrollViewer.ViewChanged -= ImageScrollViewer_ViewChanged;
        ProgressSlider.Loaded -= ProgressSlider_Loaded;
        LibraryPaneRoot.SizeChanged -= LibraryPaneRoot_SizeChanged;
        _playbackTimer.Stop();
        _controlsHideTimer.Stop();

        if (_player != null)
        {
            _player.MediaOpened -= Player_MediaOpened;
            _player.MediaEnded -= Player_MediaEnded;
            _player.PlaybackSession.PlaybackStateChanged -= PlaybackSession_PlaybackStateChanged;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedMedia))
        {
            DispatcherQueue.TryEnqueue(SyncSelectionFromViewModel);
        }
        else if (e.PropertyName == nameof(MainViewModel.SelectedPlaylist))
        {
            DispatcherQueue.TryEnqueue(RefreshPlaylistSelection);
        }
        else if (e.PropertyName == nameof(MainViewModel.IsScanning)
            || e.PropertyName == nameof(MainViewModel.ScanCurrentPath)
            || e.PropertyName == nameof(MainViewModel.ScanProgressMaximum)
            || e.PropertyName == nameof(MainViewModel.ScanProgressValue))
        {
            DispatcherQueue.TryEnqueue(RefreshScanProgressVisibility);
        }
    }

    private void SelectedTags_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshTagChips();
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteUiActionAsync(async () =>
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

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteUiActionAsync(async () =>
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
        await ShowSettingsDialogAsync();
    }

    private async void FolderLock_Click(object sender, RoutedEventArgs e)
    {
        await ShowFolderLockDialogAsync();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text ?? string.Empty;
        _debouncer.Debounce(TimeSpan.FromMilliseconds(250), () =>
        {
            DispatcherQueue.TryEnqueue(() => ViewModel.SearchQuery = query);
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
        if (sender is Button button && button.Tag is string tag)
        {
            ViewModel.RemoveSelectedTagFilter(tag);
        }
    }

    private void AllMedia_Click(object sender, RoutedEventArgs e)
    {
        PlaylistListView.SelectedItem = null;
        ViewModel.SelectedPlaylist = null;
    }

    private async void CreatePlaylist_Click(object sender, RoutedEventArgs e)
    {
        var name = await ShowTextInputDialogAsync("新建播放列表", "播放列表名称", string.Empty, "创建");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            ViewModel.CreatePlaylist(name);
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

        var name = await ShowTextInputDialogAsync("重命名播放列表", "播放列表名称", playlist.Name, "保存");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            ViewModel.RenamePlaylist(playlist.Id, name);
        }
        catch (Exception ex)
        {
            await ShowInfoDialogAsync("重命名失败", ex.Message);
        }
    }

    private async void DeletePlaylist_Click(object sender, RoutedEventArgs e)
    {
        var playlist = ViewModel.SelectedPlaylist;
        if (playlist == null)
        {
            await ShowInfoDialogAsync("提示", "请先选择一个播放列表。");
            return;
        }

        var confirmed = await ConfirmAsync("删除播放列表", $"确定要删除“{playlist.Name}”吗？\n媒体文件本身不会被删除。", "删除");
        if (!confirmed)
        {
            return;
        }

        await ViewModel.DeletePlaylistAsync(playlist.Id);
    }

    private void PlaylistListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingSelection)
        {
            return;
        }

        ViewModel.SelectedPlaylist = PlaylistListView.SelectedItem as Playlist;
    }

    private void PlaylistListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        ViewModel.UpdatePlaylistOrder(ViewModel.Playlists.ToList());
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
            DispatcherQueue.TryEnqueue(ConfigureGridViewScrolling);
        }
        DispatcherQueue.TryEnqueue(SyncSelectionFromViewModel);
    }

    private void UpdateViewModeButtonUi()
    {
        if (ViewModeToggleButton == null)
        {
            return;
        }

        ViewModeToggleButton.Content = ViewModel.ViewMode == MediaViewMode.List
            ? "切换到网格"
            : "切换到列表";
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SortField == MediaSortField.FileName && ViewModel.SortOrder == MediaSortOrder.Asc)
        {
            ViewModel.ToggleSort(MediaSortField.FileName);
            return;
        }

        if (ViewModel.SortField == MediaSortField.FileName && ViewModel.SortOrder == MediaSortOrder.Desc)
        {
            ViewModel.ToggleSort(MediaSortField.ModifiedAt);
            return;
        }

        if (ViewModel.SortField == MediaSortField.ModifiedAt && ViewModel.SortOrder == MediaSortOrder.Asc)
        {
            ViewModel.ToggleSort(MediaSortField.ModifiedAt);
            return;
        }

        ViewModel.ToggleSort(MediaSortField.FileName);
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
        ResetClipState(media);

        if (media.Type == MediaType.Video)
        {
            ImageScrollViewer.Visibility = Visibility.Collapsed;
            VideoPlayer.Visibility = Visibility.Visible;
            var player = EnsureMediaPlayer();
            player.Source = MediaSource.CreateFromUri(new Uri(media.FileSystemPath));
            UpdateControlBarState(true);
            ShowControls();
            Focus(FocusState.Programmatic);
        }
        else
        {
            VideoPlayer.Visibility = Visibility.Collapsed;
            ImageScrollViewer.Visibility = Visibility.Visible;
            Interlocked.Increment(ref _imageLoadVersion);
            if (_player != null)
            {
                _player.Pause();
                _player.Source = null;
            }

            if (PreviewImage != null)
            {
                PreviewImage.Source = media.Thumbnail;
            }
            BeginImagePreviewSession(media);
            UpdateControlBarState(false);
            _ = LoadImagePreviewAsync(media, Volatile.Read(ref _imageLoadVersion));
        }

        UpdateLibraryPaneState();
    }

    private void ClearPlayerSelection()
    {
        EmptyState.Visibility = Visibility.Visible;
        VideoPlayer.Visibility = Visibility.Collapsed;
        ImageScrollViewer.Visibility = Visibility.Collapsed;
        Interlocked.Increment(ref _imageLoadVersion);
        _pendingImageFitMediaId = null;
        _imageSourceWidth = 0;
        _imageSourceHeight = 0;
        if (PreviewImage != null)
        {
            PreviewImage.Width = double.NaN;
            PreviewImage.Height = double.NaN;
            PreviewImage.Source = null;
        }
        ResetClipState(null);
        if (_player != null)
        {
            _player.Pause();
            _player.Source = null;
        }

        UpdateControlBarState(false);
        UpdateLibraryPaneState(preferOpen: true);
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

            if (!string.Equals(ViewModel.SelectedMedia?.Id, media.Id, StringComparison.Ordinal))
            {
                return;
            }

            if (PreviewImage != null)
            {
                PreviewImage.Source = bitmap;
            }

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

            if (string.Equals(ViewModel.SelectedMedia?.Id, media.Id, StringComparison.Ordinal))
            {
                if (PreviewImage != null)
                {
                    PreviewImage.Source = media.Thumbnail;
                }

                TryApplyPendingImageFit();
            }
        }
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

    private async void MainPage_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if ((InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0)
        {
            if (e.Key == Windows.System.VirtualKey.O)
            {
                AddFolder_Click(sender, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.F)
            {
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
        else if (ViewModel.SelectedMedia.Type == MediaType.Video && e.Key == Windows.System.VirtualKey.I)
        {
            SetClipStartToCurrent();
            e.Handled = true;
        }
        else if (ViewModel.SelectedMedia.Type == MediaType.Video && e.Key == Windows.System.VirtualKey.O)
        {
            SetClipEndToCurrent();
            e.Handled = true;
        }
        else if (ViewModel.SelectedMedia.Type == MediaType.Video && e.Key == Windows.System.VirtualKey.E)
        {
            await ExportCurrentClipAsync();
            e.Handled = true;
        }
        else if (ViewModel.SelectedMedia.Type == MediaType.Image)
        {
            if (e.Key == Windows.System.VirtualKey.Add || e.Key == (Windows.System.VirtualKey)187)
            {
                ZoomImage(0.1);
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Subtract || e.Key == (Windows.System.VirtualKey)189)
            {
                ZoomImage(-0.1);
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Number0)
            {
                ResetImageZoom();
                e.Handled = true;
            }
        }

        if (e.Handled)
        {
            ShowControls();
        }
    }

    private void PlayPauseKeyboardAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!TryTogglePlaybackFromShortcut())
        {
            return;
        }

        args.Handled = true;
    }

    private async void DeleteKeyboardAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!await TryDeleteSelectedFromShortcutAsync())
        {
            return;
        }

        args.Handled = true;
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
        return FocusManager.GetFocusedElement(XamlRoot) is TextBox or PasswordBox or RichEditBox or AutoSuggestBox or ComboBox;
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        TogglePlayPause();
    }

    private void TogglePlayPause()
    {
        var player = _player;
        if (player == null)
        {
            return;
        }

        if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            player.Pause();
        }
        else
        {
            player.Play();
        }

        ShowControls();
    }

    private void PlaybackMode_Click(object sender, RoutedEventArgs e)
    {
        _playbackMode = _playbackMode switch
        {
            PlaybackMode.ListLoop => PlaybackMode.SingleLoop,
            PlaybackMode.SingleLoop => PlaybackMode.Shuffle,
            _ => PlaybackMode.ListLoop
        };

        UpdatePlaybackModeUi();
        ShowControls();
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e)
    {
        var appWindow = GetAppWindow();
        if (appWindow == null)
        {
            return;
        }

        if (_isFullScreen)
        {
            appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            _isFullScreen = false;
            FullScreenButton.Content = "全屏";
        }
        else
        {
            appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            _isFullScreen = true;
            FullScreenButton.Content = "退出全屏";
        }

        ShowControls();
    }

    private void ProgressSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isSeeking = true;
        ShowControls();
    }

    private void ProgressSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        CompleteSliderSeek();
    }

    private void ProgressSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_player == null || !_isSeeking)
        {
            return;
        }

        var target = TimeSpan.FromSeconds(ProgressSlider.Value);
        _player.PlaybackSession.Position = target;
        CurrentTimeText.Text = FormatTime(target);
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_player != null)
        {
            _player.Volume = VolumeSlider.Value;
        }
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
        Focus(FocusState.Programmatic);
    }

    private void PlayerRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        ShowControls();
        SetPlayerNavigationEdge(PlayerNavigationEdge.None);
    }

    private void ControlBar_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ShowControls();
    }

    private void ControlsHideTimer_Tick(object? sender, object e)
    {
        _controlsHideTimer.Stop();
        HideControls();
    }

    private void PlaybackTimer_Tick(object? sender, object e)
    {
        var player = _player;
        if (player == null)
        {
            return;
        }

        var duration = player.PlaybackSession.NaturalDuration;
        if (duration.TotalSeconds <= 0)
        {
            return;
        }

        if (!_isSeeking)
        {
            ProgressSlider.Maximum = Math.Max(1, duration.TotalSeconds);
            ProgressSlider.Value = player.PlaybackSession.Position.TotalSeconds;
        }

        CurrentTimeText.Text = FormatTime(player.PlaybackSession.Position);
        TotalTimeText.Text = FormatTime(duration);
    }

    private void Player_MediaOpened(MediaPlayer sender, object args)
    {
        var duration = sender.PlaybackSession.NaturalDuration;
        DispatcherQueue.TryEnqueue(() =>
        {
            ProgressSlider.Maximum = Math.Max(1, duration.TotalSeconds);
            TotalTimeText.Text = FormatTime(duration);
            CurrentTimeText.Text = "0:00";
            InitializeClipRange(duration);
            UpdateClipUi();
        });
    }

    private void Player_MediaEnded(MediaPlayer sender, object args)
    {
        DispatcherQueue.TryEnqueue(HandlePlaybackEnded);
    }

    private void PlaybackSession_PlaybackStateChanged(MediaPlaybackSession sender, object args)
    {
        DispatcherQueue.TryEnqueue(UpdatePlayPauseState);
    }

    private void UpdatePlayPauseState()
    {
        if (_player == null)
        {
            return;
        }

        if (PlayPauseSymbolIcon != null)
        {
            PlayPauseSymbolIcon.Symbol = _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing
                ? Symbol.Pause
                : Symbol.Play;
        }
    }

    private void HandlePlaybackEnded()
    {
        if (ViewModel.SelectedMedia == null)
        {
            return;
        }

        if (_playbackMode == PlaybackMode.SingleLoop)
        {
            if (_player != null)
            {
                _player.PlaybackSession.Position = TimeSpan.Zero;
                _player.Play();
            }

            return;
        }

        if (_playbackMode == PlaybackMode.Shuffle)
        {
            NavigateRandom();
            return;
        }

        _ = NavigateRelativeAsync(1);
    }

    private void NavigateRandom()
    {
        var list = ViewModel.FilteredMediaItems;
        if (list.Count == 0 || ViewModel.SelectedMedia == null)
        {
            return;
        }

        var currentIndex = list.IndexOf(ViewModel.SelectedMedia);
        var nextIndex = currentIndex;

        if (list.Count > 1)
        {
            while (nextIndex == currentIndex)
            {
                nextIndex = _random.Next(list.Count);
            }
        }

        ViewModel.SelectedMedia = list[nextIndex];
    }

    private void SeekRelative(double seconds)
    {
        var player = _player;
        if (player == null)
        {
            return;
        }

        var next = player.PlaybackSession.Position + TimeSpan.FromSeconds(seconds);
        if (next < TimeSpan.Zero)
        {
            next = TimeSpan.Zero;
        }

        player.PlaybackSession.Position = next;
    }

    private void SeekToSlider()
    {
        if (_player == null)
        {
            return;
        }

        var target = TimeSpan.FromSeconds(ProgressSlider.Value);
        _player.PlaybackSession.Position = target;
        CurrentTimeText.Text = FormatTime(target);
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

    private async void PreviousMediaHotspot_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (!CanShowPlayerNavigationHotspots())
        {
            return;
        }

        await NavigateRelativeAsync(-1);
        ShowControls();
        SetPlayerNavigationEdge(PlayerNavigationEdge.Left);
        e.Handled = true;
    }

    private async void NextMediaHotspot_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (!CanShowPlayerNavigationHotspots())
        {
            return;
        }

        await NavigateRelativeAsync(1);
        ShowControls();
        SetPlayerNavigationEdge(PlayerNavigationEdge.Right);
        e.Handled = true;
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
            PlaylistListView.SelectedItem = ViewModel.SelectedPlaylist;
        }
        finally
        {
            _isSyncingSelection = false;
        }
    }

    private void RefreshScanProgressVisibility()
    {
        var showProgress = ViewModel.IsScanning || ViewModel.ScanProgressValue > 0 || !string.IsNullOrWhiteSpace(ViewModel.ScanCurrentPath);
        ScanProgressBar.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        ScanPathText.Visibility = !string.IsNullOrWhiteSpace(ViewModel.ScanCurrentPath) ? Visibility.Visible : Visibility.Collapsed;
        ScanProgressBar.Maximum = Math.Max(1, ViewModel.ScanProgressMaximum);
        ScanProgressBar.Value = ViewModel.ScanProgressValue;
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

    private void ProgressSlider_Loaded(object sender, RoutedEventArgs e)
    {
        EnsureProgressSliderHandlers();
    }

    private void EnsureProgressSliderHandlers()
    {
        if (_progressSliderHandlersAttached)
        {
            return;
        }

        ProgressSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(ProgressSlider_PointerPressed), true);
        ProgressSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(ProgressSlider_PointerReleased), true);
        ProgressSlider.PointerCaptureLost += ProgressSlider_PointerCaptureLost;
        _progressSliderHandlersAttached = true;
    }

    private void ProgressSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_isSeeking)
        {
            CompleteSliderSeek();
        }
    }

    private void CompleteSliderSeek()
    {
        if (!_isSeeking)
        {
            return;
        }

        SeekToSlider();
        _isSeeking = false;
        UpdatePlayPauseState();
        ShowControls();
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
        SetClipStartToCurrent();
    }

    private void SetClipEnd_Click(object sender, RoutedEventArgs e)
    {
        SetClipEndToCurrent();
    }

    private void ClearClip_Click(object sender, RoutedEventArgs e)
    {
        ResetClipRangeToFullDuration();
        _clipSegments.Clear();
        _clipMode = VideoClipMode.Keep;
        _clipStatusMessage = _clipService.IsAvailable
            ? "剪辑区间已重置为整段视频。"
            : _clipService.UnavailableReason;
        UpdateClipUi();
    }

    private async void ExportClip_Click(object sender, RoutedEventArgs e)
    {
        await ExportCurrentClipAsync();
    }

    private async void ClipPlan_Click(object sender, RoutedEventArgs e)
    {
        await ShowClipPlanDialogAsync();
    }

    private void SetClipStartToCurrent()
    {
        var duration = GetCurrentVideoDuration();
        if (duration <= TimeSpan.Zero)
        {
            _clipStatusMessage = "视频时长尚未就绪，请稍后再试。";
            UpdateClipUi();
            return;
        }

        var position = ClampToDuration(GetCurrentPlaybackPosition(), duration);
        _clipStart = position;

        if (!_clipEnd.HasValue || _clipEnd.Value <= position)
        {
            _clipEnd = duration;
        }

        _clipStatusMessage = $"入点已设置为 {FormatTime(position)}。";
        UpdateClipUi();
    }

    private void SetClipEndToCurrent()
    {
        var duration = GetCurrentVideoDuration();
        if (duration <= TimeSpan.Zero)
        {
            _clipStatusMessage = "视频时长尚未就绪，请稍后再试。";
            UpdateClipUi();
            return;
        }

        var position = ClampToDuration(GetCurrentPlaybackPosition(), duration);
        _clipEnd = position;

        if (!_clipStart.HasValue || _clipStart.Value >= position)
        {
            _clipStart = TimeSpan.Zero;
        }

        _clipStatusMessage = $"出点已设置为 {FormatTime(position)}。";
        UpdateClipUi();
    }

    private async Task ExportCurrentClipAsync()
    {
        var media = ViewModel.SelectedMedia;
        if (media?.Type != MediaType.Video)
        {
            return;
        }

        if (!_clipService.IsAvailable)
        {
            _clipStatusMessage = _clipService.UnavailableReason;
            UpdateClipUi();
            return;
        }

        var segments = GetConfiguredSegments();
        if (segments.Count == 0)
        {
            _clipStatusMessage = "请先设置有效的片段，或打开“片段方案...”配置多段剪辑。";
            UpdateClipUi();
            return;
        }

        var outputPath = _clipService.CreateOutputPath(media.FileSystemPath, _clipOutputDirectory, _clipMode, segments);
        _isExportingClip = true;
        _clipStatusMessage = $"正在导出到 {Path.GetFileName(outputPath)}";
        ClipExportProgressBar.Value = 0;
        UpdateClipUi();

        try
        {
            var progress = new Progress<double>(value =>
            {
                ClipExportProgressBar.Value = value * 100;
                _clipStatusMessage = value >= 1
                    ? $"正在收尾 {Path.GetFileName(outputPath)}"
                    : $"正在导出 {Path.GetFileName(outputPath)} ({value:P0})";
                UpdateClipUi();
            });

            var result = await _clipService.ExportClipAsync(new VideoClipRequest
            {
                InputPath = media.FileSystemPath,
                Segments = segments,
                Mode = _clipMode,
                SourceDuration = GetBestKnownVideoDuration(media),
                OutputDirectory = _clipOutputDirectory,
                OutputPath = outputPath
            }, progress);

            _clipStatusMessage = $"导出完成：{Path.GetFileName(result.OutputPath)}";
            ClipExportProgressBar.Value = 100;

            await ViewModel.AddFilesAsync(new[] { result.OutputPath });
            UpdateWatchedFolders(new[] { result.OutputPath });
        }
        catch (Exception ex)
        {
            _clipStatusMessage = $"导出失败：{ex.Message}";
        }
        finally
        {
            _isExportingClip = false;
            UpdateClipUi();
        }
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
        Interlocked.Increment(ref _imageLoadVersion);
        _pendingImageFitMediaId = null;
        _imageSourceWidth = 0;
        _imageSourceHeight = 0;
        if (PreviewImage != null)
        {
            PreviewImage.Width = double.NaN;
            PreviewImage.Height = double.NaN;
            PreviewImage.Source = null;
        }
        VideoPlayer.Visibility = Visibility.Collapsed;
        ImageScrollViewer.Visibility = Visibility.Collapsed;

        if (_player != null)
        {
            _player.Pause();
            _player.Source = null;
        }
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

    private MediaPlayer EnsureMediaPlayer()
    {
        if (_player != null)
        {
            return _player;
        }

        _player = new MediaPlayer();
        _player.MediaOpened += Player_MediaOpened;
        _player.MediaEnded += Player_MediaEnded;
        _player.PlaybackSession.PlaybackStateChanged += PlaybackSession_PlaybackStateChanged;
        _player.Volume = VolumeSlider.Value;
        VideoPlayer.SetMediaPlayer(_player);
        _playbackTimer.Start();
        return _player;
    }

    private void UpdatePlaybackModeUi()
    {
        PlaybackModeButton.Content = _playbackMode switch
        {
            PlaybackMode.ListLoop => "列表循环",
            PlaybackMode.SingleLoop => "单曲循环",
            _ => "随机播放"
        };
    }

    private void UpdateControlBarState(bool showForVideo)
    {
        ControlBar.Visibility = showForVideo ? Visibility.Visible : Visibility.Collapsed;
        ControlBar.IsHitTestVisible = showForVideo;
        ControlBar.Opacity = showForVideo ? 1 : 0;
        ImageZoomBadge.Visibility = !showForVideo && ViewModel.SelectedMedia?.Type == MediaType.Image
            ? Visibility.Visible
            : Visibility.Collapsed;
        _controlsVisible = showForVideo;
        RefreshPlayerNavigationHotspots();

        if (!showForVideo)
        {
            _controlsHideTimer.Stop();
        }
    }

    private void ShowControls()
    {
        if (ControlBar.Visibility != Visibility.Visible)
        {
            return;
        }

        ControlBar.Opacity = 1;
        ControlBar.IsHitTestVisible = true;
        _controlsVisible = true;
        _controlsHideTimer.Stop();
    }

    private void HideControls()
    {
        ControlBar.Opacity = 1;
        ControlBar.IsHitTestVisible = true;
        _controlsVisible = true;
    }

    private bool CanShowPlayerNavigationHotspots()
    {
        return ViewModel.SelectedMedia != null
            && ViewModel.FilteredMediaItems.Count > 1
            && EmptyState.Visibility != Visibility.Visible;
    }

    private void RefreshPlayerNavigationHotspots()
    {
        var canShow = CanShowPlayerNavigationHotspots();
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

    private void BeginImagePreviewSession(MediaItemViewModel media)
    {
        EndImageDrag();
        _pendingImageFitMediaId = media.Id;
        UpdatePreviewImageSourceSize(media.Media.Width, media.Media.Height);
        ImageScrollViewer.ChangeView(0, 0, 1.0f, true);
        UpdateImageZoomUi();
        DispatcherQueue.TryEnqueue(TryApplyPendingImageFit);
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
        if (PreviewImage != null)
        {
            PreviewImage.Width = double.NaN;
            PreviewImage.Height = double.NaN;
        }
    }

    private void SetPreviewImageSourceSize(double width, double height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        _imageSourceWidth = width;
        _imageSourceHeight = height;
        if (PreviewImage != null)
        {
            PreviewImage.Width = width;
            PreviewImage.Height = height;
        }
    }

    private void PreviewImageElement_ImageOpened(object sender, RoutedEventArgs e)
    {
        if (_imageSourceWidth <= 0 || _imageSourceHeight <= 0)
        {
            if (PreviewImage?.Source is BitmapImage bitmap && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
            {
                SetPreviewImageSourceSize(bitmap.PixelWidth, bitmap.PixelHeight);
            }
        }

        TryApplyPendingImageFit();
    }

    private void ImageScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0)
        {
            return;
        }

        TryApplyPendingImageFit();
    }

    private void TryApplyPendingImageFit()
    {
        var selectedMedia = ViewModel.SelectedMedia;
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
        if (ImageScrollViewer.Visibility != Visibility.Visible)
        {
            return false;
        }

        if (!TryGetImageViewportSize(out var viewportWidth, out var viewportHeight)
            || !TryGetImageSourceSize(out var imageWidth, out var imageHeight))
        {
            return false;
        }

        var targetZoom = Math.Min(viewportWidth / imageWidth, viewportHeight / imageHeight);
        if (double.IsNaN(targetZoom) || double.IsInfinity(targetZoom) || targetZoom <= 0)
        {
            return false;
        }

        targetZoom = Math.Clamp(targetZoom, ImageScrollViewer.MinZoomFactor, ImageScrollViewer.MaxZoomFactor);
        ImageScrollViewer.ChangeView(0, 0, (float)targetZoom, true);
        UpdateImageZoomUi(targetZoom);

        DispatcherQueue.TryEnqueue(() =>
        {
            ImageScrollViewer.ChangeView(ImageScrollViewer.ScrollableWidth / 2, ImageScrollViewer.ScrollableHeight / 2, null, true);
        });

        return true;
    }

    private bool TryGetImageViewportSize(out double viewportWidth, out double viewportHeight)
    {
        viewportWidth = ImageScrollViewer.ViewportWidth > 0 ? ImageScrollViewer.ViewportWidth : ImageScrollViewer.ActualWidth;
        viewportHeight = ImageScrollViewer.ViewportHeight > 0 ? ImageScrollViewer.ViewportHeight : ImageScrollViewer.ActualHeight;
        return viewportWidth > 0 && viewportHeight > 0;
    }

    private bool TryGetImageSourceSize(out double imageWidth, out double imageHeight)
    {
        imageWidth = _imageSourceWidth;
        imageHeight = _imageSourceHeight;
        if (imageWidth > 0 && imageHeight > 0)
        {
            return true;
        }

        if (PreviewImage?.Source is BitmapImage bitmap && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
        {
            imageWidth = bitmap.PixelWidth;
            imageHeight = bitmap.PixelHeight;
            return true;
        }

        return false;
    }

    private void ZoomImage(double delta, Windows.Foundation.Point anchorPoint)
    {
        if (ImageScrollViewer.Visibility != Visibility.Visible)
        {
            return;
        }

        if (!TryGetImageViewportSize(out var viewportWidth, out var viewportHeight)
            || !TryGetImageSourceSize(out var imageWidth, out var imageHeight))
        {
            return;
        }

        _pendingImageFitMediaId = null;
        var currentZoom = Math.Max((double)ImageScrollViewer.ZoomFactor, 0.01);
        var targetZoom = Math.Clamp(currentZoom + delta, ImageScrollViewer.MinZoomFactor, ImageScrollViewer.MaxZoomFactor);
        if (Math.Abs(targetZoom - currentZoom) < 0.001)
        {
            return;
        }

        var contentX = (ImageScrollViewer.HorizontalOffset + anchorPoint.X) / currentZoom;
        var contentY = (ImageScrollViewer.VerticalOffset + anchorPoint.Y) / currentZoom;
        var targetHorizontalOffset = contentX * targetZoom - anchorPoint.X;
        var targetVerticalOffset = contentY * targetZoom - anchorPoint.Y;
        var maxHorizontalOffset = Math.Max(0, imageWidth * targetZoom - viewportWidth);
        var maxVerticalOffset = Math.Max(0, imageHeight * targetZoom - viewportHeight);

        ImageScrollViewer.ChangeView(
            Math.Clamp(targetHorizontalOffset, 0, maxHorizontalOffset),
            Math.Clamp(targetVerticalOffset, 0, maxVerticalOffset),
            (float)targetZoom,
            true);
    }

    private void ZoomImage(double delta)
    {
        if (!TryGetImageViewportSize(out var viewportWidth, out var viewportHeight))
        {
            return;
        }

        ZoomImage(delta, new Windows.Foundation.Point(viewportWidth / 2, viewportHeight / 2));
    }

    private void ResetImageZoom()
    {
        _pendingImageFitMediaId = null;
        if (!FitImageToViewport())
        {
            ImageScrollViewer.ChangeView(0, 0, 1.0f, true);
            UpdateImageZoomUi(1.0);
        }
    }

    private void ImageScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        UpdateImageZoomUi();
    }

    private void UpdateImageZoomUi(double? zoomFactor = null)
    {
        if (ImageZoomText == null)
        {
            return;
        }

        var effectiveZoom = zoomFactor ?? ImageScrollViewer.ZoomFactor;
        var percentage = Math.Max(1, (int)Math.Round(effectiveZoom * 100));
        ImageZoomText.Text = $"{percentage}%";
    }

    private void ImageScrollViewer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(ImageScrollViewer).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _pendingImageFitMediaId = null;
        _isImageDragging = true;
        _imageDragStartPoint = e.GetCurrentPoint(ImageScrollViewer).Position;
        _imageDragStartHorizontalOffset = ImageScrollViewer.HorizontalOffset;
        _imageDragStartVerticalOffset = ImageScrollViewer.VerticalOffset;
        ImageScrollViewer.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ImageScrollViewer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isImageDragging)
        {
            return;
        }

        var position = e.GetCurrentPoint(ImageScrollViewer).Position;
        var horizontalOffset = Math.Clamp(
            _imageDragStartHorizontalOffset - (position.X - _imageDragStartPoint.X),
            0,
            ImageScrollViewer.ScrollableWidth);
        var verticalOffset = Math.Clamp(
            _imageDragStartVerticalOffset - (position.Y - _imageDragStartPoint.Y),
            0,
            ImageScrollViewer.ScrollableHeight);

        ImageScrollViewer.ChangeView(horizontalOffset, verticalOffset, null, true);
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
        ResetImageZoom();
        e.Handled = true;
    }

    private void ImageScrollViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ImageScrollViewer);
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
        ImageScrollViewer.ReleasePointerCaptures();
    }

    private void ResetClipState(MediaItemViewModel? media)
    {
        if (media?.Type != MediaType.Video)
        {
            _clipMediaId = null;
            _clipStart = null;
            _clipEnd = null;
            _clipSegments.Clear();
            _clipOutputDirectory = null;
            _clipMode = VideoClipMode.Keep;
            _clipStatusMessage = string.Empty;
            UpdateClipUi();
            return;
        }

        if (string.Equals(_clipMediaId, media.Id, StringComparison.Ordinal))
        {
            UpdateClipUi();
            return;
        }

        _clipMediaId = media.Id;
        _clipStart = TimeSpan.Zero;
        _clipEnd = null;
        _clipSegments.Clear();
        _clipOutputDirectory = Path.GetDirectoryName(media.FileSystemPath);
        _clipMode = VideoClipMode.Keep;
        _clipStatusMessage = _clipService.IsAvailable
            ? "使用 I / O 标记区间，或打开“片段方案...”配置多段剪辑。"
            : _clipService.UnavailableReason;
        UpdateClipUi();
    }

    private void ResetClipRangeToFullDuration()
    {
        _clipStart = TimeSpan.Zero;
        var duration = GetCurrentVideoDuration();
        _clipEnd = duration > TimeSpan.Zero ? duration : null;
    }

    private void InitializeClipRange(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        _clipStart ??= TimeSpan.Zero;
        if (!_clipEnd.HasValue || _clipEnd.Value > duration)
        {
            _clipEnd = duration;
        }
    }

    private void UpdateClipUi()
    {
        var showClipBar = ViewModel.SelectedMedia?.Type == MediaType.Video;
        ClipBar.Visibility = showClipBar ? Visibility.Visible : Visibility.Collapsed;
        if (!showClipBar)
        {
            return;
        }

        var duration = GetCurrentVideoDuration();
        InitializeClipRange(duration);

        var clipStart = _clipStart ?? TimeSpan.Zero;
        var clipEnd = _clipEnd ?? duration;
        var clipLength = clipEnd > clipStart ? clipEnd - clipStart : TimeSpan.Zero;
        var configuredSegments = GetConfiguredSegments();
        var summaryDuration = CalculateEffectiveOutputDuration(configuredSegments, GetCurrentVideoDuration());

        ClipStartText.Text = $"入点：{FormatTime(clipStart)}";
        ClipEndText.Text = clipEnd > TimeSpan.Zero ? $"出点：{FormatTime(clipEnd)}" : "出点：--";
        ClipDurationText.Text = summaryDuration > TimeSpan.Zero ? $"时长：{FormatTime(summaryDuration)}" : clipLength > TimeSpan.Zero ? $"时长：{FormatTime(clipLength)}" : "时长：--";
        ClipModeText.Text = $"模式：{(_clipMode == VideoClipMode.Keep ? "保留片段" : "删除片段")}";
        ClipSegmentCountText.Text = $"片段：{configuredSegments.Count}";
        ClipOutputText.Text = $"输出：{(string.IsNullOrWhiteSpace(_clipOutputDirectory) ? "原目录" : Path.GetFileName(_clipOutputDirectory))}";
        ClipExportProgressBar.Visibility = _isExportingClip ? Visibility.Visible : Visibility.Collapsed;

        if (!_isExportingClip && string.IsNullOrWhiteSpace(_clipStatusMessage))
        {
            ClipExportProgressBar.Value = 0;
        }

        ClipStatusText.Text = _clipStatusMessage;
        SetClipStartButton.IsEnabled = !_isExportingClip && duration > TimeSpan.Zero;
        SetClipEndButton.IsEnabled = !_isExportingClip && duration > TimeSpan.Zero;
        ClipPlanButton.IsEnabled = !_isExportingClip && duration > TimeSpan.Zero;
        ClearClipButton.IsEnabled = !_isExportingClip;
        ExportClipButton.IsEnabled = !_isExportingClip && _clipService.IsAvailable && configuredSegments.Count > 0;
        ExportClipButton.Content = _isExportingClip ? "导出中..." : "导出剪辑 (E)";
    }

    private async Task ShowClipPlanDialogAsync()
    {
        var media = ViewModel.SelectedMedia;
        if (media?.Type != MediaType.Video)
        {
            return;
        }

        var duration = GetBestKnownVideoDuration(media);
        if (duration <= TimeSpan.Zero)
        {
            await ShowInfoDialogAsync("提示", "视频时长尚未准备好，请开始播放或稍后再试。");
            return;
        }

        var workingSegments = new ObservableCollection<ClipSegmentDisplayItem>(
            _clipSegments.Select(segment => new ClipSegmentDisplayItem(segment.Start, segment.End)));
        var panel = new ClipPlanPanel(
            duration,
            _clipMode,
            FormatEditableTime(_clipStart ?? TimeSpan.Zero),
            FormatEditableTime(_clipEnd ?? duration),
            _clipOutputDirectory ?? Path.GetDirectoryName(media.FileSystemPath) ?? string.Empty);
        panel.SegmentsListView.ItemsSource = workingSegments;
        panel.SegmentsListView.DisplayMemberPath = nameof(ClipSegmentDisplayItem.DisplayText);

        void RefreshSummary()
        {
            var segments = NormalizeSegments(workingSegments.Select(item => item.ToSegment()), duration);
            var mode = (VideoClipMode?)((panel.ModeComboBox.SelectedItem as ComboBoxItem)?.Tag ?? VideoClipMode.Keep) ?? VideoClipMode.Keep;
            var outputDuration = CalculateEffectiveOutputDuration(segments, duration, mode);
            panel.SummaryText.Text = segments.Count == 0
                ? $"总时长：{FormatTime(duration)}。请先添加至少一个片段。"
                : $"共 {segments.Count} 段，模式：{(mode == VideoClipMode.Keep ? "保留片段" : "删除片段")}，导出后预计时长：{FormatTime(outputDuration)}";
        }

        panel.UseCurrentButton.Click += (_, _) =>
        {
            panel.StartBox.Text = FormatEditableTime(_clipStart ?? TimeSpan.Zero);
            panel.EndBox.Text = FormatEditableTime(_clipEnd ?? duration);
        };

        panel.AddButton.Click += (_, _) =>
        {
            if (!TryParseTimeInput(panel.StartBox.Text, out var start) || !TryParseTimeInput(panel.EndBox.Text, out var end))
            {
                panel.SummaryText.Text = "时间格式无效，请使用 mm:ss、hh:mm:ss 或 hh:mm:ss.fff。";
                return;
            }

            if (end <= start)
            {
                panel.SummaryText.Text = "结束时间必须晚于开始时间。";
                return;
            }

            workingSegments.Add(new ClipSegmentDisplayItem(start, end));
            RefreshSummary();
        };

        panel.RemoveButton.Click += (_, _) =>
        {
            if (panel.SegmentsListView.SelectedItem is ClipSegmentDisplayItem selected)
            {
                workingSegments.Remove(selected);
                RefreshSummary();
            }
        };

        panel.ClearButton.Click += (_, _) =>
        {
            workingSegments.Clear();
            RefreshSummary();
        };

        panel.PickOutputButton.Click += async (_, _) =>
        {
            var window = App.MainWindow;
            if (window == null)
            {
                return;
            }

            var folder = await PickerHelpers.PickFolderAsync(window);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                panel.OutputDirBox.Text = folder;
            }
        };

        panel.ModeComboBox.SelectionChanged += (_, _) => RefreshSummary();
        workingSegments.CollectionChanged += (_, _) => RefreshSummary();
        RefreshSummary();

        var dialog = new ContentDialog
        {
            Title = "片段方案",
            Content = panel,
            PrimaryButtonText = "保存方案",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var normalized = NormalizeSegments(workingSegments.Select(item => item.ToSegment()), duration);
        _clipSegments.Clear();
        foreach (var segment in normalized)
        {
            _clipSegments.Add(segment);
        }

        _clipMode = (VideoClipMode?)((panel.ModeComboBox.SelectedItem as ComboBoxItem)?.Tag ?? VideoClipMode.Keep) ?? VideoClipMode.Keep;
        _clipOutputDirectory = string.IsNullOrWhiteSpace(panel.OutputDirBox.Text)
                ? Path.GetDirectoryName(media.FileSystemPath)
            : panel.OutputDirBox.Text.Trim();
        _clipStatusMessage = normalized.Count == 0
            ? "片段方案已清空，导出时将使用当前入点/出点。"
            : $"片段方案已保存，共 {normalized.Count} 段。";
        UpdateClipUi();
    }

    private IReadOnlyList<VideoClipSegment> GetConfiguredSegments()
    {
        var duration = GetBestKnownVideoDuration(ViewModel.SelectedMedia);
        if (_clipSegments.Count > 0)
        {
            return NormalizeSegments(_clipSegments, duration);
        }

        if (_clipStart.HasValue && _clipEnd.HasValue && _clipEnd.Value > _clipStart.Value)
        {
            return NormalizeSegments(
                new[]
                {
                    new VideoClipSegment
                    {
                        Start = _clipStart.Value,
                        End = _clipEnd.Value
                    }
                },
                duration);
        }

        return Array.Empty<VideoClipSegment>();
    }

    private TimeSpan GetBestKnownVideoDuration(MediaItemViewModel? media)
    {
        if (media?.Media.Duration is > 0)
        {
            return TimeSpan.FromSeconds(media.Media.Duration.Value);
        }

        return GetCurrentVideoDuration();
    }

    private static List<VideoClipSegment> NormalizeSegments(IEnumerable<VideoClipSegment> segments, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return segments
                .Where(segment => segment.End > segment.Start)
                .OrderBy(segment => segment.Start)
                .ToList();
        }

        var normalized = segments
            .Select(segment => new VideoClipSegment
            {
                Start = segment.Start < TimeSpan.Zero ? TimeSpan.Zero : segment.Start,
                End = segment.End > duration ? duration : segment.End
            })
            .Where(segment => segment.End > segment.Start)
            .OrderBy(segment => segment.Start)
            .ToList();
        if (normalized.Count == 0)
        {
            return normalized;
        }

        var merged = new List<VideoClipSegment> { normalized[0] };
        for (var i = 1; i < normalized.Count; i++)
        {
            var current = normalized[i];
            var previous = merged[^1];
            if (current.Start <= previous.End)
            {
                merged[^1] = new VideoClipSegment
                {
                    Start = previous.Start,
                    End = current.End > previous.End ? current.End : previous.End
                };
                continue;
            }

            merged.Add(current);
        }

        return merged;
    }

    private static TimeSpan CalculateEffectiveOutputDuration(IReadOnlyList<VideoClipSegment> segments, TimeSpan totalDuration, VideoClipMode? mode = null)
    {
        if (segments.Count == 0)
        {
            return TimeSpan.Zero;
        }

        var selectedMode = mode ?? VideoClipMode.Keep;
        if (selectedMode == VideoClipMode.Keep)
        {
            return segments.Aggregate(TimeSpan.Zero, (current, segment) => current + segment.Duration);
        }

        var removed = segments.Aggregate(TimeSpan.Zero, (current, segment) => current + segment.Duration);
        var remaining = totalDuration - removed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static string FormatEditableTime(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        var hours = (int)value.TotalHours;
        return $"{hours:00}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}";
    }

    private static bool TryParseTimeInput(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
        {
            value = TimeSpan.FromSeconds(seconds);
            return true;
        }

        var formats = new[]
        {
            @"m\:ss",
            @"mm\:ss",
            @"m\:ss\.fff",
            @"mm\:ss\.fff",
            @"h\:mm\:ss",
            @"hh\:mm\:ss",
            @"h\:mm\:ss\.fff",
            @"hh\:mm\:ss\.fff"
        };

        return TimeSpan.TryParseExact(trimmed, formats, CultureInfo.InvariantCulture, out value)
            || TimeSpan.TryParse(trimmed, CultureInfo.InvariantCulture, out value);
    }

    private TimeSpan GetCurrentPlaybackPosition() => _player?.PlaybackSession.Position ?? TimeSpan.Zero;

    private TimeSpan GetCurrentVideoDuration() => _player?.PlaybackSession.NaturalDuration ?? TimeSpan.Zero;

    private static TimeSpan ClampToDuration(TimeSpan value, TimeSpan duration)
    {
        if (value < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (duration > TimeSpan.Zero && value > duration)
        {
            return duration;
        }

        return value;
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

        await ShowTagEditorAsync(selected);
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

        var playlist = await ShowPlaylistPickerDialogAsync("加入播放列表");
        if (playlist == null)
        {
            return;
        }

        await ViewModel.AddMediaToPlaylistAsync(playlist.Id, selected);
    }

    private async Task ShowTagEditorAsync(IReadOnlyList<MediaItemViewModel> items)
    {
        var isSingle = items.Count == 1;
        var panel = new TagEditorPanel(
            isSingle,
            items.Count,
            isSingle ? string.Join(", ", items[0].Tags) : string.Empty);

        var dialog = new ContentDialog
        {
            Title = isSingle ? "编辑标签" : "批量编辑标签",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        var tags = ParseTags(panel.TagTextBox.Text);
        var mode = isSingle
            ? TagUpdateMode.Replace
            : (TagUpdateMode?)((panel.ModeComboBox.SelectedItem as ComboBoxItem)?.Tag ?? TagUpdateMode.Append) ?? TagUpdateMode.Append;
        await ViewModel.UpdateTagsAsync(items, tags, mode);
    }

    private async Task<Playlist?> ShowPlaylistPickerDialogAsync(string title)
    {
        var comboBox = new ComboBox
        {
            DisplayMemberPath = nameof(Playlist.Name),
            ItemsSource = ViewModel.Playlists,
            SelectedIndex = 0,
            MinWidth = 260,
            Style = Application.Current.Resources["GlassComboBoxStyle"] as Style
        };

        var dialog = new ContentDialog
        {
            Title = title,
            Content = comboBox,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? comboBox.SelectedItem as Playlist : null;
    }

    private MenuFlyout BuildMediaContextFlyout(IReadOnlyList<MediaItemViewModel> selected)
    {
        var flyout = new MenuFlyout();

        var openFolder = new MenuFlyoutItem { Text = "打开所在目录" };
        openFolder.Click += (_, _) => OpenMediaFolder(selected[0]);
        flyout.Items.Add(openFolder);

        var editTags = new MenuFlyoutItem { Text = selected.Count == 1 ? "编辑标签" : $"批量编辑标签 ({selected.Count})" };
        editTags.Click += async (_, _) => await ShowTagEditorAsync(selected);
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

    private void RootLayout_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        HandleLibraryAutoVisibility(e.GetCurrentPoint(RootLayout).Position.X);
    }

    private void LibraryPinButton_Click(object sender, RoutedEventArgs e)
    {
        _isLibraryPinned = !_isLibraryPinned;
        UpdateLibraryPinButtonUi();
        UpdateLibraryPaneState(preferOpen: _isLibraryPinned || ViewModel.SelectedMedia == null);
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
    }

    private void LibraryPaneResizer_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _isResizingLibraryPane = false;
        UpdateLibraryPaneState(preferOpen: true);
    }

    private void UpdateLibraryPaneState(bool preferOpen = false)
    {
        UpdateLibraryPanePresentation();
        LibrarySplitView.OpenPaneLength = _libraryPaneWidth;

        var shouldOpen = preferOpen || _isLibraryPinned || ViewModel.SelectedMedia == null;
        SetLibraryPaneOpen(shouldOpen);
        UpdateLibraryPinButtonUi();
    }

    private void HandleLibraryAutoVisibility(double pointerX)
    {
        if (_isLibraryPinned || _isResizingLibraryPane)
        {
            return;
        }

        if (ViewModel.SelectedMedia == null)
        {
            SetLibraryPaneOpen(true);
            return;
        }

        if (pointerX <= LibraryRevealHotZoneWidth)
        {
            SetLibraryPaneOpen(true);
            return;
        }

        if (LibrarySplitView.IsPaneOpen && pointerX > _libraryPaneWidth + LibraryHideBufferWidth)
        {
            SetLibraryPaneOpen(false);
        }
    }

    private void SetLibraryPaneOpen(bool isOpen)
    {
        if (LibrarySplitView.IsPaneOpen == isOpen)
        {
            return;
        }

        LibrarySplitView.IsPaneOpen = isOpen;
    }

    private void UpdateLibraryPinButtonUi()
    {
        if (LibraryPinButton == null)
        {
            return;
        }

        LibraryPinButton.Opacity = _isLibraryPinned ? 1 : 0.7;
        LibraryPinButton.Foreground = new SolidColorBrush(_isLibraryPinned
            ? Microsoft.UI.Colors.White
            : Microsoft.UI.ColorHelper.FromArgb(204, 255, 255, 255));
        ToolTipService.SetToolTip(LibraryPinButton, _isLibraryPinned ? "固定媒体库" : "媒体库自动隐藏");

        if (LibraryPinSymbolIcon != null)
        {
            LibraryPinSymbolIcon.Symbol = _isLibraryPinned ? Symbol.Pin : Symbol.UnPin;
        }
    }

    private void UpdateLibraryPanePresentation()
    {
        if (LibrarySplitView == null || LibraryPaneRoot == null)
        {
            return;
        }

        LibrarySplitView.DisplayMode = _isLibraryPinned
            ? SplitViewDisplayMode.Inline
            : SplitViewDisplayMode.Overlay;
        LibraryPaneRoot.Background = _isLibraryPinned
            ? Application.Current.Resources["SurfaceAltBrush"] as Brush ?? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 45, 45, 45))
            : new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(214, 32, 32, 32));
        LibraryPaneResizer.Opacity = _isLibraryPinned ? 1 : 0.55;
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

    private async Task ShowSettingsDialogAsync()
    {
        var panel = new SettingsPanel(ViewModel);

        panel.ChooseDataDirButton.Click += async (_, _) =>
        {
            var window = App.MainWindow;
            if (window == null)
            {
                return;
            }

            var folder = await PickerHelpers.PickFolderAsync(window);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                panel.SetDataDirectory(folder);
            }
        };

        panel.AddWatchedFolderButton.Click += async (_, _) =>
        {
            var window = App.MainWindow;
            if (window == null)
            {
                return;
            }

            var folder = await PickerHelpers.PickFolderAsync(window);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                panel.TryAddWatchedFolder(folder);
            }
        };

        panel.RemoveWatchedFolderButton.Click += (_, _) => panel.RemoveSelectedWatchedFolder();
        panel.ClearWatchedFoldersButton.Click += (_, _) => panel.ClearWatchedFolders();
        panel.ProtectFolderButton.Click += (_, _) => panel.ProtectSelectedFolder();
        panel.UnprotectFolderButton.Click += (_, _) => panel.UnprotectSelectedFolder();
        panel.ClearCacheButton.Click += (_, _) => ViewModel.ClearThumbnailCache();
        panel.ResetLibraryButton.Click += async (_, _) =>
        {
            var confirmed = await ConfirmAsync("清理缓存并重置库", "这会清空媒体记录与标签，但保留播放列表名称。是否继续？", "继续");
            if (confirmed)
            {
                await ViewModel.ResetLibraryAsync(false);
            }
        };
        panel.ClearAllButton.Click += async (_, _) =>
        {
            var confirmed = await ConfirmAsync("清理全部应用数据", "这会清空媒体库、标签、播放列表和监控文件夹配置。是否继续？", "清空");
            if (confirmed)
            {
                await ViewModel.ResetLibraryAsync(true);
                panel.ClearWatchedFolders();
            }
        };

        var dialog = new ContentDialog
        {
            Title = "设置",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        if (panel.HasProtectedFoldersWithoutPassword())
        {
            await ShowInfoDialogAsync("设置未保存", "存在受保护文件夹时必须设置全局密码。");
            return;
        }

        ViewModel.SetThemeMode(panel.SelectedThemeMode);
        App.ApplyThemeMode(panel.SelectedThemeMode);
        ViewModel.SetPortableMode(panel.PortableModeEnabled);
        if (!string.IsNullOrWhiteSpace(panel.DataDirectory))
        {
            ViewModel.SetDataDir(panel.DataDirectory);
        }

        ViewModel.SetLockPassword(panel.GlobalPassword);
        ViewModel.UpdateWatchedFolders(panel.GetWatchedFolders());
        await ShowInfoDialogAsync("设置已保存", "设置已写入。若切换了数据目录或便携模式，重启后会使用新的数据位置。");
    }

    private async Task ShowFolderLockDialogAsync()
    {
        var protectedFolders = new ObservableCollection<WatchedFolder>(ViewModel.GetProtectedFolders());
        if (protectedFolders.Count == 0)
        {
            await ShowInfoDialogAsync("锁定管理", "当前没有受保护的监控文件夹。请先在设置中为文件夹启用保护。");
            return;
        }

        var panel = new LockManagerPanel(protectedFolders);
        panel.FoldersListView.DisplayMemberPath = nameof(WatchedFolder.Path);

        void RefreshStatus()
        {
            if (panel.SelectedFolder is not WatchedFolder selected)
            {
                panel.StatusText.Text = "请选择一个受保护文件夹。";
                return;
            }

            panel.StatusText.Text = ViewModel.IsFolderUnlocked(selected.Path)
                ? $"当前状态：已解锁。{Path.GetFileName(selected.Path)} 中的媒体现在可见。"
                : $"当前状态：已锁定。输入全局密码后可解锁 {Path.GetFileName(selected.Path)}。";
        }

        panel.FoldersListView.SelectionChanged += (_, _) => RefreshStatus();
        RefreshStatus();

        panel.UnlockButton.Click += async (_, _) =>
        {
            if (panel.SelectedFolder is not WatchedFolder selected)
            {
                return;
            }

            var password = await ShowPasswordInputDialogAsync("解锁文件夹", "输入全局密码");
            if (password == null)
            {
                return;
            }

            var unlocked = await ViewModel.UnlockFolderAsync(selected.Path, password);
            if (!unlocked)
            {
                panel.StatusText.Text = "密码错误，未能解锁文件夹。";
                return;
            }

            RefreshStatus();
        };

        panel.RelockButton.Click += async (_, _) =>
        {
            if (panel.SelectedFolder is not WatchedFolder selected)
            {
                return;
            }

            await ViewModel.LockFolderAsync(selected.Path);
            RefreshStatus();
        };

        panel.RelockAllButton.Click += async (_, _) =>
        {
            await ViewModel.RelockAllFoldersAsync();
            RefreshStatus();
        };

        var dialog = new ContentDialog
        {
            Title = "锁定管理",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot
        };

        await dialog.ShowAsync();
    }

    private static IReadOnlyList<string> ParseTags(string value)
    {
        return value
            .Split([',', '，', ';', '；', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<string?> ShowTextInputDialogAsync(string title, string placeholder, string initialValue, string primaryButtonText)
    {
        var textBox = new TextBox
        {
            Text = initialValue,
            PlaceholderText = placeholder,
            MinWidth = 280,
            Style = Application.Current.Resources["GlassTextBoxStyle"] as Style
        };

        var dialog = new ContentDialog
        {
            Title = title,
            Content = textBox,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? textBox.Text.Trim() : null;
    }

    private async Task<string?> ShowPasswordInputDialogAsync(string title, string placeholder)
    {
        var passwordBox = new PasswordBox
        {
            PlaceholderText = placeholder,
            MinWidth = 280,
            Style = Application.Current.Resources["GlassPasswordBoxStyle"] as Style
        };

        var dialog = new ContentDialog
        {
            Title = title,
            Content = passwordBox,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? passwordBox.Password : null;
    }

    private async Task<bool> ConfirmAsync(string title, string message, string primaryButtonText)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowInfoDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot
        };

        await dialog.ShowAsync();
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

    private sealed class ClipSegmentDisplayItem
    {
        public ClipSegmentDisplayItem(TimeSpan start, TimeSpan end)
        {
            Start = start;
            End = end;
        }

        public TimeSpan Start { get; }

        public TimeSpan End { get; }

        public string DisplayText => $"{FormatEditableTime(Start)} - {FormatEditableTime(End)}";

        public VideoClipSegment ToSegment()
        {
            return new VideoClipSegment
            {
                Start = Start,
                End = End
            };
        }
    }
}
