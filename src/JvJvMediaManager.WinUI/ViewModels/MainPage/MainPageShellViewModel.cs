using JvJvMediaManager.Services;

namespace JvJvMediaManager.ViewModels.MainPage;

public sealed class MainPageShellViewModel
{
    public MainPageShellViewModel()
        : this(new SelectionViewModel(), LibraryShellServices.CreateDefault())
    {
    }

    public MainPageShellViewModel(SelectionViewModel selection, LibraryShellServices libraryServices)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(libraryServices);

        Selection = selection;
        Settings = libraryServices.Settings;
        Library = new LibraryShellViewModel(Selection, libraryServices);
        Player = new PlayerShellViewModel(Selection);
    }

    public SettingsService Settings { get; }

    public SelectionViewModel Selection { get; }

    public LibraryShellViewModel Library { get; }

    public PlayerShellViewModel Player { get; }
}
