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
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using JvJvMediaManager.Models;
using JvJvMediaManager.Services;
using JvJvMediaManager.Utilities;
using JvJvMediaManager.ViewModels;
using WinRT.Interop;

namespace JvJvMediaManager.Views;

public sealed partial class MainPage : Page
{
    private enum PlaybackMode
    {
        ListLoop,
        SingleLoop,
        Shuffle
    }

    public MainViewModel ViewModel { get; } = new();

    private readonly DebounceDispatcher _debouncer = new();
    private readonly DispatcherTimer _playbackTimer = new();
    private readonly DispatcherTimer _controlsHideTimer = new();
    private readonly Random _random = new();
    private readonly VideoClipService _clipService = new();

    private MediaPlayer? _player;
    private AppWindow? _appWindow;

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

    public MainPage()
    {
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.SelectedTags.CollectionChanged += SelectedTags_CollectionChanged;
        ViewModel.SetDispatcher(DispatcherQueue);
        Loaded += MainPage_Loaded;
        Unloaded += MainPage_Unloaded;
        SelectedTagsControl.ItemsSource = ViewModel.SelectedTags;
        KeyDown += MainPage_KeyDown;

        _playbackTimer.Interval = TimeSpan.FromMilliseconds(250);
        _playbackTimer.Tick += PlaybackTimer_Tick;

        _controlsHideTimer.Interval = TimeSpan.FromSeconds(2.5);
        _controlsHideTimer.Tick += ControlsHideTimer_Tick;

        UpdatePlaybackModeUi();
        UpdateControlBarState(false);
        UpdateClipUi();
        RefreshScanProgressVisibility();
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
        RefreshTagChips();
        RefreshPlaylistSelection();
        RefreshScanProgressVisibility();
        UpdateGridItemSize();
    }

