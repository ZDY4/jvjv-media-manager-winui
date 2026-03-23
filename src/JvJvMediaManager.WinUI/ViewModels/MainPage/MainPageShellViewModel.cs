namespace JvJvMediaManager.ViewModels.MainPage;

public sealed class MainPageShellViewModel
{
    public MainPageShellViewModel()
    {
        Selection = new SelectionViewModel();
        Library = new LibraryShellViewModel(Selection);
        PlaylistRail = new PlaylistRailViewModel(Library);
        MediaBrowser = new MediaBrowserViewModel(Library);
        Player = new PlayerShellViewModel(Selection);
    }

    public SelectionViewModel Selection { get; }

    public LibraryShellViewModel Library { get; }

    public PlaylistRailViewModel PlaylistRail { get; }

    public MediaBrowserViewModel MediaBrowser { get; }

    public PlayerShellViewModel Player { get; }
}
