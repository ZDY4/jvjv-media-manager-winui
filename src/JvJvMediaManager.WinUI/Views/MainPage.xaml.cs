using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using JvJvMediaManager.ViewModels;
using JvJvMediaManager.Utilities;
using JvJvMediaManager.Models;

namespace JvJvMediaManager.Views;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel { get; } = new();

    private readonly DebounceDispatcher _debouncer = new();

    public MainPage()
    {
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.SetDispatcher(DispatcherQueue);
        Loaded += MainPage_Loaded;
        SelectedTagsControl.ItemsSource = ViewModel.SelectedTags;
        KeyDown += MainPage_KeyDown;
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
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

        if (!ViewModel.SelectedTags.Contains(tag))
        {
            ViewModel.SelectedTags.Add(tag);
            ViewModel.ApplyFilters();
            RefreshTagChips();
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
    }

    private void GridViewMode_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ViewMode = MediaViewMode.Grid;
        ListView.Visibility = Visibility.Collapsed;
        GridView.Visibility = Visibility.Visible;
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
            ViewModel.SelectedMedia = media;
            UpdatePlayer(media);
        }
    }

    private void Media_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView listView && listView.SelectedItem is MediaItemViewModel media)
        {
            ViewModel.SelectedMedia = media;
            UpdatePlayer(media);
        }
        else if (sender is GridView gridView && gridView.SelectedItem is MediaItemViewModel gridMedia)
        {
            ViewModel.SelectedMedia = gridMedia;
            UpdatePlayer(gridMedia);
        }
    }

    private void UpdatePlayer(MediaItemViewModel media)
    {
        EmptyState.Visibility = Visibility.Collapsed;

        if (media.Type == MediaType.Video)
        {
            ImageViewer.Visibility = Visibility.Collapsed;
            VideoPlayer.Visibility = Visibility.Visible;
            if (VideoPlayer.MediaPlayer == null)
            {
                VideoPlayer.SetMediaPlayer(new MediaPlayer());
            }
            var uri = new Uri(media.Media.Path);
            VideoPlayer.Source = MediaSource.CreateFromUri(uri);
        }
        else
        {
            VideoPlayer.Visibility = Visibility.Collapsed;
            ImageViewer.Visibility = Visibility.Visible;
            ImageViewer.Source = media.Thumbnail ?? new BitmapImage(new Uri(media.Media.Path));
        }
    }

    private void MainPage_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ViewModel.SelectedMedia == null) return;

        if (e.Key == Windows.System.VirtualKey.Space && ViewModel.SelectedMedia.Type == MediaType.Video)
        {
            var player = VideoPlayer.MediaPlayer;
            if (player != null)
            {
                if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
                {
                    player.Pause();
                }
                else
                {
                    player.Play();
                }
            }
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
            NavigateRelative(-1);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.PageDown)
        {
            NavigateRelative(1);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Delete)
        {
            _ = DeleteSelectedAsync();
            e.Handled = true;
        }
    }

    private void SeekRelative(double seconds)
    {
        var player = VideoPlayer.MediaPlayer;
        if (player == null) return;
        var position = player.PlaybackSession.Position;
        var next = position + TimeSpan.FromSeconds(seconds);
        if (next < TimeSpan.Zero) next = TimeSpan.Zero;
        player.PlaybackSession.Position = next;
    }

    private void NavigateRelative(int offset)
    {
        var list = ViewModel.FilteredMediaItems;
        if (list.Count == 0 || ViewModel.SelectedMedia == null) return;

        var index = list.IndexOf(ViewModel.SelectedMedia);
        if (index < 0) return;
        var nextIndex = (index + offset + list.Count) % list.Count;
        var next = list[nextIndex];
        ViewModel.SelectedMedia = next;
        UpdatePlayer(next);
        ListView.SelectedItem = next;
        GridView.SelectedItem = next;
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

        ViewModel.DeleteMedia(selected);
    }

    private IEnumerable<MediaItemViewModel> GetSelectedItems()
    {
        if (ViewModel.ViewMode == MediaViewMode.Grid)
        {
            return GridView.SelectedItems.OfType<MediaItemViewModel>();
        }
        return ListView.SelectedItems.OfType<MediaItemViewModel>();
    }
}
