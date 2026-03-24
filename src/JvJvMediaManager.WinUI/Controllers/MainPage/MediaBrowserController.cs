using System.Collections.Specialized;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using JvJvMediaManager.Models;
using JvJvMediaManager.Services.MainPage;
using JvJvMediaManager.Utilities;
using JvJvMediaManager.ViewModels;
using JvJvMediaManager.ViewModels.MainPage;
using JvJvMediaManager.Views.MainPageParts;

namespace JvJvMediaManager.Controllers.MainPage;

public sealed class MediaBrowserController : IDisposable
{
    private const double GridViewWidthPadding = 24;

    private readonly JvJvMediaManager.Views.MainPage _page;
    private readonly LibraryShellViewModel _viewModel;
    private readonly LibraryPaneView _libraryPane;
    private readonly MediaContextMenuCoordinator _contextMenuCoordinator;
    private readonly Action<IEnumerable<string>, bool> _updateWatchedFolders;
    private readonly Func<string, string, Task> _showInfoAsync;
    private readonly DebounceDispatcher _debouncer = new();
    private readonly Dictionary<string, MediaItemViewModel> _loadedMediaById = new(StringComparer.Ordinal);

    private bool _isSyncingSelection;
    private ScrollViewer? _gridViewScrollViewer;
    private HashSet<string> _selectedItemIds = new(StringComparer.Ordinal);

    private TextBox SearchBox => _libraryPane.FilterBarView.SearchBox;
    private ListView ListView => _libraryPane.BrowserView.ListView;
    private GridView GridView => _libraryPane.BrowserView.GridView;

    public MediaBrowserController(
        JvJvMediaManager.Views.MainPage page,
        LibraryShellViewModel viewModel,
        LibraryPaneView libraryPane,
        MediaContextMenuCoordinator contextMenuCoordinator,
        Action<IEnumerable<string>, bool> updateWatchedFolders,
        Func<string, string, Task> showInfoAsync)
    {
        _page = page;
        _viewModel = viewModel;
        _libraryPane = libraryPane;
        _contextMenuCoordinator = contextMenuCoordinator;
        _updateWatchedFolders = updateWatchedFolders;
        _showInfoAsync = showInfoAsync;

        _libraryPane.HeaderView.RefreshButton.Click += Refresh_Click;
        _libraryPane.HeaderView.ViewModeToggleButton.Click += ToggleViewMode_Click;
        _libraryPane.HeaderView.SortButton.Click += Sort_Click;
        SearchBox.TextChanged += SearchBox_TextChanged;
        SearchBox.KeyDown += SearchBox_KeyDown;
        _libraryPane.FilterBarView.TagRemoveRequested += FilterBarView_TagRemoveRequested;

        ListView.ContainerContentChanging += Media_ContainerContentChanging;
        ListView.SelectionChanged += Media_SelectionChanged;
        ListView.RightTapped += MediaView_RightTapped;
        ListView.PointerWheelChanged += MediaLibraryView_PointerWheelChanged;

        GridView.ContainerContentChanging += Media_ContainerContentChanging;
        GridView.SelectionChanged += Media_SelectionChanged;
        GridView.RightTapped += MediaView_RightTapped;
        GridView.Loaded += GridView_Loaded;
        GridView.SizeChanged += GridView_SizeChanged;
        GridView.PointerWheelChanged += MediaLibraryView_PointerWheelChanged;

        _libraryPane.DropTargetBorder.DragOver += LibraryPanel_DragOver;
        _libraryPane.DropTargetBorder.Drop += LibraryPanel_Drop;
        _libraryPane.PaneRoot.SizeChanged += LibraryPaneRoot_SizeChanged;
        _viewModel.FilteredMediaItems.CollectionChanged += FilteredMediaItems_CollectionChanged;
    }

