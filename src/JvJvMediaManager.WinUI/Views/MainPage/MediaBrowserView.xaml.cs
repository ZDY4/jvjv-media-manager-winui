using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using JvJvMediaManager.Models;
using JvJvMediaManager.Utilities;
using JvJvMediaManager.ViewModels;
using JvJvMediaManager.ViewModels.MainPage;

namespace JvJvMediaManager.Views.MainPageParts;

public readonly record struct RealizedMediaElement(FrameworkElement Element, MediaItemViewModel Media, int Index);

public sealed class MediaDragRequestEventArgs : EventArgs
{
    public MediaDragRequestEventArgs(MediaItemViewModel media, DragStartingEventArgs drag)
    {
        Media = media;
        Drag = drag;
    }

    public MediaItemViewModel Media { get; }

    public DragStartingEventArgs Drag { get; }
}

public sealed partial class MediaBrowserView : UserControl
{
    private readonly EntryCollection _entries = new();
    private readonly Dictionary<string, int> _mediaIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _mediaOrder = new(StringComparer.Ordinal);
    private readonly List<MediaItemViewModel> _orderedMedia = new();
    private readonly Dictionary<MediaFolderGroupViewModel, int> _headerIndex = new();
    private readonly Dictionary<object, MediaFolderGroupViewModel> _groupByItems = new();
    private readonly HashSet<MediaFolderGroupViewModel> _observedGroups = new();
    private readonly List<FrameworkElement> _realizedEntries = new();
    private readonly MediaBrowserTemplateSelector _templates = new();
    private LibraryShellViewModel? _viewModel;
    private bool? _appliedGrid;
    private double _appliedIcon = double.NaN;
    private int _appliedColumns;
    private int _attachmentGeneration;
    private int _rebuildDepth;
    private bool _entriesRebuildQueued;
    private bool _rebuildRequested;
    private bool _reindexQueued;
    private double _pendingScrollOffset = -1;
    private int _scrollRestoreAttempts;
    private string? _stickyFolderLabel;
    private bool _stickyHeaderAwaitingRealization;
    private bool _stickyHeaderLayoutHooked;
    private int _stickyHeaderLayoutAttempts;

    public MediaBrowserView()
    {
        InitializeComponent();
        _templates.HeaderTemplate = FindResource("MediaFolderGroupHeaderTemplate") as DataTemplate;
        _templates.GridTemplate = FindResource("GridItemTemplate") as DataTemplate;
        _templates.ListTemplate = FindResource("ListItemTemplate") as DataTemplate;
        _templates.RowTemplate = FindResource("MediaTileRowTemplate") as DataTemplate;
        MediaRepeater.ItemTemplate = _templates;
        MediaRepeater.ItemsSource = _entries;
        MediaRepeater.ElementPrepared += MediaRepeater_ElementPrepared;
        MediaRepeater.ElementClearing += MediaRepeater_ElementClearing;
        MediaRepeater.Tapped += MediaRepeater_Tapped;
        MediaScrollViewer.SizeChanged += MediaScrollViewer_SizeChanged;
        MediaScrollViewer.ViewChanged += MediaScrollViewer_ViewChanged;
        DataContextChanged += MediaBrowserView_DataContextChanged;
        Loaded += MediaBrowserView_Loaded;
        Unloaded += MediaBrowserView_Unloaded;
    }

    public event EventHandler<MediaItemViewModel>? MediaInvoked;

    public event EventHandler<MediaItemViewModel>? MediaPrepared;

    public event EventHandler<MediaItemViewModel>? MediaCleared;

    public event EventHandler<MediaDragRequestEventArgs>? MediaDragStarting;

    public void RefreshPresentation()
    {
        if (_viewModel == null)
        {
            return;
        }

        var grid = _viewModel.ViewMode == MediaViewMode.Grid;
        var icon = Math.Clamp((double)_viewModel.IconSize, 72, 260);
        var width = MediaScrollViewer.ActualWidth;
        if (width <= 1)
        {
            width = _viewModel.LibraryPaneWidth;
        }

        var columns = _appliedColumns > 0 ? _appliedColumns : 1;
        if (width > 1)
        {
            width = Math.Max(icon, width - 24);
            var stride = icon + 4;
            columns = Math.Max(1, (int)Math.Floor((width + 4) / stride));
        }

        if (_appliedGrid == grid
            && _appliedColumns == columns
            && Math.Abs(_appliedIcon - icon) < 0.5
            && _entries.Count > 0)
        {
            return;
        }

        _appliedGrid = grid;
        _appliedColumns = columns;
        _appliedIcon = icon;
        _templates.UseGrid = grid;
        RebuildEntries();
    }

