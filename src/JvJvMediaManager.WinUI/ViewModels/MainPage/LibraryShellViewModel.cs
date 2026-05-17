using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using JvJvMediaManager.Data;
using JvJvMediaManager.Models;
using JvJvMediaManager.Services;
using JvJvMediaManager.Utilities;
using JvJvMediaManager.ViewModels;

namespace JvJvMediaManager.ViewModels.MainPage;

public sealed class LibraryShellViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly MediaDb _db;
    private readonly MediaLibraryService _library;
    private readonly ThumbnailService _thumbnails;
    private readonly TimelineThumbnailStripService _timelineThumbnails;
    private readonly HashSet<string> _sessionUnlockedFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _folderCollapseStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _folderGroupLoadRequests = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<MediaFolderSummary> _mediaFolderSummaries = Array.Empty<MediaFolderSummary>();
    private readonly SelectionViewModel _selection;
    private readonly SemaphoreSlim _scanRefreshLock = new(1, 1);
    private static readonly TimeSpan IncrementalScanRefreshInterval = TimeSpan.FromMilliseconds(350);
    private const int MaxFolderExpansionLoadPages = 100;

    private DispatcherQueue? _dispatcher;
    private int _refreshVersion;
    private int _scanSessionId;
    private int _pendingIncrementalScanRefresh;
    private int _mediaFolderGroupRebuildQueued;
    private bool _isApplyingFolderCollapseState;
    private DateTimeOffset _lastIncrementalScanRefreshAt = DateTimeOffset.MinValue;
    private CancellationTokenSource? _scanCancellation;

    private string _searchQuery = string.Empty;
    private bool _isLoading;
    private string _statusMessage = string.Empty;
    private MediaViewMode _viewMode = MediaViewMode.List;
    private MediaSortField _sortField = MediaSortField.ModifiedAt;
    private MediaSortOrder _sortOrder = MediaSortOrder.Desc;
    private int _iconSize = 120;
    private Playlist? _selectedPlaylist;
    private bool _isScanning;
    private int _scanProgressValue;
    private int _scanProgressMaximum;
    private string _scanCurrentPath = string.Empty;
    private bool _scanProgressIsIndeterminate;
    private bool _isLibraryPaneOpen = true;
    private double _libraryPaneWidth = 360;
    private double _libraryPaneResizerOpacity = 0.65;

    public LibraryShellViewModel(SelectionViewModel selection)
        : this(selection, LibraryShellServices.CreateDefault())
    {
    }

    public LibraryShellViewModel(SelectionViewModel selection, LibraryShellServices services)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(services);

        _selection = selection;
        _settings = services.Settings;
        _db = services.Database;
        _library = services.Library;
        _thumbnails = services.Thumbnails;
        _timelineThumbnails = services.TimelineThumbnails;
        FilteredMediaItems = new IncrementalMediaCollection(LoadMediaPageAsync);
        FilteredMediaItems.CollectionChanged += (_, _) => ScheduleRebuildMediaFolderGroups();
        WatchedFolders = new ObservableCollection<WatchedFolder>(_settings.WatchedFolders);
        Playlists = new ObservableCollection<Playlist>(_db.GetPlaylists());
        UpdatePlaylistRailDisplayTexts(Playlists);
        SelectedTags.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SelectedTagsVisibility));
    }

    public IncrementalMediaCollection FilteredMediaItems { get; }

    public ObservableCollection<MediaFolderGroupViewModel> MediaFolderGroups { get; } = new();

    public ObservableCollection<WatchedFolder> WatchedFolders { get; }

    public ObservableCollection<string> SelectedTags { get; } = new();

    public ObservableCollection<Playlist> Playlists { get; }

    private List<string> GetVisibleWatchedFolderPaths()
    {
        return WatchedFolders
            .Where(f => f.Visible)
            .Select(f => PathHelpers.NormalizeFolderPath(f.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public MediaItemViewModel? SelectedMedia
    {
        get => _selection.SelectedMedia;
        set
        {
            if (ReferenceEquals(_selection.SelectedMedia, value))
            {
                return;
            }

            _selection.SelectedMedia = value;
            OnPropertyChanged();
        }
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                QueueRefreshMedia(false);
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public MediaViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            if (!SetProperty(ref _viewMode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ListViewVisibility));
            OnPropertyChanged(nameof(GridViewVisibility));
            OnPropertyChanged(nameof(ViewModeToggleGlyph));
            OnPropertyChanged(nameof(ViewModeToggleToolTip));
        }
    }

    public MediaSortField SortField
    {
        get => _sortField;
        set
        {
            if (SetProperty(ref _sortField, value))
            {
                OnPropertyChanged(nameof(SortButtonText));
                OnPropertyChanged(nameof(SortButtonToolTip));
                QueueRefreshMedia(true);
            }
        }
    }

    public MediaSortOrder SortOrder
    {
        get => _sortOrder;
        set
        {
            if (SetProperty(ref _sortOrder, value))
            {
                OnPropertyChanged(nameof(SortButtonText));
                OnPropertyChanged(nameof(SortButtonToolTip));
                QueueRefreshMedia(true);
            }
        }
    }

    public int IconSize
    {
        get => _iconSize;
        set => SetProperty(ref _iconSize, value);
    }

    public Playlist? SelectedPlaylist
    {
        get => _selectedPlaylist;
        set
        {
            var previousPlaylistId = _selectedPlaylist?.Id;
            if (SetProperty(ref _selectedPlaylist, value))
            {
                OnPropertyChanged(nameof(CurrentScopeTitle));
                OnPropertyChanged(nameof(SelectedPlaylistTitle));
                OnPropertyChanged(nameof(SelectedPlaylistTitleVisibility));
                OnPropertyChanged(nameof(RefreshButtonVisibility));

                if (!string.Equals(previousPlaylistId, value?.Id, StringComparison.Ordinal))
                {
                    QueueRefreshMedia(false);
                }
            }
        }
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
            {
                RaiseScanUiProperties();
            }
        }
    }

    public int ScanProgressValue
    {
        get => _scanProgressValue;
        private set
        {
            if (SetProperty(ref _scanProgressValue, value))
            {
                RaiseScanUiProperties();
            }
        }
    }

    public int ScanProgressMaximum
    {
        get => _scanProgressMaximum;
        private set => SetProperty(ref _scanProgressMaximum, value);
    }

    public bool ScanProgressIsIndeterminate
    {
        get => _scanProgressIsIndeterminate;
        private set
        {
            if (SetProperty(ref _scanProgressIsIndeterminate, value))
            {
                RaiseScanUiProperties();
            }
        }
    }

    public string ScanCurrentPath
    {
        get => _scanCurrentPath;
        private set
        {
            if (SetProperty(ref _scanCurrentPath, value))
            {
                RaiseScanUiProperties();
            }
        }
    }

    public string DataDir => _settings.DataDir;

    public string? ConfiguredDataDir => _settings.ConfiguredDataDir;

    public bool PortableMode => _settings.PortableMode;

    public string LockPassword => _settings.LockPassword;

    public IReadOnlyList<string> NumpadTagShortcuts => _settings.NumpadTagShortcuts;

    public bool HasLockPassword => !string.IsNullOrWhiteSpace(LockPassword);

    public string CurrentScopeTitle => SelectedPlaylist?.Name ?? "全部媒体";

    public bool IsLibraryPaneOpen
    {
        get => _isLibraryPaneOpen;
        set
        {
            if (!SetProperty(ref _isLibraryPaneOpen, value))
            {
                return;
            }

            OnPropertyChanged(nameof(LibraryPaneExpandedVisibility));
            OnPropertyChanged(nameof(LibraryPaneResizerVisibility));
        }
    }

    public double LibraryPaneWidth
    {
        get => _libraryPaneWidth;
        set => SetProperty(ref _libraryPaneWidth, value);
    }

    public double LibraryPaneResizerOpacity
    {
        get => _libraryPaneResizerOpacity;
        set => SetProperty(ref _libraryPaneResizerOpacity, value);
    }

    public Visibility LibraryPaneExpandedVisibility => IsLibraryPaneOpen ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LibraryPaneResizerVisibility => IsLibraryPaneOpen ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SelectedTagsVisibility => SelectedTags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ScanProgressVisibility => IsScanning || ScanProgressValue > 0 || !string.IsNullOrWhiteSpace(ScanCurrentPath)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ScanCancelButtonVisibility => IsScanning ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ScanPathVisibility => !string.IsNullOrWhiteSpace(ScanCurrentPath)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility SelectedPlaylistTitleVisibility => SelectedPlaylist == null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility RefreshButtonVisibility => SelectedPlaylist == null ? Visibility.Visible : Visibility.Collapsed;

    public string SelectedPlaylistTitle => SelectedPlaylist?.Name ?? string.Empty;

    public string ViewModeToggleGlyph => ViewMode == MediaViewMode.List ? "\uECA5" : "\uE8FD";

    public string ViewModeToggleToolTip => ViewMode == MediaViewMode.List ? "切换到网格" : "切换到列表";

    public bool HasMediaFolderGroups => MediaFolderGroups.Count > 0;

    public bool ShouldCollapseAllMediaFolderGroups => MediaFolderGroups.Any(group => !group.IsCollapsed);

    public string MediaFolderGroupToggleGlyph => ShouldCollapseAllMediaFolderGroups ? "\uE70E" : "\uE70D";

    public string MediaFolderGroupToggleToolTip => ShouldCollapseAllMediaFolderGroups ? "收起所有文件夹" : "展开所有文件夹";

    public string SortButtonText
    {
        get
        {
            return (SortField, SortOrder) switch
            {
                (MediaSortField.ModifiedAt, MediaSortOrder.Desc) => "时间 ↓",
                (MediaSortField.ModifiedAt, MediaSortOrder.Asc) => "时间 ↑",
                (MediaSortField.FileName, MediaSortOrder.Asc) => "名称 A-Z",
                (MediaSortField.FileName, MediaSortOrder.Desc) => "名称 Z-A",
                (MediaSortField.Type, MediaSortOrder.Asc) => "类型 图片",
                (MediaSortField.Type, MediaSortOrder.Desc) => "类型 视频",
                (MediaSortField.Size, MediaSortOrder.Asc) => "大小 ↑",
                (MediaSortField.Size, MediaSortOrder.Desc) => "大小 ↓",
                (MediaSortField.Duration, MediaSortOrder.Asc) => "时长 ↑",
                (MediaSortField.Duration, MediaSortOrder.Desc) => "时长 ↓",
                (MediaSortField.Resolution, MediaSortOrder.Asc) => "分辨率 ↑",
                _ => "分辨率 ↓"
            };
        }
    }

    public string SortButtonToolTip
    {
        get
        {
            return $"排序方式：{SortButtonText}";
        }
    }

    public Visibility ListViewVisibility => ViewMode == MediaViewMode.List ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GridViewVisibility => ViewMode == MediaViewMode.Grid ? Visibility.Visible : Visibility.Collapsed;

    public void SetDispatcher(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        FilteredMediaItems.SetDispatcher(dispatcher);
    }

    public async Task InitializeAsync()
    {
        AppTraceLogger.Log("LibraryShell", "InitializeAsync start.");
        ReloadPlaylists();
        await RefreshMediaAsync(false);
        AppTraceLogger.Log("LibraryShell", $"InitializeAsync completed. LoadedCount={FilteredMediaItems.Count}, PlaylistCount={Playlists.Count}.");
    }

    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        var requestedPaths = paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        AppTraceLogger.Log("LibraryShell", $"AddFilesAsync requested. PathCount={requestedPaths.Count}.");
        await AddMediaInternalAsync(
            cancellationToken => _library.AddFilesAsync(requestedPaths, new Progress<ScanProgress>(OnScanProgress), cancellationToken),
            "文件导入完成。");
    }

    public async Task AddFolderAsync(string path)
    {
        AppTraceLogger.Log("LibraryShell", $"AddFolderAsync requested. Path='{path}'.");
        await AddMediaInternalAsync(
            cancellationToken => _library.AddFolderAsync(path, new Progress<ScanProgress>(OnScanProgress), cancellationToken),
            "文件夹导入完成。");
    }

    public async Task RescanFoldersAsync()
    {
        var visibleFolders = GetVisibleWatchedFolderPaths();
        if (visibleFolders.Count == 0)
        {
            AppTraceLogger.Log("LibraryShell", "RescanFoldersAsync skipped. No visible folders.");
            return;
        }
        
        AppTraceLogger.Log("LibraryShell", $"RescanFoldersAsync requested. VisibleFolders={visibleFolders.Count}.");
        await AddMediaInternalAsync(
            cancellationToken => _library.RescanFoldersAsync(visibleFolders, new Progress<ScanProgress>(OnScanProgress), cancellationToken),
            "媒体库刷新完成。");
    }

    public void CancelScan()
    {
        var cancellation = _scanCancellation;
        if (cancellation == null || cancellation.IsCancellationRequested)
        {
            return;
        }

        cancellation.Cancel();
        AppTraceLogger.Log("LibraryShell", "CancelScan requested.");
        StatusMessage = "正在取消扫描...";
    }

    public void UpdateWatchedFolders(IEnumerable<WatchedFolder> folders, bool refreshMedia = true)
    {
        var requestedFolders = folders.ToList();
        var previousFolderPaths = WatchedFolders
            .Select(folder => PathHelpers.NormalizeFolderPath(folder.Path))
            .ToList();
        AppTraceLogger.Log("LibraryShell", $"UpdateWatchedFolders start. RequestedCount={requestedFolders.Count}, PreviousCount={previousFolderPaths.Count}, RefreshMedia={refreshMedia}.");
        var normalized = requestedFolders
            .Where(folder => !string.IsNullOrWhiteSpace(folder.Path))
            .GroupBy(folder => PathHelpers.NormalizeFolderPath(folder.Path), StringComparer.OrdinalIgnoreCase)
            .Select(group => new WatchedFolder
            {
                Path = group.First().Path,
                Locked = group.First().Locked,
                Visible = group.First().Visible
            })
            .OrderBy(folder => folder.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var currentFolderPaths = normalized
            .Select(folder => PathHelpers.NormalizeFolderPath(folder.Path))
            .ToList();
        AppTraceLogger.Log("LibraryShell", $"UpdateWatchedFolders normalized. CurrentCount={currentFolderPaths.Count}, Current=[{string.Join(", ", currentFolderPaths)}].");

        RemoveMediaOutsideWatchedFolders(previousFolderPaths, currentFolderPaths);

        WatchedFolders.Clear();
        foreach (var folder in normalized)
        {
            WatchedFolders.Add(folder);
        }

        _settings.SetWatchedFolders(WatchedFolders.ToList());
        AppTraceLogger.Log("LibraryShell", $"UpdateWatchedFolders saved settings. ViewModelCount={WatchedFolders.Count}.");
        CleanupUnlockedFolders();
        if (refreshMedia)
        {
            AppTraceLogger.Log("LibraryShell", "UpdateWatchedFolders queueing media refresh.");
            QueueRefreshMedia(false);
        }
    }

    private void RemoveMediaOutsideWatchedFolders(
        IReadOnlyList<string> previousFolderPaths,
        IReadOnlyList<string> currentFolderPaths)
    {
        var removedFolderPaths = previousFolderPaths
            .Where(previous => currentFolderPaths.All(current => !string.Equals(current, previous, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (removedFolderPaths.Count == 0)
        {
            AppTraceLogger.Log("LibraryShell", "RemoveMediaOutsideWatchedFolders skipped. No removed folders.");
            return;
        }

        var staleIds = _db.GetMediaEntriesUnderFolders(removedFolderPaths)
            .Where(entry => currentFolderPaths.All(folder => !PathHelpers.IsPathUnderFolder(entry.Path, folder)))
            .Select(entry => entry.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        AppTraceLogger.Log("LibraryShell", $"RemoveMediaOutsideWatchedFolders removedFolders=[{string.Join(", ", removedFolderPaths)}], staleMediaCount={staleIds.Count}.");
        if (staleIds.Count == 0)
        {
            return;
        }

        _thumbnails.ClearCacheForMediaIds(staleIds);
        _timelineThumbnails.ClearCacheForMediaIds(staleIds);
        _db.DeleteMedia(staleIds);
        AppTraceLogger.Log("LibraryShell", $"RemoveMediaOutsideWatchedFolders deleted stale media records and caches. DeletedCount={staleIds.Count}.");
    }

    public void RemoveSelectedTagFilter(string tag)
    {
        var existing = SelectedTags.FirstOrDefault(item => string.Equals(item, tag, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            return;
        }

        SelectedTags.Remove(existing);
        QueueRefreshMedia(false);
    }

    public void ToggleSort(MediaSortField field)
    {
        if (SortField == field)
        {
            _sortOrder = SortOrder == MediaSortOrder.Asc ? MediaSortOrder.Desc : MediaSortOrder.Asc;
            OnPropertyChanged(nameof(SortOrder));
            OnPropertyChanged(nameof(SortButtonText));
            OnPropertyChanged(nameof(SortButtonToolTip));
        }
        else
        {
            _sortField = field;
            _sortOrder = MediaSortOrder.Asc;
            OnPropertyChanged(nameof(SortField));
            OnPropertyChanged(nameof(SortOrder));
            OnPropertyChanged(nameof(SortButtonText));
            OnPropertyChanged(nameof(SortButtonToolTip));
        }

        QueueRefreshMedia(true);
    }

    public void SetSort(MediaSortField field, MediaSortOrder order)
    {
        var changed = false;

        if (!EqualityComparer<MediaSortField>.Default.Equals(_sortField, field))
        {
            _sortField = field;
            OnPropertyChanged(nameof(SortField));
            changed = true;
        }

        if (!EqualityComparer<MediaSortOrder>.Default.Equals(_sortOrder, order))
        {
            _sortOrder = order;
            OnPropertyChanged(nameof(SortOrder));
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        OnPropertyChanged(nameof(SortButtonText));
        OnPropertyChanged(nameof(SortButtonToolTip));
        QueueRefreshMedia(true);
    }

    public void SetLibraryPaneResizing(bool isResizing)
    {
        LibraryPaneResizerOpacity = isResizing ? 1 : 0.65;
    }

    public async Task UpdateTagsAsync(IEnumerable<MediaItemViewModel> items, IEnumerable<string> tags, TagUpdateMode mode)
    {
        var mediaItems = items
            .Where(item => item != null)
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        if (mediaItems.Count == 0)
        {
            return;
        }

        var normalized = tags
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        AppTraceLogger.Log("LibraryShell", $"UpdateTagsAsync start. MediaCount={mediaItems.Count}, Mode={mode}, TagCount={normalized.Count}, SearchActive={!string.IsNullOrWhiteSpace(SearchQuery)}, SelectedTagFilters={SelectedTags.Count}.");

        foreach (var media in mediaItems)
        {
            var nextTags = mode == TagUpdateMode.Append
                ? media.Tags.Concat(normalized).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase).ToList()
                : normalized;

            foreach (var tag in media.Tags.ToList())
            {
                _db.RemoveTag(media.Id, tag);
            }

            foreach (var tag in nextTags)
            {
                _db.AddTag(media.Id, tag);
            }

            media.UpdateTags(nextTags);
        }

        if (!string.IsNullOrWhiteSpace(SearchQuery) || SelectedTags.Count > 0)
        {
            await RefreshMediaAsync(true);
        }
        AppTraceLogger.Log("LibraryShell", $"UpdateTagsAsync completed. MediaCount={mediaItems.Count}, Mode={mode}, TagCount={normalized.Count}.");
    }

    public async Task<bool> TryApplyNumpadTagShortcutAsync(int digit, IEnumerable<MediaItemViewModel> items)
    {
        if (digit is < 1 or > 9)
        {
            return false;
        }

        var tag = NumpadTagShortcuts[digit - 1];
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var mediaItems = items
            .Where(item => item != null)
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        if (mediaItems.Count == 0)
        {
            return false;
        }

        var allTagged = mediaItems.All(item => item.Tags.Any(existing => string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase)));
        AppTraceLogger.Log("LibraryShell", $"TryApplyNumpadTagShortcutAsync start. Digit={digit}, Tag='{tag}', MediaCount={mediaItems.Count}, Removing={allTagged}.");
        foreach (var media in mediaItems)
        {
            var nextTags = allTagged
                ? media.Tags
                    .Where(existing => !string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(existing => existing, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : media.Tags
                    .Concat(new[] { tag })
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(existing => existing, StringComparer.OrdinalIgnoreCase)
                    .ToList();

            foreach (var existing in media.Tags.ToList())
            {
                _db.RemoveTag(media.Id, existing);
            }

            foreach (var nextTag in nextTags)
            {
                _db.AddTag(media.Id, nextTag);
            }

            media.UpdateTags(nextTags);
        }

        if (!string.IsNullOrWhiteSpace(SearchQuery) || SelectedTags.Count > 0)
        {
            await RefreshMediaAsync(true);
        }

        StatusMessage = allTagged
            ? mediaItems.Count == 1
                ? $"已移除当前媒体标签：{tag}"
                : $"已移除 {mediaItems.Count} 个媒体的标签：{tag}"
            : mediaItems.Count == 1
                ? $"已为当前媒体追加标签：{tag}"
                : $"已为 {mediaItems.Count} 个媒体追加标签：{tag}";
        AppTraceLogger.Log("LibraryShell", $"TryApplyNumpadTagShortcutAsync completed. Digit={digit}, Tag='{tag}', MediaCount={mediaItems.Count}, Removed={allTagged}.");
        return true;
    }

    public async Task<MediaItemViewModel?> DeleteMediaAsync(IEnumerable<MediaItemViewModel> items)
    {
        var list = items.ToList();
        if (list.Count == 0)
        {
            return SelectedMedia;
        }

        var loadedCount = FilteredMediaItems.Count;
        var selectedId = SelectedMedia?.Id;
        var removedIds = new HashSet<string>(list.Select(item => item.Id), StringComparer.Ordinal);
        var firstRemovedIndex = list
            .Select(item => FilteredMediaItems.IndexOf(item))
            .Where(index => index >= 0)
            .DefaultIfEmpty(FilteredMediaItems.Count)
            .Min();
        AppTraceLogger.Log(
            "LibraryShell",
            $"DeleteMediaAsync start. Requested={list.Count}, LoadedCount={loadedCount}, FirstRemovedIndex={firstRemovedIndex}, SelectedId='{selectedId ?? "<null>"}'.");
        _db.DeleteMedia(list.Select(item => item.Id));

        await RunOnUiThreadAsync(() =>
        {
            var removedCount = FilteredMediaItems.RemoveByIds(removedIds);
            AppTraceLogger.Log(
                "LibraryShell",
                $"DeleteMediaAsync UI removal finished. Removed={removedCount}, Remaining={FilteredMediaItems.Count}.");
        });

        if (!string.IsNullOrWhiteSpace(selectedId) && !removedIds.Contains(selectedId))
        {
            var existingSelection = FilteredMediaItems.FirstOrDefault(item => item.Id == selectedId);
            if (existingSelection != null)
            {
                await RunOnUiThreadAsync(() => SelectedMedia = existingSelection);
                return existingSelection;
            }
        }

        if (FilteredMediaItems.Count == 0)
        {
            await RunOnUiThreadAsync(() => SelectedMedia = null);
            return null;
        }

        var fallbackIndex = Math.Clamp(firstRemovedIndex, 0, FilteredMediaItems.Count - 1);
        var replacement = FilteredMediaItems[fallbackIndex];
        await RunOnUiThreadAsync(() => SelectedMedia = replacement);
        AppTraceLogger.Log(
            "LibraryShell",
            $"DeleteMediaAsync fallback selection applied. FallbackIndex={fallbackIndex}, ReplacementId='{replacement.Id}', RemainingLoaded={FilteredMediaItems.Count}, PreviousLoaded={loadedCount}.");
        return replacement;
    }

    public async Task RefreshMediaAsync(bool preserveSelection, bool updateLoadingState = true)
    {
        var refreshVersion = Interlocked.Increment(ref _refreshVersion);
        var selectedId = preserveSelection ? SelectedMedia?.Id : null;
        AppTraceLogger.LogSampled(
            "LibraryShell",
            "refresh-media-start",
            $"RefreshMediaAsync start. Version={refreshVersion}, PreserveSelection={preserveSelection}, UpdateLoadingState={updateLoadingState}, SelectedId='{selectedId ?? "<null>"}'.",
            TimeSpan.FromSeconds(1));
        if (updateLoadingState)
        {
            await RunOnUiThreadAsync(() => IsLoading = true);
        }

        try
        {
            _mediaFolderSummaries = await QueryMediaFolderSummariesAsync();
            await RunOnUiThreadAsync(RebuildMediaFolderGroups);
            await FilteredMediaItems.RefreshAsync();
            await RunOnUiThreadAsync(RebuildMediaFolderGroups);
            await EnsureExpandedFolderGroupsLoadedAsync();
            if (refreshVersion == _refreshVersion)
            {
                await RestoreSelectionAsync(refreshVersion, selectedId);
            }
            AppTraceLogger.LogSampled(
                "LibraryShell",
                "refresh-media-complete",
                $"RefreshMediaAsync completed. Version={refreshVersion}, ItemCount={FilteredMediaItems.Count}, SelectedId='{SelectedMedia?.Id ?? "<null>"}'.",
                TimeSpan.FromSeconds(1));
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("LibraryShell", $"RefreshMediaAsync failed. Version={refreshVersion}.", ex);
            throw;
        }
        finally
        {
            if (updateLoadingState && refreshVersion == _refreshVersion)
            {
                await RunOnUiThreadAsync(() => IsLoading = false);
            }
        }
    }

    public Task EnsureMediaItemLoadedAsync(int index)
    {
        return FilteredMediaItems.EnsureItemAvailableAsync(index);
    }

    public async Task EnsureNextMediaPageLoadedAsync()
    {
        if (!FilteredMediaItems.HasMoreItems)
        {
            return;
        }

        var targetIndex = FilteredMediaItems.Count + FilteredMediaItems.PageSize - 1;
        await FilteredMediaItems.EnsureItemAvailableAsync(targetIndex);
    }

    public void EnsureMediaFolderExpanded(MediaItemViewModel media)
    {
        if (string.IsNullOrWhiteSpace(media.FolderPath))
        {
            return;
        }

        _folderCollapseStates[media.FolderPath] = false;
        var group = MediaFolderGroups.FirstOrDefault(item => string.Equals(item.FolderPath, media.FolderPath, StringComparison.OrdinalIgnoreCase));
        if (group != null)
        {
            group.IsCollapsed = false;
            if (!group.HasLoadedItems)
            {
                _ = EnsureFolderGroupLoadedAsync(group.FolderPath);
            }
        }
    }

    public bool IsMediaVisibleInLibrary(MediaItemViewModel media)
    {
        var group = MediaFolderGroups.FirstOrDefault(item => string.Equals(item.FolderPath, media.FolderPath, StringComparison.OrdinalIgnoreCase));
        return group == null
            || (!group.IsCollapsed && group.Contains(media.Id));
    }

    public void ToggleAllMediaFolderGroups()
    {
        if (MediaFolderGroups.Count == 0)
        {
            return;
        }

        SetAllMediaFolderGroupsCollapsed(ShouldCollapseAllMediaFolderGroups);
    }

    private void SetAllMediaFolderGroupsCollapsed(bool isCollapsed)
    {
        var folderPaths = MediaFolderGroups
            .Select(group => group.FolderPath)
            .Concat(_mediaFolderSummaries.Select(summary => PathHelpers.NormalizeFolderPath(summary.FolderPath)))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (folderPaths.Count == 0)
        {
            return;
        }

        _isApplyingFolderCollapseState = true;
        try
        {
            foreach (var folderPath in folderPaths)
            {
                _folderCollapseStates[folderPath] = isCollapsed;
            }

            foreach (var group in MediaFolderGroups)
            {
                group.IsCollapsed = isCollapsed;
            }
        }
        finally
        {
            _isApplyingFolderCollapseState = false;
        }

        RaiseMediaFolderGroupToggleProperties();
        if (!isCollapsed)
        {
            _ = EnsureExpandedFolderGroupsLoadedAsync();
        }

        AppTraceLogger.LogSampled(
            "LibraryShell",
            "media-folder-collapse-all",
            $"Media folder collapse all changed. Collapsed={isCollapsed}, GroupCount={MediaFolderGroups.Count}, FolderStateCount={folderPaths.Count}.",
            TimeSpan.FromSeconds(1));
    }

    public async Task EnsureThumbnailAsync(MediaItemViewModel item)
    {
        if (!item.TryBeginThumbnailLoad())
        {
            return;
        }

        var source = await _thumbnails.GetThumbnailAsync(item.Media);
        if (source != null)
        {
            SetThumbnailSafe(item, source);
            return;
        }

        item.ResetThumbnailLoadState();
    }

    public Playlist CreatePlaylist(string name)
    {
        var playlistName = name ?? string.Empty;
        AppTraceLogger.Log("LibraryShell", $"CreatePlaylist requested. NameLength={playlistName.Length}.");
        var playlist = _db.CreatePlaylist(playlistName);
        ReloadPlaylists(SelectedPlaylist?.Id);
        AppTraceLogger.Log("LibraryShell", $"CreatePlaylist completed. PlaylistId='{playlist.Id}', PlaylistCount={Playlists.Count}.");
        return playlist;
    }

    public void RenamePlaylist(string playlistId, string name)
    {
        var playlistName = name ?? string.Empty;
        AppTraceLogger.Log("LibraryShell", $"RenamePlaylist requested. PlaylistId='{playlistId}', NameLength={playlistName.Length}.");
        _db.RenamePlaylist(playlistId, playlistName);
        ReloadPlaylists(playlistId);
        AppTraceLogger.Log("LibraryShell", $"RenamePlaylist completed. PlaylistId='{playlistId}'.");
    }

    public void SetPlaylistColor(string playlistId, string? colorHex)
    {
        AppTraceLogger.Log("LibraryShell", $"SetPlaylistColor requested. PlaylistId='{playlistId}', HasColor={!string.IsNullOrWhiteSpace(colorHex)}.");
        _db.SetPlaylistColor(playlistId, colorHex);
        ReloadPlaylists(playlistId);
        AppTraceLogger.Log("LibraryShell", $"SetPlaylistColor completed. PlaylistId='{playlistId}'.");
    }

    public async Task DeletePlaylistAsync(string playlistId)
    {
        var deletingCurrent = string.Equals(SelectedPlaylist?.Id, playlistId, StringComparison.Ordinal);
        AppTraceLogger.Log("LibraryShell", $"DeletePlaylistAsync start. PlaylistId='{playlistId}', DeletingCurrent={deletingCurrent}.");
        _db.DeletePlaylist(playlistId);
        ReloadPlaylists();

        if (deletingCurrent)
        {
            SelectedPlaylist = null;
        }
        else
        {
            OnPropertyChanged(nameof(CurrentScopeTitle));
        }

        await RefreshMediaAsync(false);
        AppTraceLogger.Log("LibraryShell", $"DeletePlaylistAsync completed. PlaylistId='{playlistId}', PlaylistCount={Playlists.Count}.");
    }

    public void UpdatePlaylistOrder(IReadOnlyList<Playlist> playlists)
    {
        AppTraceLogger.Log("LibraryShell", $"UpdatePlaylistOrder requested. Count={playlists.Count}.");
        for (var i = 0; i < playlists.Count; i++)
        {
            playlists[i].SortOrder = i;
        }

        _db.UpdatePlaylistOrder(playlists.Select(item => item.Id).ToList());
        ReloadPlaylists(SelectedPlaylist?.Id);
        AppTraceLogger.Log("LibraryShell", $"UpdatePlaylistOrder completed. Count={playlists.Count}.");
    }

    public async Task AddMediaToPlaylistAsync(string playlistId, IEnumerable<MediaItemViewModel> items)
    {
        var mediaItems = items.Where(item => item != null).GroupBy(item => item.Id, StringComparer.Ordinal).Select(group => group.First()).ToList();
        AppTraceLogger.Log("LibraryShell", $"AddMediaToPlaylistAsync start. PlaylistId='{playlistId}', MediaCount={mediaItems.Count}, ViewingTarget={string.Equals(SelectedPlaylist?.Id, playlistId, StringComparison.Ordinal)}.");
        _db.AddMediaToPlaylist(playlistId, mediaItems.Select(item => item.Id));
        
        // Only reload if currently viewing this playlist
        if (string.Equals(SelectedPlaylist?.Id, playlistId, StringComparison.Ordinal))
        {
            await RefreshMediaAsync(true);
        }
        else
        {
            UpdatePlaylistMetadata(playlistId);
        }
        AppTraceLogger.Log("LibraryShell", $"AddMediaToPlaylistAsync completed. PlaylistId='{playlistId}', MediaCount={mediaItems.Count}.");
    }

    private void UpdatePlaylistMetadata(string playlistId)
    {
        var playlist = Playlists.FirstOrDefault(p => string.Equals(p.Id, playlistId, StringComparison.Ordinal));
        if (playlist != null)
        {
            OnPropertyChanged(nameof(Playlists));
        }
    }

    public bool AreAllMediaInPlaylist(string playlistId, IEnumerable<string> mediaIds)
    {
        return _db.AreAllMediaInPlaylist(playlistId, mediaIds);
    }

    public async Task RemoveMediaFromSelectedPlaylistAsync(IEnumerable<MediaItemViewModel> items)
    {
        if (SelectedPlaylist == null)
        {
            return;
        }

        var mediaItems = items.Where(item => item != null).GroupBy(item => item.Id, StringComparer.Ordinal).Select(group => group.First()).ToList();
        AppTraceLogger.Log("LibraryShell", $"RemoveMediaFromSelectedPlaylistAsync start. PlaylistId='{SelectedPlaylist.Id}', MediaCount={mediaItems.Count}.");
        _db.RemoveMediaFromPlaylist(SelectedPlaylist.Id, mediaItems.Select(item => item.Id));
        await RefreshMediaAsync(true);
        AppTraceLogger.Log("LibraryShell", $"RemoveMediaFromSelectedPlaylistAsync completed. PlaylistId='{SelectedPlaylist?.Id ?? "<null>"}', MediaCount={mediaItems.Count}.");
    }

    public void SetDataDir(string path)
    {
        var dataDir = path ?? string.Empty;
        AppTraceLogger.Log("LibraryShell", $"SetDataDir requested. HasPath={!string.IsNullOrWhiteSpace(dataDir)}, Length={dataDir.Length}.");
        _settings.SetDataDir(dataDir);
        OnPropertyChanged(nameof(DataDir));
        OnPropertyChanged(nameof(ConfiguredDataDir));
    }

    public void SetPortableMode(bool enabled)
    {
        AppTraceLogger.Log("LibraryShell", $"SetPortableMode requested. Enabled={enabled}.");
        _settings.SetPortableMode(enabled);
        OnPropertyChanged(nameof(PortableMode));
        OnPropertyChanged(nameof(DataDir));
    }

    public void SetLockPassword(string password)
    {
        AppTraceLogger.Log("LibraryShell", $"SetLockPassword requested. HasPassword={!string.IsNullOrWhiteSpace(password)}.");
        _settings.SetLockPassword(password.Trim());
        OnPropertyChanged(nameof(LockPassword));
        OnPropertyChanged(nameof(HasLockPassword));
    }

    public void SetNumpadTagShortcuts(IReadOnlyList<string> shortcuts)
    {
        AppTraceLogger.Log("LibraryShell", $"SetNumpadTagShortcuts requested. ConfiguredCount={shortcuts.Count(shortcut => !string.IsNullOrWhiteSpace(shortcut))}.");
        _settings.SetNumpadTagShortcuts(shortcuts);
        OnPropertyChanged(nameof(NumpadTagShortcuts));
    }

    public IReadOnlyList<WatchedFolder> GetProtectedFolders()
    {
        return WatchedFolders
            .Where(folder => folder.Locked)
            .Select(folder => new WatchedFolder
            {
                Path = folder.Path,
                Locked = folder.Locked
            })
            .ToList();
    }

    public bool IsFolderUnlocked(string folderPath)
    {
        var normalized = PathHelpers.NormalizeFolderPath(folderPath);
        return _sessionUnlockedFolders.Contains(normalized);
    }

    public async Task<bool> UnlockFolderAsync(string folderPath, string password)
    {
        if (!HasLockPassword)
        {
            return false;
        }

        if (!string.Equals(password, LockPassword, StringComparison.Ordinal))
        {
            AppTraceLogger.Log("LibraryShell", $"UnlockFolderAsync failed. Reason=PasswordMismatch, Folder='{folderPath}'.");
            return false;
        }

        _sessionUnlockedFolders.Add(PathHelpers.NormalizeFolderPath(folderPath));
        StatusMessage = $"已解锁 {Path.GetFileName(folderPath)}";
        await RefreshMediaAsync(false);
        AppTraceLogger.Log("LibraryShell", $"UnlockFolderAsync completed. Folder='{folderPath}', UnlockedCount={_sessionUnlockedFolders.Count}.");
        return true;
    }

    public async Task LockFolderAsync(string folderPath)
    {
        _sessionUnlockedFolders.Remove(PathHelpers.NormalizeFolderPath(folderPath));
        StatusMessage = $"已重新锁定 {Path.GetFileName(folderPath)}";
        await RefreshMediaAsync(false);
        AppTraceLogger.Log("LibraryShell", $"LockFolderAsync completed. Folder='{folderPath}', UnlockedCount={_sessionUnlockedFolders.Count}.");
    }

    public async Task RelockAllFoldersAsync()
    {
        var previousCount = _sessionUnlockedFolders.Count;
        _sessionUnlockedFolders.Clear();
        StatusMessage = "所有受保护文件夹已重新锁定。";
        await RefreshMediaAsync(false);
        AppTraceLogger.Log("LibraryShell", $"RelockAllFoldersAsync completed. PreviousUnlockedCount={previousCount}.");
    }

    public async Task ResetLibraryAsync(bool includePlaylists)
    {
        AppTraceLogger.Log("LibraryShell", $"ResetLibraryAsync start. IncludePlaylists={includePlaylists}, LoadedCount={FilteredMediaItems.Count}, PlaylistCount={Playlists.Count}.");
        _db.ClearAllMedia(includePlaylists);
        _thumbnails.ClearCache();
        _timelineThumbnails.ClearCache();
        _sessionUnlockedFolders.Clear();
        ReloadPlaylists();
        SelectedMedia = null;
        if (includePlaylists)
        {
            SelectedPlaylist = null;
        }

        await RefreshMediaAsync(false);
        StatusMessage = includePlaylists ? "媒体库、标签和播放列表已清空。" : "媒体库和标签已清空。";
        AppTraceLogger.Log("LibraryShell", $"ResetLibraryAsync completed. IncludePlaylists={includePlaylists}, LoadedCount={FilteredMediaItems.Count}, PlaylistCount={Playlists.Count}.");
    }

    public void ClearInvalidThumbnailCache()
    {
        AppTraceLogger.Log("LibraryShell", $"ClearInvalidThumbnailCache start. LoadedCount={FilteredMediaItems.Count}.");
        var deleted = _thumbnails.ClearInvalidCache(_db.GetAllMedia());
        StatusMessage = deleted == 0
            ? "没有发现失效的缩略图缓存。"
            : $"已清理 {deleted} 个失效的缩略图缓存。";
        AppTraceLogger.Log("LibraryShell", $"ClearInvalidThumbnailCache completed. DeletedCount={deleted}, LoadedCount={FilteredMediaItems.Count}.");
    }

    public IReadOnlyList<string> GetAllTags()
    {
        return _db.GetAllTags();
    }

    private async Task AddMediaInternalAsync(Func<CancellationToken, Task<int>> loader, string successMessage)
    {
        if (IsScanning)
        {
            AppTraceLogger.Log("LibraryShell", "AddMediaInternalAsync ignored. ScanAlreadyRunning=True.");
            StatusMessage = "已有扫描正在进行，请先取消或等待完成。";
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        using var scanCancellation = new CancellationTokenSource();
        _scanCancellation = scanCancellation;
        Interlocked.Increment(ref _scanSessionId);
        Interlocked.Exchange(ref _pendingIncrementalScanRefresh, 0);
        _lastIncrementalScanRefreshAt = DateTimeOffset.MinValue;
        IsLoading = true;
        await RunOnUiThreadAsync(() =>
        {
            IsLoading = true;
            IsScanning = true;
            ScanProgressValue = 0;
            ScanProgressMaximum = 0;
            ScanCurrentPath = string.Empty;
            ScanProgressIsIndeterminate = false;
        });

        try
        {
            AppTraceLogger.Log("LibraryShell", $"AddMediaInternalAsync scan started. Session={Volatile.Read(ref _scanSessionId)}.");
            var added = await loader(scanCancellation.Token);
            scanCancellation.Token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _scanSessionId);
            await WaitForPendingScanRefreshAsync();
            await FinalizeScanRefreshAsync();
            await RunOnUiThreadAsync(() => StatusMessage = $"{successMessage} 新增或更新 {added} 个媒体。");
            AppTraceLogger.Log("LibraryShell", $"AddMediaInternalAsync scan completed. AddedOrUpdated={added}, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
        }
        catch (OperationCanceledException) when (scanCancellation.IsCancellationRequested)
        {
            Interlocked.Increment(ref _scanSessionId);
            await WaitForPendingScanRefreshAsync();
            await FinalizeScanRefreshAsync();
            await RunOnUiThreadAsync(() => StatusMessage = "扫描已取消，已保留已完成写入的媒体。");
            AppTraceLogger.Log("LibraryShell", $"AddMediaInternalAsync scan canceled. ElapsedMs={stopwatch.ElapsedMilliseconds}.");
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("LibraryShell", $"AddMediaInternalAsync scan failed. ElapsedMs={stopwatch.ElapsedMilliseconds}.", ex);
            throw;
        }
        finally
        {
            if (ReferenceEquals(_scanCancellation, scanCancellation))
            {
                _scanCancellation = null;
            }

            await RunOnUiThreadAsync(() =>
            {
                IsLoading = false;
                IsScanning = false;
                ScanCurrentPath = string.Empty;
                ScanProgressIsIndeterminate = false;
                if (ScanProgressMaximum > 0)
                {
                    ScanProgressValue = ScanProgressMaximum;
                }
            });
        }
    }

    private Task<MediaPageResult> LoadMediaPageAsync(int offset, int limit)
    {
        var visibleFolders = GetVisibleWatchedFolderPaths();
        AppTraceLogger.LogSampled(
            "LibraryShell",
            "load-media-page",
            $"LoadMediaPageAsync requested. Offset={offset}, Limit={limit}, VisibleFolders={visibleFolders.Count}, PlaylistId='{SelectedPlaylist?.Id ?? "<null>"}'.",
            TimeSpan.FromSeconds(1));
        return _library.QueryPageAsync(CreateCurrentMediaQuery(offset, limit, visibleFolders));
    }

    private Task<IReadOnlyList<MediaFolderSummary>> QueryMediaFolderSummariesAsync()
    {
        var visibleFolders = GetVisibleWatchedFolderPaths();
        AppTraceLogger.LogSampled(
            "LibraryShell",
            "load-media-folder-summaries",
            $"QueryMediaFolderSummaries requested. VisibleFolders={visibleFolders.Count}, PlaylistId='{SelectedPlaylist?.Id ?? "<null>"}'.",
            TimeSpan.FromSeconds(1));
        return _library.QueryFolderSummariesAsync(CreateCurrentMediaQuery(0, 0, visibleFolders));
    }

    private MediaQuery CreateCurrentMediaQuery(int offset, int limit, IReadOnlyList<string>? visibleFolders = null)
    {
        return new MediaQuery
        {
            SearchText = SearchQuery,
            SelectedTags = SelectedTags.ToList(),
            PlaylistId = SelectedPlaylist?.Id,
            IncludedFolderPaths = visibleFolders ?? GetVisibleWatchedFolderPaths(),
            ExcludedFolderPaths = GetActiveLockedFolderPaths(),
            SortField = SortField,
            SortOrder = SortOrder,
            Offset = offset,
            Limit = limit
        };
    }

    private void RebuildMediaFolderGroups()
    {
        Interlocked.Exchange(ref _mediaFolderGroupRebuildQueued, 0);
        foreach (var group in MediaFolderGroups)
        {
            group.CollapseChanged -= MediaFolderGroup_CollapseChanged;
        }

        MediaFolderGroups.Clear();
        var loadedGroups = FilteredMediaItems
            .GroupBy(item => item.FolderPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var summaryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var summary in _mediaFolderSummaries)
        {
            var folderPath = PathHelpers.NormalizeFolderPath(summary.FolderPath);
            summaryPaths.Add(folderPath);
            loadedGroups.TryGetValue(folderPath, out var loadedItems);

            var displayName = GetFolderDisplayName(folderPath);
            var isCollapsed = _folderCollapseStates.TryGetValue(folderPath, out var collapsed) && collapsed;
            var group = new MediaFolderGroupViewModel(folderPath, displayName, isCollapsed, summary.Count);
            group.SetItems(loadedItems ?? Enumerable.Empty<MediaItemViewModel>());
            group.CollapseChanged += MediaFolderGroup_CollapseChanged;
            MediaFolderGroups.Add(group);
        }

        foreach (var folderGroup in loadedGroups
            .Where(group => !summaryPaths.Contains(group.Key))
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var first = folderGroup.Value.FirstOrDefault();
            if (first == null)
            {
                continue;
            }

            var isCollapsed = _folderCollapseStates.TryGetValue(first.FolderPath, out var collapsed) && collapsed;
            var group = new MediaFolderGroupViewModel(first.FolderPath, first.FolderDisplayName, isCollapsed, folderGroup.Value.Count);
            group.SetItems(folderGroup.Value);
            group.CollapseChanged += MediaFolderGroup_CollapseChanged;
            MediaFolderGroups.Add(group);
        }

        RaiseMediaFolderGroupToggleProperties();
    }

    private void ScheduleRebuildMediaFolderGroups()
    {
        if (Interlocked.Exchange(ref _mediaFolderGroupRebuildQueued, 1) == 1)
        {
            return;
        }

        if (_dispatcher == null)
        {
            RebuildMediaFolderGroups();
            return;
        }

        if (!_dispatcher.TryEnqueue(RebuildMediaFolderGroups))
        {
            Interlocked.Exchange(ref _mediaFolderGroupRebuildQueued, 0);
            AppTraceLogger.Log("LibraryShell", "ScheduleRebuildMediaFolderGroups failed because DispatcherQueue rejected the callback.");
        }
    }

    private void MediaFolderGroup_CollapseChanged(object? sender, EventArgs e)
    {
        if (sender is not MediaFolderGroupViewModel group)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(group.FolderPath))
        {
            return;
        }

        _folderCollapseStates[group.FolderPath] = group.IsCollapsed;
        if (_isApplyingFolderCollapseState)
        {
            return;
        }

        RaiseMediaFolderGroupToggleProperties();
        AppTraceLogger.LogSampled(
            "LibraryShell",
            $"media-folder-collapse:{group.FolderPath}",
            $"Media folder collapse changed. Folder='{group.FolderPath}', Collapsed={group.IsCollapsed}, Count={group.TotalCount}.",
            TimeSpan.FromSeconds(1));

        if (!group.IsCollapsed && !group.HasLoadedItems)
        {
            _ = EnsureFolderGroupLoadedAsync(group.FolderPath);
        }
    }

    private void RaiseMediaFolderGroupToggleProperties()
    {
        OnPropertyChanged(nameof(HasMediaFolderGroups));
        OnPropertyChanged(nameof(ShouldCollapseAllMediaFolderGroups));
        OnPropertyChanged(nameof(MediaFolderGroupToggleGlyph));
        OnPropertyChanged(nameof(MediaFolderGroupToggleToolTip));
    }

    private async Task EnsureExpandedFolderGroupsLoadedAsync()
    {
        var folderPaths = MediaFolderGroups
            .Where(group => !group.IsCollapsed && !group.HasLoadedItems && group.TotalCount > 0)
            .Select(group => group.FolderPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (folderPaths.Count == 0)
        {
            return;
        }

        AppTraceLogger.LogSampled(
            "LibraryShell",
            "expanded-folder-groups-load",
            $"Expanded folder group load requested. GroupCount={folderPaths.Count}, LoadedCount={FilteredMediaItems.Count}, HasMore={FilteredMediaItems.HasMoreItems}.",
            TimeSpan.FromSeconds(1));

        foreach (var folderPath in folderPaths)
        {
            if (!FilteredMediaItems.HasMoreItems)
            {
                break;
            }

            if (FilteredMediaItems.Any(item => string.Equals(item.FolderPath, folderPath, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            await EnsureFolderGroupLoadedAsync(folderPath);
        }

        await RunOnUiThreadAsync(RebuildMediaFolderGroups);
    }

    private async Task EnsureFolderGroupLoadedAsync(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)
            || !_folderGroupLoadRequests.Add(folderPath))
        {
            return;
        }

        try
        {
            AppTraceLogger.LogSampled(
                "LibraryShell",
                $"folder-group-load:{folderPath}",
                $"Folder group load requested. Folder='{folderPath}', LoadedCount={FilteredMediaItems.Count}, HasMore={FilteredMediaItems.HasMoreItems}.",
                TimeSpan.FromSeconds(1));

            var loadedPages = 0;
            while (FilteredMediaItems.HasMoreItems
                && loadedPages < MaxFolderExpansionLoadPages
                && !FilteredMediaItems.Any(item => string.Equals(item.FolderPath, folderPath, StringComparison.OrdinalIgnoreCase)))
            {
                var beforeCount = FilteredMediaItems.Count;
                await EnsureNextMediaPageLoadedAsync();
                loadedPages++;
                if (FilteredMediaItems.Count == beforeCount)
                {
                    break;
                }
            }

            AppTraceLogger.LogSampled(
                "LibraryShell",
                $"folder-group-load-complete:{folderPath}",
                $"Folder group load completed. Folder='{folderPath}', LoadedPages={loadedPages}, LoadedCount={FilteredMediaItems.Count}, HasItems={FilteredMediaItems.Any(item => string.Equals(item.FolderPath, folderPath, StringComparison.OrdinalIgnoreCase))}, HasMore={FilteredMediaItems.HasMoreItems}.",
                TimeSpan.FromSeconds(1));
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("LibraryShell", $"Folder group load failed. Folder='{folderPath}'.", ex);
        }
        finally
        {
            _folderGroupLoadRequests.Remove(folderPath);
        }
    }

    private static string GetFolderDisplayName(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return "未知文件夹";
        }

        var fileName = Path.GetFileName(folderPath.TrimEnd('/', '\\'));
        return string.IsNullOrWhiteSpace(fileName) ? folderPath : fileName;
    }

    private async Task WaitForPendingScanRefreshAsync()
    {
        await _scanRefreshLock.WaitAsync();
        _scanRefreshLock.Release();
    }

    private void OnScanProgress(ScanProgress progress)
    {
        _ = RunOnUiThreadAsync(() =>
        {
            IsScanning = !progress.IsComplete;
            ScanProgressIsIndeterminate = progress.IsIndeterminate;
            ScanProgressMaximum = progress.Total > 0 ? progress.Total : 1;
            ScanProgressValue = progress.IsIndeterminate ? 0 : progress.Scanned;
            ScanCurrentPath = progress.CurrentPath;

            if (progress.Total > 0)
            {
                StatusMessage = progress.IsComplete
                    ? $"扫描完成 {progress.Scanned}/{progress.Total}"
                    : $"正在扫描 {progress.Scanned}/{progress.Total}";
                return;
            }

            StatusMessage = progress.IsComplete
                ? "扫描完成。"
                : progress.Scanned > 0
                    ? $"正在扫描 {progress.Scanned} 个媒体..."
                    : "准备扫描...";
        });

        if (progress.ShouldRefreshLibrary && !progress.IsComplete)
        {
            RequestIncrementalScanRefresh();
        }
    }

    private void SetThumbnailSafe(MediaItemViewModel item, ImageSource source)
    {
        if (_dispatcher == null || _dispatcher.HasThreadAccess)
        {
            item.Thumbnail = source;
            return;
        }

        _dispatcher.TryEnqueue(() => item.Thumbnail = source);
    }

    private void QueueRefreshMedia(bool preserveSelection)
    {
        _ = RefreshMediaAsync(preserveSelection);
    }

    private void RequestIncrementalScanRefresh()
    {
        if (!CanApplyLiveIncrementalScanRefresh())
        {
            return;
        }

        Interlocked.Exchange(ref _pendingIncrementalScanRefresh, 1);
        var scanSessionId = Volatile.Read(ref _scanSessionId);
        _ = FlushIncrementalScanRefreshAsync(scanSessionId);
    }

    private async Task FlushIncrementalScanRefreshAsync(int scanSessionId)
    {
        if (!await _scanRefreshLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            while (Interlocked.Exchange(ref _pendingIncrementalScanRefresh, 0) == 1)
            {
                if (scanSessionId != Volatile.Read(ref _scanSessionId))
                {
                    return;
                }

                var delay = _lastIncrementalScanRefreshAt + IncrementalScanRefreshInterval - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay);
                }

                if (scanSessionId != Volatile.Read(ref _scanSessionId) || !IsScanning)
                {
                    return;
                }

                await FilteredMediaItems.RefreshLoadedWindowAsync();
                _lastIncrementalScanRefreshAt = DateTimeOffset.UtcNow;
                AppTraceLogger.LogSampled(
                    "LibraryShell",
                    "scan-incremental-merge",
                    $"Applied incremental scan merge. Session={scanSessionId}, LoadedCount={FilteredMediaItems.Count}, SelectedId='{SelectedMedia?.Id ?? "<null>"}'.",
                    TimeSpan.FromSeconds(1));
            }
        }
        finally
        {
            _scanRefreshLock.Release();
        }
    }

    private async Task FinalizeScanRefreshAsync()
    {
        if (CanIncrementallyFinalizeScanRefresh())
        {
            await FilteredMediaItems.RefreshLoadedWindowAsync();
            AppTraceLogger.Log(
                "LibraryShell",
                $"FinalizeScanRefreshAsync applied incremental merge. LoadedCount={FilteredMediaItems.Count}, SelectedId='{SelectedMedia?.Id ?? "<null>"}'.");
            return;
        }

        AppTraceLogger.Log(
            "LibraryShell",
            $"FinalizeScanRefreshAsync fell back to full refresh. LoadedCount={FilteredMediaItems.Count}, SelectedId='{SelectedMedia?.Id ?? "<null>"}'.");
        await RefreshMediaAsync(true);
    }

    private bool CanApplyLiveIncrementalScanRefresh()
    {
        if (SelectedMedia?.Type == MediaType.Video)
        {
            AppTraceLogger.LogSampled(
                "LibraryShell",
                "scan-incremental-skip-video",
                $"Skipped live incremental scan merge because selected media is video. SelectedId='{SelectedMedia.Id}', LoadedCount={FilteredMediaItems.Count}.",
                TimeSpan.FromSeconds(2));
            return false;
        }

        return CanIncrementallyFinalizeScanRefresh();
    }

    private bool CanIncrementallyFinalizeScanRefresh()
    {
        if (SelectedMedia == null)
        {
            return true;
        }

        return FilteredMediaItems
            .Take(FilteredMediaItems.PageSize)
            .Any(item => string.Equals(item.Id, SelectedMedia.Id, StringComparison.Ordinal));
    }

    private IReadOnlyList<string> GetActiveLockedFolderPaths()
    {
        var invisibleFolderPaths = WatchedFolders
            .Where(folder => !folder.Visible)
            .Select(folder => PathHelpers.NormalizeFolderPath(folder.Path));
        
        var lockedFolderPaths = WatchedFolders
            .Where(folder => folder.Locked)
            .Select(folder => PathHelpers.NormalizeFolderPath(folder.Path))
            .Where(path => !_sessionUnlockedFolders.Contains(path));
        
        return invisibleFolderPaths
            .Concat(lockedFolderPaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void CleanupUnlockedFolders()
    {
        var protectedPaths = WatchedFolders
            .Where(folder => folder.Locked)
            .Select(folder => PathHelpers.NormalizeFolderPath(folder.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _sessionUnlockedFolders.RemoveWhere(path => !protectedPaths.Contains(path));
    }

    private void ReloadPlaylists(string? selectedPlaylistId = null)
    {
        var preferredId = selectedPlaylistId ?? SelectedPlaylist?.Id;
        var playlists = _db.GetPlaylists();
        UpdatePlaylistRailDisplayTexts(playlists);

        RunOnUiThread(() =>
        {
            Playlists.Clear();
            foreach (var playlist in playlists)
            {
                Playlists.Add(playlist);
            }

            SelectedPlaylist = string.IsNullOrWhiteSpace(preferredId)
                ? null
                : Playlists.FirstOrDefault(item => string.Equals(item.Id, preferredId, StringComparison.Ordinal));
        });
    }

    private static void UpdatePlaylistRailDisplayTexts(IEnumerable<Playlist> playlists)
    {
        var playlistList = playlists as IList<Playlist> ?? playlists.ToList();
        var initialCounts = playlistList
            .Select(playlist => Playlist.GetRailInitialKey(playlist.Name))
            .Where(initialKey => !string.IsNullOrEmpty(initialKey))
            .GroupBy(initialKey => initialKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var playlist in playlistList)
        {
            var initialKey = Playlist.GetRailInitialKey(playlist.Name);
            var shouldUseTwoTextElements = !string.IsNullOrEmpty(initialKey)
                && initialCounts.TryGetValue(initialKey, out var count)
                && count > 1;

            playlist.UpdateRailDisplayText(shouldUseTwoTextElements);
        }
    }

    private async Task RestoreSelectionAsync(int refreshVersion, string? selectedId)
    {
        if (string.IsNullOrWhiteSpace(selectedId))
        {
            await RunOnUiThreadAsync(() => SelectedMedia = null);
            return;
        }

        var restored = await FindMediaByIdAsync(selectedId, refreshVersion);
        if (refreshVersion != _refreshVersion)
        {
            return;
        }

        await RunOnUiThreadAsync(() => SelectedMedia = restored);
    }

    private async Task<MediaItemViewModel?> FindMediaByIdAsync(string mediaId, int refreshVersion)
    {
        while (refreshVersion == _refreshVersion)
        {
            var existing = FilteredMediaItems.FirstOrDefault(item => item.Id == mediaId);
            if (existing != null)
            {
                return existing;
            }

            if (!FilteredMediaItems.HasMoreItems)
            {
                return null;
            }

            var previousCount = FilteredMediaItems.Count;
            await FilteredMediaItems.EnsureItemAvailableAsync(previousCount);
            if (FilteredMediaItems.Count == previousCount)
            {
                return null;
            }
        }

        return null;
    }

    private Task RunOnUiThreadAsync(Action action)
    {
        if (_dispatcher == null || _dispatcher.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() =>
            {
                try
                {
                    action();
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    AppTraceLogger.LogException("LibraryShell", "RunOnUiThreadAsync action failed.", ex);
                    tcs.SetException(ex);
                }
            }))
        {
            AppTraceLogger.Log("LibraryShell", "RunOnUiThreadAsync failed because DispatcherQueue rejected the callback.");
            tcs.SetException(new InvalidOperationException("无法切换到 UI 线程更新主视图模型。"));
        }

        return tcs.Task;
    }

    private void RunOnUiThread(Action action)
    {
        if (_dispatcher == null || _dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        _dispatcher.TryEnqueue(() => action());
    }

    private void RaiseScanUiProperties()
    {
        OnPropertyChanged(nameof(ScanProgressVisibility));
        OnPropertyChanged(nameof(ScanCancelButtonVisibility));
        OnPropertyChanged(nameof(ScanPathVisibility));
    }
}
