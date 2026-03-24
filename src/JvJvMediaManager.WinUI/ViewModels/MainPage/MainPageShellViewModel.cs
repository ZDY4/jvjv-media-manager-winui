namespace JvJvMediaManager.ViewModels.MainPage;

public sealed class MainPageShellViewModel
{
    public MainPageShellViewModel()
    {
        Selection = new SelectionViewModel();
        Library = new LibraryShellViewModel(Selection);
        Player = new PlayerShellViewModel(Selection);
    }

    public SelectionViewModel Selection { get; }

    public LibraryShellViewModel Library { get; }

    public PlayerShellViewModel Player { get; }
}
