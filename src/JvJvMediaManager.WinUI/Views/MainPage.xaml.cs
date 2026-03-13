using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using JvJvMediaManager.ViewModels;
using JvJvMediaManager.Utilities;
using JvJvMediaManager.Models;
using JvJvMediaManager.Services;
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
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
        RefreshTagChips();
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
    }

    private void SelectedTags_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshTagChips();
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var window = App.MainWindow;
        if (window == null) return;

        var paths = await PickerHelpers.PickFilesAsync(window);
        if (paths.Count == 0) return;

        await ViewModel.AddFilesAsync(paths);
        UpdateWatchedFolders(paths);
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var window = App.MainWindow;
        if (window == null) return;

        var folder = await PickerHelpers.PickFolderAsync(window);
        if (string.IsNullOrWhiteSpace(folder)) return;

        await ViewModel.AddFolderAsync(folder);
        UpdateWatchedFolders(new[] { folder });
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.WatchedFolders.Count == 0) return;
        await ViewModel.RescanFoldersAsync();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "设置",
            Content = new TextBlock
            {
                Text = "设置页面正在构建中。当前仅提供基础扫描与播放功能。",
                TextWrapping = TextWrapping.Wrap
            },
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot
        };
        _ = dialog.ShowAsync();
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
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        var value = SearchBox.Text?.Trim() ?? string.Empty;
        if (!value.StartsWith("#") || value.Length <= 1) return;

        var tag = value[1..].Trim();
        if (tag.Length == 0) return;
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

    private void MainPage_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox or PasswordBox or RichEditBox or AutoSuggestBox)
        {
            return;
        }

        if (ViewModel.SelectedMedia == null) return;

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
            _ = NavigateRelativeAsync(-1);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.PageDown)
        {
            _ = NavigateRelativeAsync(1);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Delete)
        {
            _ = DeleteSelectedAsync();
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
            _ = ExportCurrentClipAsync();
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
        if (player == null) return;

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
        if (appWindow == null) return;

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
        if (_player == null || !_isSeeking) return;
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
        if (player == null) return;

        var duration = player.PlaybackSession.NaturalDuration;
        if (duration.TotalSeconds <= 0) return;

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
        if (_player == null) return;

        PlayPauseIcon.Symbol = _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing
            ? Symbol.Pause
            : Symbol.Play;
    }

    private void HandlePlaybackEnded()
    {
        if (ViewModel.SelectedMedia == null) return;

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
        if (list.Count == 0 || ViewModel.SelectedMedia == null) return;

        var currentIndex = list.IndexOf(ViewModel.SelectedMedia);
        var nextIndex = currentIndex;

        if (list.Count > 1)
        {
            while (nextIndex == currentIndex)
            {
                nextIndex = _random.Next(list.Count);
            }
        }

        var next = list[nextIndex];
        ViewModel.SelectedMedia = next;
    }

    private void SeekRelative(double seconds)
    {
        var player = _player;
        if (player == null) return;

        var position = player.PlaybackSession.Position;
        var next = position + TimeSpan.FromSeconds(seconds);
        if (next < TimeSpan.Zero) next = TimeSpan.Zero;
        player.PlaybackSession.Position = next;
    }

    private void SeekToSlider()
    {
        if (_player == null) return;
        _player.PlaybackSession.Position = TimeSpan.FromSeconds(ProgressSlider.Value);
    }

    private async Task NavigateRelativeAsync(int offset)
    {
        var list = ViewModel.FilteredMediaItems;
        if (list.Count == 0 || ViewModel.SelectedMedia == null) return;

        var index = list.IndexOf(ViewModel.SelectedMedia);
        if (index < 0) return;

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
        var next = list[nextIndex];
        ViewModel.SelectedMedia = next;
    }

    private void UpdateWatchedFolders(IEnumerable<string> paths)
    {
        var folderPaths = paths
            .Select(path => Directory.Exists(path) ? path : Path.GetDirectoryName(path))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => p!)
            .ToList();

        if (folderPaths.Count == 0) return;

        var current = ViewModel.WatchedFolders.ToList();
        foreach (var folder in folderPaths)
        {
            if (current.All(f => !string.Equals(f.Path, folder, StringComparison.OrdinalIgnoreCase)))
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
        _clipStatusMessage = _clipService.IsAvailable
            ? "剪辑区间已重置为整段视频。"
            : _clipService.UnavailableReason;
        UpdateClipUi();
    }

    private async void ExportClip_Click(object sender, RoutedEventArgs e)
    {
        await ExportCurrentClipAsync();
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

        if (!_clipStart.HasValue || !_clipEnd.HasValue || _clipEnd.Value <= _clipStart.Value)
        {
            _clipStatusMessage = "请先设置有效的入点和出点。";
            UpdateClipUi();
            return;
        }

        var outputPath = _clipService.CreateOutputPath(media.Path, _clipStart.Value, _clipEnd.Value);
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
                Start = _clipStart.Value,
                End = _clipEnd.Value,
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
        if (selected.Count == 0) return;

        var dialog = new ContentDialog
        {
            Title = "确认删除",
            Content = new TextBlock
            {
                Text = $"确定要删除选中的 {selected.Count} 个媒体文件吗？",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

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
        if (_player != null) return _player;

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
        if (ControlBar.Visibility != Visibility.Visible) return;

        ControlBar.Opacity = 1;
        ControlBar.IsHitTestVisible = true;
        _controlsVisible = true;

        _controlsHideTimer.Stop();
        _controlsHideTimer.Start();
    }

    private void HideControls()
    {
        if (!_controlsVisible) return;

        ControlBar.Opacity = 0;
        ControlBar.IsHitTestVisible = false;
        _controlsVisible = false;
    }

    private void ZoomImage(double delta)
    {
        if (ImageScrollViewer.Visibility != Visibility.Visible) return;

        var min = ImageScrollViewer.MinZoomFactor;
        var max = ImageScrollViewer.MaxZoomFactor;
        var target = Math.Clamp(ImageScrollViewer.ZoomFactor + delta, min, max);
        ImageScrollViewer.ChangeView(null, null, (float)target, true);
    }

    private void ResetImageZoom()
    {
        if (ImageScrollViewer.Visibility != Visibility.Visible) return;
        ImageScrollViewer.ChangeView(0, 0, 1.0f, true);
    }

    private void ImageScrollViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(ImageScrollViewer).Properties.MouseWheelDelta;
        if (delta == 0) return;

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
        _clipStatusMessage = _clipService.IsAvailable
            ? "使用 I / O 标记入点和出点，然后按 E 导出。"
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

        ClipStartText.Text = $"入点：{FormatTime(clipStart)}";
        ClipEndText.Text = clipEnd > TimeSpan.Zero ? $"出点：{FormatTime(clipEnd)}" : "出点：--";
        ClipDurationText.Text = clipLength > TimeSpan.Zero ? $"时长：{FormatTime(clipLength)}" : "时长：--";
        ClipExportProgressBar.Visibility = _isExportingClip ? Visibility.Visible : Visibility.Collapsed;

        if (!_isExportingClip && ClipExportProgressBar.Value >= 100 && clipLength > TimeSpan.Zero)
        {
            ClipExportProgressBar.Value = 100;
        }
        else if (!_isExportingClip && string.IsNullOrWhiteSpace(_clipStatusMessage))
        {
            ClipExportProgressBar.Value = 0;
        }

        ClipStatusText.Text = _clipStatusMessage;
        SetClipStartButton.IsEnabled = !_isExportingClip && duration > TimeSpan.Zero;
        SetClipEndButton.IsEnabled = !_isExportingClip && duration > TimeSpan.Zero;
        ClearClipButton.IsEnabled = !_isExportingClip;
        ExportClipButton.IsEnabled = !_isExportingClip && _clipService.IsAvailable && clipLength > TimeSpan.Zero;
        ExportClipButton.Content = _isExportingClip ? "导出中..." : "导出剪辑 (E)";
    }

    private TimeSpan GetCurrentPlaybackPosition()
    {
        return _player?.PlaybackSession.Position ?? TimeSpan.Zero;
    }

    private TimeSpan GetCurrentVideoDuration()
    {
        return _player?.PlaybackSession.NaturalDuration ?? TimeSpan.Zero;
    }

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

    private AppWindow? GetAppWindow()
    {
        if (_appWindow != null) return _appWindow;
        var window = App.MainWindow;
        if (window == null) return null;

        var hWnd = WindowNative.GetWindowHandle(window);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        return _appWindow;
    }
}
