using System.Collections.ObjectModel;
using JvJvMediaManager.Models;
using JvJvMediaManager.ViewModels;

namespace JvJvMediaManager.ViewModels.MainPage;

public sealed class MediaBrowserViewModel
{
    private readonly LibraryShellViewModel _library;

    public MediaBrowserViewModel(LibraryShellViewModel library)
    {
        _library = library;
    }

    public IncrementalMediaCollection FilteredMediaItems => _library.FilteredMediaItems;

    public ObservableCollection<string> SelectedTags => _library.SelectedTags;

    public MediaViewMode ViewMode
    {
        get => _library.ViewMode;
        set => _library.ViewMode = value;
    }

    public int IconSize
    {
        get => _library.IconSize;
        set => _library.IconSize = value;
    }

    public MediaSortField SortField => _library.SortField;

    public MediaSortOrder SortOrder => _library.SortOrder;
}