    public void Dispose()
    {
        _libraryPane.HeaderView.RefreshButton.Click -= Refresh_Click;
        _libraryPane.HeaderView.ViewModeToggleButton.Click -= ToggleViewMode_Click;
        _libraryPane.HeaderView.SortButton.Click -= Sort_Click;
        SearchBox.TextChanged -= SearchBox_TextChanged;
        SearchBox.KeyDown -= SearchBox_KeyDown;
        _libraryPane.FilterBarView.TagRemoveRequested -= FilterBarView_TagRemoveRequested;

        ListView.ContainerContentChanging -= Media_ContainerContentChanging;
        ListView.SelectionChanged -= Media_SelectionChanged;
        ListView.RightTapped -= MediaView_RightTapped;
        ListView.PointerWheelChanged -= MediaLibraryView_PointerWheelChanged;

        GridView.ContainerContentChanging -= Media_ContainerContentChanging;
        GridView.SelectionChanged -= Media_SelectionChanged;
        GridView.RightTapped -= MediaView_RightTapped;
        GridView.Loaded -= GridView_Loaded;
        GridView.SizeChanged -= GridView_SizeChanged;
        GridView.PointerWheelChanged -= MediaLibraryView_PointerWheelChanged;

        _libraryPane.DropTargetBorder.DragOver -= LibraryPanel_DragOver;
        _libraryPane.DropTargetBorder.Drop -= LibraryPanel_Drop;
        _libraryPane.PaneRoot.SizeChanged -= LibraryPaneRoot_SizeChanged;
        _viewModel.FilteredMediaItems.CollectionChanged -= FilteredMediaItems_CollectionChanged;
    }

    public async Task InitializeAsync()
    {
        await _viewModel.InitializeAsync();
        RebuildLoadedMediaIndex();
        QueueThumbnailLoads(_viewModel.FilteredMediaItems);
        UpdateMediaItemSize();
        ConfigureGridViewScrolling();
    }

    public Task AddFolderAsync()
    {
        return ExecuteUiActionAsync(async () =>
        {
            var window = App.MainWindow;
            if (window == null)
            {
                return;
            }

            var folder = await PickerHelpers.PickFolderAsync(window);
            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            await _viewModel.AddFolderAsync(folder);
            _updateWatchedFolders(new[] { folder }, false);
        }, "导入文件夹失败", _showInfoAsync);
    }

    public Task AddFilesAsync()
    {
        return ExecuteUiActionAsync(async () =>
        {
            var window = App.MainWindow;
            if (window == null)
            {
                return;
            }

            var paths = await PickerHelpers.PickFilesAsync(window);
            if (paths.Count == 0)
            {
                return;
            }

            await _viewModel.AddFilesAsync(paths);
            _updateWatchedFolders(paths, false);
        }, "导入文件失败", _showInfoAsync);
    }

    public IReadOnlyList<MediaItemViewModel> GetSelectedItems()
    {
        return _selectedItemIds
            .Select(id => _loadedMediaById.TryGetValue(id, out var item) ? item : null)
            .OfType<MediaItemViewModel>()
            .ToList();
    }

    public void SyncSelectionFromViewModel(MediaItemViewModel? selectedMedia)
    {
        _isSyncingSelection = true;
        try
        {
            if (selectedMedia == null)
            {
                _selectedItemIds.Clear();
            }
            else if (_selectedItemIds.Count == 0 || !_selectedItemIds.Contains(selectedMedia.Id))
            {
                _selectedItemIds = new HashSet<string>(new[] { selectedMedia.Id }, StringComparer.Ordinal);
            }

            ApplySelectionToView(ListView, selectedMedia);
            ApplySelectionToView(GridView, selectedMedia);
            UpdateSelectedStateFlags();
        }
        finally
        {
            _isSyncingSelection = false;
        }
    }

    public void RevealSelectedMedia(MediaItemViewModel media)
    {
        if (_viewModel.ViewMode == MediaViewMode.Grid)
        {
            GridView.ScrollIntoView(media);
            return;
        }

        ListView.ScrollIntoView(media);
    }

    public void FocusSearchBox()
    {
        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteUiActionAsync(async () =>
        {
            if (_viewModel.WatchedFolders.Count == 0)
            {
                return;
            }

            await _viewModel.RescanFoldersAsync();
        }, "刷新媒体库失败", _showInfoAsync);
    }

    private void ToggleViewMode_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ViewMode = _viewModel.ViewMode == MediaViewMode.List
            ? MediaViewMode.Grid
            : MediaViewMode.List;
        UpdateMediaItemSize();
        if (_viewModel.ViewMode == MediaViewMode.Grid)
        {
            ConfigureGridViewScrolling();
            _page.DispatcherQueue.TryEnqueue(() =>
            {
                ConfigureGridViewScrolling();
                SynchronizeSelectionToActiveView();
            });
            return;
        }