    public void BringMediaIntoView(MediaItemViewModel media)
    {
        if (media == null || !_mediaIndex.TryGetValue(media.Id, out var index))
        {
            return;
        }

        var element = MediaRepeater.TryGetElement(index) ?? MediaRepeater.GetOrCreateElement(index);
        element.StartBringIntoView();
    }

    public bool TryGetRealizedElement(MediaItemViewModel media, out FrameworkElement? element)
    {
        element = null;
        if (media == null)
        {
            return false;
        }

        if (_mediaIndex.TryGetValue(media.Id, out var index)
            && MediaRepeater.TryGetElement(index) is FrameworkElement realized
            && TryMatchElement(realized, media, out element))
        {
            return true;
        }

        for (var entryIndex = 0; entryIndex < _realizedEntries.Count; entryIndex++)
        {
            if (TryMatchElement(_realizedEntries[entryIndex], media, out element))
            {
                return true;
            }
        }

        element = null;
        return false;
    }

    public IReadOnlyList<RealizedMediaElement> GetRealizedMedia()
    {
        // Realized set is maintained from ElementPrepared/ElementClearing.
        // Scanning every row with TryGetElement made marquee selection and
        // thumbnail prefetch walk the whole library on each pointer frame.
        var result = new List<RealizedMediaElement>(_realizedEntries.Count);
        for (var index = 0; index < _realizedEntries.Count; index++)
        {
            var element = _realizedEntries[index];
            var entryIndex = MediaRepeater.GetElementIndex(element);
            if (element.DataContext is MediaItemViewModel media)
            {
                result.Add(new RealizedMediaElement(element, media, entryIndex));
                continue;
            }

            if (element is not Panel panel)
            {
                continue;
            }

            for (var childIndex = 0; childIndex < panel.Children.Count; childIndex++)
            {
                if (panel.Children[childIndex] is FrameworkElement tile
                    && tile.DataContext is MediaItemViewModel tileMedia)
                {
                    result.Add(new RealizedMediaElement(tile, tileMedia, entryIndex));
                }
            }
        }

        return result;
    }

    public bool TryGetMediaRange(MediaItemViewModel anchor, MediaItemViewModel current, List<MediaItemViewModel> range)
    {
        range.Clear();
        if (!_mediaOrder.TryGetValue(anchor.Id, out var start)
            || !_mediaOrder.TryGetValue(current.Id, out var end))
        {
            return false;
        }

        if (start > end)
        {
            (start, end) = (end, start);
        }

        for (var index = start; index <= end && index < _orderedMedia.Count; index++)
        {
            range.Add(_orderedMedia[index]);
        }

        return range.Count > 0;
    }

    public static bool IsFolderChrome(DependencyObject? origin)
    {
        return FindContext(origin, out MediaItemViewModel? _, out var header) && header;
    }

    public static bool TryGetMedia(DependencyObject? origin, out MediaItemViewModel? media)
    {
        FindContext(origin, out media, out _);
        return media != null;
    }

