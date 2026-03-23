using System.Collections.ObjectModel;
using JvJvMediaManager.Models;

namespace JvJvMediaManager.ViewModels.MainPage;

public sealed class PlaylistRailViewModel
{
    private readonly LibraryShellViewModel _library;

    public PlaylistRailViewModel(LibraryShellViewModel library)
    {
        _library = library;
    }

    public ObservableCollection<Playlist> Playlists => _library.Playlists;

    public Playlist? SelectedPlaylist
    {
        get => _library.SelectedPlaylist;
        set => _library.SelectedPlaylist = value;
    }
}
