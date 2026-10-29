using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
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
        if (nextItems.Count == 0 && _items.Count > 0)
        {
            // A projection rebuild can race a folder-group load and pass an
            // empty list. Keep the items already shown rather than dumping
            // the GridView and immediately re-inserting them.
            ApplyVisibleItems();
            return;
        }

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

    // H5: appends newly loaded items to an existing group without a full
    // re-group of the loaded window. Duplicates are skipped by id, so a later
    // full rebuild (which dedupes the same way) always converges to the same
    // state; order follows the collection-change order, which is the filtered
    // list order that a full rebuild's GroupBy would produce too.
    public void AppendLoadedItems(IReadOnlyList<MediaItemViewModel> items)
    {
        var added = false;
        foreach (var item in items)
        {
            if (!_itemIds.Add(item.Id))
            {
                continue;
            }

            _items.Add(item);
            added = true;
        }

        if (!added)
        {
            return;
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
        // Grouped GridView fail-fasts (StackHash / 0xc000027b / E_UNEXPECTED)
        // when one layout pass applies more notifications than this. Expand
        // and collapse stay on a single Reset: one side of that change is an
        // empty group, so there are no realized item containers to recycle.
        // A scroll page append is the opposite case. Resetting a group that
        // already has containers crashes inside Microsoft.UI.Xaml.dll, so the
        // suffix is applied as ItemInserted notifications, off the current
        // layout pass, and never more than this many per dispatcher turn.
        private const int VisibleItemNotificationBudget = 16;

        private readonly List<MediaItemViewModel> _pendingAppend = new();
        private int _pendingAppendIndex;
        private int _appendGeneration;
        private bool _appendPumpScheduled;

        public void ReplaceAll(IEnumerable<MediaItemViewModel> items)
        {
            var desiredItems = items.ToList();
            if (Items.Count > 0
                && desiredItems.Count > Items.Count
                && HasIdPrefix(desiredItems, Items))
            {
                ReplaceMaterializedReferences(desiredItems);
                SetPendingAppend(desiredItems);
                var materializedCount = Items.Count;
                var appendCount = PendingAppendCount;
                AppTraceLogger.LogSampled(
                    "MediaFolderGroup",
                    "visible-items-append",
                    () => $"Queued visible media append. Materialized={materializedCount}, AppendCount={appendCount}.",
                    TimeSpan.FromSeconds(1));
                EnsureAppendPumpScheduled();
                return;
            }

            CancelPendingAppend();
            if (HasSameIds(Items, desiredItems))
            {
                ReplaceMaterializedReferences(desiredItems);
                return;
            }

            if (desiredItems.Count == 0
                || Items.Count == 0
                || Math.Abs(Items.Count - desiredItems.Count) > VisibleItemNotificationBudget)
            {
                ResetTo(desiredItems);
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

        private void ResetTo(IReadOnlyList<MediaItemViewModel> desiredItems)
        {
            var previousCount = Items.Count;
            CheckReentrancy();
            using (BlockReentrancy())
            {
                Items.Clear();
                foreach (var item in desiredItems)
                {
                    Items.Add(item);
                }
            }

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            AppTraceLogger.LogSampled(
                "MediaFolderGroup",
                "visible-items-reset",
                $"Reset visible media. PreviousCount={previousCount}, Count={Items.Count}.",
                TimeSpan.FromSeconds(1));
        }

        private int PendingAppendCount => _pendingAppend.Count - _pendingAppendIndex;

        private void SetPendingAppend(IReadOnlyList<MediaItemViewModel> desiredItems)
        {
            _pendingAppend.Clear();
            _pendingAppendIndex = 0;
            for (var index = Items.Count; index < desiredItems.Count; index++)
            {
                _pendingAppend.Add(desiredItems[index]);
            }
        }

        private void ClearPendingAppend()
        {
            _pendingAppend.Clear();
            _pendingAppendIndex = 0;
        }

        private void CancelPendingAppend()
        {
            ClearPendingAppend();
            _appendGeneration++;
        }

        private void ReplaceMaterializedReferences(IReadOnlyList<MediaItemViewModel> desiredItems)
        {
            var count = Math.Min(Items.Count, desiredItems.Count);
            for (var index = 0; index < count; index++)
            {
                if (!ReferenceEquals(Items[index], desiredItems[index]))
                {
                    SetItem(index, desiredItems[index]);
                }
            }
        }

        private void EnsureAppendPumpScheduled()
        {
            if (_appendPumpScheduled || PendingAppendCount == 0)
            {
                return;
            }

            var queue = DispatcherQueue.GetForCurrentThread();
            if (queue == null)
            {
                DrainPendingAppend();
                return;
            }

            var generation = _appendGeneration;
            _appendPumpScheduled = true;
            if (!queue.TryEnqueue(DispatcherQueuePriority.Normal, () => PumpAppendBatch(generation)))
            {
                _appendPumpScheduled = false;
                DrainPendingAppend();
            }
        }

        private void PumpAppendBatch(int generation)
        {
            if (generation != _appendGeneration)
            {
                CompleteAppendPumpTurn(generation);
                return;
            }

            var inserted = 0;
            while (inserted < VisibleItemNotificationBudget
                   && PendingAppendCount > 0
                   && generation == _appendGeneration)
            {
                var next = _pendingAppend[_pendingAppendIndex++];
                InsertItem(Items.Count, next);
                inserted++;
            }

            CompleteAppendPumpTurn(generation);
        }

        private void CompleteAppendPumpTurn(int generation)
        {
            if (generation != _appendGeneration)
            {
                _appendPumpScheduled = false;
                if (PendingAppendCount > 0)
                {
                    EnsureAppendPumpScheduled();
                }

                return;
            }

            if (PendingAppendCount == 0)
            {
                ClearPendingAppend();
                _appendPumpScheduled = false;
                return;
            }

            var queue = DispatcherQueue.GetForCurrentThread();
            if (queue == null || !queue.TryEnqueue(DispatcherQueuePriority.Normal, () => PumpAppendBatch(generation)))
            {
                _appendPumpScheduled = false;
                DrainPendingAppend();
            }
        }

        private void DrainPendingAppend()
        {
            var generation = _appendGeneration;
            while (PendingAppendCount > 0 && generation == _appendGeneration)
            {
                var next = _pendingAppend[_pendingAppendIndex++];
                InsertItem(Items.Count, next);
            }

            if (generation == _appendGeneration && PendingAppendCount == 0)
            {
                ClearPendingAppend();
            }
        }

        private static bool HasSameIds(IList<MediaItemViewModel> current, IReadOnlyList<MediaItemViewModel> desired)
        {
            return current.Count == desired.Count && HasIdPrefix(desired, current);
        }

        private static bool HasIdPrefix(IReadOnlyList<MediaItemViewModel> desired, IList<MediaItemViewModel> prefix)
        {
            if (desired.Count < prefix.Count)
            {
                return false;
            }

            for (var index = 0; index < prefix.Count; index++)
            {
                if (!string.Equals(desired[index].Id, prefix[index].Id, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