    private void MediaBrowserView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null && DataContext is LibraryShellViewModel viewModel)
        {
            AttachViewModel(viewModel);
        }

        RefreshPresentation();
    }

    private void MediaBrowserView_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (args.NewValue is LibraryShellViewModel viewModel)
        {
            if (!ReferenceEquals(_viewModel, viewModel))
            {
                DetachViewModel();
                AttachViewModel(viewModel);
            }

            return;
        }

        DetachViewModel();
    }

    private void MediaBrowserView_Unloaded(object sender, RoutedEventArgs e)
    {
        DetachViewModel();
    }

    private void MediaScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0)
        {
            RefreshPresentation();
        }

        UpdateStickyFolderHeader();
    }

    private void MediaScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        UpdateStickyFolderHeader();
    }

    private void AttachViewModel(LibraryShellViewModel viewModel)
    {
        _viewModel = viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.MediaFolderGroups.CollectionChanged += MediaFolderGroups_CollectionChanged;
        foreach (var group in _viewModel.MediaFolderGroups)
        {
            AttachGroup(group);
        }

        RefreshPresentation();
    }

    private void DetachViewModel()
    {
        _attachmentGeneration++;
        _entriesRebuildQueued = false;
        _rebuildRequested = false;
        _reindexQueued = false;
        CancelScrollRestore();

        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _viewModel.MediaFolderGroups.CollectionChanged -= MediaFolderGroups_CollectionChanged;
        }

        foreach (var group in _observedGroups.ToList())
        {
            DetachGroup(group);
        }

        _entries.Clear();
        _realizedEntries.Clear();
        HideStickyFolderHeader();
        _mediaIndex.Clear();
        _mediaOrder.Clear();
        _orderedMedia.Clear();
        _headerIndex.Clear();
        _viewModel = null;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LibraryShellViewModel.ViewMode) or nameof(LibraryShellViewModel.IconSize))
        {
            RefreshPresentation();
        }
    }

    private void MediaFolderGroups_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var group in _observedGroups.ToList())
            {
                DetachGroup(group);
            }

            if (_viewModel != null)
            {
                foreach (var group in _viewModel.MediaFolderGroups)
                {
                    AttachGroup(group);
                }
            }
        }
        else
        {
            if (e.OldItems != null)
            {
                foreach (var group in e.OldItems.OfType<MediaFolderGroupViewModel>())
                {
                    DetachGroup(group);
                }
            }

            if (e.NewItems != null)
            {
                foreach (var group in e.NewItems.OfType<MediaFolderGroupViewModel>())
                {
                    AttachGroup(group);
                }
            }
        }

        // Folder projection applies remove/insert/replace one group at a time.
        // Rebuilding on each notification walks every visible tile. Coalesce
        // the burst into a single repeater reset after the projection finishes.
        QueueRebuildEntries();
    }

    private void MediaFolderGroupVisibleItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_entriesRebuildQueued || _rebuildDepth > 0)
        {
            return;
        }

        if (sender == null || !_groupByItems.TryGetValue(sender, out var group))
        {
            QueueRebuildEntries();
            return;
        }

        if (!group.IsCollapsed
            && e.Action == NotifyCollectionChangedAction.Add
            && e.NewItems is { Count: > 0 } newItems)
        {
            var added = new List<MediaItemViewModel>(newItems.Count);
            foreach (var entry in newItems)
            {
                if (entry is MediaItemViewModel media)
                {
                    added.Add(media);
                }
            }

            // Only a suffix belongs on the in-place row path. An insert or
            // reorder in the middle has to repack every following row.
            if (added.Count == newItems.Count
                && e.NewStartingIndex >= 0
                && e.NewStartingIndex + added.Count == group.VisibleItems.Count
                && TryAppendVisibleItems(group, added))
            {
                return;
            }
        }

        if (e.Action == NotifyCollectionChangedAction.Replace
            && e.NewStartingIndex >= 0
            && e.NewItems is { Count: 1 }
            && e.NewItems[0] is MediaItemViewModel replacement
            && TryReplaceVisibleItem(group, e.NewStartingIndex, replacement))
        {
            return;
        }

        // Reset, remove, and move reflow row packing. One deferred reset beats
        // removing and reinserting every row of the folder.
        QueueRebuildEntries();
    }

    private void AttachGroup(MediaFolderGroupViewModel group)
    {
        if (!_observedGroups.Add(group))
        {
            return;
        }

        _groupByItems[group.VisibleItems] = group;
        group.VisibleItems.CollectionChanged += MediaFolderGroupVisibleItems_CollectionChanged;
    }

    private void DetachGroup(MediaFolderGroupViewModel group)
    {
        if (!_observedGroups.Remove(group))
        {
            return;
        }

        _groupByItems.Remove(group.VisibleItems);
        group.VisibleItems.CollectionChanged -= MediaFolderGroupVisibleItems_CollectionChanged;
    }

    private void QueueRebuildEntries()
    {
        if (_viewModel == null)
        {
            return;
        }

        if (_rebuildDepth > 0)
        {
            _rebuildRequested = true;
            return;
        }

        if (_entriesRebuildQueued)
        {
            return;
        }

        _entriesRebuildQueued = true;
        var generation = _attachmentGeneration;
        var queue = DispatcherQueue;
        if (queue != null && queue.TryEnqueue(() => ApplyQueuedRebuild(generation)))
        {
            return;
        }

        _entriesRebuildQueued = false;
        RebuildEntries();
    }

    private void ApplyQueuedRebuild(int generation)
    {
        if (generation != _attachmentGeneration)
        {
            return;
        }

        _entriesRebuildQueued = false;
        if (_viewModel == null)
        {
            return;
        }

        RebuildEntries();
    }

    private void RebuildEntries()
    {
        if (_rebuildDepth > 0)
        {
            _rebuildRequested = true;
            return;
        }

        var offset = MediaScrollViewer.VerticalOffset;
        _rebuildDepth++;
        _entries.BeginDefer();
        try
        {
            _entries.Clear();
            if (_viewModel != null)
            {
                foreach (var group in _viewModel.MediaFolderGroups)
                {
                    _entries.Add(group);
                    if (group.IsCollapsed)
                    {
                        continue;
                    }

                    InsertGroupContent(group.VisibleItems, _entries.Count);
                }
            }

            Reindex();
        }
        finally
        {
            // Drop the realized set before the single Reset so ElementPrepared
            // repopulates it. Notifying per inserted row let the scroll height
            // collapse to zero in between and pinned the viewport to the top.
            _realizedEntries.Clear();
            _entries.EndDefer();
            _rebuildDepth--;
        }

        if (_rebuildRequested)
        {
            _rebuildRequested = false;
            QueueRebuildEntries();
            return;
        }

        ScheduleScrollRestore(offset);
        UpdateStickyFolderHeader();
        DispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Low, UpdateStickyFolderHeader);
    }

    private bool TryAppendVisibleItems(MediaFolderGroupViewModel group, List<MediaItemViewModel> added)
    {
        if (!_headerIndex.TryGetValue(group, out var headerIndex))
        {
            return false;
        }

        var insertAt = headerIndex + 1;
        while (insertAt < _entries.Count && _entries[insertAt] is MediaTileRow or MediaItemViewModel)
        {
            insertAt++;
        }

        var start = 0;
        if (_appliedGrid == true)
        {
            var columns = Math.Max(1, _appliedColumns);
            if (insertAt > 0
                && _entries[insertAt - 1] is MediaTileRow partial
                && partial.Items.Count < columns)
            {
                var take = Math.Min(columns - partial.Items.Count, added.Count);
                var previousCount = partial.Items.Count;
                for (var index = 0; index < take; index++)
                {
                    partial.Items.Add(added[index]);
                }

                // The folder loader inserts one item at a time. Replacing the
                // partial row on each insert rebuilt its tiles triangularly
                // (1+2+...+columns LoadContent calls per row).
                AppendRealizedTiles(partial, insertAt - 1, previousCount);
                start = take;
            }
        }

        if (start < added.Count)
        {
            InsertGroupContent(added, insertAt, start);
        }

        Reindex();
        return true;
    }

    private bool TryReplaceVisibleItem(MediaFolderGroupViewModel group, int visibleIndex, MediaItemViewModel replacement)
    {
        if (!_headerIndex.TryGetValue(group, out var headerIndex))
        {
            return false;
        }

        if (_appliedGrid != true)
        {
            var entryIndex = headerIndex + 1 + visibleIndex;
            if ((uint)entryIndex >= (uint)_entries.Count || _entries[entryIndex] is not MediaItemViewModel)
            {
                return false;
            }

            _entries[entryIndex] = replacement;
            QueueReindex();
            return true;
        }

        var columns = Math.Max(1, _appliedColumns);
        var entryIndexRow = headerIndex + 1 + (visibleIndex / columns);
        var column = visibleIndex % columns;
        if ((uint)entryIndexRow >= (uint)_entries.Count
            || _entries[entryIndexRow] is not MediaTileRow row
            || (uint)column >= (uint)row.Items.Count)
        {
            return false;
        }

        row.Items[column] = replacement;
        if (MediaRepeater.TryGetElement(entryIndexRow) is Panel panel
            && (uint)column < (uint)panel.Children.Count
            && panel.Children[column] is FrameworkElement tile)
        {
            if (!ReferenceEquals(tile.DataContext, replacement))
            {
                DetachTile(tile);
                tile.DataContext = replacement;
                tile.Width = row.TileSize;
                tile.Height = row.TileSize;
                AttachTile(tile, replacement);
            }
        }

        QueueReindex();
        return true;
    }

    private void QueueReindex()
    {
        if (_reindexQueued || _entriesRebuildQueued || _rebuildDepth > 0 || _viewModel == null)
        {
            return;
        }

        _reindexQueued = true;
        var generation = _attachmentGeneration;
        var queue = DispatcherQueue;
        if (queue != null && queue.TryEnqueue(() =>
            {
                if (generation != _attachmentGeneration)
                {
                    return;
                }

                _reindexQueued = false;
                if (_entriesRebuildQueued || _rebuildDepth > 0 || _viewModel == null)
                {
                    return;
                }

                Reindex();
            }))
        {
            return;
        }

        _reindexQueued = false;
        Reindex();
    }

    private void InsertGroupContent(IReadOnlyList<MediaItemViewModel> items, int insertAt, int start = 0)
    {
        if (start >= items.Count)
        {
            return;
        }

        if (_appliedGrid != true)
        {
            for (var index = start; index < items.Count; index++)
            {
                InsertEntry(items[index], insertAt);
                insertAt++;
            }

            return;
        }

        var columns = Math.Max(1, _appliedColumns);
        var tile = _appliedIcon > 1 ? _appliedIcon : 140;
        for (var index = start; index < items.Count; index += columns)
        {
            var row = new MediaTileRow(tile);
            var count = Math.Min(columns, items.Count - index);
            for (var offset = 0; offset < count; offset++)
            {
                row.Items.Add(items[index + offset]);
            }

            InsertEntry(row, insertAt);
            insertAt++;
        }
    }

    private void InsertEntry(object entry, int insertAt)
    {
        if (insertAt >= _entries.Count)
        {
            _entries.Add(entry);
            return;
        }

        _entries.Insert(insertAt, entry);
    }

    private void Reindex()
    {
        _reindexQueued = false;
        _mediaIndex.Clear();
        _mediaOrder.Clear();
        _headerIndex.Clear();
        _orderedMedia.Clear();
        for (var index = 0; index < _entries.Count; index++)
        {
            switch (_entries[index])
            {
                case MediaItemViewModel media:
                    AddMediaIndex(media, index);
                    break;
                case MediaTileRow row:
                    foreach (var item in row.Items)
                    {
                        AddMediaIndex(item, index);
                    }

                    break;
                case MediaFolderGroupViewModel group:
                    _headerIndex[group] = index;
                    break;
            }
        }
    }

    private void AddMediaIndex(MediaItemViewModel media, int entryIndex)
    {
        _mediaIndex[media.Id] = entryIndex;
        _mediaOrder[media.Id] = _orderedMedia.Count;
        _orderedMedia.Add(media);
    }

    private void MediaRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not FrameworkElement element)
        {
            return;
        }

        TrackRealized(element);
        if (_stickyHeaderAwaitingRealization)
        {
            UpdateStickyFolderHeader();
        }

        if (element.DataContext is MediaTileRow row && element is Panel panel)
        {
            FillTileRow(panel, row);
            return;
        }

        if (element.DataContext is MediaItemViewModel media)
        {
            AttachTile(element, media);
        }
    }

    private void MediaRepeater_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is not FrameworkElement element)
        {
            return;
        }

        UntrackRealized(element);
        if (element is Panel panel && element.DataContext is MediaTileRow)
        {
            // Keep the tile elements. The next prepare rebinds them; tearing
            // the template down per recycled row was the scroll-frame cost.
            for (var index = 0; index < panel.Children.Count; index++)
            {
                if (panel.Children[index] is FrameworkElement child)
                {
                    DetachTile(child);
                }
            }

            return;
        }

        DetachTile(element);
    }

    private void FillTileRow(Panel panel, MediaTileRow row)
    {
        panel.Height = row.TileSize;
        var index = 0;
        foreach (var item in row.Items)
        {
            FrameworkElement? tile = null;
            if (index < panel.Children.Count)
            {
                tile = panel.Children[index] as FrameworkElement;
            }

            if (tile == null)
            {
                tile = CreateTile();
                if (tile == null)
                {
                    continue;
                }

                // Set the item before the tile joins the row. Otherwise it
                // inherits the row object and the template binds against it.
                tile.DataContext = item;
                panel.Children.Add(tile);
            }

            tile.Width = row.TileSize;
            tile.Height = row.TileSize;
            if (!ReferenceEquals(tile.DataContext, item))
            {
                DetachTile(tile);
                tile.DataContext = item;
            }

            // ElementClearing already dropped the drag hook and told the
            // controller this tile is gone. Rebind even when the same media
            // comes back so its thumbnail load starts again.
            AttachTile(tile, item);
            index++;
        }

        for (var extra = panel.Children.Count - 1; extra >= index; extra--)
        {
            if (panel.Children[extra] is FrameworkElement surplus)
            {
                DetachTile(surplus);
            }

            panel.Children.RemoveAt(extra);
        }
    }

    private void AppendRealizedTiles(MediaTileRow row, int entryIndex, int previousCount)
    {
        if (MediaRepeater.TryGetElement(entryIndex) is not Panel panel)
        {
            return;
        }

        var staleChildren = previousCount > 0
            && panel.Children[previousCount - 1] is FrameworkElement existing
            && existing.DataContext is not MediaItemViewModel;
        if (panel.Children.Count != previousCount || staleChildren)
        {
            FillTileRow(panel, row);
            return;
        }

        panel.Height = row.TileSize;
        for (var index = previousCount; index < row.Items.Count; index++)
        {
            var tile = CreateTile();
            if (tile == null)
            {
                continue;
            }

            var item = row.Items[index];
            tile.Width = row.TileSize;
            tile.Height = row.TileSize;
            tile.DataContext = item;
            panel.Children.Add(tile);
            AttachTile(tile, item);
        }
    }

    private FrameworkElement? CreateTile()
    {
        return _templates.GridTemplate?.LoadContent() as FrameworkElement;
    }

    private void TrackRealized(FrameworkElement element)
    {
        if (!_realizedEntries.Contains(element))
        {
            _realizedEntries.Add(element);
        }
    }

    private void UntrackRealized(FrameworkElement element)
    {
        _realizedEntries.Remove(element);
    }

    private void AttachTile(FrameworkElement element, MediaItemViewModel media)
    {
        element.DragStarting -= Element_DragStarting;
        element.CanDrag = true;
        element.DragStarting += Element_DragStarting;
        MediaPrepared?.Invoke(this, media);
    }

    private void DetachTile(FrameworkElement element)
    {
        element.DragStarting -= Element_DragStarting;
        element.CanDrag = false;
        // Leave DataContext alone. List rows are owned by the repeater; clearing
        // them here drops the item the next prepare is about to show.
        if (element.DataContext is MediaItemViewModel media)
        {
            MediaCleared?.Invoke(this, media);
        }
    }

    private void UpdateStickyFolderHeader()
    {
        if (_viewModel == null || _entries.Count == 0 || MediaScrollViewer.VerticalOffset <= 1)
        {
            HideStickyFolderHeader();
            return;
        }

        var group = FindGroupAtViewportTop();
        if (group == null)
        {
            // Rebuild clears the realized set before the repeater measures the
            // new window. Hiding here flashes the bar; keeping a stale label
            // until the next scroll leaves the previous folder on screen.
            if (_realizedEntries.Count == 0 || !HasMeasuredRealizedEntry())
            {
                ArmStickyHeaderRealization();
                return;
            }

            HideStickyFolderHeader();
            return;
        }

        DisarmStickyHeaderRealization();

        if (IsFolderHeaderVisibleAtTop(group))
        {
            HideStickyFolderHeader();
            return;
        }

        var label = BuildFolderHierarchyLabel(group);
        if (string.IsNullOrWhiteSpace(label))
        {
            HideStickyFolderHeader();
            return;
        }

        if (!string.Equals(_stickyFolderLabel, label, StringComparison.Ordinal)
            || StickyFolderBar.Visibility != Visibility.Visible)
        {
            _stickyFolderLabel = label;
            SetStickyFolderLabel(label);
        }

        StickyFolderBar.Visibility = Visibility.Visible;
    }

    private void HideStickyFolderHeader()
    {
        DisarmStickyHeaderRealization();
        _stickyFolderLabel = null;
        StickyFolderText.Inlines.Clear();
        StickyFolderBar.Visibility = Visibility.Collapsed;
    }

    private bool HasMeasuredRealizedEntry()
    {
        for (var index = 0; index < _realizedEntries.Count; index++)
        {
            if (_realizedEntries[index].ActualHeight > 0)
            {
                return true;
            }
        }

        return false;
    }

    private void ArmStickyHeaderRealization()
    {
        _stickyHeaderAwaitingRealization = true;
        if (_stickyHeaderLayoutHooked || _stickyHeaderLayoutAttempts >= 8)
        {
            return;
        }

        _stickyHeaderLayoutAttempts++;
        _stickyHeaderLayoutHooked = true;
        MediaRepeater.LayoutUpdated += MediaRepeater_LayoutUpdated;
    }

    private void DisarmStickyHeaderRealization()
    {
        _stickyHeaderAwaitingRealization = false;
        _stickyHeaderLayoutAttempts = 0;
        if (!_stickyHeaderLayoutHooked)
        {
            return;
        }

        _stickyHeaderLayoutHooked = false;
        MediaRepeater.LayoutUpdated -= MediaRepeater_LayoutUpdated;
    }

    private void MediaRepeater_LayoutUpdated(object? sender, object e)
    {
        if (!_stickyHeaderAwaitingRealization)
        {
            DisarmStickyHeaderRealization();
            return;
        }

        if (_stickyHeaderLayoutAttempts >= 8)
        {
            _stickyHeaderAwaitingRealization = false;
            _stickyHeaderLayoutAttempts = 0;
            _stickyHeaderLayoutHooked = false;
            MediaRepeater.LayoutUpdated -= MediaRepeater_LayoutUpdated;
            return;
        }

        _stickyHeaderLayoutAttempts++;
        UpdateStickyFolderHeader();
    }

    private MediaFolderGroupViewModel? FindGroupAtViewportTop()
    {
        FrameworkElement? topElement = null;
        var top = double.PositiveInfinity;
        for (var index = 0; index < _realizedEntries.Count; index++)
        {
            var element = _realizedEntries[index];
            if (element.ActualHeight <= 0)
            {
                continue;
            }

            double y;
            try
            {
                y = element.TransformToVisual(MediaScrollViewer).TransformPoint(default).Y;
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (y + element.ActualHeight <= 0 || y >= top)
            {
                continue;
            }

            top = y;
            topElement = element;
        }

        if (topElement == null)
        {
            return null;
        }

        var entryIndex = MediaRepeater.GetElementIndex(topElement);
        if (entryIndex < 0)
        {
            return null;
        }

        for (var index = entryIndex; index >= 0; index--)
        {
            if (_entries[index] is MediaFolderGroupViewModel group)
            {
                return group;
            }
        }

        return null;
    }

    private bool IsFolderHeaderVisibleAtTop(MediaFolderGroupViewModel group)
    {
        if (!_headerIndex.TryGetValue(group, out var headerIndex)
            || MediaRepeater.TryGetElement(headerIndex) is not FrameworkElement header)
        {
            return false;
        }

        try
        {
            var y = header.TransformToVisual(MediaScrollViewer).TransformPoint(default).Y;
            return y >= -1;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private string BuildFolderHierarchyLabel(MediaFolderGroupViewModel group)
    {
        var names = new List<string>();
        var path = PathHelpers.NormalizeFolderPath(group.FolderPath);
        var guard = 0;
        while (!string.IsNullOrWhiteSpace(path) && guard++ < 40)
        {
            var match = FindGroupByPath(path);
            if (match != null)
            {
                names.Add(match.FolderDisplayName);
            }

            var parent = GetParentFolderPath(path);
            if (parent == null || string.Equals(parent, path, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            path = parent;
        }

        if (names.Count == 0)
        {
            names.Add(group.FolderDisplayName);
        }

        names.Reverse();
        return string.Join("\u001f", names);
    }

    private MediaFolderGroupViewModel? FindGroupByPath(string folderPath)
    {
        if (_viewModel == null)
        {
            return null;
        }

        foreach (var group in _viewModel.MediaFolderGroups)
        {
            if (string.Equals(PathHelpers.NormalizeFolderPath(group.FolderPath), folderPath, StringComparison.OrdinalIgnoreCase))
            {
                return group;
            }
        }

        return null;
    }

    private static string? GetParentFolderPath(string folderPath)
    {
        var slashIndex = folderPath.LastIndexOf('/');
        if (slashIndex <= 0)
        {
            return null;
        }

        if (slashIndex == 2 && folderPath.Length > 1 && folderPath[1] == ':')
        {
            return null;
        }

        return folderPath[..slashIndex];
    }

    private void SetStickyFolderLabel(string label)
    {
        StickyFolderText.Inlines.Clear();
        var names = label.Split('\u001f');
        for (var index = 0; index < names.Length; index++)
        {
            if (index > 0)
            {
                StickyFolderText.Inlines.Add(new Run
                {
                    Text = "  /  ",
                    Foreground = (Brush)Application.Current.Resources["MutedTextBrush"]
                });
            }

            StickyFolderText.Inlines.Add(new Run
            {
                Text = names[index],
                Foreground = (Brush)Application.Current.Resources["TextBrush"]
            });
        }
    }

    private void ScheduleScrollRestore(double offset)
    {
        if (offset <= 1 || _entries.Count == 0)
        {
            return;
        }

        _pendingScrollOffset = offset;
        _scrollRestoreAttempts = 0;
        MediaScrollViewer.LayoutUpdated -= RestoreScrollOffset;
        MediaScrollViewer.LayoutUpdated += RestoreScrollOffset;
    }

    private void RestoreScrollOffset(object? sender, object e)
    {
        var offset = _pendingScrollOffset;
        _scrollRestoreAttempts++;
        if (offset <= 1 || _entries.Count == 0)
        {
            CancelScrollRestore();
            return;
        }

        var extent = MediaScrollViewer.ScrollableHeight;
        var giveUp = _scrollRestoreAttempts >= 8;
        var extentReady = extent > 1 && (extent + 1 >= offset || giveUp);
        if (!extentReady)
        {
            if (giveUp)
            {
                CancelScrollRestore();
            }

            return;
        }

        CancelScrollRestore();
        if (MediaScrollViewer.VerticalOffset > 1)
        {
            return;
        }

        var target = Math.Min(offset, extent);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_viewModel == null || MediaScrollViewer.VerticalOffset > 1)
            {
                return;
            }

            var available = MediaScrollViewer.ScrollableHeight;
            if (available <= 1)
            {
                return;
            }

            MediaScrollViewer.ChangeView(null, Math.Min(target, available), null, true);
        });
    }

    private void CancelScrollRestore()
    {
        _pendingScrollOffset = -1;
        MediaScrollViewer.LayoutUpdated -= RestoreScrollOffset;
    }

    private void MediaRepeater_Tapped(object sender, TappedRoutedEventArgs e)
    {
        var origin = e.OriginalSource as DependencyObject;
        if (!TryGetMedia(origin, out var media) || media == null)
        {
            // Silent no-ops here used to be indistinguishable from a frozen app;
            // log them so "clicking a media does nothing" is diagnosable.
            AppTraceLogger.LogSampled(
                "MediaBrowser",
                "tap-unresolved",
                () =>
                {
                    var originElement = origin as FrameworkElement;
                    var originType = (originElement ?? origin)?.GetType().Name ?? "<null>";
                    var dataContextType = originElement?.DataContext?.GetType().Name ?? "<null>";
                    return $"Tap did not resolve to a media item. Handler=MediaRepeater_Tapped, OriginalSourceType={originType}, OriginalSourceDataContext={dataContextType}, FolderChrome={IsFolderChrome(origin)}, EntryCount={_entries.Count}.";
                },
                TimeSpan.FromSeconds(1));
            return;
        }

        MediaInvoked?.Invoke(this, media);
        e.Handled = true;
    }

    private void Element_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is not FrameworkElement element || element.DataContext is not MediaItemViewModel media)
        {
            return;
        }

        MediaDragStarting?.Invoke(this, new MediaDragRequestEventArgs(media, args));
    }

    private object? FindResource(string key)
    {
        if (Resources.TryGetValue(key, out var direct))
        {
            return direct;
        }

        foreach (var dictionary in Resources.MergedDictionaries)
        {
            if (dictionary.TryGetValue(key, out var merged))
            {
                return merged;
            }
        }

        return null;
    }

    private static bool TryMatchElement(FrameworkElement element, MediaItemViewModel media, out FrameworkElement? match)
    {
        if (element.DataContext is MediaItemViewModel candidate
            && string.Equals(candidate.Id, media.Id, StringComparison.Ordinal))
        {
            match = element;
            return true;
        }

        if (element is Panel panel)
        {
            for (var index = 0; index < panel.Children.Count; index++)
            {
                if (panel.Children[index] is FrameworkElement tile
                    && tile.DataContext is MediaItemViewModel tileMedia
                    && string.Equals(tileMedia.Id, media.Id, StringComparison.Ordinal))
                {
                    match = tile;
                    return true;
                }
            }
        }

        match = null;
        return false;
    }

    private static bool FindContext(DependencyObject? origin, out MediaItemViewModel? media, out bool header)
    {
        media = null;
        header = false;
        var current = origin;
        while (current != null)
        {
            if (current is FrameworkElement element)
            {
                if (element.DataContext is MediaItemViewModel item)
                {
                    media = item;
                    return true;
                }

                if (element.DataContext is MediaFolderGroupViewModel)
                {
                    header = true;
                    return true;
                }
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    /// <summary>
    /// ObservableCollection notifies on every insert. A folder of a few thousand
    /// tiles was one layout pass per row. Defer collapses a rebuild into one Reset.
    /// </summary>
    private sealed class EntryCollection : ObservableCollection<object>
    {
        private int _deferDepth;
        private bool _dirty;

        public void BeginDefer()
        {
            _deferDepth++;
        }

        public void EndDefer()
        {
            if (_deferDepth == 0)
            {
                return;
            }

            _deferDepth--;
            if (_deferDepth != 0 || !_dirty)
            {
                return;
            }

            _dirty = false;
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
        {
            if (_deferDepth > 0)
            {
                _dirty = true;
                return;
            }

            base.OnCollectionChanged(e);
        }

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            if (_deferDepth > 0)
            {
                _dirty = true;
                return;
            }

            base.OnPropertyChanged(e);
        }
    }
}