    private void MainPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.SelectedTags.CollectionChanged -= SelectedTags_CollectionChanged;
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
        UpdateWatchedFolders(paths);
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
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
        UpdateWatchedFolders(new[] { folder });
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.WatchedFolders.Count == 0)
        {
            return;
        }

        await ViewModel.RescanFoldersAsync();
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

    private void ListViewMode_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ViewMode = MediaViewMode.List;
        ListView.Visibility = Visibility.Visible;
        GridView.Visibility = Visibility.Collapsed;
        DispatcherQueue.TryEnqueue(SyncSelectionFromViewModel);
    }

    private void GridViewMode_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ViewMode = MediaViewMode.Grid;
        ListView.Visibility = Visibility.Collapsed;
        GridView.Visibility = Visibility.Visible;
        UpdateGridItemSize();
        DispatcherQueue.TryEnqueue(SyncSelectionFromViewModel);
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
            player.Source = MediaSource.CreateFromUri(new Uri(media.Media.Path));
            UpdateControlBarState(true);
            ShowControls();
        }
        else
        {
            VideoPlayer.Visibility = Visibility.Collapsed;
            ImageScrollViewer.Visibility = Visibility.Visible;
            if (_player != null)
            {
                _player.Pause();
                _player.Source = null;
            }

            ImageViewer.Source = media.Thumbnail ?? new BitmapImage(new Uri(media.Media.Path));
            ResetImageZoom();
            UpdateControlBarState(false);
        }
    }

    private void ClearPlayerSelection()
    {
        EmptyState.Visibility = Visibility.Visible;
        VideoPlayer.Visibility = Visibility.Collapsed;
        ImageScrollViewer.Visibility = Visibility.Collapsed;
        ImageViewer.Source = null;
        ResetClipState(null);
        if (_player != null)
        {
            _player.Pause();
            _player.Source = null;
        }

        UpdateControlBarState(false);
    }

    private void SyncSelectionFromViewModel()
    {
        _isSyncingSelection = true;
        try
        {
            var selected = ViewModel.SelectedMedia;
            ListView.SelectedItem = selected;
            GridView.SelectedItem = selected;

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

        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox or PasswordBox or RichEditBox or AutoSuggestBox or ComboBox)
        {
            return;
        }

        if (ViewModel.SelectedMedia == null)
        {
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Space && ViewModel.SelectedMedia.Type == MediaType.Video)
        {
            TogglePlayPause();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Left && ViewModel.SelectedMedia.Type == MediaType.Video)
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
        else if (e.Key == Windows.System.VirtualKey.Delete)
        {
            await DeleteSelectedAsync();
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
        SeekToSlider();
        _isSeeking = false;
        ShowControls();
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
    }

    private void PlayerRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        ShowControls();
    }

    private void PlayerRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        ShowControls();
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

        PlayPauseIcon.Symbol = _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing
            ? Symbol.Pause
            : Symbol.Play;
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

        _player.PlaybackSession.Position = TimeSpan.FromSeconds(ProgressSlider.Value);
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

    private void UpdateWatchedFolders(IEnumerable<string> paths)
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

        ViewModel.UpdateWatchedFolders(current);
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

    private void UpdateGridItemSize()
    {
        GridView.Tag = ViewModel.IconSize;
        if (GridView.ItemsPanelRoot is ItemsWrapGrid panel)
        {
            panel.ItemWidth = ViewModel.IconSize;
            panel.ItemHeight = ViewModel.IconSize;
        }
    }

    private void GridView_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var ctrlDown = (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (!ctrlDown)
        {
            return;
        }

        var delta = e.GetCurrentPoint(GridView).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        ViewModel.IconSize = Math.Clamp(ViewModel.IconSize + (delta > 0 ? 12 : -12), 96, 260);
        UpdateGridItemSize();
        e.Handled = true;
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

        var outputPath = _clipService.CreateOutputPath(media.Path, _clipOutputDirectory, _clipMode, segments);
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
                InputPath = media.Path,
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

        if (args.Item is MediaItemViewModel media)
        {
            _ = ViewModel.EnsureThumbnailAsync(media);
        }
    }

    private async Task DeleteSelectedAsync()
    {
        var selected = GetSelectedItems().ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var confirmed = await ConfirmAsync("确认删除", $"确定要删除选中的 {selected.Count} 个媒体记录吗？\n这不会删除原始文件。", "删除");
        if (!confirmed)
        {
            return;
        }

        var nextSelection = await ViewModel.DeleteMediaAsync(selected);
        if (nextSelection == null)
        {
            ClearPlayerSelection();
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
        _controlsVisible = showForVideo;

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
        _controlsHideTimer.Start();
    }

    private void HideControls()
    {
        if (!_controlsVisible)
        {
            return;
        }

        ControlBar.Opacity = 0;
        ControlBar.IsHitTestVisible = false;
        _controlsVisible = false;
    }

    private void ZoomImage(double delta)
    {
        if (ImageScrollViewer.Visibility != Visibility.Visible)
        {
            return;
        }

        var target = Math.Clamp(ImageScrollViewer.ZoomFactor + delta, ImageScrollViewer.MinZoomFactor, ImageScrollViewer.MaxZoomFactor);
        ImageScrollViewer.ChangeView(null, null, (float)target, true);
    }

    private void ResetImageZoom()
    {
        if (ImageScrollViewer.Visibility != Visibility.Visible)
        {
            return;
        }

        ImageScrollViewer.ChangeView(0, 0, 1.0f, true);
    }

    private void ImageScrollViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(ImageScrollViewer).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        ZoomImage(delta > 0 ? 0.1 : -0.1);
        e.Handled = true;
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
        _clipOutputDirectory = Path.GetDirectoryName(media.Path);
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
        var modeBox = new ComboBox
        {
            ItemsSource = new[]
            {
                new ComboBoxItem { Content = "保留片段", Tag = VideoClipMode.Keep },
                new ComboBoxItem { Content = "删除片段", Tag = VideoClipMode.Delete }
            },
            SelectedIndex = _clipMode == VideoClipMode.Keep ? 0 : 1
        };
        var startBox = new TextBox
        {
            PlaceholderText = "开始时间，如 00:01:23.500",
            Text = FormatEditableTime(_clipStart ?? TimeSpan.Zero)
        };
        var endBox = new TextBox
        {
            PlaceholderText = "结束时间，如 00:02:10.000",
            Text = FormatEditableTime(_clipEnd ?? duration)
        };
        var outputDirBox = new TextBox
        {
            Text = _clipOutputDirectory ?? Path.GetDirectoryName(media.Path) ?? string.Empty,
            PlaceholderText = "输出目录"
        };
        var summaryText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var listView = new ListView
        {
            ItemsSource = workingSegments,
            DisplayMemberPath = nameof(ClipSegmentDisplayItem.DisplayText),
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 180
        };

        void RefreshSummary()
        {
            var segments = NormalizeSegments(workingSegments.Select(item => item.ToSegment()), duration);
            var mode = (VideoClipMode?)((modeBox.SelectedItem as ComboBoxItem)?.Tag ?? VideoClipMode.Keep) ?? VideoClipMode.Keep;
            var outputDuration = CalculateEffectiveOutputDuration(segments, duration, mode);
            summaryText.Text = segments.Count == 0
                ? $"总时长：{FormatTime(duration)}。请先添加至少一个片段。"
                : $"共 {segments.Count} 段，模式：{(mode == VideoClipMode.Keep ? "保留片段" : "删除片段")}，导出后预计时长：{FormatTime(outputDuration)}";
        }

        var useCurrentButton = new Button { Content = "使用当前入/出点" };
        useCurrentButton.Click += (_, _) =>
        {
            startBox.Text = FormatEditableTime(_clipStart ?? TimeSpan.Zero);
            endBox.Text = FormatEditableTime(_clipEnd ?? duration);
        };

        var addButton = new Button { Content = "添加片段" };
        addButton.Click += (_, _) =>
        {
            if (!TryParseTimeInput(startBox.Text, out var start) || !TryParseTimeInput(endBox.Text, out var end))
            {
                summaryText.Text = "时间格式无效，请使用 mm:ss、hh:mm:ss 或 hh:mm:ss.fff。";
                return;
            }

            if (end <= start)
            {
                summaryText.Text = "结束时间必须晚于开始时间。";
                return;
            }

            workingSegments.Add(new ClipSegmentDisplayItem(start, end));
            RefreshSummary();
        };

        var removeButton = new Button { Content = "移除选中" };
        removeButton.Click += (_, _) =>
        {
            if (listView.SelectedItem is ClipSegmentDisplayItem selected)
            {
                workingSegments.Remove(selected);
                RefreshSummary();
            }
        };

        var clearButton = new Button { Content = "清空片段" };
        clearButton.Click += (_, _) =>
        {
            workingSegments.Clear();
            RefreshSummary();
        };

        var pickOutputButton = new Button { Content = "选择输出目录" };
        pickOutputButton.Click += async (_, _) =>
        {
            var window = App.MainWindow;
            if (window == null)
            {
                return;
            }

            var folder = await PickerHelpers.PickFolderAsync(window);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                outputDirBox.Text = folder;
            }
        };

        modeBox.SelectionChanged += (_, _) => RefreshSummary();
        workingSegments.CollectionChanged += (_, _) => RefreshSummary();

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(useCurrentButton);
        actions.Children.Add(addButton);
        actions.Children.Add(removeButton);
        actions.Children.Add(clearButton);

        var outputActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        outputActions.Children.Add(outputDirBox);
        outputActions.Children.Add(pickOutputButton);

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = $"源视频时长：{FormatTime(duration)}",
            Foreground = Application.Current.Resources["MutedTextBrush"] as Microsoft.UI.Xaml.Media.Brush
        });
        content.Children.Add(modeBox);
        content.Children.Add(startBox);
        content.Children.Add(endBox);
        content.Children.Add(actions);
        content.Children.Add(listView);
        content.Children.Add(outputActions);
        content.Children.Add(summaryText);
        RefreshSummary();

        var dialog = new ContentDialog
        {
            Title = "片段方案",
            Content = new ScrollViewer { Content = content, MaxHeight = 520 },
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

        _clipMode = (VideoClipMode?)((modeBox.SelectedItem as ComboBoxItem)?.Tag ?? VideoClipMode.Keep) ?? VideoClipMode.Keep;
        _clipOutputDirectory = string.IsNullOrWhiteSpace(outputDirBox.Text)
            ? Path.GetDirectoryName(media.Path)
            : outputDirBox.Text.Trim();
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
        var tagTextBox = new TextBox
        {
            AcceptsReturn = true,
            MinHeight = 120,
            TextWrapping = TextWrapping.Wrap,
            PlaceholderText = "输入标签，支持逗号、分号或换行分隔",
            Text = isSingle ? string.Join(", ", items[0].Tags) : string.Empty
        };

        var modeBox = new ComboBox
        {
            ItemsSource = new[]
            {
                new ComboBoxItem { Content = "覆盖标签", Tag = TagUpdateMode.Replace },
                new ComboBoxItem { Content = "追加标签", Tag = TagUpdateMode.Append }
            },
            SelectedIndex = isSingle ? 0 : 1
        };

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = isSingle ? "编辑当前媒体的标签。" : $"将对选中的 {items.Count} 个媒体统一编辑标签。",
            TextWrapping = TextWrapping.Wrap
        });

        if (!isSingle)
        {
            content.Children.Add(modeBox);
        }

        content.Children.Add(tagTextBox);

        var dialog = new ContentDialog
        {
            Title = isSingle ? "编辑标签" : "批量编辑标签",
            Content = content,
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

        var tags = ParseTags(tagTextBox.Text);
        var mode = isSingle
            ? TagUpdateMode.Replace
            : (TagUpdateMode?)((modeBox.SelectedItem as ComboBoxItem)?.Tag ?? TagUpdateMode.Append) ?? TagUpdateMode.Append;
        await ViewModel.UpdateTagsAsync(items, tags, mode);
    }

    private async Task<Playlist?> ShowPlaylistPickerDialogAsync(string title)
    {
        var comboBox = new ComboBox
        {
            DisplayMemberPath = nameof(Playlist.Name),
            ItemsSource = ViewModel.Playlists,
            SelectedIndex = 0,
            MinWidth = 260
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

        var delete = new MenuFlyoutItem { Text = "从媒体库删除" };
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
                Arguments = $"/select,\"{media.Path}\"",
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
        var dataDirTextBox = new TextBox
        {
            Text = ViewModel.ConfiguredDataDir ?? ViewModel.DataDir,
            PlaceholderText = "留空时使用默认目录"
        };
        var passwordBox = new PasswordBox
        {
            Password = ViewModel.LockPassword,
            PlaceholderText = "为受保护文件夹设置全局密码"
        };

        var portableToggle = new ToggleSwitch
        {
            Header = "便携模式（数据保存在程序目录 data）",
            IsOn = ViewModel.PortableMode
        };

        var watchedFolders = new ObservableCollection<WatchedFolder>(
            ViewModel.WatchedFolders.Select(item => new WatchedFolder
            {
                Path = item.Path,
                Locked = item.Locked
            }));
        var watchedFoldersList = new ListView
        {
            ItemsSource = watchedFolders,
            DisplayMemberPath = nameof(WatchedFolder.Path),
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 180
        };
        var watchedFolderStatusText = new TextBlock
        {
            Foreground = Application.Current.Resources["MutedTextBrush"] as Microsoft.UI.Xaml.Media.Brush,
            TextWrapping = TextWrapping.Wrap
        };
        var validationText = new TextBlock
        {
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange),
            TextWrapping = TextWrapping.Wrap
        };

        void RefreshWatchedFolderStatus()
        {
            if (watchedFoldersList.SelectedItem is not WatchedFolder folder)
            {
                watchedFolderStatusText.Text = "选中文件夹后，可设置是否受密码保护。";
                return;
            }

            watchedFolderStatusText.Text = folder.Locked
                ? $"当前状态：受保护。运行时可在“锁定管理”里输入密码解锁 {Path.GetFileName(folder.Path)}。"
                : "当前状态：未受保护。该文件夹中的媒体始终可见。";
        }

        watchedFoldersList.SelectionChanged += (_, _) => RefreshWatchedFolderStatus();
        RefreshWatchedFolderStatus();

        var chooseDataDirButton = new Button { Content = "选择数据目录" };
        chooseDataDirButton.Click += async (_, _) =>
        {
            var window = App.MainWindow;
            if (window == null)
            {
                return;
            }

            var folder = await PickerHelpers.PickFolderAsync(window);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                dataDirTextBox.Text = folder;
            }
        };

        var addWatchedFolderButton = new Button { Content = "添加监控文件夹" };
        addWatchedFolderButton.Click += async (_, _) =>
        {
            var window = App.MainWindow;
            if (window == null)
            {
                return;
            }

            var folder = await PickerHelpers.PickFolderAsync(window);
            if (!string.IsNullOrWhiteSpace(folder)
                && !watchedFolders.Any(item => string.Equals(item.Path, folder, StringComparison.OrdinalIgnoreCase)))
            {
                watchedFolders.Add(new WatchedFolder { Path = folder, Locked = false });
                RefreshWatchedFolderStatus();
            }
        };

        var removeWatchedFolderButton = new Button { Content = "移除选中" };
        removeWatchedFolderButton.Click += (_, _) =>
        {
            if (watchedFoldersList.SelectedItem is WatchedFolder folder)
            {
                watchedFolders.Remove(folder);
                RefreshWatchedFolderStatus();
            }
        };

        var clearWatchedFoldersButton = new Button { Content = "清空监控列表" };
        clearWatchedFoldersButton.Click += (_, _) =>
        {
            watchedFolders.Clear();
            RefreshWatchedFolderStatus();
        };

        var protectFolderButton = new Button { Content = "设为受保护" };
        protectFolderButton.Click += (_, _) =>
        {
            if (watchedFoldersList.SelectedItem is not WatchedFolder folder)
            {
                validationText.Text = "请先选择一个监控文件夹。";
                return;
            }

            if (string.IsNullOrWhiteSpace(passwordBox.Password))
            {
                validationText.Text = "要保护文件夹，请先填写全局密码。";
                return;
            }

            folder.Locked = true;
            watchedFoldersList.ItemsSource = null;
            watchedFoldersList.ItemsSource = watchedFolders;
            watchedFoldersList.DisplayMemberPath = nameof(WatchedFolder.Path);
            watchedFoldersList.SelectedItem = folder;
            validationText.Text = string.Empty;
            RefreshWatchedFolderStatus();
        };

        var unprotectFolderButton = new Button { Content = "取消保护" };
        unprotectFolderButton.Click += (_, _) =>
        {
            if (watchedFoldersList.SelectedItem is not WatchedFolder folder)
            {
                validationText.Text = "请先选择一个监控文件夹。";
                return;
            }

            folder.Locked = false;
            watchedFoldersList.ItemsSource = null;
            watchedFoldersList.ItemsSource = watchedFolders;
            watchedFoldersList.DisplayMemberPath = nameof(WatchedFolder.Path);
            watchedFoldersList.SelectedItem = folder;
            validationText.Text = string.Empty;
            RefreshWatchedFolderStatus();
        };

        var clearCacheButton = new Button { Content = "清理缩略图缓存" };
        clearCacheButton.Click += (_, _) => ViewModel.ClearThumbnailCache();

        var resetLibraryButton = new Button { Content = "清理缓存并重置库" };
        resetLibraryButton.Click += async (_, _) =>
        {
            var confirmed = await ConfirmAsync("清理缓存并重置库", "这会清空媒体记录与标签，但保留播放列表名称。是否继续？", "继续");
            if (confirmed)
            {
                await ViewModel.ResetLibraryAsync(false);
            }
        };

        var clearAllButton = new Button { Content = "清理全部应用数据" };
        clearAllButton.Click += async (_, _) =>
        {
            var confirmed = await ConfirmAsync("清理全部应用数据", "这会清空媒体库、标签、播放列表和监控文件夹配置。是否继续？", "清空");
            if (confirmed)
            {
                await ViewModel.ResetLibraryAsync(true);
                watchedFolders.Clear();
            }
        };

        var actions = new StackPanel { Spacing = 8 };
        actions.Children.Add(chooseDataDirButton);
        actions.Children.Add(addWatchedFolderButton);
        actions.Children.Add(removeWatchedFolderButton);
        actions.Children.Add(clearWatchedFoldersButton);
        actions.Children.Add(protectFolderButton);
        actions.Children.Add(unprotectFolderButton);
        actions.Children.Add(clearCacheButton);
        actions.Children.Add(resetLibraryButton);
        actions.Children.Add(clearAllButton);

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(portableToggle);
        content.Children.Add(dataDirTextBox);
        content.Children.Add(new TextBlock { Text = "全局锁定密码", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        content.Children.Add(passwordBox);
        content.Children.Add(actions);
        content.Children.Add(new TextBlock { Text = "监控文件夹", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        content.Children.Add(watchedFoldersList);
        content.Children.Add(watchedFolderStatusText);
        content.Children.Add(validationText);
        content.Children.Add(new TextBlock
        {
            Text = "修改数据目录或便携模式后，建议重启应用以切换到新的数据库位置。受保护文件夹需要在“锁定管理”里输入密码后才会显示媒体。",
            Foreground = Application.Current.Resources["MutedTextBrush"] as Microsoft.UI.Xaml.Media.Brush,
            TextWrapping = TextWrapping.Wrap
        });

        var dialog = new ContentDialog
        {
            Title = "设置",
            Content = new ScrollViewer { Content = content, MaxHeight = 520 },
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

        if (watchedFolders.Any(folder => folder.Locked) && string.IsNullOrWhiteSpace(passwordBox.Password))
        {
            await ShowInfoDialogAsync("设置未保存", "存在受保护文件夹时必须设置全局密码。");
            return;
        }

        ViewModel.SetPortableMode(portableToggle.IsOn);
        if (!string.IsNullOrWhiteSpace(dataDirTextBox.Text))
        {
            ViewModel.SetDataDir(dataDirTextBox.Text.Trim());
        }

        ViewModel.SetLockPassword(passwordBox.Password);
        ViewModel.UpdateWatchedFolders(watchedFolders);
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

        var listView = new ListView
        {
            ItemsSource = protectedFolders,
            DisplayMemberPath = nameof(WatchedFolder.Path),
            SelectionMode = ListViewSelectionMode.Single,
            SelectedIndex = 0,
            MaxHeight = 220
        };
        var statusText = new TextBlock
        {
            Foreground = Application.Current.Resources["MutedTextBrush"] as Microsoft.UI.Xaml.Media.Brush,
            TextWrapping = TextWrapping.Wrap
        };

        void RefreshStatus()
        {
            if (listView.SelectedItem is not WatchedFolder selected)
            {
                statusText.Text = "请选择一个受保护文件夹。";
                return;
            }

            statusText.Text = ViewModel.IsFolderUnlocked(selected.Path)
                ? $"当前状态：已解锁。{Path.GetFileName(selected.Path)} 中的媒体现在可见。"
                : $"当前状态：已锁定。输入全局密码后可解锁 {Path.GetFileName(selected.Path)}。";
        }

        listView.SelectionChanged += (_, _) => RefreshStatus();
        RefreshStatus();

        var unlockButton = new Button { Content = "解锁选中" };
        unlockButton.Click += async (_, _) =>
        {
            if (listView.SelectedItem is not WatchedFolder selected)
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
                statusText.Text = "密码错误，未能解锁文件夹。";
                return;
            }

            RefreshStatus();
        };

        var relockButton = new Button { Content = "重新锁定选中" };
        relockButton.Click += async (_, _) =>
        {
            if (listView.SelectedItem is not WatchedFolder selected)
            {
                return;
            }

            await ViewModel.LockFolderAsync(selected.Path);
            RefreshStatus();
        };

        var relockAllButton = new Button { Content = "全部重新锁定" };
        relockAllButton.Click += async (_, _) =>
        {
            await ViewModel.RelockAllFoldersAsync();
            RefreshStatus();
        };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(unlockButton);
        actions.Children.Add(relockButton);
        actions.Children.Add(relockAllButton);

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = "受保护文件夹默认隐藏。解锁只对当前会话生效，重新锁定后会再次隐藏。",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(listView);
        content.Children.Add(statusText);
        content.Children.Add(actions);

        var dialog = new ContentDialog
        {
            Title = "锁定管理",
            Content = content,
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
            MinWidth = 280
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
            MinWidth = 280
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
