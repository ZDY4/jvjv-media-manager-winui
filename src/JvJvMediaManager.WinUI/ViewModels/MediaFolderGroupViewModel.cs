using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.ViewModels;

public sealed class MediaFolderGroupViewModel : ObservableObject
{
    private readonly List<MediaItemViewModel> _items = new();
    private readonly HashSet<string> _itemIds = new(StringComparer.Ordinal);
    private readonly Func<string, Task>? _refreshFolderAsync;
    private bool _isCollapsed;
    private string _folderDisplayName;
    private string _folderTreePath;
    private int _treeDepth;
    private int _totalCount;
    private bool _hasDirectMedia = true;

    public MediaFolderGroupViewModel(
        string folderPath,
        string folderDisplayName,
        bool isCollapsed,
        int totalCount = 0,
        Func<string, Task>? refreshFolderAsync = null)
    {
        FolderPath = folderPath;
        _folderDisplayName = folderDisplayName;
        _folderTreePath = folderDisplayName;
        _isCollapsed = isCollapsed;
        _totalCount = totalCount;
        _refreshFolderAsync = refreshFolderAsync;
        RefreshFolderCommand = new RelayCommand(RefreshFolderAsync, () => _refreshFolderAsync != null);
    }

    public event EventHandler? CollapseChanged;

    public string FolderPath { get; }

    public string FolderDisplayName => _folderDisplayName;

    public string FolderTreePath => _folderTreePath;

    public int TreeDepth => _treeDepth;

    public double HeaderIndent => Math.Min(120, TreeDepth * 22);

    public ObservableCollection<MediaItemViewModel> VisibleItems { get; } = new MediaItemCollection();

    public RelayCommand RefreshFolderCommand { get; }

    public int TotalCount => Math.Max(_totalCount, _items.Count);

    public string CountText => HasDirectMedia ? $"{TotalCount} 个媒体" : $"含 {TotalCount} 个媒体";

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
        return !string.IsNullOrWhiteSpace(mediaId) && _itemIds.Contains(mediaId);
    }

    public bool HasLoadedItems => _items.Count > 0;

    public bool HasDirectMedia => _hasDirectMedia;

    public void SetCollapsedFromProjection(bool isCollapsed)
    {
        if (_isCollapsed == isCollapsed)
        {
            return;
        }

        _isCollapsed = isCollapsed;
        OnPropertyChanged(nameof(IsCollapsed));
        OnPropertyChanged(nameof(CollapseGlyph));
        ApplyVisibleItems();
    }

    public void SetTreeDisplay(string folderDisplayName, string folderTreePath, int treeDepth)
    {
        var normalizedDepth = Math.Max(0, treeDepth);
        if (string.Equals(_folderDisplayName, folderDisplayName, StringComparison.Ordinal)
            && string.Equals(_folderTreePath, folderTreePath, StringComparison.Ordinal)
            && _treeDepth == normalizedDepth)
        {
            return;
        }

        _folderDisplayName = folderDisplayName;
        _folderTreePath = folderTreePath;
        _treeDepth = normalizedDepth;
        OnPropertyChanged(nameof(FolderDisplayName));
        OnPropertyChanged(nameof(FolderTreePath));
        OnPropertyChanged(nameof(TreeDepth));
        OnPropertyChanged(nameof(HeaderIndent));
    }

    public void SetTotalCount(int totalCount, bool hasDirectMedia = true)
    {
        var normalizedTotalCount = Math.Max(0, totalCount);
        if (_totalCount == normalizedTotalCount && _hasDirectMedia == hasDirectMedia)
        {
            return;
        }

        _totalCount = normalizedTotalCount;
        _hasDirectMedia = hasDirectMedia;
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(HasDirectMedia));
    }

    public void SetItems(IEnumerable<MediaItemViewModel> items)
    {
        // Folder groups are a projection of the loaded media list. Keep these references intact
        // so selection, navigation, and deletion can use object identity safely.
        var nextItems = items.ToList();
        if (_items.Count == nextItems.Count
            && _items.Select(item => item.Id).SequenceEqual(nextItems.Select(item => item.Id), StringComparer.Ordinal))
        {
            for (var index = 0; index < _items.Count; index++)
            {
                if (!ReferenceEquals(_items[index], nextItems[index]))
                {
                    _items[index] = nextItems[index];
                }
            }

            ApplyVisibleItems();
            return;
        }

        _items.Clear();
        _items.AddRange(nextItems);
        _itemIds.Clear();
        foreach (var item in nextItems)
        {
            _itemIds.Add(item.Id);
        }

        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(CountText));
        ApplyVisibleItems();
    }

    private async void RefreshFolderAsync()
    {
        if (_refreshFolderAsync == null)
        {
            return;
        }

        try
        {
            await _refreshFolderAsync(FolderPath);
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("MediaFolderGroup", $"Refresh folder command failed. FolderPath='{FolderPath}'.", ex);
        }
    }

    private void ApplyVisibleItems()
    {
        IEnumerable<MediaItemViewModel> visibleItems = IsCollapsed
            ? Array.Empty<MediaItemViewModel>()
            : _items;

        if (VisibleItems is MediaItemCollection collection)
        {
            collection.ReplaceAll(visibleItems);
            return;
        }

        VisibleItems.Clear();
        foreach (var item in visibleItems)
        {
            VisibleItems.Add(item);
        }
    }

    private sealed class MediaItemCollection : ObservableCollection<MediaItemViewModel>
    {
        public void ReplaceAll(IEnumerable<MediaItemViewModel> items)
        {
            var desiredItems = items.ToList();
            if (Items.Count == desiredItems.Count
                && Items.Select(item => item.Id).SequenceEqual(desiredItems.Select(item => item.Id), StringComparer.Ordinal))
            {
                for (var index = 0; index < desiredItems.Count; index++)
                {
                    if (!ReferenceEquals(Items[index], desiredItems[index]))
                    {
                        SetItem(index, desiredItems[index]);
                    }
                }

                return;
            }

            var diff = CollectionDiffApplier.Apply(
                Items,
                desiredItems,
                item => item.Id,
                RemoveItem,
                MoveItem,
                InsertItem,
                (index, item) =>
                {
                    if (!ReferenceEquals(Items[index], item))
                    {
                        SetItem(index, item);
                    }
                },
                StringComparer.Ordinal);

            AppTraceLogger.LogSampled(
                "MediaFolderGroup",
                "visible-items-diff",
                $"Applied visible media diff. Count={Items.Count}, Inserted={diff.Inserted}, Removed={diff.Removed}, Moved={diff.Moved}, Replaced={diff.Updated}.",
                TimeSpan.FromSeconds(1));
        }
    }
}
