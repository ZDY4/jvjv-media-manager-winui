using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using JvJvMediaManager.ViewModels;
using JvJvMediaManager.ViewModels.MainPage;

namespace JvJvMediaManager.Controllers.MainPage;

public sealed class MediaContextMenuCoordinator
{
    private readonly LibraryShellViewModel _viewModel;
    private readonly Func<IReadOnlyList<MediaItemViewModel>, Task> _applyTagEditorAsync;
    private readonly Func<IReadOnlyList<MediaItemViewModel>, Task> _deleteSelectionAsync;

    public MediaContextMenuCoordinator(
        LibraryShellViewModel viewModel,
        Func<IReadOnlyList<MediaItemViewModel>, Task> applyTagEditorAsync,
        Func<IReadOnlyList<MediaItemViewModel>, Task> deleteSelectionAsync)
    {
        _viewModel = viewModel;
        _applyTagEditorAsync = applyTagEditorAsync;
        _deleteSelectionAsync = deleteSelectionAsync;
    }

    public void ShowForTarget(FrameworkElement target, Windows.Foundation.Point position, IReadOnlyList<MediaItemViewModel> selected)
    {
        if (selected.Count == 0)
        {
            return;
        }

        BuildFlyout(selected).ShowAt(target, position);
    }

    private MenuFlyout BuildFlyout(IReadOnlyList<MediaItemViewModel> selected)
    {
        var flyout = new MenuFlyout();

        var openFolder = new MenuFlyoutItem { Text = "打开所在目录" };
        openFolder.Click += (_, _) => OpenMediaFolder(selected[0]);
        flyout.Items.Add(openFolder);

        var editTags = new MenuFlyoutItem { Text = selected.Count == 1 ? "编辑标签" : $"批量编辑标签 ({selected.Count})" };
        editTags.Click += async (_, _) => await _applyTagEditorAsync(selected);
        flyout.Items.Add(editTags);

        var addToPlaylist = new MenuFlyoutSubItem { Text = "添加到播放列表" };
        if (_viewModel.Playlists.Count == 0)
        {
            addToPlaylist.Items.Add(new MenuFlyoutItem { Text = "暂无播放列表", IsEnabled = false });
        }
        else
        {
            foreach (var playlist in _viewModel.Playlists)
            {
                var playlistId = playlist.Id;
                var item = new MenuFlyoutItem { Text = playlist.Name };
                item.Click += async (_, _) => await _viewModel.AddMediaToPlaylistAsync(playlistId, selected);
                addToPlaylist.Items.Add(item);
            }
        }

        flyout.Items.Add(addToPlaylist);

        if (_viewModel.SelectedPlaylist != null)
        {
            var removeFromPlaylist = new MenuFlyoutItem { Text = $"从“{_viewModel.SelectedPlaylist.Name}”移除" };
            removeFromPlaylist.Click += async (_, _) => await _viewModel.RemoveMediaFromSelectedPlaylistAsync(selected);
            flyout.Items.Add(removeFromPlaylist);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        var delete = new MenuFlyoutItem { Text = "删除文件" };
        delete.Click += async (_, _) => await _deleteSelectionAsync(selected);
        flyout.Items.Add(delete);

        return flyout;
    }

    private static void OpenMediaFolder(MediaItemViewModel media)
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
}
