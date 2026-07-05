using System.Collections.Specialized;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
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
    private const double DragSelectionActivationThreshold = 6;
    private const double DragSelectionAutoScrollEdgeThreshold = 40;
    private const double DragSelectionAutoScrollMaxStep = 28;
    private const double MediaPagePreloadDistance = 1800;
    private static readonly TimeSpan DragSelectionAutoScrollInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan RecentUserSelectionRevealSuppression = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ItemsChangedVisibilityVerificationWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PostMutationSelectionSuppressionWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ViewModelSelectionSettleWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan VisibleThumbnailLoadDelay = TimeSpan.FromMilliseconds(140);
    private static readonly TimeSpan ContextMenuDuplicateSuppression = TimeSpan.FromMilliseconds(600);
    private const int MaxMediaScrollAttempts = 3;
    private readonly KeyboardAccelerator _listContextMenuAccelerator = CreateContextMenuKeyboardAccelerator();
    private readonly KeyboardAccelerator _listShiftF10ContextMenuAccelerator = CreateShiftF10ContextMenuKeyboardAccelerator();
    private readonly KeyboardAccelerator _gridContextMenuAccelerator = CreateContextMenuKeyboardAccelerator();
    private readonly KeyboardAccelerator _gridShiftF10ContextMenuAccelerator = CreateShiftF10ContextMenuKeyboardAccelerator();
    private readonly TypedEventHandler<UIElement, ContextRequestedEventArgs> _contextRequestedHandler;
    private readonly RightTappedEventHandler _rightTappedHandler;
    private readonly KeyEventHandler _keyDownHandler;
    private readonly PointerEventHandler _pointerPressedHandler;
    private readonly PointerEventHandler _pointerMovedHandler;
    private readonly PointerEventHandler _pointerReleasedHandler;

    private readonly JvJvMediaManager.Views.MainPage _page;
    private readonly LibraryShellViewModel _viewModel;
    private readonly LibraryPaneView _libraryPane;
    private readonly MediaContextMenuCoordinator _contextMenuCoordinator;
    private readonly Func<IEnumerable<string>, bool, Task> _updateWatchedFoldersAsync;
    private readonly Func<string, string, Task> _showInfoAsync;
    private readonly DebounceDispatcher _debouncer = new();
    private readonly MediaSelectionSyncController _selectionSync = new();
    private readonly DispatcherTimer _dragSelectionAutoScrollTimer = new();
    private readonly DispatcherTimer _visibleThumbnailLoadTimer = new();
    private int _itemsChangedVisibilityVersion;
    private int _itemsChangedVisibilityVerificationScheduled;
    private int _isLoadingMoreMedia;
    private bool _isMediaViewScrolling;

    private bool _isSyncingSelection;
    private ScrollViewer? _listViewScrollViewer;
    private ScrollViewer? _gridViewScrollViewer;
    private readonly MediaSelectionCoordinator _selectionCoordinator;
    private readonly MediaScrollController _mediaScroll = new();
    private readonly DragSelectionController _dragSelectionController = new(
        DragSelectionActivationThreshold,
        DragSelectionAutoScrollEdgeThreshold,
        DragSelectionAutoScrollMaxStep);
    private DragSelectionState _dragSelection => _dragSelectionController.State;
    private long _lastMediaContextMenuOpenedTicks;

    private TextBox SearchBox => _libraryPane.FilterBarView.SearchBox;
    private ListView ListView => _libraryPane.BrowserView.ListView;
    private GridView GridView => _libraryPane.BrowserView.GridView;
    private Grid BrowserRoot => _libraryPane.BrowserView.RootGrid;
    private Canvas SelectionCanvas => _libraryPane.BrowserView.SelectionCanvas;
    private Microsoft.UI.Xaml.Shapes.Rectangle SelectionRectangle => _libraryPane.BrowserView.SelectionRectangle;

    public MediaBrowserController(
        JvJvMediaManager.Views.MainPage page,
        LibraryShellViewModel viewModel,
        LibraryPaneView libraryPane,
        MediaContextMenuCoordinator contextMenuCoordinator,
        Func<IEnumerable<string>, bool, Task> updateWatchedFoldersAsync,
        Func<string, string, Task> showInfoAsync)
    {
        _page = page;
        _viewModel = viewModel;
        _libraryPane = libraryPane;
        _contextMenuCoordinator = contextMenuCoordinator;
        _updateWatchedFoldersAsync = updateWatchedFoldersAsync;
        _showInfoAsync = showInfoAsync;
        _selectionCoordinator = new MediaSelectionCoordinator(_selectionSync.LoadedMediaById);
        _contextRequestedHandler = MediaView_ContextRequested;
        _rightTappedHandler = MediaView_RightTapped;
        _keyDownHandler = MediaView_KeyDown;
        _pointerPressedHandler = MediaView_PointerPressed;
        _pointerMovedHandler = MediaView_PointerMoved;
        _pointerReleasedHandler = MediaView_PointerReleased;
        _dragSelectionAutoScrollTimer.Interval = DragSelectionAutoScrollInterval;
        _dragSelectionAutoScrollTimer.Tick += DragSelectionAutoScrollTimer_Tick;
        _visibleThumbnailLoadTimer.Interval = VisibleThumbnailLoadDelay;
        _visibleThumbnailLoadTimer.Tick += VisibleThumbnailLoadTimer_Tick;

        _libraryPane.HeaderView.RefreshButton.Click += Refresh_Click;
        _libraryPane.HeaderView.CancelScanButton.Click += CancelScan_Click;
        _libraryPane.HeaderView.ViewModeToggleButton.Click += ToggleViewMode_Click;
        _libraryPane.HeaderView.FolderGroupToggleButton.Click += ToggleFolderGroups_Click;
        _libraryPane.HeaderView.MediaTypeFilterButton.Click += MediaTypeFilter_Click;
        _libraryPane.HeaderView.SortButton.Click += Sort_Click;
        SearchBox.TextChanged += SearchBox_TextChanged;
        SearchBox.KeyDown += SearchBox_KeyDown;
        _libraryPane.FilterBarView.TagRemoveRequested += FilterBarView_TagRemoveRequested;

        ListView.ContainerContentChanging += Media_ContainerContentChanging;
        ListView.SelectionChanged += Media_SelectionChanged;
        ListView.DragItemsStarting += MediaView_DragItemsStarting;
        ListView.AddHandler(UIElement.ContextRequestedEvent, _contextRequestedHandler, true);
        ListView.AddHandler(UIElement.RightTappedEvent, _rightTappedHandler, true);
        ListView.AddHandler(UIElement.KeyDownEvent, _keyDownHandler, true);
        _listContextMenuAccelerator.Invoked += MediaViewContextMenuAccelerator_Invoked;
        _listShiftF10ContextMenuAccelerator.Invoked += MediaViewContextMenuAccelerator_Invoked;
        ListView.KeyboardAccelerators.Add(_listContextMenuAccelerator);
        ListView.KeyboardAccelerators.Add(_listShiftF10ContextMenuAccelerator);
        ListView.PointerWheelChanged += MediaLibraryView_PointerWheelChanged;
        ListView.AddHandler(UIElement.PointerPressedEvent, _pointerPressedHandler, true);
        ListView.AddHandler(UIElement.PointerMovedEvent, _pointerMovedHandler, true);
        ListView.AddHandler(UIElement.PointerReleasedEvent, _pointerReleasedHandler, true);
        ListView.PointerCaptureLost += MediaView_PointerCaptureLost;
        ListView.PointerCanceled += MediaView_PointerCanceled;

        GridView.ContainerContentChanging += Media_ContainerContentChanging;
        GridView.SelectionChanged += Media_SelectionChanged;
        GridView.DragItemsStarting += MediaView_DragItemsStarting;
        GridView.AddHandler(UIElement.ContextRequestedEvent, _contextRequestedHandler, true);
        GridView.AddHandler(UIElement.RightTappedEvent, _rightTappedHandler, true);
        GridView.AddHandler(UIElement.KeyDownEvent, _keyDownHandler, true);
        _gridContextMenuAccelerator.Invoked += MediaViewContextMenuAccelerator_Invoked;
        _gridShiftF10ContextMenuAccelerator.Invoked += MediaViewContextMenuAccelerator_Invoked;
        GridView.KeyboardAccelerators.Add(_gridContextMenuAccelerator);
        GridView.KeyboardAccelerators.Add(_gridShiftF10ContextMenuAccelerator);
        GridView.Loaded += GridView_Loaded;
        GridView.SizeChanged += GridView_SizeChanged;
        GridView.PointerWheelChanged += MediaLibraryView_PointerWheelChanged;
        GridView.AddHandler(UIElement.PointerPressedEvent, _pointerPressedHandler, true);
        GridView.AddHandler(UIElement.PointerMovedEvent, _pointerMovedHandler, true);
        GridView.AddHandler(UIElement.PointerReleasedEvent, _pointerReleasedHandler, true);
        GridView.PointerCaptureLost += MediaView_PointerCaptureLost;
        GridView.PointerCanceled += MediaView_PointerCanceled;

        _libraryPane.DropTargetBorder.DragOver += LibraryPanel_DragOver;
        _libraryPane.DropTargetBorder.Drop += LibraryPanel_Drop;
        _libraryPane.PaneRoot.SizeChanged += LibraryPaneRoot_SizeChanged;
        _viewModel.FilteredMediaItems.CollectionChanged += FilteredMediaItems_CollectionChanged;
    }

    public void Dispose()
    {
        _libraryPane.HeaderView.RefreshButton.Click -= Refresh_Click;
        _libraryPane.HeaderView.CancelScanButton.Click -= CancelScan_Click;
        _libraryPane.HeaderView.ViewModeToggleButton.Click -= ToggleViewMode_Click;
        _libraryPane.HeaderView.FolderGroupToggleButton.Click -= ToggleFolderGroups_Click;
        _libraryPane.HeaderView.MediaTypeFilterButton.Click -= MediaTypeFilter_Click;
        _libraryPane.HeaderView.SortButton.Click -= Sort_Click;
        SearchBox.TextChanged -= SearchBox_TextChanged;
        SearchBox.KeyDown -= SearchBox_KeyDown;
        _libraryPane.FilterBarView.TagRemoveRequested -= FilterBarView_TagRemoveRequested;

        ListView.ContainerContentChanging -= Media_ContainerContentChanging;
        ListView.SelectionChanged -= Media_SelectionChanged;
        ListView.DragItemsStarting -= MediaView_DragItemsStarting;
        ListView.RemoveHandler(UIElement.ContextRequestedEvent, _contextRequestedHandler);
        ListView.RemoveHandler(UIElement.RightTappedEvent, _rightTappedHandler);
        ListView.RemoveHandler(UIElement.KeyDownEvent, _keyDownHandler);
        _listContextMenuAccelerator.Invoked -= MediaViewContextMenuAccelerator_Invoked;
        _listShiftF10ContextMenuAccelerator.Invoked -= MediaViewContextMenuAccelerator_Invoked;
        _ = ListView.KeyboardAccelerators.Remove(_listContextMenuAccelerator);
        _ = ListView.KeyboardAccelerators.Remove(_listShiftF10ContextMenuAccelerator);
        ListView.PointerWheelChanged -= MediaLibraryView_PointerWheelChanged;
        ListView.RemoveHandler(UIElement.PointerPressedEvent, _pointerPressedHandler);
        ListView.RemoveHandler(UIElement.PointerMovedEvent, _pointerMovedHandler);
        ListView.RemoveHandler(UIElement.PointerReleasedEvent, _pointerReleasedHandler);
        ListView.PointerCaptureLost -= MediaView_PointerCaptureLost;
        ListView.PointerCanceled -= MediaView_PointerCanceled;

        GridView.ContainerContentChanging -= Media_ContainerContentChanging;
        GridView.SelectionChanged -= Media_SelectionChanged;
        GridView.DragItemsStarting -= MediaView_DragItemsStarting;
        GridView.RemoveHandler(UIElement.ContextRequestedEvent, _contextRequestedHandler);
        GridView.RemoveHandler(UIElement.RightTappedEvent, _rightTappedHandler);
        GridView.RemoveHandler(UIElement.KeyDownEvent, _keyDownHandler);
        _gridContextMenuAccelerator.Invoked -= MediaViewContextMenuAccelerator_Invoked;
        _gridShiftF10ContextMenuAccelerator.Invoked -= MediaViewContextMenuAccelerator_Invoked;
        _ = GridView.KeyboardAccelerators.Remove(_gridContextMenuAccelerator);
        _ = GridView.KeyboardAccelerators.Remove(_gridShiftF10ContextMenuAccelerator);
        GridView.Loaded -= GridView_Loaded;
        GridView.SizeChanged -= GridView_SizeChanged;
        GridView.PointerWheelChanged -= MediaLibraryView_PointerWheelChanged;
        GridView.RemoveHandler(UIElement.PointerPressedEvent, _pointerPressedHandler);
        GridView.RemoveHandler(UIElement.PointerMovedEvent, _pointerMovedHandler);
        GridView.RemoveHandler(UIElement.PointerReleasedEvent, _pointerReleasedHandler);
        GridView.PointerCaptureLost -= MediaView_PointerCaptureLost;
        GridView.PointerCanceled -= MediaView_PointerCanceled;

        _libraryPane.DropTargetBorder.DragOver -= LibraryPanel_DragOver;
        _libraryPane.DropTargetBorder.Drop -= LibraryPanel_Drop;
        _libraryPane.PaneRoot.SizeChanged -= LibraryPaneRoot_SizeChanged;
        _viewModel.FilteredMediaItems.CollectionChanged -= FilteredMediaItems_CollectionChanged;
        SetListViewScrollViewer(null);
        SetGridViewScrollViewer(null);
        _dragSelectionAutoScrollTimer.Tick -= DragSelectionAutoScrollTimer_Tick;
        _dragSelectionAutoScrollTimer.Stop();
        _visibleThumbnailLoadTimer.Tick -= VisibleThumbnailLoadTimer_Tick;
        _visibleThumbnailLoadTimer.Stop();
        EndDragSelection();
    }

    public async Task InitializeAsync()
    {
        AppTraceLogger.Log("MediaBrowser", "InitializeAsync start.");
        await _viewModel.InitializeAsync();
        RebuildLoadedMediaIndex();
        UpdateMediaItemSize();
        ConfigureListViewScrolling();
        ConfigureGridViewScrolling();
        AppTraceLogger.Log("MediaBrowser", $"InitializeAsync completed. LoadedIndex={_selectionSync.LoadedCount}, ViewItemCount={_viewModel.FilteredMediaItems.Count}.");
    }

    public Task AddFolderAsync()
    {
        return ExecuteUiActionAsync(async () =>
        {
            AppTraceLogger.Log("MediaBrowser", "AddFolderAsync picker opening.");
            var window = App.MainWindow;
            if (window == null)
            {
                AppTraceLogger.Log("MediaBrowser", "AddFolderAsync skipped. MainWindowMissing=True.");
                return;
            }

            var folder = await PickerHelpers.PickFolderAsync(window);
            if (string.IsNullOrWhiteSpace(folder))
            {
                AppTraceLogger.Log("MediaBrowser", "AddFolderAsync canceled by user.");
                return;
            }

            AppTraceLogger.Log("MediaBrowser", $"AddFolderAsync picked folder. Path='{folder}'.");
            await _viewModel.AddFolderAsync(folder);
            await _updateWatchedFoldersAsync(new[] { folder }, false);
            AppTraceLogger.Log("MediaBrowser", $"AddFolderAsync completed. Path='{folder}'.");
        }, "导入文件夹失败", _showInfoAsync);
    }

    public Task AddFilesAsync()
    {
        return ExecuteUiActionAsync(async () =>
        {
            AppTraceLogger.Log("MediaBrowser", "AddFilesAsync picker opening.");
            var window = App.MainWindow;
            if (window == null)
            {
                AppTraceLogger.Log("MediaBrowser", "AddFilesAsync skipped. MainWindowMissing=True.");
                return;
            }

            var paths = await PickerHelpers.PickFilesAsync(window);
            if (paths.Count == 0)
            {
                AppTraceLogger.Log("MediaBrowser", "AddFilesAsync canceled by user.");
                return;
            }

            AppTraceLogger.Log("MediaBrowser", $"AddFilesAsync picked files. Count={paths.Count}.");
            await _viewModel.AddFilesAsync(paths);
            AppTraceLogger.Log("MediaBrowser", $"AddFilesAsync completed. Count={paths.Count}.");
        }, "导入文件失败", _showInfoAsync);
    }

    public IReadOnlyList<MediaItemViewModel> GetSelectedItems()
    {
        return _selectionCoordinator.GetSelectedItems();
    }

    public void SyncSelectionFromViewModel(MediaItemViewModel? selectedMedia)
    {
        var currentSelection = ResolveLoadedSelection(selectedMedia);
        if (selectedMedia != null && currentSelection == null)
        {
            AppTraceLogger.LogSampled(
                "MediaBrowser",
                "selection-map-miss",
                $"SyncSelectionFromViewModel could not map selected media '{selectedMedia.Id}' to a loaded instance. LoadedCount={_selectionSync.LoadedCount}, ViewItemCount={_viewModel.FilteredMediaItems.Count}.",
                TimeSpan.FromSeconds(2));
        }

        if (IsSelectionAlreadyApplied(selectedMedia, currentSelection))
        {
            AppTraceLogger.LogSampled(
                "MediaBrowser",
                "selection-already-applied",
                $"SyncSelectionFromViewModel no-op. SelectedId='{selectedMedia?.Id ?? "<null>"}', CurrentSelectionId='{currentSelection?.Id ?? "<null>"}', LoadedCount={_selectionSync.LoadedCount}, ViewItemCount={_viewModel.FilteredMediaItems.Count}, ListSelectedCount={ListView.SelectedItems.Count}, GridSelectedCount={GridView.SelectedItems.Count}.",
                TimeSpan.FromSeconds(2));
            if (selectedMedia != null)
            {
                _selectionCoordinator.MarkViewModelSelectionSynced(selectedMedia.Id);
            }

            UpdateNowPlayingState(selectedMedia);
            return;
        }

        var previousSelectedIds = _selectionCoordinator.Snapshot();
        _isSyncingSelection = true;
        try
        {
            if (selectedMedia == null)
            {
                _selectionCoordinator.Clear();
            }
            else if (!IsSelectionTracked(selectedMedia.Id))
            {
                _selectionCoordinator.ReplaceSelection(new[] { selectedMedia.Id });
            }

            if (selectedMedia != null)
            {
                _selectionCoordinator.MarkViewModelSelectionSynced(selectedMedia.Id);
            }

            if (selectedMedia == null || currentSelection != null)
            {
                ApplySelectionToView(ListView, currentSelection);
                ApplySelectionToView(GridView, currentSelection);
            }
            else
            {
                AppTraceLogger.LogSampled(
                    "MediaBrowser",
                    "selection-map-miss-preserved-view",
                    $"SyncSelectionFromViewModel preserved existing view selection because selected media '{selectedMedia.Id}' is not loaded. LoadedCount={_selectionSync.LoadedCount}, ViewItemCount={_viewModel.FilteredMediaItems.Count}, ListSelectedCount={ListView.SelectedItems.Count}, GridSelectedCount={GridView.SelectedItems.Count}.",
                    TimeSpan.FromSeconds(2));
            }

            UpdateSelectedStateFlags(previousSelectedIds);
            UpdateNowPlayingState(selectedMedia);
        }
        finally
        {
            _isSyncingSelection = false;
        }
    }

    public IDisposable PreserveSelectionDuringCollectionMutation(MediaItemViewModel? selectedMedia)
    {
        var preserveId = selectedMedia?.Id;
        _selectionCoordinator.BeginMutation(preserveId);

        AppTraceLogger.Log(
            "MediaBrowser",
            $"Selection mutation scope entered. Depth={_selectionCoordinator.MutationDepth}, PreserveId='{preserveId ?? "<null>"}', CurrentSelectedId='{_viewModel.SelectedMedia?.Id ?? "<null>"}'.");
        return new SelectionMutationScope(this);
    }

    public void SuppressTransientEmptySelectionFor(MediaItemViewModel? selectedMedia, string reason)
    {
        if (selectedMedia == null || !IsMediaInFilteredItems(selectedMedia.Id))
        {
            return;
        }

        _selectionCoordinator.ArmPostMutationSuppression(selectedMedia.Id, PostMutationSelectionSuppressionWindow);
        AppTraceLogger.Log(
            "MediaBrowser",
            $"Post-mutation empty selection suppression armed. Reason={reason}, PreserveId='{selectedMedia.Id}', ListSelectedCount={ListView.SelectedItems.Count}, GridSelectedCount={GridView.SelectedItems.Count}.");
        SyncSelectionFromViewModel(selectedMedia);
    }

    public void RevealSelectedMedia(MediaItemViewModel media)
    {
        RequestMediaScroll(media, "RevealSelectedMedia", "reveal-selected-media", "reveal-selected-media-applied", "reveal-selection-miss");
    }

    public void FocusMediaInLibrary(MediaItemViewModel media)
    {
        RequestMediaScroll(media, "FocusMediaInLibrary", "focus-media", "focus-media-applied", "focus-media-miss");
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

    private void CancelScan_Click(object sender, RoutedEventArgs e)
    {
        AppTraceLogger.Log("MediaBrowser", "CancelScan button clicked.");
        _viewModel.CancelScan();
    }

    private void ToggleViewMode_Click(object sender, RoutedEventArgs e)
    {
        EndDragSelection();
        _viewModel.ViewMode = _viewModel.ViewMode == MediaViewMode.List
            ? MediaViewMode.Grid
            : MediaViewMode.List;
        UpdateMediaItemSize();
        if (_viewModel.ViewMode == MediaViewMode.Grid)
        {
            ConfigureGridViewScrolling();
            _page.DispatcherQueue.TryEnqueue(() =>
            {
                ConfigureListViewScrolling();
                ConfigureGridViewScrolling();
                SynchronizeSelectionToActiveView();
            });
            return;
        }

        _page.DispatcherQueue.TryEnqueue(() =>
        {
            ConfigureListViewScrolling();
            SynchronizeSelectionToActiveView();
        });
    }

    private void ToggleFolderGroups_Click(object sender, RoutedEventArgs e)
    {
        EndDragSelection();
        _viewModel.ToggleAllMediaFolderGroups();
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement target)
        {
            return;
        }

        BuildSortFlyout().ShowAt(target);
    }

    private void MediaTypeFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement target)
        {
            return;
        }

        BuildMediaTypeFilterFlyout().ShowAt(target);
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
                _viewModel.QueueUserFilterRefresh(false);
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

        if (_dragSelection.IsPending && !_dragSelection.IsSelecting)
        {
            RestoreSelectionSnapshot(_dragSelection.OriginalIds, _dragSelection.OriginalPrimaryId);
            return;
        }

        var selectedIds = listViewBase.SelectedItems
            .OfType<MediaItemViewModel>()
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (TrySuppressSelectionDuringCollectionMutation(listViewBase, selectedIds))
        {
            return;
        }

        if (TrySuppressTransientEmptySelection(listViewBase, selectedIds))
        {
            return;
        }

        if (selectedIds.Count == 0
            && _selectionCoordinator.HasSelection
            && _selectionCoordinator.SelectedItemIds
                .Select(id => _selectionSync.TryGet(id, out var item) ? item : null)
                .OfType<MediaItemViewModel>()
                .All(item => !_viewModel.IsMediaVisibleInLibrary(item)))
        {
            return;
        }

        var selectedMedia = ResolvePrimarySelection(listViewBase, e, selectedIds);
        var previousSelectedIds = _selectionCoordinator.Snapshot();
        if (selectedMedia != null
            && ReferenceEquals(listViewBase, GetActiveMediaView())
            && IsMediaVisible(listViewBase, selectedMedia))
        {
            StoreRecentUserVisibleSelection(selectedMedia.Id);
        }

        _selectionCoordinator.ReplaceSelection(selectedIds);
        _isSyncingSelection = true;
        try
        {
            ApplySelectionToView(ReferenceEquals(listViewBase, ListView) ? GridView : ListView, selectedMedia);
            UpdateSelectedStateFlags(previousSelectedIds);
            UpdateNowPlayingState(selectedMedia);
        }
        finally
        {
            _isSyncingSelection = false;
        }

        _viewModel.SelectedMedia = selectedMedia;
    }

    private void MediaView_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListViewBase listViewBase
            || !ReferenceEquals(listViewBase, GetActiveMediaView()))
        {
            return;
        }

        var pointerPoint = e.GetCurrentPoint(listViewBase);
        if (pointerPoint.Properties.IsLeftButtonPressed
            && TryGetMediaFromElement(e.OriginalSource as DependencyObject, out var pressedMedia)
            && pressedMedia != null
            && IsMediaVisible(listViewBase, pressedMedia))
        {
            StoreRecentUserVisibleSelection(pressedMedia.Id);
        }

        if (!CanBeginDragSelection(listViewBase, e))
        {
            return;
        }

        BeginDragSelection(listViewBase, e);
        e.Handled = true;
    }

    private void MediaView_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, _dragSelection.View) || (!_dragSelection.IsPending && !_dragSelection.IsSelecting))
        {
            return;
        }

        var rawPoint = e.GetCurrentPoint(BrowserRoot).Position;
        _dragSelection.CurrentPointerPoint = rawPoint;
        var currentPoint = ClampToBrowserBounds(rawPoint);
        if (_dragSelection.IsPending)
        {
            if (!HasExceededDragSelectionThreshold(currentPoint))
            {
                return;
            }

            _dragSelection.IsSelecting = true;
            _dragSelection.IsPending = false;
        }

        UpdateDragSelectionAutoScroll(rawPoint);
        UpdateDragSelection(currentPoint);
        e.Handled = true;
    }

    private void MediaView_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (TryShowPointerReleasedContextMenu(sender, e))
        {
            return;
        }

        if (!ReferenceEquals(sender, _dragSelection.View) || (!_dragSelection.IsPending && !_dragSelection.IsSelecting))
        {
            return;
        }

        var rawPoint = e.GetCurrentPoint(BrowserRoot).Position;
        _dragSelection.CurrentPointerPoint = rawPoint;
        var currentPoint = ClampToBrowserBounds(rawPoint);
        if (_dragSelection.IsSelecting)
        {
            StopDragSelectionAutoScroll();
            UpdateDragSelection(currentPoint);
        }
        else if (_dragSelection.StartedOnItem)
        {
            ApplyPressedItemSelection();
        }
        else if (!_dragSelection.IsAdditive)
        {
            ApplySelectionSnapshot(Array.Empty<string>(), null);
        }

        EndDragSelection();
        e.Handled = true;
    }

    private void MediaView_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        EndDragSelection();
    }

    private void MediaView_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        EndDragSelection();
    }

    private void MediaView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (WasMediaContextMenuRecentlyOpened())
        {
            e.Handled = true;
            return;
        }

        if (sender is not FrameworkElement target)
        {
            return;
        }

        if (sender is ListViewBase listViewBase
            && TryGetMediaFromElement(e.OriginalSource as DependencyObject, out var rightTappedMedia)
            && rightTappedMedia != null)
        {
            EnsureRightTappedSelection(listViewBase, rightTappedMedia);
        }

        var selected = ResolveContextMenuSelection(e.OriginalSource as DependencyObject);
        if (selected.Count == 0)
        {
            AppTraceLogger.LogSampled(
                "MediaBrowser",
                "right-tap-empty-selection",
                $"MediaView_RightTapped skipped because no media selection could be resolved. Sender={sender.GetType().Name}, LoadedCount={_selectionSync.LoadedCount}, ViewItemCount={_viewModel.FilteredMediaItems.Count}.",
                TimeSpan.FromSeconds(2));
            return;
        }

        _contextMenuCoordinator.ShowForTarget(target, e.GetPosition(target), selected);
        MarkMediaContextMenuOpened();
        e.Handled = true;
    }

    private bool TryShowPointerReleasedContextMenu(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListViewBase listViewBase
            || sender is not FrameworkElement target
            || !ReferenceEquals(listViewBase, GetActiveMediaView()))
        {
            return false;
        }

        var pointerPoint = e.GetCurrentPoint(target);
        if (pointerPoint.Properties.PointerUpdateKind != PointerUpdateKind.RightButtonReleased)
        {
            return false;
        }

        if (TryGetMediaFromElement(e.OriginalSource as DependencyObject, out var rightClickedMedia)
            && rightClickedMedia != null)
        {
            EnsureRightTappedSelection(listViewBase, rightClickedMedia);
        }

        var selected = ResolveContextMenuSelection(e.OriginalSource as DependencyObject);
        if (selected.Count == 0)
        {
            AppTraceLogger.LogSampled(
                "MediaBrowser",
                "right-click-release-empty-selection",
                $"PointerReleased context menu skipped because no media selection could be resolved. Sender={sender.GetType().Name}, LoadedCount={_selectionSync.LoadedCount}, ViewItemCount={_viewModel.FilteredMediaItems.Count}.",
                TimeSpan.FromSeconds(2));
            return false;
        }

        _contextMenuCoordinator.ShowForTarget(target, pointerPoint.Position, selected);
        MarkMediaContextMenuOpened();
        e.Handled = true;
        AppTraceLogger.Log(
            "MediaBrowser",
            $"PointerReleased context menu opened. Sender={sender.GetType().Name}, SelectionCount={selected.Count}, PrimaryId='{selected[0].Id}'.");
        return true;
    }

    private void MediaView_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (WasMediaContextMenuRecentlyOpened())
        {
            e.Handled = true;
            return;
        }

        if (sender is not ListViewBase listViewBase
            || sender is not FrameworkElement target
            || !ReferenceEquals(listViewBase, GetActiveMediaView()))
        {
            return;
        }

        if (TryGetMediaFromElement(e.OriginalSource as DependencyObject, out var requestedMedia)
            && requestedMedia != null)
        {
            EnsureRightTappedSelection(listViewBase, requestedMedia);
        }

        var selected = ResolveContextMenuSelection(e.OriginalSource as DependencyObject);
        if (selected.Count == 0)
        {
            AppTraceLogger.LogSampled(
                "MediaBrowser",
                "context-request-empty-selection",
                $"ContextRequested skipped because no media selection could be resolved. Sender={sender.GetType().Name}, LoadedCount={_selectionSync.LoadedCount}, ViewItemCount={_viewModel.FilteredMediaItems.Count}.",
                TimeSpan.FromSeconds(2));
            return;
        }

        if (!e.TryGetPosition(target, out var position))
        {
            position = GetKeyboardContextMenuPoint(listViewBase, target, selected[0]);
        }

        _contextMenuCoordinator.ShowForTarget(target, position, selected);
        MarkMediaContextMenuOpened();
        e.Handled = true;
        AppTraceLogger.Log(
            "MediaBrowser",
            $"ContextRequested menu opened. Sender={sender.GetType().Name}, SelectionCount={selected.Count}, PrimaryId='{selected[0].Id}'.");
    }

    private void MarkMediaContextMenuOpened()
    {
        _lastMediaContextMenuOpenedTicks = DateTime.UtcNow.Ticks;
    }

    private bool WasMediaContextMenuRecentlyOpened()
    {
        var ticks = _lastMediaContextMenuOpenedTicks;
        if (ticks <= 0)
        {
            return false;
        }

        return DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) <= ContextMenuDuplicateSuppression;
    }

    private void MediaView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!IsKeyboardContextMenuRequest(e)
            || sender is not ListViewBase listViewBase
            || sender is not FrameworkElement target)
        {
            return;
        }

        var selected = GetSelectedItems();
        if (selected.Count == 0 && listViewBase.SelectedItem is MediaItemViewModel selectedItem)
        {
            selected = new[] { selectedItem };
        }

        if (selected.Count == 0)
        {
            AppTraceLogger.LogSampled(
                "MediaBrowser",
                "keyboard-context-empty-selection",
                $"MediaView_KeyDown skipped because no media selection could be resolved. Sender={sender.GetType().Name}, LoadedCount={_selectionSync.LoadedCount}, ViewItemCount={_viewModel.FilteredMediaItems.Count}.",
                TimeSpan.FromSeconds(2));
            return;
        }

        _contextMenuCoordinator.ShowForTarget(target, GetKeyboardContextMenuPoint(listViewBase, target, selected[0]), selected);
        e.Handled = true;
        AppTraceLogger.Log(
            "MediaBrowser",
            $"Keyboard context menu opened. Sender={sender.GetType().Name}, SelectionCount={selected.Count}, PrimaryId='{selected[0].Id}'.");
    }

    private static bool IsKeyboardContextMenuRequest(KeyRoutedEventArgs e)
    {
        return e.Key == Windows.System.VirtualKey.Application
            || (e.Key == Windows.System.VirtualKey.F10 && IsShiftKeyDown());
    }

    private static Point GetKeyboardContextMenuPoint(
        ListViewBase listViewBase,
        FrameworkElement target,
        MediaItemViewModel primary)
    {
        if (listViewBase.ContainerFromItem(primary) is FrameworkElement container)
        {
            try
            {
                return container
                    .TransformToVisual(target)
                    .TransformPoint(new Point(
                        Math.Max(0, container.ActualWidth / 2),
                        Math.Max(0, container.ActualHeight / 2)));
            }
            catch
            {
            }
        }

        return new Point(
            Math.Max(0, target.ActualWidth / 2),
            Math.Max(0, target.ActualHeight / 2));
    }

    private void MediaViewContextMenuAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        var listViewBase = args.Element == GridView || GridView.Visibility == Visibility.Visible
            ? (ListViewBase)GridView
            : ListView;
        if (ShowKeyboardContextMenu(listViewBase))
        {
            args.Handled = true;
        }
    }

    private bool ShowKeyboardContextMenu(ListViewBase listViewBase)
    {
        if (listViewBase is not FrameworkElement target)
        {
            return false;
        }

        var selected = GetSelectedItems();
        if (selected.Count == 0 && listViewBase.SelectedItem is MediaItemViewModel selectedItem)
        {
            selected = new[] { selectedItem };
        }

        if (selected.Count == 0)
        {
            AppTraceLogger.LogSampled(
                "MediaBrowser",
                "keyboard-context-empty-selection",
                $"ShowKeyboardContextMenu skipped because no media selection could be resolved. Sender={listViewBase.GetType().Name}, LoadedCount={_selectionSync.LoadedCount}, ViewItemCount={_viewModel.FilteredMediaItems.Count}.",
                TimeSpan.FromSeconds(2));
            return false;
        }

        _contextMenuCoordinator.ShowForTarget(target, GetKeyboardContextMenuPoint(listViewBase, target, selected[0]), selected);
        AppTraceLogger.Log(
            "MediaBrowser",
            $"Keyboard context menu opened. Sender={listViewBase.GetType().Name}, SelectionCount={selected.Count}, PrimaryId='{selected[0].Id}'.");
        return true;
    }

    private static KeyboardAccelerator CreateContextMenuKeyboardAccelerator()
    {
        return new KeyboardAccelerator
        {
            Key = Windows.System.VirtualKey.Application
        };
    }

    private static KeyboardAccelerator CreateShiftF10ContextMenuKeyboardAccelerator()
    {
        return new KeyboardAccelerator
        {
            Key = Windows.System.VirtualKey.F10,
            Modifiers = Windows.System.VirtualKeyModifiers.Shift
        };
    }

    private void Media_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not MediaItemViewModel media)
        {
            AppTraceLogger.LogSampled(
                "MediaBrowser",
                $"container-non-media:{sender.GetType().Name}",
                $"Container content change received non-media item. Sender={sender.GetType().Name}, ItemType='{args.Item?.GetType().FullName ?? "<null>"}', Phase={args.Phase}, InRecycleQueue={args.InRecycleQueue}, LoadedCount={_viewModel.FilteredMediaItems.Count}, ListItems={ListView.Items.Count}, GridItems={GridView.Items.Count}.",
                TimeSpan.FromSeconds(3));
            return;
        }

        AppTraceLogger.LogSampled(
            "MediaBrowser",
            $"container-media-realized:{sender.GetType().Name}",
            $"Media container realized. Sender={sender.GetType().Name}, MediaId='{media.Id}', File='{media.FileName}', Phase={args.Phase}, InRecycleQueue={args.InRecycleQueue}, LoadedCount={_viewModel.FilteredMediaItems.Count}, ListItems={ListView.Items.Count}, GridItems={GridView.Items.Count}, ThumbnailLoaded={media.Thumbnail != null}.",
            TimeSpan.FromSeconds(2));

        if (sender == ListView)
        {
            ApplyListItemSize(args.ItemContainer as SelectorItem);
        }

        if (args.InRecycleQueue)
        {
            AppTraceLogger.LogSampled(
                "MediaBrowser",
                $"container-media-recycle:{sender.GetType().Name}",
                $"Media container entered recycle queue. Sender={sender.GetType().Name}, MediaId='{media.Id}', File='{media.FileName}', LoadedCount={_viewModel.FilteredMediaItems.Count}, ListItems={ListView.Items.Count}, GridItems={GridView.Items.Count}.",
                TimeSpan.FromSeconds(3));
            return;
        }

        if (args.Phase == 0)
        {
            args.RegisterUpdateCallback(Media_ContainerContentChanging);
            return;
        }

        if (media.Thumbnail != null)
        {
            return;
        }

        if (_isMediaViewScrolling)
        {
            ScheduleVisibleThumbnailLoad();
            return;
        }

        _ = _viewModel.EnsureThumbnailAsync(media);
    }

    private void MediaView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var draggedItems = e.Items
            .OfType<MediaItemViewModel>()
            .Select(item => item.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (draggedItems.Count == 0)
        {
            draggedItems = GetSelectedItems()
                .Select(item => item.Id)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        if (draggedItems.Count == 0)
        {
            e.Cancel = true;
            return;
        }

        e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
        e.Data.Properties.Add(InternalDragData.MediaDragMarkerProperty, true);
        e.Data.Properties.Add(InternalDragData.MediaIdsProperty, draggedItems);
        if (_viewModel.SelectedPlaylist != null)
        {
            e.Data.Properties.Add(InternalDragData.SourcePlaylistIdProperty, _viewModel.SelectedPlaylist.Id);
        }
    }

    private void FilteredMediaItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_dragSelection.IsPending || _dragSelection.IsSelecting)
        {
            EndDragSelection();
        }

        if (e.Action == NotifyCollectionChangedAction.Reset
            || (e.NewItems?.Count ?? 0) > 1
            || (e.OldItems?.Count ?? 0) > 1)
        {
            AppTraceLogger.LogSampled(
                "MediaBrowser",
                "filtered-items-changed",
                $"FilteredMediaItems changed. Action={e.Action}, NewCount={e.NewItems?.Count ?? 0}, OldCount={e.OldItems?.Count ?? 0}, TotalLoaded={_viewModel.FilteredMediaItems.Count}.",
                TimeSpan.FromSeconds(1));
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            RebuildLoadedMediaIndex();
            ScheduleSelectedMediaVisibilityVerificationAfterItemsChanged("filtered-items-reset");
            return;
        }

        var removedCount = 0;
        if (e.OldItems != null)
        {
            foreach (var item in e.OldItems.OfType<MediaItemViewModel>())
            {
                _selectionSync.Remove(item);
                removedCount++;
            }
        }

        var addedCount = 0;
        if (e.NewItems != null)
        {
            foreach (var item in e.NewItems.OfType<MediaItemViewModel>())
            {
                _selectionSync.Add(item);
                item.IsSelected = IsSelectionTracked(item.Id);
                addedCount++;
            }
        }

        PruneTrackedSelectionToLoadedItems("filtered-items-changed");
        UpdateNowPlayingState(_viewModel.SelectedMedia);
        TryApplyPendingMediaScroll();
        if (addedCount > 0 || removedCount > 0)
        {
            ScheduleSelectedMediaVisibilityVerificationAfterItemsChanged(
                removedCount > 0 ? "filtered-items-removed-or-replaced" : "filtered-items-added");
        }
    }

    private bool TrySuppressSelectionDuringCollectionMutation(ListViewBase listViewBase, IReadOnlySet<string> selectedIds)
    {
        var selectedMedia = _viewModel.SelectedMedia;
        if (selectedMedia == null)
        {
            return false;
        }

        if (selectedIds.Contains(selectedMedia.Id))
        {
            return false;
        }

        if (IsActiveSelectionMutationPreserve(selectedMedia.Id))
        {
            return SuppressTransientSelectionChange(listViewBase, selectedIds, selectedMedia, "active selection mutation");
        }

        if (selectedIds.Count == 0
            && IsSelectionTracked(selectedMedia.Id)
            && IsPostMutationSelectionSuppressionActive(selectedMedia.Id))
        {
            return SuppressTransientSelectionChange(listViewBase, selectedIds, selectedMedia, "post-mutation selection settle");
        }

        return false;
    }

    private bool TrySuppressTransientEmptySelection(ListViewBase listViewBase, IReadOnlySet<string> selectedIds)
    {
        if (selectedIds.Count != 0 || _viewModel.SelectedMedia is not { } selectedMedia)
        {
            return false;
        }

        var activeView = GetActiveMediaView();
        var otherView = ReferenceEquals(listViewBase, ListView) ? (ListViewBase)GridView : ListView;
        var currentSelectionIsLogical = IsSelectionTracked(selectedMedia.Id);
        var recentLogicalSelection = _selectionCoordinator.IsRecentViewModelSelectionSync(
            selectedMedia.Id,
            ViewModelSelectionSettleWindow);
        var otherViewStillHasSelection = otherView.SelectedItems
            .OfType<MediaItemViewModel>()
            .Any(item => string.Equals(item.Id, selectedMedia.Id, StringComparison.Ordinal));
        var senderIsInactiveView = !ReferenceEquals(listViewBase, activeView);
        var selectedMediaIsNotVisible = !IsMediaInFilteredItems(selectedMedia.Id);

        if (!currentSelectionIsLogical
            && !recentLogicalSelection
            && !otherViewStillHasSelection
            && !senderIsInactiveView
            && !selectedMediaIsNotVisible)
        {
            return false;
        }

        AppTraceLogger.Log(
            "MediaBrowser",
            $"Suppressed transient empty SelectionChanged. SelectedId='{selectedMedia.Id}', Sender={listViewBase.GetType().Name}, SenderIsInactive={senderIsInactiveView}, LogicalSelection={currentSelectionIsLogical}, RecentLogicalSelection={recentLogicalSelection}, OtherViewStillSelected={otherViewStillHasSelection}, SelectedMediaVisible={!selectedMediaIsNotVisible}, ListSelectedCount={ListView.SelectedItems.Count}, GridSelectedCount={GridView.SelectedItems.Count}, ViewItemCount={_viewModel.FilteredMediaItems.Count}.");
        SyncSelectionFromViewModel(selectedMedia);
        return true;
    }

    private bool IsActiveSelectionMutationPreserve(string mediaId)
    {
        return _selectionCoordinator.IsActiveMutationPreserve(mediaId);
    }

    private bool IsPostMutationSelectionSuppressionActive(string mediaId)
    {
        return _selectionCoordinator.IsPostMutationSuppressionActive(mediaId);
    }

    private bool SuppressTransientSelectionChange(
        ListViewBase listViewBase,
        IReadOnlySet<string> selectedIds,
        MediaItemViewModel selectedMedia,
        string reason)
    {
        if (!IsMediaInFilteredItems(selectedMedia.Id))
        {
            return false;
        }

        _selectionCoordinator.MarkMutationSuppressedEmptySelection();
        AppTraceLogger.Log(
            "MediaBrowser",
            $"Suppressed transient SelectionChanged during {reason}. SelectedId='{selectedMedia.Id}', IncomingSelectedCount={selectedIds.Count}, Sender={listViewBase.GetType().Name}, Depth={_selectionCoordinator.MutationDepth}, ListSelectedCount={ListView.SelectedItems.Count}, GridSelectedCount={GridView.SelectedItems.Count}, ViewItemCount={_viewModel.FilteredMediaItems.Count}.");
        SyncSelectionFromViewModel(selectedMedia);
        return true;
    }

    private bool IsMediaInFilteredItems(string mediaId)
    {
        return _selectionSync.Contains(mediaId);
    }

    private bool IsSelectionTracked(string mediaId)
    {
        return _selectionCoordinator.Contains(mediaId);
    }

    private void EndSelectionMutationScope()
    {
        var result = _selectionCoordinator.EndMutation();
        if (!result.HadActiveMutation)
        {
            AppTraceLogger.Log("MediaBrowser", "Selection mutation scope dispose ignored because depth is already zero.");
            return;
        }

        var preserveId = result.PreserveId;
        var suppressedEmptySelection = result.SuppressedEmptySelection;
        AppTraceLogger.Log(
            "MediaBrowser",
            $"Selection mutation scope exited. Depth={result.Depth}, PreserveId='{preserveId ?? "<null>"}', SuppressedEmptySelection={suppressedEmptySelection}, CurrentSelectedId='{_viewModel.SelectedMedia?.Id ?? "<null>"}'.");

        if (result.Depth > 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(preserveId)
            || _viewModel.SelectedMedia == null
            || !string.Equals(_viewModel.SelectedMedia.Id, preserveId, StringComparison.Ordinal)
            || !IsMediaInFilteredItems(preserveId))
        {
            return;
        }

        _selectionCoordinator.ArmPostMutationSuppression(preserveId, PostMutationSelectionSuppressionWindow);

        if (!_page.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                var selectedMedia = _viewModel.SelectedMedia;
                if (selectedMedia == null
                    || !string.Equals(selectedMedia.Id, preserveId, StringComparison.Ordinal)
                    || !IsMediaInFilteredItems(preserveId))
                {
                    return;
                }

                AppTraceLogger.Log(
                    "MediaBrowser",
                    $"Selection mutation scope applying deferred selection sync. PreserveId='{preserveId}', SuppressedEmptySelection={suppressedEmptySelection}, ListSelectedCount={ListView.SelectedItems.Count}, GridSelectedCount={GridView.SelectedItems.Count}.");
                SyncSelectionFromViewModel(selectedMedia);
            }))
        {
            AppTraceLogger.Log("MediaBrowser", $"Selection mutation scope failed to enqueue deferred selection sync. PreserveId='{preserveId}'.");
        }
    }

    private sealed class SelectionMutationScope : IDisposable
    {
        private MediaBrowserController? _owner;

        public SelectionMutationScope(MediaBrowserController owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.EndSelectionMutationScope();
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
            ConfigureListViewScrolling();
            ConfigureGridViewScrolling();
        }
    }

    private void LibraryPanel_DragOver(object sender, DragEventArgs e)
    {
        if (TryResolveInternalDragMediaIds(e.DataView, out _))
        {
            if (_viewModel.SelectedPlaylist != null && IsDragFromCurrentPlaylist(e.DataView))
            {
                e.AcceptedOperation = DataPackageOperation.Move;
                e.DragUIOverride.Caption = $"从“{_viewModel.SelectedPlaylist.Name}”移除";
            }
            else
            {
                e.AcceptedOperation = DataPackageOperation.None;
            }

            e.Handled = true;
            return;
        }

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
        if (TryResolveInternalDragMediaIds(e.DataView, out var mediaIds))
        {
            AppTraceLogger.Log("MediaBrowser", $"LibraryPanel_Drop internal drag. MediaCount={mediaIds.Count}, SelectedPlaylist='{_viewModel.SelectedPlaylist?.Id ?? "<null>"}'.");
            if (_viewModel.SelectedPlaylist == null || !IsDragFromCurrentPlaylist(e.DataView))
            {
                return;
            }

            var items = _viewModel.FilteredMediaItems
                .Where(item => mediaIds.Contains(item.Id))
                .ToList();
            if (items.Count == 0)
            {
                return;
            }

            await _viewModel.RemoveMediaFromSelectedPlaylistAsync(items);
            AppTraceLogger.Log("MediaBrowser", $"LibraryPanel_Drop removed from current playlist. MediaCount={items.Count}.");
            e.Handled = true;
            return;
        }

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
                AppTraceLogger.Log("MediaBrowser", "LibraryPanel_Drop skipped. StorageItemPathCount=0.");
                return;
            }

            AppTraceLogger.Log("MediaBrowser", $"LibraryPanel_Drop importing storage items. PathCount={paths.Count}.");
            await _viewModel.AddFilesAsync(paths);
            var folderPaths = paths
                .Where(Directory.Exists)
                .ToList();
            await _updateWatchedFoldersAsync(folderPaths, false);
            AppTraceLogger.Log("MediaBrowser", $"LibraryPanel_Drop import completed. PathCount={paths.Count}.");
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("MediaBrowser", "LibraryPanel_Drop failed.", ex);
            await _showInfoAsync("导入失败", ex.Message);
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

        if (GridView.ItemsPanelRoot is ItemsWrapGrid panel)
        {
            panel.Orientation = Orientation.Horizontal;
            var iconSize = GetCurrentGridItemSize();
            panel.ItemWidth = CalculateAdaptiveGridSlotSize(iconSize);
            panel.ItemHeight = iconSize;
        }
        SetGridViewScrollViewer(MainPageVisualTreeHelpers.FindDescendant<ScrollViewer>(GridView));
    }

    private void ConfigureListViewScrolling()
    {
        SetListViewScrollViewer(MainPageVisualTreeHelpers.FindDescendant<ScrollViewer>(ListView));
    }

    private void SetListViewScrollViewer(ScrollViewer? scrollViewer)
    {
        if (ReferenceEquals(_listViewScrollViewer, scrollViewer))
        {
            return;
        }

        if (_listViewScrollViewer != null)
        {
            _listViewScrollViewer.ViewChanged -= MediaScrollViewer_ViewChanged;
        }

        _listViewScrollViewer = scrollViewer;
        if (_listViewScrollViewer != null)
        {
            _listViewScrollViewer.ViewChanged += MediaScrollViewer_ViewChanged;
        }
    }

    private void SetGridViewScrollViewer(ScrollViewer? scrollViewer)
    {
        if (ReferenceEquals(_gridViewScrollViewer, scrollViewer))
        {
            return;
        }

        if (_gridViewScrollViewer != null)
        {
            _gridViewScrollViewer.ViewChanged -= MediaScrollViewer_ViewChanged;
        }

        _gridViewScrollViewer = scrollViewer;
        if (_gridViewScrollViewer != null)
        {
            _gridViewScrollViewer.ViewChanged += MediaScrollViewer_ViewChanged;
        }
    }

    private async void MediaScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        _isMediaViewScrolling = e.IsIntermediate;
        ScheduleVisibleThumbnailLoad();

        if (sender is not ScrollViewer scrollViewer
            || scrollViewer.ScrollableHeight <= 0
            || scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset > MediaPagePreloadDistance)
        {
            return;
        }

        if (Interlocked.Exchange(ref _isLoadingMoreMedia, 1) == 1)
        {
            return;
        }

        try
        {
            await _viewModel.EnsureNextMediaPageLoadedAsync();
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("MediaBrowser", "Manual incremental media load failed.", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _isLoadingMoreMedia, 0);
        }
    }

    private void ScheduleVisibleThumbnailLoad()
    {
        _visibleThumbnailLoadTimer.Stop();
        _visibleThumbnailLoadTimer.Start();
    }

    private void VisibleThumbnailLoadTimer_Tick(object? sender, object e)
    {
        _visibleThumbnailLoadTimer.Stop();
        LoadRealizedThumbnails(GetActiveMediaView());
    }

    private void LoadRealizedThumbnails(ListViewBase listViewBase)
    {
        foreach (var item in _selectionSync.LoadedItems)
        {
            if (item.Thumbnail != null
                || listViewBase.ContainerFromItem(item) == null)
            {
                continue;
            }

            _ = _viewModel.EnsureThumbnailAsync(item);
        }
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
        foreach (var item in _viewModel.FilteredMediaItems)
        {
            if (ListView.ContainerFromItem(item) is SelectorItem container)
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

    private IReadOnlyList<MediaItemViewModel> ResolveContextMenuSelection(DependencyObject? origin)
    {
        if (!TryGetMediaFromElement(origin, out var media) || media == null)
        {
            return GetSelectedItems();
        }

        var selected = GetSelectedItems();
        if (selected.Any(item => string.Equals(item.Id, media.Id, StringComparison.Ordinal)))
        {
            return selected;
        }

        return new[] { media };
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

    private void BeginDragSelection(ListViewBase listViewBase, PointerRoutedEventArgs e)
    {
        EndDragSelection();

        var startedOnItem = TryGetMediaFromElement(e.OriginalSource as DependencyObject, out var pressedMedia) && pressedMedia != null;

        _dragSelectionController.Begin(
            listViewBase,
            ClampToBrowserBounds(e.GetCurrentPoint(BrowserRoot).Position),
            IsCtrlKeyDown(),
            _selectionCoordinator.Snapshot(),
            _viewModel.SelectedMedia?.Id,
            startedOnItem,
            pressedMedia?.Id);

        HideSelectionRectangle();
        listViewBase.CapturePointer(e.Pointer);
    }

    private bool CanBeginDragSelection(ListViewBase listViewBase, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(listViewBase);
        if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse || !point.Properties.IsLeftButtonPressed)
        {
            return false;
        }

        if (IsShiftKeyDown())
        {
            return false;
        }

        var origin = e.OriginalSource as DependencyObject;
        if (MainPageVisualTreeHelpers.FindAncestor<ScrollBar>(origin) != null)
        {
            return false;
        }

        // Let the native ListView/GridView item interaction handle single-click selection
        // and drag initiation for actual media items. Custom rectangle selection only needs
        // to start from blank space.
        if (TryGetMediaFromElement(origin, out _))
        {
            return false;
        }

        return true;
    }

    private void ApplyPressedItemSelection()
    {
        var selection = _dragSelectionController.BuildPressedItemSelection();
        if (selection == null)
        {
            return;
        }

        ApplySelectionSnapshot(selection.Value.SelectedIds, selection.Value.PrimaryId);
    }

    private void RestoreSelectionSnapshot(IReadOnlySet<string> selectedIds, string? primaryId)
    {
        var selectedMedia = ResolvePrimarySelection(primaryId, selectedIds);
        var previousSelectedIds = _selectionCoordinator.Snapshot();

        _isSyncingSelection = true;
        try
        {
            _selectionCoordinator.ReplaceSelection(selectedIds);
            ApplySelectionToView(ListView, selectedMedia);
            ApplySelectionToView(GridView, selectedMedia);
            UpdateSelectedStateFlags(previousSelectedIds);
        }
        finally
        {
            _isSyncingSelection = false;
        }

        _viewModel.SelectedMedia = selectedMedia;
    }

    private bool HasExceededDragSelectionThreshold(Point currentPoint)
    {
        return _dragSelectionController.HasExceededActivationThreshold(currentPoint);
    }

    private void UpdateDragSelection(Point currentPoint)
    {
        var selectionRect = CreateNormalizedRect(_dragSelection.StartPoint, currentPoint);
        ShowSelectionRectangle(selectionRect);

        var hitItems = GetIntersectingItems(_dragSelection.View, selectionRect);
        var nextSelection = _dragSelection.IsAdditive
            ? new HashSet<string>(_dragSelection.SeedIds, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in hitItems)
        {
            nextSelection.Add(item.Id);
        }

        var primaryId = hitItems.LastOrDefault()?.Id;
        if (string.IsNullOrWhiteSpace(primaryId) && !string.IsNullOrWhiteSpace(_dragSelection.SeedPrimaryId) && nextSelection.Contains(_dragSelection.SeedPrimaryId))
        {
            primaryId = _dragSelection.SeedPrimaryId;
        }

        ApplySelectionSnapshot(nextSelection, primaryId);
    }

    private void DragSelectionAutoScrollTimer_Tick(object? sender, object e)
    {
        if (!_dragSelection.IsSelecting || _dragSelection.View == null)
        {
            StopDragSelectionAutoScroll();
            return;
        }

        if (Math.Abs(_dragSelection.AutoScrollVelocity) < double.Epsilon)
        {
            StopDragSelectionAutoScroll();
            return;
        }

        var scrollViewer = GetScrollViewer(_dragSelection.View);
        if (scrollViewer == null || scrollViewer.ScrollableHeight <= 0)
        {
            StopDragSelectionAutoScroll();
            return;
        }

        var currentOffset = scrollViewer.VerticalOffset;
        var nextOffset = Math.Clamp(currentOffset + _dragSelection.AutoScrollVelocity, 0, scrollViewer.ScrollableHeight);
        if (Math.Abs(nextOffset - currentOffset) < 0.1)
        {
            StopDragSelectionAutoScroll();
            return;
        }

        scrollViewer.ChangeView(null, nextOffset, null, true);
        _dragSelection.View.UpdateLayout();
        BrowserRoot.UpdateLayout();
        UpdateDragSelection(ClampToBrowserBounds(_dragSelection.CurrentPointerPoint));
    }

    private void UpdateDragSelectionAutoScroll(Point rawPoint)
    {
        if (!_dragSelection.IsSelecting)
        {
            StopDragSelectionAutoScroll();
            return;
        }

        var height = BrowserRoot.ActualHeight;
        if (height <= 0)
        {
            StopDragSelectionAutoScroll();
            return;
        }

        var velocity = _dragSelectionController.CalculateAutoScrollVelocity(rawPoint, height);

        _dragSelection.AutoScrollVelocity = velocity;
        if (Math.Abs(velocity) < double.Epsilon)
        {
            StopDragSelectionAutoScroll();
            return;
        }

        if (!_dragSelectionAutoScrollTimer.IsEnabled)
        {
            _dragSelectionAutoScrollTimer.Start();
        }
    }

    private void StopDragSelectionAutoScroll()
    {
        _dragSelection.AutoScrollVelocity = 0;
        if (_dragSelectionAutoScrollTimer.IsEnabled)
        {
            _dragSelectionAutoScrollTimer.Stop();
        }
    }

    private ScrollViewer? GetScrollViewer(ListViewBase listViewBase)
    {
        if (ReferenceEquals(listViewBase, GridView))
        {
            if (_gridViewScrollViewer == null)
            {
                SetGridViewScrollViewer(MainPageVisualTreeHelpers.FindDescendant<ScrollViewer>(GridView));
            }

            return _gridViewScrollViewer;
        }

        if (_listViewScrollViewer == null)
        {
            SetListViewScrollViewer(MainPageVisualTreeHelpers.FindDescendant<ScrollViewer>(ListView));
        }

        return _listViewScrollViewer;
    }

    private IReadOnlyList<MediaItemViewModel> GetIntersectingItems(ListViewBase? listViewBase, Rect selectionRect)
    {
        if (listViewBase == null)
        {
            return Array.Empty<MediaItemViewModel>();
        }

        var result = new List<MediaItemViewModel>();
        foreach (var media in _viewModel.FilteredMediaItems)
        {
            if (listViewBase.ContainerFromItem(media) is not SelectorItem container
                || container.Visibility != Visibility.Visible
                || container.ActualWidth <= 0
                || container.ActualHeight <= 0)
            {
                continue;
            }

            var containerBounds = GetElementBounds(container, BrowserRoot);
            if (containerBounds.Width <= 0 || containerBounds.Height <= 0)
            {
                continue;
            }

            if (DoRectsIntersect(selectionRect, containerBounds))
            {
                result.Add(media);
            }
        }

        return result;
    }

    private static Rect GetElementBounds(FrameworkElement element, UIElement relativeTo)
    {
        var transform = element.TransformToVisual(relativeTo);
        var origin = transform.TransformPoint(new Point(0, 0));
        return new Rect(origin.X, origin.Y, element.ActualWidth, element.ActualHeight);
    }

    private void ApplySelectionSnapshot(IEnumerable<string> selectedIds, string? primaryId)
    {
        var nextSelectedIds = selectedIds.ToHashSet(StringComparer.Ordinal);
        if (_selectionCoordinator.SelectedItemIds.SetEquals(nextSelectedIds)
            && string.Equals(_viewModel.SelectedMedia?.Id, primaryId, StringComparison.Ordinal))
        {
            UpdateNowPlayingState(_viewModel.SelectedMedia);
            return;
        }

        var previousSelectedIds = _selectionCoordinator.Snapshot();
        _selectionCoordinator.ReplaceSelection(nextSelectedIds);
        var selectedMedia = ResolvePrimarySelection(primaryId, nextSelectedIds);

        _isSyncingSelection = true;
        try
        {
            ApplySelectionToView(ListView, selectedMedia);
            ApplySelectionToView(GridView, selectedMedia);
            UpdateSelectedStateFlags(previousSelectedIds);
            UpdateNowPlayingState(selectedMedia);
        }
        finally
        {
            _isSyncingSelection = false;
        }

        _viewModel.SelectedMedia = selectedMedia;
    }

    private MediaItemViewModel? ResolvePrimarySelection(string? preferredId, IReadOnlySet<string> selectedIds)
    {
        if (selectedIds.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(preferredId)
            && selectedIds.Contains(preferredId)
            && _selectionSync.TryGet(preferredId, out var preferred))
        {
            return preferred;
        }

        if (_viewModel.SelectedMedia is { } current && selectedIds.Contains(current.Id))
        {
            return current;
        }

        return _selectionCoordinator.SelectedItemIds
            .Select(id => _selectionSync.TryGet(id, out var item) ? item : null)
            .OfType<MediaItemViewModel>()
            .FirstOrDefault();
    }

    private MediaItemViewModel? ResolveLoadedSelection(MediaItemViewModel? selectedMedia)
    {
        if (selectedMedia == null)
        {
            return null;
        }

        if (_selectionSync.TryGet(selectedMedia.Id, out var loaded))
        {
            return loaded;
        }

        return _viewModel.FilteredMediaItems.FirstOrDefault(item => string.Equals(item.Id, selectedMedia.Id, StringComparison.Ordinal));
    }

    private void RequestMediaScroll(
        MediaItemViewModel media,
        string operationName,
        string scheduledSampleKey,
        string appliedSampleKey,
        string missSampleKey)
    {
        var requestId = _mediaScroll.StartRequest(media.Id, operationName);

        var target = ResolveLoadedSelection(media);
        if (target == null)
        {
            AppTraceLogger.LogSampled(
                "MediaBrowser",
                missSampleKey,
                $"{operationName} queued but media '{media.Id}' is not loaded yet. RequestId={requestId}, LoadedCount={_selectionSync.LoadedCount}, ViewItemCount={_viewModel.FilteredMediaItems.Count}, ViewMode={_viewModel.ViewMode}.",
                TimeSpan.FromSeconds(2));
            return;
        }

        _viewModel.EnsureMediaFolderExpanded(target);

        if (ShouldSuppressRevealForRecentUserSelection(operationName, target.Id))
        {
            AppTraceLogger.LogSampled(
                "MediaBrowser",
                "media-scroll-suppressed-recent-user-selection",
                $"{operationName} skipped because media '{target.Id}' was just selected visibly by the user. RequestId={requestId}, ViewMode={_viewModel.ViewMode}, LoadedCount={_selectionSync.LoadedCount}, ViewItemCount={_viewModel.FilteredMediaItems.Count}.",
                TimeSpan.FromSeconds(2));
            ClearPendingMediaScroll(requestId, target.Id);
            return;
        }

        var activeView = GetActiveMediaView();
        var isVisible = IsMediaVisible(activeView, target);
        AppTraceLogger.LogSampled(
            "MediaBrowser",
            scheduledSampleKey,
            $"{operationName} scheduled. RequestId={requestId}, ViewMode={_viewModel.ViewMode}, MediaId='{target.Id}', AlreadyVisible={isVisible}, LoadedCount={_selectionSync.LoadedCount}, ViewItemCount={_viewModel.FilteredMediaItems.Count}.",
            TimeSpan.FromSeconds(1));

        if (isVisible)
        {
            ClearPendingMediaScroll(requestId, target.Id);
            return;
        }

        EnqueueMediaScroll(requestId, target.Id, operationName, appliedSampleKey);
    }

    private void EnqueueMediaScroll(int requestId, string mediaId, string operationName, string appliedSampleKey, int attempt = 1)
    {
        if (!_page.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => ScrollMediaIntoViewCore(requestId, mediaId, operationName, appliedSampleKey, attempt)))
        {
            AppTraceLogger.LogSampled(
                "MediaBrowser",
                "media-scroll-dispatch-failed",
                $"{operationName} dispatch failed. RequestId={requestId}, MediaId='{mediaId}', Attempt={attempt}.",
                TimeSpan.FromSeconds(2));
        }
    }

    private void ScrollMediaIntoViewCore(int requestId, string mediaId, string operationName, string appliedSampleKey, int attempt)
    {
        if (!_mediaScroll.IsCurrentRequest(requestId))
        {
            return;
        }

        var selectedMedia = _viewModel.SelectedMedia;
        if (selectedMedia == null || !string.Equals(selectedMedia.Id, mediaId, StringComparison.Ordinal))
        {
            return;
        }

        var target = ResolveLoadedSelection(selectedMedia);
        if (target == null)
        {
            AppTraceLogger.LogSampled(
                "MediaBrowser",
                $"{operationName}-late-miss",
                $"{operationName} skipped because media '{mediaId}' is still not loaded. RequestId={requestId}, LoadedCount={_selectionSync.LoadedCount}, ViewItemCount={_viewModel.FilteredMediaItems.Count}, ViewMode={_viewModel.ViewMode}.",
                TimeSpan.FromSeconds(2));
            return;
        }

        _viewModel.EnsureMediaFolderExpanded(target);

        if (ShouldSuppressRevealForRecentUserSelection(operationName, target.Id))
        {
            AppTraceLogger.LogSampled(
                "MediaBrowser",
                "media-scroll-suppressed-recent-user-selection-late",
                $"{operationName} skipped late because media '{target.Id}' was just selected visibly by the user. RequestId={requestId}, ViewMode={_viewModel.ViewMode}, LoadedCount={_selectionSync.LoadedCount}, ViewItemCount={_viewModel.FilteredMediaItems.Count}.",
                TimeSpan.FromSeconds(2));
            ClearPendingMediaScroll(requestId, target.Id);
            return;
        }

        var activeView = GetActiveMediaView();
        TryUpdateMediaViewLayout(activeView, $"{operationName} pre-scroll");
        if (IsMediaVisible(activeView, target))
        {
            ClearPendingMediaScroll(requestId, target.Id);
            return;
        }

        var beforeMetrics = GetScrollMetrics(activeView);
        var containerState = DescribeMediaContainer(activeView, target);
        var targetIndex = _viewModel.FilteredMediaItems.IndexOf(target);
        activeView.ScrollIntoView(target);
        AppTraceLogger.Log(
            "MediaBrowser",
            $"{operationName} applied. RequestId={requestId}, Attempt={attempt}, ViewMode={_viewModel.ViewMode}, MediaId='{target.Id}', TargetIndex={targetIndex}, LoadedCount={_selectionSync.LoadedCount}, ViewItemCount={_viewModel.FilteredMediaItems.Count}, Before=({beforeMetrics}), Container=({containerState}).");
        ScheduleMediaScrollConfirmation(requestId, target.Id, operationName, appliedSampleKey, attempt);
    }

    private void ScheduleMediaScrollConfirmation(int requestId, string mediaId, string operationName, string appliedSampleKey, int attempt)
    {
        if (!_page.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (!_page.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => ConfirmMediaScroll(requestId, mediaId, operationName, appliedSampleKey, attempt)))
                {
                    AppTraceLogger.Log(
                        "MediaBrowser",
                        $"{operationName} confirmation dispatch failed. RequestId={requestId}, Attempt={attempt}, MediaId='{mediaId}'.");
                    ClearPendingMediaScroll(requestId, mediaId);
                }
            }))
        {
            AppTraceLogger.Log(
                "MediaBrowser",
                $"{operationName} confirmation dispatch failed. RequestId={requestId}, Attempt={attempt}, MediaId='{mediaId}'.");
            ClearPendingMediaScroll(requestId, mediaId);
        }
    }

    private void ConfirmMediaScroll(int requestId, string mediaId, string operationName, string appliedSampleKey, int attempt)
    {
        if (!_mediaScroll.IsCurrentRequest(requestId))
        {
            return;
        }

        var selectedMedia = _viewModel.SelectedMedia;
        if (selectedMedia == null || !string.Equals(selectedMedia.Id, mediaId, StringComparison.Ordinal))
        {
            return;
        }

        var target = ResolveLoadedSelection(selectedMedia);
        if (target == null)
        {
            AppTraceLogger.Log(
                "MediaBrowser",
                $"{operationName} confirmation skipped because media is no longer loaded. RequestId={requestId}, Attempt={attempt}, MediaId='{mediaId}', LoadedCount={_selectionSync.LoadedCount}, ViewItemCount={_viewModel.FilteredMediaItems.Count}.");
            return;
        }

        var activeView = GetActiveMediaView();
        TryUpdateMediaViewLayout(activeView, $"{operationName} confirmation");
        var visible = IsMediaVisible(activeView, target);
        var scrollMetrics = GetScrollMetrics(activeView);
        var containerState = DescribeMediaContainer(activeView, target);
        if (visible)
        {
            AppTraceLogger.LogSampled(
                "MediaBrowser",
                appliedSampleKey,
                $"{operationName} confirmed visible. RequestId={requestId}, Attempt={attempt}, ViewMode={_viewModel.ViewMode}, MediaId='{target.Id}', LoadedCount={_selectionSync.LoadedCount}, ViewItemCount={_viewModel.FilteredMediaItems.Count}, {scrollMetrics}, Container=({containerState}).",
                TimeSpan.FromSeconds(1));
            ClearPendingMediaScroll(requestId, target.Id);
            return;
        }

        if (attempt < MaxMediaScrollAttempts)
        {
            AppTraceLogger.Log(
                "MediaBrowser",
                $"{operationName} did not reveal target; retrying. RequestId={requestId}, Attempt={attempt}, ViewMode={_viewModel.ViewMode}, MediaId='{target.Id}', LoadedCount={_selectionSync.LoadedCount}, ViewItemCount={_viewModel.FilteredMediaItems.Count}, {scrollMetrics}, Container=({containerState}).");
            EnqueueMediaScroll(requestId, target.Id, operationName, appliedSampleKey, attempt + 1);
            return;
        }

        AppTraceLogger.Log(
            "MediaBrowser",
            $"{operationName} failed to reveal target after retries. RequestId={requestId}, Attempts={attempt}, ViewMode={_viewModel.ViewMode}, MediaId='{target.Id}', LoadedCount={_selectionSync.LoadedCount}, ViewItemCount={_viewModel.FilteredMediaItems.Count}, {scrollMetrics}, Container=({containerState}).");
        ClearPendingMediaScroll(requestId, target.Id);
    }

    private void TryApplyPendingMediaScroll()
    {
        var pendingRequestId = _mediaScroll.PendingRequestId;
        var pendingMediaId = _mediaScroll.PendingMediaId;
        if (pendingRequestId <= 0 || string.IsNullOrWhiteSpace(pendingMediaId))
        {
            return;
        }

        var selectedMedia = _viewModel.SelectedMedia;
        if (selectedMedia == null || !string.Equals(selectedMedia.Id, pendingMediaId, StringComparison.Ordinal))
        {
            return;
        }

        if (!_mediaScroll.IsCurrentRequest(pendingRequestId))
        {
            return;
        }

        var target = ResolveLoadedSelection(selectedMedia);
        if (target == null)
        {
            return;
        }

        _viewModel.EnsureMediaFolderExpanded(target);

        if (!IsSelectionAlreadyApplied(selectedMedia, target))
        {
            SyncSelectionFromViewModel(selectedMedia);
            target = ResolveLoadedSelection(selectedMedia);
            if (target == null)
            {
                return;
            }
        }

        if (ShouldSuppressRevealForRecentUserSelection(_mediaScroll.PendingReason ?? "media scroll", target.Id))
        {
            AppTraceLogger.LogSampled(
                "MediaBrowser",
                "media-scroll-suppressed-recent-user-selection-retry",
                $"Pending {(_mediaScroll.PendingReason ?? "media scroll")} request skipped after load because media '{target.Id}' was just selected visibly by the user. RequestId={pendingRequestId}, ViewMode={_viewModel.ViewMode}, LoadedCount={_selectionSync.LoadedCount}, ViewItemCount={_viewModel.FilteredMediaItems.Count}.",
                TimeSpan.FromSeconds(2));
            ClearPendingMediaScroll(pendingRequestId, target.Id);
            return;
        }

        var activeView = GetActiveMediaView();
        if (IsMediaVisible(activeView, target))
        {
            ClearPendingMediaScroll(pendingRequestId, target.Id);
            return;
        }

        AppTraceLogger.LogSampled(
            "MediaBrowser",
            "media-scroll-retry",
            $"Pending {(_mediaScroll.PendingReason ?? "media scroll")} request retrying after load. RequestId={pendingRequestId}, MediaId='{target.Id}', ViewMode={_viewModel.ViewMode}, LoadedCount={_selectionSync.LoadedCount}, ViewItemCount={_viewModel.FilteredMediaItems.Count}.",
            TimeSpan.FromSeconds(1));
        EnqueueMediaScroll(pendingRequestId, target.Id, _mediaScroll.PendingReason ?? "media scroll", "media-scroll-applied");
    }

    private void ScheduleSelectedMediaVisibilityVerificationAfterItemsChanged(string reason)
    {
        var selectedMedia = _viewModel.SelectedMedia;
        if (selectedMedia == null || !ShouldVerifySelectedMediaAfterItemsChanged(selectedMedia.Id))
        {
            return;
        }

        var version = Interlocked.Increment(ref _itemsChangedVisibilityVersion);
        if (Interlocked.Exchange(ref _itemsChangedVisibilityVerificationScheduled, 1) == 1)
        {
            return;
        }

        AppTraceLogger.LogSampled(
            "MediaBrowser",
            "selected-media-visibility-check-scheduled",
            $"Selected media visibility check scheduled after {reason}. Version={version}, MediaId='{selectedMedia.Id}', ViewMode={_viewModel.ViewMode}, LoadedCount={_selectionSync.LoadedCount}, ViewItemCount={_viewModel.FilteredMediaItems.Count}.",
            TimeSpan.FromSeconds(1));

        if (!_page.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (!_page.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => VerifySelectedMediaVisibleAfterItemsChanged(reason)))
                {
                    Interlocked.Exchange(ref _itemsChangedVisibilityVerificationScheduled, 0);
                    AppTraceLogger.LogSampled(
                        "MediaBrowser",
                        "selected-media-visibility-check-dispatch-failed",
                        $"Selected media visibility check dispatch failed after {reason}. Version={Volatile.Read(ref _itemsChangedVisibilityVersion)}.",
                        TimeSpan.FromSeconds(2));
                }
            }))
        {
            Interlocked.Exchange(ref _itemsChangedVisibilityVerificationScheduled, 0);
            AppTraceLogger.LogSampled(
                "MediaBrowser",
                "selected-media-visibility-check-dispatch-failed",
                $"Selected media visibility check dispatch failed after {reason}. Version={version}.",
                TimeSpan.FromSeconds(2));
        }
    }

    private bool ShouldVerifySelectedMediaAfterItemsChanged(string mediaId)
    {
        return _mediaScroll.ShouldVerifyAfterItemsChanged(mediaId, ItemsChangedVisibilityVerificationWindow);
    }

    private void VerifySelectedMediaVisibleAfterItemsChanged(string reason)
    {
        Interlocked.Exchange(ref _itemsChangedVisibilityVerificationScheduled, 0);

        var version = Volatile.Read(ref _itemsChangedVisibilityVersion);
        var selectedMedia = _viewModel.SelectedMedia;
        if (selectedMedia == null)
        {
            return;
        }

        if (!ShouldVerifySelectedMediaAfterItemsChanged(selectedMedia.Id))
        {
            AppTraceLogger.LogSampled(
                "MediaBrowser",
                "selected-media-visibility-check-expired",
                $"Selected media visibility check skipped after {reason} because the selection is no longer recent. Version={version}, MediaId='{selectedMedia.Id}', ViewMode={_viewModel.ViewMode}, LoadedCount={_selectionSync.LoadedCount}, ViewItemCount={_viewModel.FilteredMediaItems.Count}.",
                TimeSpan.FromSeconds(2));
            return;
        }

        var target = ResolveLoadedSelection(selectedMedia);
        if (target == null)
        {
            AppTraceLogger.LogSampled(
                "MediaBrowser",
                "selected-media-visibility-check-not-loaded",
                $"Selected media visibility check skipped after {reason} because media '{selectedMedia.Id}' is not loaded. Version={version}, ViewMode={_viewModel.ViewMode}, LoadedCount={_selectionSync.LoadedCount}, ViewItemCount={_viewModel.FilteredMediaItems.Count}.",
                TimeSpan.FromSeconds(2));
            return;
        }

        _viewModel.EnsureMediaFolderExpanded(target);

        if (!IsSelectionAlreadyApplied(selectedMedia, target))
        {
            SyncSelectionFromViewModel(selectedMedia);
            target = ResolveLoadedSelection(selectedMedia);
            if (target == null)
            {
                return;
            }
        }

        var activeView = GetActiveMediaView();
        TryUpdateMediaViewLayout(activeView, "selected media visibility check");

        var scrollMetrics = GetScrollMetrics(activeView);
        if (IsMediaVisible(activeView, target))
        {
            AppTraceLogger.LogSampled(
                "MediaBrowser",
                "selected-media-visibility-check-visible",
                $"Selected media visibility check completed after {reason}; media is visible. Version={version}, MediaId='{target.Id}', ViewMode={_viewModel.ViewMode}, LoadedCount={_selectionSync.LoadedCount}, ViewItemCount={_viewModel.FilteredMediaItems.Count}, {scrollMetrics}.",
                TimeSpan.FromSeconds(1));
            return;
        }

        activeView.ScrollIntoView(target);
        AppTraceLogger.LogSampled(
            "MediaBrowser",
            "selected-media-visibility-check-applied",
            $"Selected media visibility check scrolled after {reason}. Version={version}, MediaId='{target.Id}', ViewMode={_viewModel.ViewMode}, LoadedCount={_selectionSync.LoadedCount}, ViewItemCount={_viewModel.FilteredMediaItems.Count}, {scrollMetrics}.",
            TimeSpan.FromSeconds(1));
        ScheduleSelectedMediaVisibilityConfirmation(version, target.Id, reason);
    }

    private void ScheduleSelectedMediaVisibilityConfirmation(int version, string mediaId, string reason)
    {
        _page.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            var selectedMedia = _viewModel.SelectedMedia;
            if (selectedMedia == null || !string.Equals(selectedMedia.Id, mediaId, StringComparison.Ordinal))
            {
                return;
            }

            var target = ResolveLoadedSelection(selectedMedia);
            if (target == null)
            {
                return;
            }

            var activeView = GetActiveMediaView();
            TryUpdateMediaViewLayout(activeView, "selected media visibility confirmation");
            AppTraceLogger.LogSampled(
                "MediaBrowser",
                "selected-media-visibility-check-confirmed",
                $"Selected media visibility check confirmation after {reason}. Version={version}, MediaId='{target.Id}', Visible={IsMediaVisible(activeView, target)}, ViewMode={_viewModel.ViewMode}, LoadedCount={_selectionSync.LoadedCount}, ViewItemCount={_viewModel.FilteredMediaItems.Count}, {GetScrollMetrics(activeView)}.",
                TimeSpan.FromSeconds(1));
        });
    }

    private static void TryUpdateMediaViewLayout(ListViewBase activeView, string operationName)
    {
        try
        {
            activeView.UpdateLayout();
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("MediaBrowser", $"{operationName} layout update failed.", ex);
        }
    }

    private void StorePendingMediaScroll(int requestId, string mediaId, string reason)
    {
        _mediaScroll.StorePending(requestId, mediaId, reason);
    }

    private void ClearPendingMediaScroll(int requestId, string mediaId)
    {
        _mediaScroll.ClearPending(requestId, mediaId);
    }

    private void StoreRecentUserVisibleSelection(string mediaId)
    {
        _mediaScroll.StoreRecentUserVisibleSelection(mediaId);
    }

    private void RememberMediaScrollCandidate(string mediaId)
    {
        _mediaScroll.RememberCandidate(mediaId);
    }

    private bool ShouldSuppressRevealForRecentUserSelection(string operationName, string mediaId)
    {
        return _mediaScroll.ShouldSuppressRevealForRecentUserSelection(operationName, mediaId, RecentUserSelectionRevealSuppression);
    }

    private void RebuildLoadedMediaIndex()
    {
        _selectionSync.Rebuild(_viewModel.FilteredMediaItems);

        PruneTrackedSelectionToLoadedItems("rebuild-loaded-index");
        foreach (var item in _selectionSync.LoadedItems)
        {
            item.IsSelected = IsSelectionTracked(item.Id);
        }

        UpdateNowPlayingState(_viewModel.SelectedMedia);
        TryApplyPendingMediaScroll();
    }

    private void PruneTrackedSelectionToLoadedItems(string reason)
    {
        var previousSelectedIds = _selectionCoordinator.Snapshot();
        if (previousSelectedIds.Count == 0)
        {
            return;
        }

        var selectedMediaId = _viewModel.SelectedMedia?.Id;
        var nextSelectedIds = previousSelectedIds
            .Where(id => _selectionSync.Contains(id)
                || !string.IsNullOrWhiteSpace(selectedMediaId)
                && string.Equals(id, selectedMediaId, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        if (previousSelectedIds.SetEquals(nextSelectedIds))
        {
            return;
        }

        _selectionCoordinator.ReplaceSelection(nextSelectedIds);
        UpdateSelectedStateFlags(previousSelectedIds);
        AppTraceLogger.LogSampled(
            "MediaBrowser",
            "selection-pruned",
            $"Pruned tracked selection after {reason}. Previous={previousSelectedIds.Count}, Remaining={nextSelectedIds.Count}, LoadedCount={_selectionSync.LoadedCount}, SelectedId='{selectedMediaId ?? "<null>"}'.",
            TimeSpan.FromSeconds(1));
    }

    private bool IsMediaVisible(ListViewBase listViewBase, MediaItemViewModel media)
    {
        if (listViewBase.ContainerFromItem(media) is not FrameworkElement container
            || container.ActualWidth <= 0
            || container.ActualHeight <= 0)
        {
            return false;
        }

        var scrollViewer = GetScrollViewer(listViewBase);
        if (scrollViewer == null || scrollViewer.ActualWidth <= 0 || scrollViewer.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            var bounds = GetElementBounds(container, scrollViewer);
            var visibleWidth = Math.Max(0, Math.Min(bounds.Right, scrollViewer.ActualWidth) - Math.Max(bounds.Left, 0));
            var visibleHeight = Math.Max(0, Math.Min(bounds.Bottom, scrollViewer.ActualHeight) - Math.Max(bounds.Top, 0));
            var visibleArea = visibleWidth * visibleHeight;
            var totalArea = bounds.Width * bounds.Height;
            return totalArea > 0 && visibleArea / totalArea >= 0.7;
        }
        catch
        {
            return false;
        }
    }

    private string GetScrollMetrics(ListViewBase listViewBase)
    {
        var scrollViewer = GetScrollViewer(listViewBase);
        if (scrollViewer == null)
        {
            return "ScrollViewer=<null>";
        }

        return $"VerticalOffset={scrollViewer.VerticalOffset:0.##}, ScrollableHeight={scrollViewer.ScrollableHeight:0.##}, ViewportHeight={scrollViewer.ViewportHeight:0.##}, ActualHeight={scrollViewer.ActualHeight:0.##}";
    }

    private string DescribeMediaContainer(ListViewBase listViewBase, MediaItemViewModel media)
    {
        if (listViewBase.ContainerFromItem(media) is not FrameworkElement container)
        {
            return "Container=<null>";
        }

        var scrollViewer = GetScrollViewer(listViewBase);
        if (scrollViewer == null)
        {
            return $"ContainerType={container.GetType().Name}, ActualWidth={container.ActualWidth:0.##}, ActualHeight={container.ActualHeight:0.##}, Bounds=<no-scrollviewer>";
        }

        try
        {
            var bounds = GetElementBounds(container, scrollViewer);
            return $"ContainerType={container.GetType().Name}, ActualWidth={container.ActualWidth:0.##}, ActualHeight={container.ActualHeight:0.##}, Bounds=({bounds.X:0.##},{bounds.Y:0.##},{bounds.Width:0.##},{bounds.Height:0.##})";
        }
        catch (Exception ex)
        {
            return $"ContainerType={container.GetType().Name}, ActualWidth={container.ActualWidth:0.##}, ActualHeight={container.ActualHeight:0.##}, BoundsError={ex.GetType().Name}";
        }
    }

    private bool IsSelectionAlreadyApplied(MediaItemViewModel? selectedMedia, MediaItemViewModel? currentSelection)
    {
        if (selectedMedia == null)
        {
            return _selectionCoordinator.SelectedItemIds.Count == 0
                && ListView.SelectedItems.Count == 0
                && GridView.SelectedItems.Count == 0;
        }

        if (_selectionCoordinator.SelectedItemIds.Count != 1 || !IsSelectionTracked(selectedMedia.Id))
        {
            return false;
        }

        if (ListView.SelectedItems.Count > 1 || GridView.SelectedItems.Count > 1)
        {
            return false;
        }

        var currentSelectionId = currentSelection?.Id;
        var listSelectionId = (ListView.SelectedItem as MediaItemViewModel)?.Id;
        var gridSelectionId = (GridView.SelectedItem as MediaItemViewModel)?.Id;
        return string.Equals(listSelectionId, currentSelectionId, StringComparison.Ordinal)
            && string.Equals(gridSelectionId, currentSelectionId, StringComparison.Ordinal);
    }

    private void ShowSelectionRectangle(Rect rect)
    {
        SelectionCanvas.Width = BrowserRoot.ActualWidth;
        SelectionCanvas.Height = BrowserRoot.ActualHeight;
        Canvas.SetLeft(SelectionRectangle, rect.X);
        Canvas.SetTop(SelectionRectangle, rect.Y);
        SelectionRectangle.Width = rect.Width;
        SelectionRectangle.Height = rect.Height;
        SelectionRectangle.Visibility = Visibility.Visible;
    }

    private void HideSelectionRectangle()
    {
        SelectionRectangle.Visibility = Visibility.Collapsed;
        SelectionRectangle.Width = 0;
        SelectionRectangle.Height = 0;
        Canvas.SetLeft(SelectionRectangle, 0);
        Canvas.SetTop(SelectionRectangle, 0);
    }

    private void EndDragSelection()
    {
        StopDragSelectionAutoScroll();
        _dragSelectionController.End();
        HideSelectionRectangle();
    }

    private Point ClampToBrowserBounds(Point point)
    {
        return new Point(
            Math.Clamp(point.X, 0, BrowserRoot.ActualWidth),
            Math.Clamp(point.Y, 0, BrowserRoot.ActualHeight));
    }

    private static Rect CreateNormalizedRect(Point startPoint, Point endPoint)
    {
        var x = Math.Min(startPoint.X, endPoint.X);
        var y = Math.Min(startPoint.Y, endPoint.Y);
        var width = Math.Abs(endPoint.X - startPoint.X);
        var height = Math.Abs(endPoint.Y - startPoint.Y);
        return new Rect(x, y, width, height);
    }

    private static bool DoRectsIntersect(Rect first, Rect second)
    {
        return first.X < second.X + second.Width
            && first.X + first.Width > second.X
            && first.Y < second.Y + second.Height
            && first.Y + first.Height > second.Y;
    }

    private static bool IsCtrlKeyDown()
    {
        return (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
    }

    private static bool IsShiftKeyDown()
    {
        return (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
    }

    private ListViewBase GetActiveMediaView()
    {
        return _viewModel.ViewMode == MediaViewMode.Grid ? GridView : ListView;
    }

    private void ApplySelectionToView(ListViewBase listViewBase, MediaItemViewModel? selectedMedia)
    {
        if (_selectionCoordinator.SelectedItemIds.Count <= 1)
        {
            if (IsSingleSelectionAppliedToView(listViewBase, selectedMedia))
            {
                return;
            }

            listViewBase.SelectedItems.Clear();
            listViewBase.SelectedItem = selectedMedia;
            return;
        }

        var targetItems = _viewModel.FilteredMediaItems
            .Where(item => IsSelectionTracked(item.Id) && _viewModel.IsMediaVisibleInLibrary(item))
            .ToList();
        if (IsMultiSelectionAppliedToView(listViewBase, targetItems))
        {
            return;
        }

        listViewBase.SelectedItems.Clear();
        foreach (var item in targetItems)
        {
            listViewBase.SelectedItems.Add(item);
        }
    }

    private static bool IsSingleSelectionAppliedToView(ListViewBase listViewBase, MediaItemViewModel? selectedMedia)
    {
        if (selectedMedia == null)
        {
            return listViewBase.SelectedItem == null && listViewBase.SelectedItems.Count == 0;
        }

        var selectedItem = listViewBase.SelectedItem as MediaItemViewModel;
        return listViewBase.SelectedItems.Count <= 1
            && selectedItem != null
            && string.Equals(selectedItem.Id, selectedMedia.Id, StringComparison.Ordinal);
    }

    private static bool IsMultiSelectionAppliedToView(
        ListViewBase listViewBase,
        IReadOnlyList<MediaItemViewModel> targetItems)
    {
        if (listViewBase.SelectedItems.Count != targetItems.Count)
        {
            return false;
        }

        var targetIds = targetItems
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        return listViewBase.SelectedItems
            .Cast<object?>()
            .All(item => item is MediaItemViewModel media && targetIds.Contains(media.Id));
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

    private void UpdateSelectedStateFlags(IReadOnlySet<string> previousSelectedIds)
    {
        _selectionCoordinator.ApplySelectedStateFlags(previousSelectedIds);
    }

    private void UpdateNowPlayingState(MediaItemViewModel? selectedMedia)
    {
        var selectedId = selectedMedia?.Id;
        foreach (var item in _selectionSync.LoadedItems)
        {
            var isNowPlaying = !string.IsNullOrWhiteSpace(selectedId)
                && string.Equals(item.Id, selectedId, StringComparison.Ordinal);
            if (item.IsNowPlaying != isNowPlaying)
            {
                item.IsNowPlaying = isNowPlaying;
            }
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
            AppTraceLogger.LogException("MediaBrowser", $"ExecuteUiActionAsync failed. FailureTitle='{failureTitle}'.", ex);
            await showInfoAsync(failureTitle, ex.Message);
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
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(CreateSortMenuItem("按类型排序（图片优先）", MediaSortField.Type, MediaSortOrder.Asc));
        flyout.Items.Add(CreateSortMenuItem("按类型排序（视频优先）", MediaSortField.Type, MediaSortOrder.Desc));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(CreateSortMenuItem("按大小排序（小到大）", MediaSortField.Size, MediaSortOrder.Asc));
        flyout.Items.Add(CreateSortMenuItem("按大小排序（大到小）", MediaSortField.Size, MediaSortOrder.Desc));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(CreateSortMenuItem("按时长排序（短到长）", MediaSortField.Duration, MediaSortOrder.Asc));
        flyout.Items.Add(CreateSortMenuItem("按时长排序（长到短）", MediaSortField.Duration, MediaSortOrder.Desc));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(CreateSortMenuItem("按分辨率排序（低到高）", MediaSortField.Resolution, MediaSortOrder.Asc));
        flyout.Items.Add(CreateSortMenuItem("按分辨率排序（高到低）", MediaSortField.Resolution, MediaSortOrder.Desc));
        return flyout;
    }

    private MenuFlyout BuildMediaTypeFilterFlyout()
    {
        var flyout = new MenuFlyout();
        flyout.Items.Add(CreateMediaTypeFilterMenuItem("全部媒体", null));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(CreateMediaTypeFilterMenuItem("只显示图片", MediaType.Image));
        flyout.Items.Add(CreateMediaTypeFilterMenuItem("只显示视频", MediaType.Video));
        return flyout;
    }

    private ToggleMenuFlyoutItem CreateMediaTypeFilterMenuItem(string text, MediaType? typeFilter)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = text,
            IsChecked = EqualityComparer<MediaType?>.Default.Equals(_viewModel.MediaTypeFilter, typeFilter)
        };
        item.Click += (_, _) => _viewModel.SetMediaTypeFilter(typeFilter);
        return item;
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

    private static bool TryResolveInternalDragMediaIds(DataPackageView dataView, out List<string> mediaIds)
    {
        mediaIds = new List<string>();
        if (!dataView.Properties.TryGetValue(InternalDragData.MediaDragMarkerProperty, out _)
            || !dataView.Properties.TryGetValue(InternalDragData.MediaIdsProperty, out var mediaIdsObject)
            || mediaIdsObject is not List<string> dragIds)
        {
            return false;
        }

        mediaIds = dragIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return mediaIds.Count > 0;
    }

    private bool IsDragFromCurrentPlaylist(DataPackageView dataView)
    {
        return _viewModel.SelectedPlaylist != null
            && dataView.Properties.TryGetValue(InternalDragData.SourcePlaylistIdProperty, out var playlistIdObject)
            && playlistIdObject is string sourcePlaylistId
            && string.Equals(sourcePlaylistId, _viewModel.SelectedPlaylist.Id, StringComparison.Ordinal);
    }
}