        _page.DispatcherQueue.TryEnqueue(SynchronizeSelectionToActiveView);
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement target)
        {
            return;
        }

        BuildSortFlyout().ShowAt(target);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text ?? string.Empty;
        _debouncer.Debounce(TimeSpan.FromMilliseconds(250), () =>
        {
            _page.DispatcherQueue.TryEnqueue(() => _viewModel.SearchQuery = query);
        });
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
        {
            return;
        }

        var value = SearchBox.Text?.Trim() ?? string.Empty;
        if (!value.StartsWith("#", StringComparison.Ordinal) || value.Length <= 1)
        {
            return;
        }

        var tag = value[1..].Trim();
        if (tag.Length == 0)
        {
            return;
        }

        var needsImmediateRefresh = string.IsNullOrWhiteSpace(_viewModel.SearchQuery);
        if (!_viewModel.SelectedTags.Any(existing => string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase)))
        {
            _viewModel.SelectedTags.Add(tag);
            if (needsImmediateRefresh)
            {
                _ = _viewModel.RefreshMediaAsync(false);
            }
        }

        SearchBox.Text = string.Empty;
        _viewModel.SearchQuery = string.Empty;
        e.Handled = true;
    }

    private void FilterBarView_TagRemoveRequested(object? sender, string tag)
    {
        _viewModel.RemoveSelectedTagFilter(tag);
    }

    private void Media_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingSelection || sender is not ListViewBase listViewBase)
        {
            return;
        }

        var selectedIds = listViewBase.SelectedItems
            .OfType<MediaItemViewModel>()
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var selectedMedia = ResolvePrimarySelection(listViewBase, e, selectedIds);

        _selectedItemIds = selectedIds;
        _isSyncingSelection = true;
        try
        {
            ApplySelectionToView(ReferenceEquals(listViewBase, ListView) ? GridView : ListView, selectedMedia);
            UpdateSelectedStateFlags();
        }
        finally
        {
            _isSyncingSelection = false;
        }

        _viewModel.SelectedMedia = selectedMedia;
    }

    private void MediaView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not ListViewBase listViewBase || sender is not FrameworkElement target)
        {
            return;
        }

        if (TryGetMediaFromElement(e.OriginalSource as DependencyObject, out var media) && media != null)
        {
            EnsureRightTappedSelection(listViewBase, media);
        }

        var selected = GetSelectedItems();
        if (selected.Count == 0)
        {
            return;
        }

        _contextMenuCoordinator.ShowForTarget(target, e.GetPosition(target), selected);
        e.Handled = true;
    }

    private void Media_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not MediaItemViewModel media)
        {
            return;
        }

        if (sender == ListView)
        {
            ApplyListItemSize(args.ItemContainer as SelectorItem);
        }

        if (args.InRecycleQueue)
        {
            return;
        }

        _ = _viewModel.EnsureThumbnailAsync(media);
    }

    private void FilteredMediaItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            RebuildLoadedMediaIndex();
            _page.DispatcherQueue.TryEnqueue(() => SyncSelectionFromViewModel(_viewModel.SelectedMedia));
        }

        if (e.OldItems != null)
        {
            foreach (var item in e.OldItems.OfType<MediaItemViewModel>())
            {
                _loadedMediaById.Remove(item.Id);
            }
        }

        if (e.NewItems == null || e.NewItems.Count == 0)
        {
            return;
        }

        var newItems = e.NewItems.OfType<MediaItemViewModel>().ToList();
        foreach (var item in newItems)
        {
            _loadedMediaById[item.Id] = item;
            item.IsSelected = _selectedItemIds.Contains(item.Id);
        }

        QueueThumbnailLoads(newItems);

        if (_selectedItemIds.Count > 0 && newItems.Any(item => _selectedItemIds.Contains(item.Id)))
        {
            _page.DispatcherQueue.TryEnqueue(() => SyncSelectionFromViewModel(_viewModel.SelectedMedia));
        }
    }

    private void MediaLibraryView_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var ctrlDown = (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (!ctrlDown)
        {
            return;
        }

        var source = sender as UIElement ?? ListView;
        var delta = e.GetCurrentPoint(source).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        _viewModel.IconSize = Math.Clamp(_viewModel.IconSize + (delta > 0 ? 8 : -8), 72, 260);
        UpdateMediaItemSize();
        ConfigureGridViewScrolling();
        e.Handled = true;
    }

    private void GridView_Loaded(object sender, RoutedEventArgs e)
    {
        ConfigureGridViewScrolling();
    }

    private void GridView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0)
        {
            UpdateMediaItemSize();
        }
    }

    private void LibraryPaneRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0)
        {
            UpdateMediaItemSize();
        }
    }

    private void LibraryPanel_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "导入媒体文件或文件夹";
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }

        e.Handled = true;
    }

    private async void LibraryPanel_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items
                .OfType<IStorageItem>()
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToList();

            if (paths.Count == 0)
            {
                return;
            }

            await _viewModel.AddFilesAsync(paths);
            _updateWatchedFolders(paths, false);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void ConfigureGridViewScrolling()
    {
        ScrollViewer.SetHorizontalScrollMode(GridView, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(GridView, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollMode(GridView, ScrollMode.Enabled);
        ScrollViewer.SetVerticalScrollBarVisibility(GridView, ScrollBarVisibility.Visible);

        GridView.ApplyTemplate();
        GridView.UpdateLayout();

        if (GridView.ItemsPanelRoot is ItemsWrapGrid panel)
        {
            panel.Orientation = Orientation.Horizontal;
            var iconSize = GetCurrentGridItemSize();
            panel.ItemWidth = CalculateAdaptiveGridSlotSize(iconSize);
            panel.ItemHeight = iconSize;
        }

        _gridViewScrollViewer = MainPageVisualTreeHelpers.FindDescendant<ScrollViewer>(GridView);
    }

    private void UpdateMediaItemSize()
    {
        var gridItemSize = GetCurrentGridItemSize();
        GridView.Tag = gridItemSize;
        UpdateVisibleListItemSizes();
        if (GridView.ItemsPanelRoot is ItemsWrapGrid panel)
        {
            panel.Orientation = Orientation.Horizontal;
            var slotSize = CalculateAdaptiveGridSlotSize(gridItemSize);
            panel.ItemWidth = slotSize;
            panel.ItemHeight = gridItemSize;
        }
    }

    private double GetCurrentGridItemSize()
    {
        return Math.Clamp((double)_viewModel.IconSize, 72, 260);
    }

    private double CalculateAdaptiveGridSlotSize(double itemSize)
    {
        var availableWidth = GetGridViewAvailableWidth();
        if (availableWidth <= itemSize)
        {
            return itemSize;
        }

        var columns = Math.Max(1, (int)Math.Floor(availableWidth / itemSize));
        var adjustedSize = Math.Floor(availableWidth / columns);
        return Math.Clamp(adjustedSize, itemSize, 320);
    }

    private double GetGridViewAvailableWidth()
    {
        var width = _gridViewScrollViewer?.ActualWidth ?? 0;
        if (width <= 0)
        {
            width = GridView.ActualWidth;
        }

        if (width <= 0)
        {
            width = _viewModel.LibraryPaneWidth;
        }

        return Math.Max(72, width - GridViewWidthPadding);
    }

    private void UpdateVisibleListItemSizes()
    {
        for (var index = 0; index < ListView.Items.Count; index++)
        {
            if (ListView.ContainerFromIndex(index) is SelectorItem container)
            {
                ApplyListItemSize(container);
            }
        }
    }

    private void ApplyListItemSize(SelectorItem? container)
    {
        if (container == null)
        {
            return;
        }

        if (MainPageVisualTreeHelpers.FindDescendantByName(container, "ListThumbnailHost") is not FrameworkElement thumbnailHost)
        {
            return;
        }

        var size = Math.Clamp((int)Math.Round(_viewModel.IconSize * 0.6), 48, 180);
        thumbnailHost.Width = size;
        thumbnailHost.Height = size;
    }

    private void EnsureRightTappedSelection(ListViewBase listViewBase, MediaItemViewModel media)
    {
        var selected = GetSelectedItems().Any(item => string.Equals(item.Id, media.Id, StringComparison.Ordinal));
        if (selected)
        {
            return;
        }

        listViewBase.SelectedItems.Clear();
        listViewBase.SelectedItem = media;
    }

    private bool TryGetMediaFromElement(DependencyObject? origin, out MediaItemViewModel? media)
    {
        media = null;
        if (origin == null)
        {
            return false;
        }

        var container = MainPageVisualTreeHelpers.FindAncestor<SelectorItem>(origin);
        media = container?.Content as MediaItemViewModel;
        return media != null;
    }

    private void ApplySelectionToView(ListViewBase listViewBase, MediaItemViewModel? selectedMedia)
    {
        listViewBase.SelectedItems.Clear();
        foreach (var item in listViewBase.Items.OfType<MediaItemViewModel>())
        {
            if (_selectedItemIds.Contains(item.Id))
            {
                listViewBase.SelectedItems.Add(item);
            }
        }

        if (_selectedItemIds.Count <= 1)
        {
            listViewBase.SelectedItem = selectedMedia;
        }
    }

    private MediaItemViewModel? ResolvePrimarySelection(
        ListViewBase listViewBase,
        SelectionChangedEventArgs e,
        IReadOnlySet<string> selectedIds)
    {
        if (selectedIds.Count == 0)
        {
            return null;
        }

        if (listViewBase.SelectedItem is MediaItemViewModel selectedItem && selectedIds.Contains(selectedItem.Id))
        {
            return selectedItem;
        }

        if (e.AddedItems.OfType<MediaItemViewModel>().LastOrDefault() is { } added && selectedIds.Contains(added.Id))
        {
            return added;
        }

        if (_viewModel.SelectedMedia is { } current && selectedIds.Contains(current.Id))
        {
            return current;
        }

        return listViewBase.SelectedItems.OfType<MediaItemViewModel>().LastOrDefault();
    }

    private void UpdateSelectedStateFlags()
    {
        foreach (var removedId in _loadedMediaById.Keys.Except(_selectedItemIds).ToList())
        {
            if (_loadedMediaById.TryGetValue(removedId, out var removedItem))
            {
                removedItem.IsSelected = false;
            }
        }

        foreach (var addedId in _selectedItemIds)
        {
            if (_loadedMediaById.TryGetValue(addedId, out var addedItem))
            {
                addedItem.IsSelected = true;
            }
        }
    }

    private void RebuildLoadedMediaIndex()
    {
        _loadedMediaById.Clear();
        foreach (var item in _viewModel.FilteredMediaItems)
        {
            _loadedMediaById[item.Id] = item;
            item.IsSelected = _selectedItemIds.Contains(item.Id);
        }
    }

    private static async Task ExecuteUiActionAsync(Func<Task> action, string failureTitle, Func<string, string, Task> showInfoAsync)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            await showInfoAsync(failureTitle, ex.Message);
        }
    }

    private void QueueThumbnailLoads(IEnumerable<MediaItemViewModel> items)
    {
        foreach (var item in items)
        {
            _ = _viewModel.EnsureThumbnailAsync(item);
        }
    }

    private void SynchronizeSelectionToActiveView()
    {
        SyncSelectionFromViewModel(_viewModel.SelectedMedia);
        if (_viewModel.SelectedMedia != null)
        {
            RevealSelectedMedia(_viewModel.SelectedMedia);
        }
    }

    private MenuFlyout BuildSortFlyout()
    {
        var flyout = new MenuFlyout();
        flyout.Items.Add(CreateSortMenuItem("按时间排序（新到旧）", MediaSortField.ModifiedAt, MediaSortOrder.Desc));
        flyout.Items.Add(CreateSortMenuItem("按时间排序（旧到新）", MediaSortField.ModifiedAt, MediaSortOrder.Asc));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(CreateSortMenuItem("按名称排序（A-Z）", MediaSortField.FileName, MediaSortOrder.Asc));
        flyout.Items.Add(CreateSortMenuItem("按名称排序（Z-A）", MediaSortField.FileName, MediaSortOrder.Desc));
        return flyout;
    }

    private ToggleMenuFlyoutItem CreateSortMenuItem(string text, MediaSortField field, MediaSortOrder order)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = text,
            IsChecked = _viewModel.SortField == field && _viewModel.SortOrder == order
        };
        item.Click += (_, _) => _viewModel.SetSort(field, order);
        return item;
    }
}
