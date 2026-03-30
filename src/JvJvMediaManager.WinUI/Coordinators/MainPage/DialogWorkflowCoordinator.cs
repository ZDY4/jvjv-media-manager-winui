using JvJvMediaManager.Models;
using JvJvMediaManager.Services.MainPage;
using JvJvMediaManager.ViewModels;
using JvJvMediaManager.ViewModels.MainPage;

namespace JvJvMediaManager.Coordinators.MainPage;

public sealed class DialogWorkflowCoordinator
{
    private readonly SettingsDialogCoordinator _settingsCoordinator;
    private readonly LockManagerDialogCoordinator _lockManagerCoordinator;
    private readonly TagEditorDialogCoordinator _tagEditorCoordinator;
    private readonly PlaylistDialogCoordinator _playlistCoordinator;

    public DialogWorkflowCoordinator(LibraryShellViewModel libraryViewModel, IContentDialogService dialogService)
    {
        _settingsCoordinator = new SettingsDialogCoordinator(libraryViewModel, dialogService);
        _lockManagerCoordinator = new LockManagerDialogCoordinator(libraryViewModel, dialogService);
        _tagEditorCoordinator = new TagEditorDialogCoordinator(dialogService);
        _playlistCoordinator = new PlaylistDialogCoordinator(dialogService);
    }

    public Task<SettingsDialogResult?> ShowSettingsDialogAsync()
    {
        return _settingsCoordinator.ShowAsync();
    }

    public Task<FolderLockResult?> ShowFolderLockDialogAsync()
    {
        return _lockManagerCoordinator.ShowAsync();
    }

    public Task<TagEditorResult?> ShowTagEditorDialogAsync(IReadOnlyList<MediaItemViewModel> items)
    {
        return _tagEditorCoordinator.ShowAsync(items);
    }

    public Task<PlaylistPickerResult?> ShowPlaylistPickerDialogAsync(string title, IReadOnlyList<Playlist> playlists)
    {
        return _playlistCoordinator.ShowAsync(title, playlists);
    }
}
