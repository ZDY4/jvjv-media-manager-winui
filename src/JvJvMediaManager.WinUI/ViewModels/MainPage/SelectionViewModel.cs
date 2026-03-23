using JvJvMediaManager.Utilities;
using JvJvMediaManager.ViewModels;

namespace JvJvMediaManager.ViewModels.MainPage;

public sealed class SelectionViewModel : ObservableObject
{
    private MediaItemViewModel? _selectedMedia;

    public MediaItemViewModel? SelectedMedia
    {
        get => _selectedMedia;
        set => SetProperty(ref _selectedMedia, value);
    }
}
