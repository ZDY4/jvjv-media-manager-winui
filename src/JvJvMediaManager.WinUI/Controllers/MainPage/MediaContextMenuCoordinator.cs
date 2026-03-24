using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using JvJvMediaManager.ViewModels;
using JvJvMediaManager.ViewModels.MainPage;

namespace JvJvMediaManager.Controllers.MainPage;

public sealed class MediaContextMenuCoordinator
{
    public const string OpenFolderShortcutText = "Ctrl+Shift+O";
    public const string EditTagsShortcutText = "Ctrl+T";
    public const string AddToPlaylistShortcutText = "Ctrl+Shift+A";
    public const string RemoveFromPlaylistShortcutText = "Ctrl+Shift+R";
    public const string DeleteShortcutText = "Delete";

    private readonly LibraryShellViewModel _viewModel;
    private readonly Func<IReadOnlyList<MediaItemViewModel>, Task> _applyTagEditorAsync;
    private readonly Func<IReadOnlyList<MediaItemViewModel>, Task> _addToPlaylistAsync;
    private readonly Func<IReadOnlyList<MediaItemViewModel>, Task> _deleteSelectionAsync;
    private readonly MenuFlyout _flyout;
    private readonly MenuFlyoutItem _openFolderItem;
    private readonly MenuFlyoutItem _editTagsItem;
    private readonly MenuFlyoutItem _addToPlaylistItem;
    private readonly MenuFlyoutSubItem _quickAddToPlaylistItem;
    private readonly MenuFlyoutItem _removeFromPlaylistItem;
    private readonly MenuFlyoutSeparator _deleteSeparator;
    private readonly MenuFlyoutItem _deleteItem;

    private IReadOnlyList<MediaItemViewModel> _currentSelection = Array.Empty<MediaItemViewModel>();

    public MediaContextMenuCoordinator(
        LibraryShellViewModel viewModel,
        Func<IReadOnlyList<MediaItemViewModel>, Task> applyTagEditorAsync,
        Func<IReadOnlyList<MediaItemViewModel>, Task> addToPlaylistAsync,
        Func<IReadOnlyList<MediaItemViewModel>, Task> deleteSelectionAsync)
    {
        _viewModel = viewModel;
        _applyTagEditorAsync = applyTagEditorAsync;
        _addToPlaylistAsync = addToPlaylistAsync;
        _deleteSelectionAsync = deleteSelectionAsync;

        _flyout = new MenuFlyout();
        _openFolderItem = CreateMenuItem("打开所在目录", OpenFolderShortcutText, OpenFolderItem_Click);
        _editTagsItem = CreateMenuItem("编辑标签", EditTagsShortcutText, EditTagsItem_Click);
        _addToPlaylistItem = CreateMenuItem("加入播放列表...", AddToPlaylistShortcutText, AddToPlaylistItem_Click);
        _quickAddToPlaylistItem = new MenuFlyoutSubItem { Text = "快速加入到播放列表" };
        _removeFromPlaylistItem = CreateMenuItem(string.Empty, RemoveFromPlaylistShortcutText, RemoveFromPlaylistItem_Click);
        _deleteSeparator = new MenuFlyoutSeparator();
        _deleteItem = CreateMenuItem("删除文件", DeleteShortcutText, DeleteItem_Click);

        _flyout.Items.Add(_openFolderItem);
        _flyout.Items.Add(_editTagsItem);
        _flyout.Items.Add(_addToPlaylistItem);
        _flyout.Items.Add(_quickAddToPlaylistItem);
        _flyout.Items.Add(_deleteSeparator);
        _flyout.Items.Add(_deleteItem);
    }

    public void ShowForTarget(FrameworkElement target, Windows.Foundation.Point position, IReadOnlyList<MediaItemViewModel> selected)
    {
        if (selected.Count == 0)
        {
            return;
        }

        _currentSelection = selected.ToArray();
        RefreshMenuState();
        _flyout.ShowAt(target, position);
    }

    public static void OpenMediaFolder(MediaItemViewModel media)
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

    private static MenuFlyoutItem CreateMenuItem(string text, string shortcutText, RoutedEventHandler clickHandler)
    {
        var item = new MenuFlyoutItem
        {
            Text = text,
            KeyboardAcceleratorTextOverride = shortcutText
        };
        item.Click += clickHandler;
        return item;
    }

    private void RefreshMenuState()
    {
        _editTagsItem.Text = _currentSelection.Count == 1 ? "编辑标签" : $"批量编辑标签 ({_currentSelection.Count})";
        _addToPlaylistItem.IsEnabled = _viewModel.Playlists.Count > 0;
        RefreshQuickPlaylistItems();

        if (_viewModel.SelectedPlaylist != null)
        {
            _removeFromPlaylistItem.Text = $"从“{_viewModel.SelectedPlaylist.Name}”移除";
            if (!_flyout.Items.Contains(_removeFromPlaylistItem))
            {
                _flyout.Items.Insert(_flyout.Items.IndexOf(_deleteSeparator), _removeFromPlaylistItem);
            }
        }
        else
        {
            _ = _flyout.Items.Remove(_removeFromPlaylistItem);
        }
    }

    private void RefreshQuickPlaylistItems()
    {
        _quickAddToPlaylistItem.Items.Clear();
        if (_viewModel.Playlists.Count == 0)
        {
            _quickAddToPlaylistItem.Items.Add(new MenuFlyoutItem { Text = "暂无播放列表", IsEnabled = false });
            _quickAddToPlaylistItem.IsEnabled = false;
            return;
        }

        _quickAddToPlaylistItem.IsEnabled = true;
        foreach (var playlist in _viewModel.Playlists)
        {
            var item = new MenuFlyoutItem { Text = playlist.Name, Tag = playlist.Id };
            item.Click += QuickPlaylistItem_Click;
            _quickAddToPlaylistItem.Items.Add(item);
        }
    }

    private void OpenFolderItem_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSelection.Count == 0)
        {
            return;
        }

        OpenMediaFolder(_currentSelection[0]);
    }

    private async void EditTagsItem_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSelection.Count == 0)
        {
            return;
        }

        await _applyTagEditorAsync(_currentSelection);
    }

    private async void AddToPlaylistItem_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSelection.Count == 0)
        {
            return;
        }

        await _addToPlaylistAsync(_currentSelection);
    }

    private async void QuickPlaylistItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not string playlistId || _currentSelection.Count == 0)
        {
            return;
        }

        await _viewModel.AddMediaToPlaylistAsync(playlistId, _currentSelection);
    }

    private async void RemoveFromPlaylistItem_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSelection.Count == 0 || _viewModel.SelectedPlaylist == null)
        {
            return;
        }

        await _viewModel.RemoveMediaFromSelectedPlaylistAsync(_currentSelection);
    }

    private async void DeleteItem_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSelection.Count == 0)
        {
            return;
        }

        await _deleteSelectionAsync(_currentSelection);
    }
}
