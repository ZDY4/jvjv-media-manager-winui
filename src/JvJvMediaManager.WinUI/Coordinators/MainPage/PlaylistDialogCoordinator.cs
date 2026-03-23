using JvJvMediaManager.Models;
using JvJvMediaManager.Services.MainPage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JvJvMediaManager.Coordinators.MainPage;

public sealed class PlaylistDialogCoordinator
{
    private readonly IContentDialogService _dialogService;

    public PlaylistDialogCoordinator(IContentDialogService dialogService)
    {
        _dialogService = dialogService;
    }

    public async Task<PlaylistPickerResult?> ShowAsync(string title, IReadOnlyList<Playlist> playlists)
    {
        var comboBox = new ComboBox
        {
            DisplayMemberPath = nameof(Playlist.Name),
            ItemsSource = playlists,
            SelectedIndex = playlists.Count > 0 ? 0 : -1,
            MinWidth = 260,
            Style = Application.Current.Resources["GlassComboBoxStyle"] as Style
        };

        var dialog = new ContentDialog
        {
            Title = title,
            Content = comboBox,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await _dialogService.ShowAsync(dialog) != ContentDialogResult.Primary
            || comboBox.SelectedItem is not Playlist playlist)
        {
            return null;
        }

        return new PlaylistPickerResult
        {
            Playlist = playlist
        };
    }
}
