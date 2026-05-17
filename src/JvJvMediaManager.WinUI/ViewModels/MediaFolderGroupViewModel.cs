using System.Collections.ObjectModel;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.ViewModels;

public sealed class MediaFolderGroupViewModel : ObservableObject
{
    private readonly List<MediaItemViewModel> _items = new();
    private bool _isCollapsed;
    private int _totalCount;

    public MediaFolderGroupViewModel(string folderPath, string folderDisplayName, bool isCollapsed, int totalCount = 0)
    {
        FolderPath = folderPath;
        FolderDisplayName = folderDisplayName;
        _isCollapsed = isCollapsed;
        _totalCount = totalCount;
    }

    public event EventHandler? CollapseChanged;

    public string FolderPath { get; }

    public string FolderDisplayName { get; }

    public ObservableCollection<MediaItemViewModel> VisibleItems { get; } = new();

    public int TotalCount => Math.Max(_totalCount, _items.Count);

    public string CountText => $"{TotalCount} 个媒体";

    public string CollapseGlyph => IsCollapsed ? "\uE70D" : "\uE70E";

    public bool IsCollapsed
    {
        get => _isCollapsed;
        set
        {
            if (!SetProperty(ref _isCollapsed, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CollapseGlyph));
            ApplyVisibleItems();
            CollapseChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool Contains(string mediaId)
    {
        return _items.Any(item => string.Equals(item.Id, mediaId, StringComparison.Ordinal));
    }

    public bool HasLoadedItems => _items.Count > 0;

    public void SetTotalCount(int totalCount)
    {
        _totalCount = Math.Max(0, totalCount);
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(CountText));
    }

    public void SetItems(IEnumerable<MediaItemViewModel> items)
    {
        _items.Clear();
        _items.AddRange(items);
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(CountText));
        ApplyVisibleItems();
    }

    private void ApplyVisibleItems()
    {
        VisibleItems.Clear();
        if (IsCollapsed)
        {
            return;
        }

        foreach (var item in _items)
        {
            VisibleItems.Add(item);
        }
    }
}
