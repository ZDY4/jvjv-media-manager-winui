using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using JvJvMediaManager.Models;
using JvJvMediaManager.Services.MainPage;
using JvJvMediaManager.ViewModels.MainPage;
using JvJvMediaManager.Views.MainPageParts;

namespace JvJvMediaManager.Controllers.MainPage;

public sealed class PlaylistRailCoordinator : IDisposable
{
    private readonly LibraryShellViewModel _viewModel;
    private readonly PlaylistRailView _playlistRailView;
    private readonly LibraryHeaderView _headerView;
    private readonly LibraryPaneController _libraryPaneController;
    private readonly IContentDialogService _dialogService;
    private readonly Func<string, string, Task> _showInfoAsync;
    private readonly Func<string, string, string, Task<bool>> _confirmAsync;

    public PlaylistRailCoordinator(
        LibraryShellViewModel viewModel,
        PlaylistRailView playlistRailView,
        LibraryHeaderView headerView,
        LibraryPaneController libraryPaneController,
        IContentDialogService dialogService,
        Func<string, string, Task> showInfoAsync,
        Func<string, string, string, Task<bool>> confirmAsync)
    {
        _viewModel = viewModel;
        _playlistRailView = playlistRailView;
        _headerView = headerView;
        _libraryPaneController = libraryPaneController;
        _dialogService = dialogService;
        _showInfoAsync = showInfoAsync;
        _confirmAsync = confirmAsync;

        _playlistRailView.MediaTabButton.Click += MediaTabButton_Click;
        _playlistRailView.PlaylistDropRequested += PlaylistRailView_PlaylistDropRequested;
        _playlistRailView.PlaylistRailListView.ItemClick += PlaylistRailListView_ItemClick;
        _playlistRailView.PlaylistRailListView.DragItemsCompleted += PlaylistRailListView_DragItemsCompleted;
        _playlistRailView.PlaylistRailListView.RightTapped += PlaylistRailListView_RightTapped;
        _playlistRailView.CreatePlaylistRailButton.Click += CreatePlaylist_Click;
        _headerView.SelectedPlaylistTitleButton.Click += SelectedPlaylistTitleButton_Click;
    }

    public void Dispose()
    {
        _playlistRailView.MediaTabButton.Click -= MediaTabButton_Click;
        _playlistRailView.PlaylistDropRequested -= PlaylistRailView_PlaylistDropRequested;
        _playlistRailView.PlaylistRailListView.ItemClick -= PlaylistRailListView_ItemClick;
        _playlistRailView.PlaylistRailListView.DragItemsCompleted -= PlaylistRailListView_DragItemsCompleted;
        _playlistRailView.PlaylistRailListView.RightTapped -= PlaylistRailListView_RightTapped;
        _playlistRailView.CreatePlaylistRailButton.Click -= CreatePlaylist_Click;
        _headerView.SelectedPlaylistTitleButton.Click -= SelectedPlaylistTitleButton_Click;
    }

    public void HighlightPlaylist(string playlistId)
    {
        _playlistRailView.HighlightPlaylist(playlistId);
    }

    private void MediaTabButton_Click(object sender, RoutedEventArgs e)
    {
        _libraryPaneController.ToggleMediaLibrary();
    }

    private void PlaylistRailListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Playlist playlist)
        {
            _libraryPaneController.TogglePlaylist(playlist);
        }
    }

    private async Task PlaylistRailView_PlaylistDropRequested(string playlistId, List<string> mediaIds)
    {
        var items = _viewModel.FilteredMediaItems
            .Where(item => mediaIds.Contains(item.Id, StringComparer.Ordinal))
            .ToList();
        if (items.Count == 0)
        {
            return;
        }

        await _viewModel.AddMediaToPlaylistAsync(playlistId, items);
        HighlightPlaylist(playlistId);
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
            _viewModel.CreatePlaylist(name);
            _libraryPaneController.SetPaneOpen(true);
        }
        catch (Exception ex)
        {
            await _showInfoAsync("创建失败", ex.Message);
        }
    }

    private async void SelectedPlaylistTitleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedPlaylist != null)
        {
            await RenamePlaylistAsync(_viewModel.SelectedPlaylist);
        }
    }

    private void PlaylistRailListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        _viewModel.UpdatePlaylistOrder(_viewModel.Playlists.ToList());
    }

    private void PlaylistRailListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject origin)
        {
            return;
        }

        var container = MainPageVisualTreeHelpers.FindAncestor<ListViewItem>(origin);
        if (container?.Content is not Playlist playlist)
        {
            return;
        }

        e.Handled = true;
        BuildPlaylistFlyout(playlist).ShowAt(container, e.GetPosition(container));
    }

    private MenuFlyout BuildPlaylistFlyout(Playlist playlist)
    {
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
        return flyout;
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
            _viewModel.RenamePlaylist(playlist.Id, name);
        }
        catch (Exception ex)
        {
            await _showInfoAsync("重命名失败", ex.Message);
        }
    }

    private async Task DeletePlaylistAsync(Playlist playlist)
    {
        var confirmed = await _confirmAsync("删除播放列表", $"确定要删除“{playlist.Name}”吗？\n媒体文件本身不会被删除。", "删除");
        if (!confirmed)
        {
            return;
        }

        await _viewModel.DeletePlaylistAsync(playlist.Id);
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
            _viewModel.SetPlaylistColor(playlist.Id, colorHex);
        }
        catch (Exception ex)
        {
            await _showInfoAsync("更改颜色失败", ex.Message);
        }
    }
}
