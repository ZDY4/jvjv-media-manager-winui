using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.ComponentModel;
using ObservablePropertyAttribute = CommunityToolkit.Mvvm.ComponentModel.ObservablePropertyAttribute;
using NotifyPropertyChangedForAttribute = CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedForAttribute;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using JvJvMediaManager.Data;
using JvJvMediaManager.Models;
using JvJvMediaManager.Services;
using JvJvMediaManager.Services.MainPage;
using JvJvMediaManager.Utilities;
using JvJvMediaManager.ViewModels;

namespace JvJvMediaManager.ViewModels.MainPage;

public sealed partial class LibraryShellViewModel : ObservableObject, IDisposable
{
    private readonly SettingsService _settings;
    private readonly SettingsAndStateManager _settingsAndState;
    private readonly PlaylistManager _playlistManager;
    private readonly WatchedFolderManager _watchedFolderManager = new();
    private readonly MediaDb _db;
    private readonly MediaLibraryService _library;
    private readonly ThumbnailService _thumbnails;
    private readonly TimelineThumbnailStripService _timelineThumbnails;
    private readonly MediaItemStore _mediaItemStore = new();
    private readonly MediaPageStore _mediaPageStore;
    private readonly MediaFolderHierarchyManager _folderHierarchyManager = new();
    private readonly MediaLibraryRefreshOrchestrator _refreshOrchestrator;
    private readonly WatchedFolderMonitorService _watchedFolderMonitor = new();
    private readonly Dictionary<string, MediaFolderGroupViewModel> _mediaFolderGroupsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<MediaItemViewModel>> _folderGroupDirectItemsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _persistMediaLibraryViewState;
    private readonly bool _enableWatchedFolderMonitoring;
    private bool _folderWatchersReady;
    private readonly HashSet<string> _sessionUnlockedFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _folderCollapseStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _recentUserCollapsedFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _folderGroupLoadRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _pendingDeletedMediaIdsSync = new();
    private readonly HashSet<string> _pendingDeletedMediaIds = new(StringComparer.Ordinal);
    private readonly LibraryPersistenceCoordinator _persistenceCoordinator;
    private IReadOnlyList<MediaFolderSummary> _mediaFolderSummaries = Array.Empty<MediaFolderSummary>();
    private readonly SelectionViewModel _selection;
    private readonly SemaphoreSlim _scanRefreshLock = new(1, 1);
    private readonly SemaphoreSlim _refreshMediaLock = new(1, 1);
    // WP-D: re-querying the loaded window + folder summaries is expensive on
    // large libraries; once per second is plenty for live scan progress.
    private static readonly TimeSpan IncrementalScanRefreshInterval = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan MediaLibraryViewStateSaveDelay = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan RecentUserFolderCollapseAutoExpandSuppression = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WatcherImportStabilityWindow = TimeSpan.FromMilliseconds(1200);
    private const int MaxFolderExpansionLoadPages = 100;
    private const int MaxExpandAllAutoLoadGroups = 3;
    private const int MaxStartupCacheCheckFolders = 8;
    private const double MinLibraryPaneWidth = 240;
    private const double MinPlayerPaneWidth = 280;

    private IUiThreadDispatcher _dispatcher = new UiThreadDispatcher(null, "LibraryShell");
    private int _refreshVersion;
    private int _scanSessionId;
    private int _pendingIncrementalScanRefresh;
    private int _mediaFolderGroupRebuildQueued;
    private int _suppressMediaFolderGroupRebuildSchedule;
    // WP-D: rebuilds are rate-limited so bursts coalesce; see ScheduleRebuildMediaFolderGroups.
    private static readonly TimeSpan MediaFolderGroupRebuildMinInterval = TimeSpan.FromMilliseconds(300);
    private DateTimeOffset _lastMediaFolderGroupRebuildCompletedAt = DateTimeOffset.MinValue;
    private bool _suppressViewStatePersistence = true;
    private bool _isApplyingFolderCollapseState;
    private bool _hasAppliedInitialLibraryPaneRatio;
    private long _scanProgressSequence;
    private double? _libraryPaneHostWidth;
    private double? _libraryPaneRatio;
    private int _folderWatcherProcessing;
    private bool _isDisposed;
    private DateTimeOffset _lastIncrementalScanRefreshAt = DateTimeOffset.MinValue;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _viewStateSaveCancellation;
    private readonly Dictionary<string, WatcherFileObservation> _watcherFileObservations = new(StringComparer.OrdinalIgnoreCase);

    private string _searchQuery = string.Empty;
    private bool _isLoading;
    private string _statusMessage = string.Empty;
    private MediaSortField _sortField = MediaSortField.ModifiedAt;
    private MediaSortOrder _sortOrder = MediaSortOrder.Desc;
    private MediaType? _mediaTypeFilter;
    private int _iconSize = 120;
    private Playlist? _selectedPlaylist;
    private bool _isScanning;
    private int _scanProgressValue;
    private int _scanProgressMaximum;
    private string _scanCurrentPath = string.Empty;
    private bool _scanProgressIsIndeterminate;
    private double _libraryPaneWidth = 360;
    private double _libraryPaneResizerOpacity = 0.65;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListViewVisibility))]
    [NotifyPropertyChangedFor(nameof(GridViewVisibility))]
    [NotifyPropertyChangedFor(nameof(ViewModeToggleGlyph))]
    [NotifyPropertyChangedFor(nameof(ViewModeToggleToolTip))]
    public partial MediaViewMode ViewMode { get; set; } = MediaViewMode.List;

    private bool _isBossMode;

    public bool IsBossMode
    {
        get => _isBossMode;
        set
        {
            if (SetProperty(ref _isBossMode, value))
            {
                OnPropertyChanged(nameof(ListViewVisibility));
                OnPropertyChanged(nameof(GridViewVisibility));
                OnPropertyChanged(nameof(MediaBrowserVisibility));
            }
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LibraryPaneExpandedVisibility))]
    [NotifyPropertyChangedFor(nameof(LibraryPaneResizerVisibility))]
    public partial bool IsLibraryPaneOpen { get; set; } = true;

    public LibraryShellViewModel(SelectionViewModel selection)
        : this(selection, LibraryShellServices.CreateDefault())
    {
    }

    public LibraryShellViewModel(
        SelectionViewModel selection,
        LibraryShellServices services,
        bool persistMediaLibraryViewState = true,
        bool enableWatchedFolderMonitoring = true)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(services);

        _selection = selection;
        _persistMediaLibraryViewState = persistMediaLibraryViewState;
        _enableWatchedFolderMonitoring = enableWatchedFolderMonitoring;
        _settings = services.Settings;
        _settingsAndState = new SettingsAndStateManager(_settings);
        _db = services.Database;
        _playlistManager = new PlaylistManager(_db);
        _library = services.Library;
        _thumbnails = services.Thumbnails;
        _timelineThumbnails = services.TimelineThumbnails;
        _persistenceCoordinator = new LibraryPersistenceCoordinator(
            _db,
            _thumbnails,
            _timelineThumbnails,
            RemovePendingDeletedMediaIds);
        if (persistMediaLibraryViewState)
        {
            // WP-06: bound cache growth from previous sessions without blocking
            // first paint. Only the primary shell triggers this.
            _thumbnails.ScheduleStartupQuotaCheck();
            _timelineThumbnails.ScheduleStartupQuotaCheck();
        }
        var viewState = _settingsAndState.LoadMediaLibraryViewState();
        ViewMode = viewState.ViewMode;
        _sortField = viewState.SortField;
        _sortOrder = viewState.SortOrder;
        _mediaTypeFilter = viewState.TypeFilter;
        _iconSize = viewState.IconSize;
        IsLibraryPaneOpen = viewState.IsLibraryPaneOpen;
        _libraryPaneWidth = viewState.LibraryPaneWidth;
        _libraryPaneRatio = viewState.LibraryPaneRatio;
        foreach (var pair in viewState.FolderCollapseStates)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key))
            {
                _folderCollapseStates[PathHelpers.NormalizeFolderPath(pair.Key)] = pair.Value;
            }
        }

        _mediaPageStore = new MediaPageStore(_library, _mediaItemStore.GetOrUpdateRange);
        _refreshOrchestrator = new MediaLibraryRefreshOrchestrator(ExecuteRefreshRequestAsync);
        FilteredMediaItems = _mediaPageStore.Items;
        FilteredMediaItems.CollectionChanged += FilteredMediaItems_CollectionChanged;
        WatchedFolders = new ObservableCollection<WatchedFolder>(_settingsAndState.LoadWatchedFolders());
        Playlists = new ObservableCollection<Playlist>();
        SelectedTags.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SelectedTagsVisibility));
        _suppressViewStatePersistence = false;
    }

    public IncrementalMediaCollection FilteredMediaItems { get; }

    public ObservableCollection<MediaFolderGroupViewModel> MediaFolderGroups { get; } = new MediaFolderGroupCollection();

    public ObservableCollection<WatchedFolder> WatchedFolders { get; }

    public ObservableCollection<string> SelectedTags { get; } = new();

    public ObservableCollection<Playlist> Playlists { get; }

    private List<string> GetVisibleWatchedFolderPaths()
    {
        return _watchedFolderManager.GetVisibleFolderPaths(WatchedFolders);
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
            AppTraceLogger.LogSampled(
                "LibraryShell",
                "selected-media-set",
                $"SelectedMedia set. MediaId='{value?.Id ?? "<null>"}', File='{value?.FileName ?? "<null>"}'.",
                TimeSpan.FromMilliseconds(300));
        }
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                QueueRefreshMedia(false, LibraryRefreshSource.UserFilter);
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

    public MediaSortField SortField
    {
        get => _sortField;
        set
        {
            if (SetProperty(ref _sortField, value))
            {
                OnPropertyChanged(nameof(SortButtonText));
                OnPropertyChanged(nameof(SortButtonToolTip));
                SaveMediaBrowserSort();
                QueueRefreshMedia(true, LibraryRefreshSource.UserFilter);
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
                SaveMediaBrowserSort();
                QueueRefreshMedia(true, LibraryRefreshSource.UserFilter);
            }
        }
    }

    public MediaType? MediaTypeFilter
    {
        get => _mediaTypeFilter;
        set
        {
            if (!SetProperty(ref _mediaTypeFilter, value))
            {
                return;
            }

            OnPropertyChanged(nameof(MediaTypeFilterButtonText));
            OnPropertyChanged(nameof(MediaTypeFilterButtonToolTip));
            SaveMediaBrowserTypeFilter();
            QueueRefreshMedia(false, LibraryRefreshSource.UserFilter);
        }
    }

    public int IconSize
    {
        get => _iconSize;
        set
        {
            var normalized = Math.Clamp(value, 72, 260);
            if (SetProperty(ref _iconSize, normalized))
            {
                SaveMediaBrowserIconSize();
            }
        }
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
                    QueueRefreshMedia(false, LibraryRefreshSource.UserFilter);
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

    public IReadOnlyList<string> NumpadTagShortcuts => _settingsAndState.LoadNumpadTagShortcuts();

    public bool HasLockPassword => !string.IsNullOrWhiteSpace(LockPassword);

    public string CurrentScopeTitle => SelectedPlaylist?.Name ?? "全部媒体";

    public double LibraryPaneWidth
    {
        get => _libraryPaneWidth;
        set => SetProperty(ref _libraryPaneWidth, ClampLibraryPaneWidth(value, _libraryPaneHostWidth));
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

    public string MediaTypeFilterButtonText => MediaTypeFilter switch
    {
        MediaType.Image => "图片",
        MediaType.Video => "视频",
        _ => "全部"
    };

    public string MediaTypeFilterButtonToolTip => MediaTypeFilter switch
    {
        MediaType.Image => "只显示图片",
        MediaType.Video => "只显示视频",
        _ => "显示图片和视频"
    };

    public Visibility ListViewVisibility => IsBossMode ? Visibility.Collapsed : (ViewMode == MediaViewMode.List ? Visibility.Visible : Visibility.Collapsed);

    public Visibility GridViewVisibility => IsBossMode ? Visibility.Collapsed : (ViewMode == MediaViewMode.Grid ? Visibility.Visible : Visibility.Collapsed);

    public Visibility MediaBrowserVisibility => IsBossMode ? Visibility.Collapsed : Visibility.Visible;

    public void SetDispatcher(DispatcherQueue dispatcher)
    {
        _dispatcher = new UiThreadDispatcher(dispatcher, "LibraryShell");
        _mediaPageStore.SetDispatcher(dispatcher);
    }

    partial void OnViewModeChanged(MediaViewMode value)
    {
        if (!_suppressViewStatePersistence)
        {
            SaveMediaBrowserViewMode();
        }
    }

    partial void OnIsLibraryPaneOpenChanged(bool value)
    {
        if (!_suppressViewStatePersistence)
        {
            SaveLibraryPaneLayout();
        }
    }

    public async Task InitializeAsync()
    {
        AppTraceLogger.Log("LibraryShell", "InitializeAsync start.");
        _isDisposed = false;
        // First paint should not wait for duplicate-merge / FTS / CREATE INDEX.
        // Folder watchers stay unarmed until maintenance finishes so they cannot
        // write the DB while those jobs still hold locks.
        var maintenancePending = !_db.StartupMaintenanceTask.IsCompleted;
        await ReloadPlaylistsAsync();
        await RefreshMediaAsync(false);
        if (maintenancePending)
        {
            AppTraceLogger.Log("LibraryShell", "First library page shown before startup maintenance completed.");
            await _db.StartupMaintenanceTask;
            if (!_isDisposed)
            {
                await RefreshMediaAsync(true, updateLoadingState: false);
            }
        }
        else
        {
            await _db.StartupMaintenanceTask;
        }

        if (_isDisposed)
        {
            return;
        }

        ArmFolderWatchers();
        ReportInterruptedScanIfNeeded();
        QueueStartupCacheValidation();
        AppTraceLogger.Log("LibraryShell", $"InitializeAsync completed. LoadedCount={FilteredMediaItems.Count}, PlaylistCount={Playlists.Count}.");
    }

    public void Dispose()
    {
        if (_persistMediaLibraryViewState)
        {
            FlushMediaLibraryViewState();
        }

        _isDisposed = true;
        FilteredMediaItems.CollectionChanged -= FilteredMediaItems_CollectionChanged;
        DrainDeletedMediaPersistenceCleanupTasks();
        _viewStateSaveCancellation?.Cancel();
        _viewStateSaveCancellation?.Dispose();
        _viewStateSaveCancellation = null;
        _refreshOrchestrator.Dispose();
        if (_enableWatchedFolderMonitoring)
        {
            _watchedFolderMonitor.ChangesReady -= WatchedFolderMonitor_ChangesReady;
            _watchedFolderMonitor.Dispose();
        }
        _folderWatchersReady = false;
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
        var completed = await AddMediaInternalAsync(
            cancellationToken => _library.AddFolderAsync(path, new Progress<ScanProgress>(OnScanProgress), cancellationToken),
            "文件夹导入完成。");
        if (completed)
        {
            SaveWatchedFolderScanSnapshots(new[] { path });
        }
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
        var completed = await AddMediaInternalAsync(
            cancellationToken => _library.RescanFoldersAsync(visibleFolders, new Progress<ScanProgress>(OnScanProgress), cancellationToken),
            "媒体库刷新完成。");
        if (completed)
        {
            SaveWatchedFolderScanSnapshots(visibleFolders);
        }
    }

    public async Task RescanFolderAsync(string folderPath)
    {
        var normalizedFolderPath = PathHelpers.NormalizeFolderPath(folderPath);
        if (string.IsNullOrWhiteSpace(normalizedFolderPath))
        {
            AppTraceLogger.Log("LibraryShell", "RescanFolderAsync skipped. FolderPathEmpty=True.");
            return;
        }

        AppTraceLogger.Log("LibraryShell", $"RescanFolderAsync requested. FolderPath='{normalizedFolderPath}'.");
        var completed = await AddMediaInternalAsync(
            cancellationToken => _library.RescanFoldersAsync(new[] { normalizedFolderPath }, new Progress<ScanProgress>(OnScanProgress), cancellationToken),
            "文件夹刷新完成。");
        if (completed)
        {
            SaveWatchedFolderScanSnapshots(new[] { normalizedFolderPath });
        }
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

    public async Task UpdateWatchedFoldersAsync(IEnumerable<WatchedFolder> folders, bool refreshMedia = true)
    {
        var requestedFolders = folders.ToList();
        var previousFolderPaths = _watchedFolderManager.GetFolderPaths(WatchedFolders);
        AppTraceLogger.Log("LibraryShell", $"UpdateWatchedFolders start. RequestedCount={requestedFolders.Count}, PreviousCount={previousFolderPaths.Count}, RefreshMedia={refreshMedia}.");
        var normalized = _watchedFolderManager.Normalize(requestedFolders);
        var currentFolderPaths = _watchedFolderManager.GetFolderPaths(normalized);
        AppTraceLogger.Log("LibraryShell", $"UpdateWatchedFolders normalized. CurrentCount={currentFolderPaths.Count}, Current=[{string.Join(", ", currentFolderPaths)}].");

        WatchedFolders.Clear();
        foreach (var folder in normalized)
        {
            WatchedFolders.Add(folder);
        }

        _settingsAndState.SaveWatchedFolders(WatchedFolders.ToList());
        AppTraceLogger.Log("LibraryShell", $"UpdateWatchedFolders saved settings. ViewModelCount={WatchedFolders.Count}.");
        UpdateWatchedFolderWatchers();
        CleanupUnlockedFolders();
        if (refreshMedia)
        {
            AppTraceLogger.Log("LibraryShell", "UpdateWatchedFolders queueing media refresh.");
            QueueRefreshMedia(false, LibraryRefreshSource.WatchedFolderSettings);
        }

        await RemoveMediaOutsideWatchedFoldersAsync(previousFolderPaths, currentFolderPaths);
    }

    public async Task UpdateWatchedFoldersAndImportNewAsync(IEnumerable<WatchedFolder> folders)
    {
        var requestedFolders = folders.ToList();
        var newVisibleFolders = _watchedFolderManager.GetNewVisibleFolders(WatchedFolders, requestedFolders);

        await UpdateWatchedFoldersAsync(requestedFolders, refreshMedia: newVisibleFolders.Count == 0);

        if (newVisibleFolders.Count == 0)
        {
            AppTraceLogger.Log("LibraryShell", "UpdateWatchedFoldersAndImportNewAsync completed without new visible folders.");
            return;
        }

        AppTraceLogger.Log("LibraryShell", $"UpdateWatchedFoldersAndImportNewAsync importing new folders. Count={newVisibleFolders.Count}.");
        foreach (var folderPath in newVisibleFolders)
        {
            if (!Directory.Exists(PathHelpers.ToNativePath(folderPath)))
            {
                AppTraceLogger.Log("LibraryShell", $"Skipped importing new watched folder because it does not exist. Folder='{folderPath}'.");
                continue;
            }

            if (IsScanning)
            {
                AppTraceLogger.Log("LibraryShell", $"Stopped importing new watched folders because a scan is already running. RemainingFolder='{folderPath}'.");
                StatusMessage = "已保存监控文件夹，当前已有扫描在进行，稍后可手动刷新新增文件夹。";
                break;
            }

            await AddFolderAsync(folderPath);
        }
    }

    private async Task RemoveMediaOutsideWatchedFoldersAsync(
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

        AppTraceLogger.Log("LibraryShell", $"RemoveMediaOutsideWatchedFolders removedFolders=[{string.Join(", ", removedFolderPaths)}].");
        if (currentFolderPaths.Count == 0)
        {
            _thumbnails.ClearCache();
            _timelineThumbnails.ClearCache();
        }

        await Task.Run(() => _db.DeleteMediaUnderFoldersAsync(removedFolderPaths, currentFolderPaths));
        AppTraceLogger.Log("LibraryShell", "RemoveMediaOutsideWatchedFolders deleted stale media records.");
    }

    private void ArmFolderWatchers()
    {
        if (_isDisposed || !_enableWatchedFolderMonitoring || _folderWatchersReady)
        {
            return;
        }

        _folderWatchersReady = true;
        _watchedFolderMonitor.ChangesReady += WatchedFolderMonitor_ChangesReady;
        UpdateWatchedFolderWatchers();
        AppTraceLogger.Log("LibraryShell", "Folder watchers armed after startup maintenance.");
    }

    private void UpdateWatchedFolderWatchers()
    {
        if (_isDisposed || !_enableWatchedFolderMonitoring || !_folderWatchersReady)
        {
            return;
        }

        _watchedFolderMonitor.SetWatchedFolders(WatchedFolders.Select(item => item.Path));
    }

    private void WatchedFolderMonitor_ChangesReady(object? sender, WatchedFolderChangesEventArgs e)
    {
        _ = ProcessFolderWatcherChangesAsync(e);
    }

    private async Task ProcessFolderWatcherChangesAsync(WatchedFolderChangesEventArgs changes)
    {
        if (_isDisposed)
        {
            return;
        }

        if (Interlocked.Exchange(ref _folderWatcherProcessing, 1) == 1)
        {
            AppTraceLogger.LogSampled(
                "LibraryShell",
                "folder-watcher-requeue-busy",
                $"Requeued folder watcher batch because processing is busy. BatchId={changes.BatchId}, Imports={changes.Imports.Count}, Deletes={changes.Deletes.Count}, Relocations={changes.Relocations.Count}.",
                TimeSpan.FromSeconds(1));
            _watchedFolderMonitor.Requeue(changes);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (IsScanning)
            {
                AppTraceLogger.LogSampled(
                    "LibraryShell",
                    "folder-watcher-requeue-scanning",
                    $"Requeued folder watcher batch because scan is active. BatchId={changes.BatchId}, Imports={changes.Imports.Count}, Deletes={changes.Deletes.Count}, Relocations={changes.Relocations.Count}.",
                    TimeSpan.FromSeconds(1));
                _watchedFolderMonitor.Requeue(changes);
                return;
            }

            var changed = false;
            var relocationIds = await ResolveMediaIdsForWatcherPathsAsync(changes.Relocations);
            if (relocationIds.Count > 0)
            {
                await _db.RemoveMediaRecordsOnlyAsync(relocationIds);
                changed = true;
            }

            var (importTargets, deferredImportPaths) = await ResolveEffectiveWatcherImportPathsAsync(changes.Imports);
            if (deferredImportPaths.Count > 0)
            {
                _watchedFolderMonitor.Requeue(new WatchedFolderChangesEventArgs(
                    changes.BatchId,
                    deferredImportPaths,
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    changes.FirstEventAt));
            }
            var effectiveDeletePaths = ResolveEffectiveWatcherDeletePaths(
                changes.Deletes,
                importTargets.Concat(deferredImportPaths));
            var shadowedDeleteCount = changes.Deletes.Count - effectiveDeletePaths.Count;
            var deletedIds = await ResolveMediaIdsForWatcherPathsAsync(effectiveDeletePaths);
            if (deletedIds.Count > 0)
            {
                await _db.DeleteMediaAsync(deletedIds);
                _thumbnails.ClearCacheForMediaIds(deletedIds);
                _timelineThumbnails.ClearCacheForMediaIds(deletedIds);
                changed = true;
            }

            if (importTargets.Count > 0)
            {
                var persisted = await _library.AddFilesAsync(
                    importTargets,
                    new Progress<ScanProgress>(progress =>
                    {
                        if (progress.IsBackgroundUpdate)
                        {
                            OnBackgroundLibraryProgress(progress);
                        }
                    }));
                if (persisted > 0)
                {
                    changed = true;
                }
                else
                {
                    AppTraceLogger.LogSampled(
                        "LibraryShell",
                        "folder-watcher-imports-no-persist",
                        $"Folder watcher imports produced no persisted media. BatchId={changes.BatchId}, ImportTargets={importTargets.Count}.",
                        TimeSpan.FromSeconds(2));
                }
            }

            if (changed)
            {
                var affectedFolders = ResolveWatchedFoldersForWatcherPaths(
                    changes.Imports
                        .Concat(effectiveDeletePaths)
                        .Concat(changes.Relocations));
                SaveWatchedFolderScanSnapshots(affectedFolders);
                QueueRefreshMedia(
                    true,
                    LibraryRefreshSource.FolderWatcher,
                    LibraryRefreshKind.LoadedWindowRefresh,
                    updateLoadingState: false);
                QueueRefreshMedia(
                    true,
                    LibraryRefreshSource.FolderWatcher,
                    LibraryRefreshKind.FolderSummaryRefresh,
                    updateLoadingState: false);
                AppTraceLogger.LogSampled(
                    "LibraryShell",
                    "folder-watcher-processed",
                    $"Processed folder watcher changes. BatchId={changes.BatchId}, Imports={importTargets.Count}, Deletes={effectiveDeletePaths.Count}, ShadowedDeletes={shadowedDeleteCount}, Relocations={changes.Relocations.Count}, DeletedIds={deletedIds.Count}, RelocationIds={relocationIds.Count}, AffectedFolders={affectedFolders.Count}, Changed={changed}, ElapsedMs={stopwatch.ElapsedMilliseconds}.",
                    TimeSpan.FromSeconds(2));
            }
            else
            {
                AppTraceLogger.LogSampled(
                    "LibraryShell",
                    "folder-watcher-noop",
                    $"Processed folder watcher changes with no library changes. BatchId={changes.BatchId}, Imports={changes.Imports.Count}, Deletes={effectiveDeletePaths.Count}, ShadowedDeletes={shadowedDeleteCount}, Relocations={changes.Relocations.Count}, ElapsedMs={stopwatch.ElapsedMilliseconds}.",
                    TimeSpan.FromSeconds(2));
            }
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("LibraryShell", "ProcessFolderWatcherChangesAsync failed.", ex);
            if (!_isDisposed)
            {
                // Do not silently lose a watcher batch when a transient file
                // or database error occurs. The monitor debounces this requeue
                // and coalesces it with any newer filesystem events.
                _watchedFolderMonitor.Requeue(changes);
                AppTraceLogger.LogSampled(
                    "LibraryShell",
                    "folder-watcher-requeued-after-failure",
                    $"Requeued failed folder watcher batch. BatchId={changes.BatchId}, Imports={changes.Imports.Count}, Deletes={changes.Deletes.Count}, Relocations={changes.Relocations.Count}.",
                    TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            Interlocked.Exchange(ref _folderWatcherProcessing, 0);
        }
    }

    private static IReadOnlyList<string> ResolveEffectiveWatcherDeletePaths(
        IEnumerable<string> deletePaths,
        IEnumerable<string> liveImportPaths)
    {
        var liveImports = liveImportPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(PathHelpers.NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return deletePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(PathHelpers.NormalizePath)
            .Where(path => !liveImports.Contains(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<(IReadOnlyList<string> Targets, IReadOnlyList<string> Deferred)> ResolveEffectiveWatcherImportPathsAsync(IEnumerable<string> importPaths)
    {
        var targets = new List<string>();
        var deferred = new List<string>();
        var skippedExistingUnchanged = 0;
        var skippedUnsupported = 0;
        var skippedMissing = 0;
        var skippedUnstable = 0;

        foreach (var path in importPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(PathHelpers.NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var nativePath = PathHelpers.ToNativePath(path);
            if (Directory.Exists(nativePath))
            {
                targets.Add(path);
                continue;
            }

            if (!File.Exists(nativePath))
            {
                _watcherFileObservations.Remove(path);
                skippedMissing++;
                continue;
            }

            if (!MediaExtensions.IsSupported(nativePath))
            {
                _watcherFileObservations.Remove(path);
                skippedUnsupported++;
                continue;
            }

            if (!IsWatcherImportStable(path, nativePath))
            {
                deferred.Add(path);
                skippedUnstable++;
                continue;
            }

            if (await IsWatcherImportExistingUnchangedAsync(path, nativePath))
            {
                _watcherFileObservations.Remove(path);
                skippedExistingUnchanged++;
                continue;
            }

            _watcherFileObservations.Remove(path);
            targets.Add(path);
        }

        if (skippedExistingUnchanged > 0 || skippedUnsupported > 0 || skippedMissing > 0 || skippedUnstable > 0)
        {
            AppTraceLogger.LogSampled(
                "LibraryShell",
                "folder-watcher-imports-filtered",
                $"Filtered folder watcher import paths. Targets={targets.Count}, DeferredUnstable={skippedUnstable}, SkippedExistingUnchanged={skippedExistingUnchanged}, SkippedUnsupported={skippedUnsupported}, SkippedMissing={skippedMissing}.",
                TimeSpan.FromSeconds(2));
        }

        return (targets, deferred);
    }

    private bool IsWatcherImportStable(string normalizedPath, string nativePath)
    {
        try
        {
            var info = new FileInfo(nativePath);
            var now = DateTimeOffset.UtcNow;
            var observation = new WatcherFileObservation(info.Length, info.LastWriteTimeUtc, now);
            if (!_watcherFileObservations.TryGetValue(normalizedPath, out var previous)
                || previous.Length != observation.Length
                || previous.LastWriteTimeUtc != observation.LastWriteTimeUtc)
            {
                _watcherFileObservations[normalizedPath] = observation;
                return false;
            }

            return now - previous.FirstObservedAt >= WatcherImportStabilityWindow;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> IsWatcherImportExistingUnchangedAsync(string normalizedPath, string nativePath)
    {
        MediaFile? existing;
        try
        {
            existing = await _db.GetMediaByPathAsync(normalizedPath);
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogSampled(
                "LibraryShell",
                "folder-watcher-existing-check-failed",
                $"Could not check existing watcher import. Path='{normalizedPath}', ErrorType={ex.GetType().Name}, Message='{ex.Message}'.",
                TimeSpan.FromSeconds(5));
            return false;
        }

        if (existing == null)
        {
            return false;
        }

        try
        {
            var info = new FileInfo(nativePath);
            if (!info.Exists)
            {
                return false;
            }

            var modifiedAt = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();
            return existing.Size == info.Length && existing.ModifiedAt == modifiedAt;
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogSampled(
                "LibraryShell",
                "folder-watcher-file-info-failed",
                $"Could not read watcher import file info. Path='{normalizedPath}', ErrorType={ex.GetType().Name}, Message='{ex.Message}'.",
                TimeSpan.FromSeconds(5));
            return false;
        }
    }

    private async Task<IReadOnlyList<string>> ResolveMediaIdsForWatcherPathsAsync(IEnumerable<string> paths)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var normalizedPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(PathHelpers.NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var path in normalizedPaths)
        {
            var media = await _db.GetMediaByPathAsync(path);
            if (!string.IsNullOrWhiteSpace(media?.Id))
            {
                ids.Add(media.Id);
            }
        }

            foreach (var entry in await _db.GetMediaEntriesUnderFoldersAsync(normalizedPaths))
        {
            if (!string.IsNullOrWhiteSpace(entry.Id))
            {
                ids.Add(entry.Id);
            }
        }

        return ids.ToList();
    }

    private IReadOnlyList<string> ResolveWatchedFoldersForWatcherPaths(IEnumerable<string> paths)
    {
        var normalizedPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(PathHelpers.NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedPaths.Count == 0)
        {
            return Array.Empty<string>();
        }

        return WatchedFolders
            .Select(folder => PathHelpers.NormalizeFolderPath(folder.Path))
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Where(folder => normalizedPaths.Any(path =>
                string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)
                || PathHelpers.IsPathUnderFolder(path, folder)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void RemoveSelectedTagFilter(string tag)
    {
        var existing = SelectedTags.FirstOrDefault(item => string.Equals(item, tag, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            return;
        }

        SelectedTags.Remove(existing);
        QueueRefreshMedia(false, LibraryRefreshSource.UserFilter);
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

        SaveMediaBrowserSort();
        QueueRefreshMedia(true, LibraryRefreshSource.UserFilter);
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
        SaveMediaBrowserSort();
        QueueRefreshMedia(true, LibraryRefreshSource.UserFilter);
    }

    public void SetMediaTypeFilter(MediaType? typeFilter)
    {
        MediaTypeFilter = typeFilter;
    }

    public void SetLibraryPaneResizing(bool isResizing)
    {
        LibraryPaneResizerOpacity = isResizing ? 1 : 0.65;
    }

    public void CommitLibraryPaneLayout()
    {
        FlushMediaLibraryViewState();
    }

    public void ApplyLibraryPaneHostWidth(double hostWidth)
    {
        if (hostWidth <= 0)
        {
            return;
        }

        _libraryPaneHostWidth = hostWidth;
        if (!_hasAppliedInitialLibraryPaneRatio)
        {
            _hasAppliedInitialLibraryPaneRatio = true;
            if (_libraryPaneRatio.HasValue)
            {
                LibraryPaneWidth = hostWidth * _libraryPaneRatio.Value;
            }

            return;
        }

        if (_libraryPaneRatio.HasValue)
        {
            LibraryPaneWidth = hostWidth * _libraryPaneRatio.Value;
        }
        else
        {
            LibraryPaneWidth = LibraryPaneWidth;
        }
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

        // WP-E: compute every target tag set first, then persist in one
        // transaction with one search-index refresh per media. The previous
        // per-tag loop opened 2xM connections per item and caused lock storms
        // and multi-second UI freezes on large selections.
        var tagUpdates = new List<(string MediaId, IReadOnlyList<string> Tags)>(mediaItems.Count);
        var appliedUpdates = new List<(MediaItemViewModel Media, IReadOnlyList<string> Tags)>(mediaItems.Count);
        foreach (var media in mediaItems)
        {
            var nextTags = mode == TagUpdateMode.Append
                ? media.Tags.Concat(normalized).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase).ToList()
                : normalized;
            tagUpdates.Add((media.Id, nextTags));
            appliedUpdates.Add((media, nextTags));
        }

        await _db.SetTagsForMediaAsync(tagUpdates);
        foreach (var (media, nextTags) in appliedUpdates)
        {
            media.UpdateTags(nextTags);
        }

        if (!string.IsNullOrWhiteSpace(SearchQuery) || SelectedTags.Count > 0)
        {
            await QueueUserFilterRefreshAsync(true);
        }
        AppTraceLogger.Log("LibraryShell", $"UpdateTagsAsync completed. MediaCount={mediaItems.Count}, Mode={mode}, TagCount={normalized.Count}.");
    }

    public async Task<bool> TryApplyNumpadTagShortcutAsync(int digit, IEnumerable<MediaItemViewModel> items)
    {
        if (digit is < 1 or > 9)
        {
            return false;
        }

        return await TryApplyTagShortcutAsync(digit, items);
    }

    public async Task<bool> TryApplyTagShortcutAsync(int index, IEnumerable<MediaItemViewModel> items)
    {
        if (index < 1 || index > NumpadTagShortcuts.Count)
        {
            return false;
        }

        var tag = NumpadTagShortcuts[index - 1];
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
        AppTraceLogger.Log("LibraryShell", $"TryApplyTagShortcutAsync start. Index={index}, Tag='{tag}', MediaCount={mediaItems.Count}, Removing={allTagged}.");

        // WP-E: batch persistence like UpdateTagsAsync; one transaction and one
        // search-index refresh per media instead of 2xM per-item connections.
        var tagUpdates = new List<(string MediaId, IReadOnlyList<string> Tags)>(mediaItems.Count);
        var appliedUpdates = new List<(MediaItemViewModel Media, IReadOnlyList<string> Tags)>(mediaItems.Count);
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
            tagUpdates.Add((media.Id, nextTags));
            appliedUpdates.Add((media, nextTags));
        }

        await _db.SetTagsForMediaAsync(tagUpdates);
        foreach (var (media, nextTags) in appliedUpdates)
        {
            media.UpdateTags(nextTags);
        }

        if (!string.IsNullOrWhiteSpace(SearchQuery) || SelectedTags.Count > 0)
        {
            await QueueUserFilterRefreshAsync(true);
        }

        StatusMessage = allTagged
            ? mediaItems.Count == 1
                ? $"已移除当前媒体标签：{tag}"
                : $"已移除 {mediaItems.Count} 个媒体的标签：{tag}"
            : mediaItems.Count == 1
                ? $"已为当前媒体追加标签：{tag}"
                : $"已为 {mediaItems.Count} 个媒体追加标签：{tag}";
        AppTraceLogger.Log("LibraryShell", $"TryApplyTagShortcutAsync completed. Index={index}, Tag='{tag}', MediaCount={mediaItems.Count}, Removed={allTagged}.");
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
            .Select(item => IndexOfMediaId(FilteredMediaItems, item.Id))
            .Where(index => index >= 0)
            .DefaultIfEmpty(FilteredMediaItems.Count)
            .Min();
        AppTraceLogger.Log(
            "LibraryShell",
            $"DeleteMediaAsync start. Requested={list.Count}, LoadedCount={loadedCount}, FirstRemovedIndex={firstRemovedIndex}, SelectedId='{selectedId ?? "<null>"}'.");
        var mediaIds = removedIds.ToList();
        lock (_pendingDeletedMediaIdsSync)
        {
            foreach (var mediaId in mediaIds)
            {
                _pendingDeletedMediaIds.Add(mediaId);
            }
        }

        var uiRemovalStopwatch = Stopwatch.StartNew();
        var removedCount = await _mediaPageStore.RemoveByIdsAsync(removedIds);
        _mediaItemStore.RemoveMany(removedIds);
        AppTraceLogger.Log(
            "LibraryShell",
            $"DeleteMediaAsync UI removal finished. Removed={removedCount}, Remaining={FilteredMediaItems.Count}, ElapsedMs={uiRemovalStopwatch.ElapsedMilliseconds}.");
        QueueDeletedMediaPersistenceCleanup(mediaIds);

        if (!string.IsNullOrWhiteSpace(selectedId) && !removedIds.Contains(selectedId))
        {
            var existingSelection = FilteredMediaItems.FirstOrDefault(item => item.Id == selectedId);
            if (existingSelection != null)
            {
                await RunOnUiThreadAsync(() => SelectedMedia = existingSelection);
                return existingSelection;
            }

            AppTraceLogger.Log(
                "LibraryShell",
                $"DeleteMediaAsync preserved current selection outside loaded window. SelectedId='{selectedId}', Removed={removedIds.Count}, Remaining={FilteredMediaItems.Count}.");
            return SelectedMedia;
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
            $"DeleteMediaAsync fallback selection applied. FallbackIndex={fallbackIndex}, ReplacementId='{replacement.Id}', RemainingLoaded={FilteredMediaItems.Count}, PreviousLoaded={loadedCount}, TotalElapsedMs={uiRemovalStopwatch.ElapsedMilliseconds}.");
        return replacement;
    }

    private static int IndexOfMediaId(IReadOnlyList<MediaItemViewModel> items, string mediaId)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return -1;
        }

        for (var index = 0; index < items.Count; index++)
        {
            if (string.Equals(items[index].Id, mediaId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    public void MarkMediaDeletedExternally(IEnumerable<string> mediaIds)
    {
        var ids = mediaIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0)
        {
            return;
        }

        lock (_pendingDeletedMediaIdsSync)
        {
            foreach (var mediaId in ids)
            {
                _pendingDeletedMediaIds.Add(mediaId);
            }
        }

        _mediaItemStore.RemoveMany(ids);
        QueueDeletedMediaPersistenceCleanup(ids);
        AppTraceLogger.Log(
            "LibraryShell",
            $"Marked externally deleted media pending. Count={ids.Count}.");
    }

    private void QueueDeletedMediaPersistenceCleanup(IReadOnlyList<string> mediaIds)
    {
        _persistenceCoordinator.QueueDeletedMediaCleanup(mediaIds);
    }

    private void RemovePendingDeletedMediaIds(IReadOnlyList<string> ids)
    {
        lock (_pendingDeletedMediaIdsSync)
        {
            foreach (var id in ids)
            {
                _pendingDeletedMediaIds.Remove(id);
            }
        }
    }

    private void DrainDeletedMediaPersistenceCleanupTasks()
    {
        _persistenceCoordinator.Drain();
    }

    public async Task RefreshMediaAsync(bool preserveSelection, bool updateLoadingState = true)
    {
        var stopwatch = Stopwatch.StartNew();
        var refreshVersion = Interlocked.Increment(ref _refreshVersion);
        var selectedId = preserveSelection ? SelectedMedia?.Id : null;
        _mediaPageStore.InvalidatePendingLoads();
        _folderGroupDirectItemsByPath.Clear();
        AppTraceLogger.LogSampled(
            "LibraryShell",
            "refresh-media-start",
            $"RefreshMediaAsync start. Version={refreshVersion}, PreserveSelection={preserveSelection}, UpdateLoadingState={updateLoadingState}, SelectedId='{selectedId ?? "<null>"}'.",
            TimeSpan.FromSeconds(1));
        if (updateLoadingState)
        {
            await RunOnUiThreadAsync(() => IsLoading = true);
        }

        await _refreshMediaLock.WaitAsync();
        try
        {
            if (refreshVersion != Volatile.Read(ref _refreshVersion))
            {
                AppTraceLogger.LogSampled(
                    "LibraryShell",
                    "refresh-media-skip-stale",
                    $"RefreshMediaAsync skipped stale request before query. Version={refreshVersion}, CurrentVersion={Volatile.Read(ref _refreshVersion)}.",
                    TimeSpan.FromSeconds(1));
                return;
            }

            var summariesTimestamp = Stopwatch.GetTimestamp();
            var summariesTask = QueryMediaFolderSummariesAsync();
            var pageRefreshTimestamp = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _suppressMediaFolderGroupRebuildSchedule);
            try
            {
                await _mediaPageStore.RefreshAsync(CreateCurrentQueryState());
            }
            finally
            {
                Interlocked.Decrement(ref _suppressMediaFolderGroupRebuildSchedule);
            }

            var pageRefreshElapsedMs = Stopwatch.GetElapsedTime(pageRefreshTimestamp).TotalMilliseconds;
            if (refreshVersion != Volatile.Read(ref _refreshVersion))
            {
                AppTraceLogger.LogSampled(
                    "LibraryShell",
                    "refresh-media-skip-stale",
                    $"RefreshMediaAsync skipped stale request after item refresh. Version={refreshVersion}, CurrentVersion={Volatile.Read(ref _refreshVersion)}.",
                    TimeSpan.FromSeconds(1));
                try
                {
                    await summariesTask;
                }
                catch (Exception ex)
                {
                    AppTraceLogger.LogException("LibraryShell", "Stale refresh discarded folder summaries.", ex);
                }

                return;
            }

            _mediaFolderSummaries = await summariesTask;
            var summariesElapsedMs = Stopwatch.GetElapsedTime(summariesTimestamp).TotalMilliseconds;
            if (refreshVersion != Volatile.Read(ref _refreshVersion))
            {
                AppTraceLogger.LogSampled(
                    "LibraryShell",
                    "refresh-media-skip-stale",
                    $"RefreshMediaAsync skipped stale request after summaries. Version={refreshVersion}, CurrentVersion={Volatile.Read(ref _refreshVersion)}.",
                    TimeSpan.FromSeconds(1));
                return;
            }

            var groupTimestamp = Stopwatch.GetTimestamp();
            await RunOnUiThreadAsync(() => RebuildMediaFolderGroups(allowLoadedMediaVisibilityRecovery: false));
            var groupElapsedMs = Stopwatch.GetElapsedTime(groupTimestamp).TotalMilliseconds;
            var restoreSelectionElapsedMs = 0d;
            if (refreshVersion == _refreshVersion)
            {
                var restoreSelectionTimestamp = Stopwatch.GetTimestamp();
                await RestoreSelectionAsync(refreshVersion, selectedId);
                restoreSelectionElapsedMs = Stopwatch.GetElapsedTime(restoreSelectionTimestamp).TotalMilliseconds;
            }

            var pruneStoreElapsedMs = 0d;
            var prunedStoreItems = 0;
            if (refreshVersion == _refreshVersion)
            {
                var pruneStoreTimestamp = Stopwatch.GetTimestamp();
                prunedStoreItems = await RunOnUiThreadAsync(PruneMediaItemStoreAfterRefresh);
                pruneStoreElapsedMs = Stopwatch.GetElapsedTime(pruneStoreTimestamp).TotalMilliseconds;
            }

            AppTraceLogger.LogSampled(
                "LibraryShell",
                "refresh-media-complete",
                $"RefreshMediaAsync completed. Version={refreshVersion}, ItemCount={FilteredMediaItems.Count}, SelectedId='{SelectedMedia?.Id ?? "<null>"}', SummariesMs={summariesElapsedMs:0}, FirstGroupMs=0, PageRefreshMs={pageRefreshElapsedMs:0}, SecondGroupMs={groupElapsedMs:0}, RestoreSelectionMs={restoreSelectionElapsedMs:0}, PrunedStoreItems={prunedStoreItems}, PruneStoreMs={pruneStoreElapsedMs:0}, TotalMs={stopwatch.ElapsedMilliseconds}.",
                TimeSpan.FromSeconds(1));
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("LibraryShell", $"RefreshMediaAsync failed. Version={refreshVersion}.", ex);
            throw;
        }
        finally
        {
            _refreshMediaLock.Release();
            if (updateLoadingState && refreshVersion == _refreshVersion)
            {
                await RunOnUiThreadAsync(() => IsLoading = false);
            }
        }
    }

    public Task EnsureMediaItemLoadedAsync(int index)
    {
        return _mediaPageStore.EnsureItemAvailableAsync(index);
    }

    public Task<bool> EnsureMediaItemLoadedAsync(string mediaId)
    {
        return _mediaPageStore.EnsureMediaIdAvailableAsync(mediaId);
    }

    public void QueueUserFilterRefresh(bool preserveSelection)
    {
        QueueRefreshMedia(preserveSelection, LibraryRefreshSource.UserFilter);
    }

    public Task QueueUserFilterRefreshAsync(bool preserveSelection)
    {
        return QueueRefreshMediaAsync(preserveSelection, LibraryRefreshSource.UserFilter);
    }

    public async Task EnsureNextMediaPageLoadedAsync()
    {
        await _mediaPageStore.EnsureNextPageLoadedAsync();
    }

    public async Task<MediaItemViewModel?> GetRandomMediaAsync(bool videoOnly, IReadOnlyCollection<string> excludedMediaIds)
    {
        var visibleFolders = GetVisibleWatchedFolderPaths();
        var query = CreateCurrentMediaQuery(
            0,
            1,
            visibleFolders,
            videoOnly ? MediaType.Video : null,
            excludedMediaIds,
            randomOrder: true);
        var result = await _library.QueryPageAsync(query);
        var media = result.Items.FirstOrDefault();
        if (media == null)
        {
            AppTraceLogger.LogSampled(
                "LibraryShell",
                "random-media-empty",
                $"GetRandomMediaAsync returned no candidate. VideoOnly={videoOnly}, ExcludedMediaIds={excludedMediaIds.Count}, VisibleFolders={visibleFolders.Count}, PlaylistId='{SelectedPlaylist?.Id ?? "<null>"}'.",
                TimeSpan.FromSeconds(2));
            return null;
        }

        return await RunOnUiThreadAsync(() => _mediaItemStore.GetOrUpdate(media));
    }

    public async Task<MediaItemViewModel?> GetMediaByIdAsync(string mediaId)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return null;
        }

        var loaded = FilteredMediaItems.FirstOrDefault(item => string.Equals(item.Id, mediaId, StringComparison.Ordinal));
        if (loaded != null)
        {
            return loaded;
        }

        var media = await _library.GetMediaByIdAsync(mediaId);
        return media == null
            ? null
            : await RunOnUiThreadAsync(() => _mediaItemStore.GetOrUpdate(media));
    }

    public bool IsMediaInCollapsedOrUnloadedGroup(MediaItemViewModel media)
    {
        if (string.IsNullOrWhiteSpace(media.FolderPath))
        {
            return false;
        }

        if (!_mediaFolderGroupsByPath.TryGetValue(PathHelpers.NormalizeFolderPath(media.FolderPath), out var group))
        {
            return true;
        }

        return group.IsCollapsed || !group.HasLoadedItems;
    }

    public void EnsureMediaFolderExpanded(MediaItemViewModel media)
    {
        if (string.IsNullOrWhiteSpace(media.FolderPath))
        {
            return;
        }

        var folderPathsToExpand = GetFolderPathAndVisibleAncestors(media.FolderPath);
        if (folderPathsToExpand.Count == 0)
        {
            return;
        }

        if (ShouldSuppressAutoExpandForRecentUserCollapse(folderPathsToExpand))
        {
            AppTraceLogger.LogSampled(
                "LibraryShell",
                "media-folder-auto-expand-suppressed",
                $"Suppressed media folder auto-expand after recent user collapse. MediaId='{media.Id}', Folder='{media.FolderPath}'.",
                TimeSpan.FromSeconds(1));
            return;
        }

        var expansionChanged = false;
        foreach (var folderPath in folderPathsToExpand)
        {
            if (!_folderCollapseStates.TryGetValue(folderPath, out var collapsed) || collapsed)
            {
                _folderCollapseStates[folderPath] = false;
                expansionChanged = true;
            }
        }

        var groupsToExpand = folderPathsToExpand
            .Select(folderPath => _mediaFolderGroupsByPath.TryGetValue(folderPath, out var group) ? group : null)
            .OfType<MediaFolderGroupViewModel>()
            .Where(group => group.IsCollapsed)
            .ToList();
        if (groupsToExpand.Count > 0)
        {
            _isApplyingFolderCollapseState = true;
            try
            {
                foreach (var groupToExpand in groupsToExpand)
                {
                    groupToExpand.IsCollapsed = false;
                }
            }
            finally
            {
                _isApplyingFolderCollapseState = false;
            }
        }

        if (expansionChanged)
        {
            RebuildMediaFolderGroups();
        }

        var normalizedMediaFolderPath = PathHelpers.NormalizeFolderPath(media.FolderPath);
        _mediaFolderGroupsByPath.TryGetValue(normalizedMediaFolderPath, out var group);
        if (group != null && !group.HasLoadedItems)
        {
            _ = EnsureFolderGroupLoadedAsync(group.FolderPath);
        }
    }

    private bool ShouldSuppressAutoExpandForRecentUserCollapse(IReadOnlyList<string> folderPathsToExpand)
    {
        if (folderPathsToExpand.Count == 0 || _recentUserCollapsedFolders.Count == 0)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var stalePath in _recentUserCollapsedFolders
            .Where(pair => now - pair.Value > RecentUserFolderCollapseAutoExpandSuppression)
            .Select(pair => pair.Key)
            .ToList())
        {
            _recentUserCollapsedFolders.Remove(stalePath);
        }

        return folderPathsToExpand.Any(folderPath =>
            _recentUserCollapsedFolders.TryGetValue(folderPath, out var collapsedAt)
            && now - collapsedAt <= RecentUserFolderCollapseAutoExpandSuppression);
    }

    public bool IsMediaVisibleInLibrary(MediaItemViewModel media)
    {
        var normalizedFolderPath = PathHelpers.NormalizeFolderPath(media.FolderPath);
        _mediaFolderGroupsByPath.TryGetValue(normalizedFolderPath, out var group);
        return MediaFolderGroups.Count == 0
            || group != null && !group.IsCollapsed && group.Contains(media.Id);
    }

    private IReadOnlyList<string> GetFolderPathAndVisibleAncestors(string folderPath)
    {
        var normalizedFolderPath = PathHelpers.NormalizeFolderPath(folderPath);
        if (string.IsNullOrWhiteSpace(normalizedFolderPath))
        {
            return Array.Empty<string>();
        }

        var rootFolderPath = ResolveVisibleFolderRoot(normalizedFolderPath);
        var ancestors = new Stack<string>();
        var current = normalizedFolderPath;
        while (!string.IsNullOrWhiteSpace(current))
        {
            ancestors.Push(current);
            if (string.Equals(current, rootFolderPath, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = GetParentFolderPath(current);
        }

        return ancestors.ToList();
    }

    private string ResolveVisibleFolderRoot(string normalizedFolderPath)
    {
        return GetVisibleWatchedFolderPaths()
            .Where(root => string.Equals(normalizedFolderPath, root, StringComparison.OrdinalIgnoreCase)
                || PathHelpers.IsPathUnderFolder(normalizedFolderPath, root))
            .OrderByDescending(root => root.Length)
            .FirstOrDefault()
            ?? normalizedFolderPath;
    }

    private static string? GetParentFolderPath(string folderPath)
    {
        var normalized = PathHelpers.NormalizeFolderPath(folderPath);
        var slashIndex = normalized.LastIndexOf('/');
        if (slashIndex <= 0)
        {
            return null;
        }

        if (slashIndex == 2 && normalized.Length > 1 && normalized[1] == ':')
        {
            return null;
        }

        return normalized[..slashIndex];
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
        var folderPaths = _folderHierarchyManager.BuildCollapseStatePaths(MediaFolderGroups, _mediaFolderSummaries);
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
        SaveMediaFolderCollapseStates(folderPaths);
        if (!isCollapsed)
        {
            _ = EnsureExpandedFolderGroupsLoadedAsync(maxGroupsToLoad: MaxExpandAllAutoLoadGroups);
        }

        AppTraceLogger.LogSampled(
            "LibraryShell",
            "media-folder-collapse-all",
            $"Media folder collapse all changed. Collapsed={isCollapsed}, GroupCount={MediaFolderGroups.Count}, FolderStateCount={folderPaths.Count}.",
            TimeSpan.FromSeconds(1));
    }

    public bool TryApplyCachedThumbnail(MediaItemViewModel item)
    {
        if (item.Thumbnail != null)
        {
            return true;
        }

        if (_thumbnails.TryGetCachedThumbnail(item.Media, out var source) && source != null)
        {
            item.Thumbnail = source;
            return true;
        }

        return false;
    }

    public async Task EnsureThumbnailAsync(MediaItemViewModel item, CancellationToken cancellationToken = default)
    {
        if (item.Thumbnail != null)
        {
            return;
        }

        if (_thumbnails.TryGetCachedThumbnail(item.Media, out var cached) && cached != null)
        {
            SetThumbnailSafe(item, cached, item.ThumbnailIdentityVersion);
            return;
        }

        if (!item.TryBeginThumbnailLoad())
        {
            return;
        }

        var thumbnailIdentityVersion = item.ThumbnailIdentityVersion;
        try
        {
            var source = await _thumbnails.GetThumbnailAsync(item.Media, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (source != null
                && item.ThumbnailIdentityVersion == thumbnailIdentityVersion)
            {
                SetThumbnailSafe(item, source, thumbnailIdentityVersion);
                return;
            }

            // A refresh may have replaced the media metadata while the old
            // request was still running. Its result is stale and must not be
            // treated as a generation failure or written into the new item.
            if (item.ThumbnailIdentityVersion == thumbnailIdentityVersion)
            {
                item.RecordThumbnailLoadFailure();
            }
        }
        catch (OperationCanceledException)
        {
            item.CancelThumbnailLoad();
        }
    }

    public async Task<Playlist> CreatePlaylistAsync(string name)
    {
        var playlistName = name ?? string.Empty;
        AppTraceLogger.Log("LibraryShell", $"CreatePlaylist requested. NameLength={playlistName.Length}.");
        var playlist = await _playlistManager.CreateAsync(playlistName);
        await ReloadPlaylistsAsync(SelectedPlaylist?.Id);
        AppTraceLogger.Log("LibraryShell", $"CreatePlaylist completed. PlaylistId='{playlist.Id}', PlaylistCount={Playlists.Count}.");
        return playlist;
    }

    public async Task RenamePlaylistAsync(string playlistId, string name)
    {
        var playlistName = name ?? string.Empty;
        AppTraceLogger.Log("LibraryShell", $"RenamePlaylist requested. PlaylistId='{playlistId}', NameLength={playlistName.Length}.");
        await _playlistManager.RenameAsync(playlistId, playlistName);
        await ReloadPlaylistsAsync(playlistId);
        AppTraceLogger.Log("LibraryShell", $"RenamePlaylist completed. PlaylistId='{playlistId}'.");
    }

    public async Task SetPlaylistColorAsync(string playlistId, string? colorHex)
    {
        AppTraceLogger.Log("LibraryShell", $"SetPlaylistColor requested. PlaylistId='{playlistId}', HasColor={!string.IsNullOrWhiteSpace(colorHex)}.");
        await _playlistManager.SetColorAsync(playlistId, colorHex);
        await ReloadPlaylistsAsync(playlistId);
        AppTraceLogger.Log("LibraryShell", $"SetPlaylistColor completed. PlaylistId='{playlistId}'.");
    }

    public async Task DeletePlaylistAsync(string playlistId)
    {
        var deletingCurrent = string.Equals(SelectedPlaylist?.Id, playlistId, StringComparison.Ordinal);
        AppTraceLogger.Log("LibraryShell", $"DeletePlaylistAsync start. PlaylistId='{playlistId}', DeletingCurrent={deletingCurrent}.");
        await _playlistManager.DeleteAsync(playlistId);
        await ReloadPlaylistsAsync();

        if (deletingCurrent)
        {
            SelectedPlaylist = null;
        }
        else
        {
            OnPropertyChanged(nameof(CurrentScopeTitle));
        }

        await QueueUserFilterRefreshAsync(false);
        AppTraceLogger.Log("LibraryShell", $"DeletePlaylistAsync completed. PlaylistId='{playlistId}', PlaylistCount={Playlists.Count}.");
    }

    public async Task UpdatePlaylistOrderAsync(IReadOnlyList<Playlist> playlists)
    {
        AppTraceLogger.Log("LibraryShell", $"UpdatePlaylistOrder requested. Count={playlists.Count}.");
        await _playlistManager.UpdateOrderAsync(playlists);
        await ReloadPlaylistsAsync(SelectedPlaylist?.Id);
        AppTraceLogger.Log("LibraryShell", $"UpdatePlaylistOrder completed. Count={playlists.Count}.");
    }

    public async Task AddMediaToPlaylistAsync(string playlistId, IEnumerable<MediaItemViewModel> items)
    {
        var mediaItems = items.Where(item => item != null).GroupBy(item => item.Id, StringComparer.Ordinal).Select(group => group.First()).ToList();
        AppTraceLogger.Log("LibraryShell", $"AddMediaToPlaylistAsync start. PlaylistId='{playlistId}', MediaCount={mediaItems.Count}, ViewingTarget={string.Equals(SelectedPlaylist?.Id, playlistId, StringComparison.Ordinal)}.");
        await _playlistManager.AddMediaAsync(playlistId, mediaItems);
        
        // Only reload if currently viewing this playlist
        if (string.Equals(SelectedPlaylist?.Id, playlistId, StringComparison.Ordinal))
        {
            await QueueUserFilterRefreshAsync(true);
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

    public Task<bool> AreAllMediaInPlaylistAsync(string playlistId, IEnumerable<string> mediaIds)
    {
        return _playlistManager.AreAllMediaInPlaylistAsync(playlistId, mediaIds);
    }

    public async Task RemoveMediaFromSelectedPlaylistAsync(IEnumerable<MediaItemViewModel> items)
    {
        if (SelectedPlaylist == null)
        {
            return;
        }

        var mediaItems = items.Where(item => item != null).GroupBy(item => item.Id, StringComparer.Ordinal).Select(group => group.First()).ToList();
        AppTraceLogger.Log("LibraryShell", $"RemoveMediaFromSelectedPlaylistAsync start. PlaylistId='{SelectedPlaylist.Id}', MediaCount={mediaItems.Count}.");
        await _playlistManager.RemoveMediaAsync(SelectedPlaylist.Id, mediaItems);
        await QueueUserFilterRefreshAsync(true);
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
        _settingsAndState.SaveNumpadTagShortcuts(shortcuts);
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
        await QueueUserFilterRefreshAsync(false);
        AppTraceLogger.Log("LibraryShell", $"UnlockFolderAsync completed. Folder='{folderPath}', UnlockedCount={_sessionUnlockedFolders.Count}.");
        return true;
    }

    public async Task LockFolderAsync(string folderPath)
    {
        _sessionUnlockedFolders.Remove(PathHelpers.NormalizeFolderPath(folderPath));
        StatusMessage = $"已重新锁定 {Path.GetFileName(folderPath)}";
        await QueueUserFilterRefreshAsync(false);
        AppTraceLogger.Log("LibraryShell", $"LockFolderAsync completed. Folder='{folderPath}', UnlockedCount={_sessionUnlockedFolders.Count}.");
    }

    public async Task RelockAllFoldersAsync()
    {
        var previousCount = _sessionUnlockedFolders.Count;
        _sessionUnlockedFolders.Clear();
        StatusMessage = "所有受保护文件夹已重新锁定。";
        await QueueUserFilterRefreshAsync(false);
        AppTraceLogger.Log("LibraryShell", $"RelockAllFoldersAsync completed. PreviousUnlockedCount={previousCount}.");
    }

    public async Task ResetLibraryAsync(bool includePlaylists)
    {
        AppTraceLogger.Log("LibraryShell", $"ResetLibraryAsync start. IncludePlaylists={includePlaylists}, LoadedCount={FilteredMediaItems.Count}, PlaylistCount={Playlists.Count}.");
        _mediaPageStore.InvalidatePendingLoads();
        _mediaItemStore.Clear();
        _folderGroupLoadRequests.Clear();
        _mediaFolderSummaries = Array.Empty<MediaFolderSummary>();
        lock (_pendingDeletedMediaIdsSync)
        {
            _pendingDeletedMediaIds.Clear();
        }

        await _db.ClearAllMediaAsync(includePlaylists);
        _thumbnails.ClearCache();
        _timelineThumbnails.ClearCache();
        _sessionUnlockedFolders.Clear();
        await ReloadPlaylistsAsync();
        SelectedMedia = null;
        if (includePlaylists)
        {
            SelectedPlaylist = null;
        }

        await RefreshMediaAsync(false);
        StatusMessage = includePlaylists ? "媒体库、标签和播放列表已清空。" : "媒体库和标签已清空。";
        AppTraceLogger.Log("LibraryShell", $"ResetLibraryAsync completed. IncludePlaylists={includePlaylists}, LoadedCount={FilteredMediaItems.Count}, PlaylistCount={Playlists.Count}.");
    }

    public async Task ClearInvalidThumbnailCacheAsync()
    {
        AppTraceLogger.Log("LibraryShell", $"ClearInvalidThumbnailCache start. LoadedCount={FilteredMediaItems.Count}.");
        // H2: stream the library in bounded pages and sweep the cache directory
        // on background threads instead of loading all media into memory.
        var deleted = await _thumbnails.ClearInvalidCacheAsync(_db.StreamAllMediaAsync());
        StatusMessage = deleted == 0
            ? "没有发现失效的缩略图缓存。"
            : $"已清理 {deleted} 个失效的缩略图缓存。";
        AppTraceLogger.Log("LibraryShell", $"ClearInvalidThumbnailCache completed. DeletedCount={deleted}, LoadedCount={FilteredMediaItems.Count}.");
    }

    public Task<IReadOnlyList<string>> GetAllTagsAsync()
    {
        return _db.GetAllTagsAsync();
    }

    /// <summary>
    /// P-rotation: persists the per-media display rotation and keeps the
    /// in-memory view model in sync so later refreshes do not clobber it.
    /// </summary>
    public async Task SaveMediaRotationAsync(string mediaId, int rotation)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return;
        }

        var normalized = (int)Math.Round(rotation / 90.0) * 90 % 360;
        if (normalized < 0)
        {
            normalized += 360;
        }

        await _db.SetMediaRotationAsync(mediaId, normalized);
        var item = FilteredMediaItems.FirstOrDefault(media => string.Equals(media.Id, mediaId, StringComparison.Ordinal));
        if (item != null)
        {
            item.Media.Rotation = normalized;
        }

        AppTraceLogger.Log("LibraryShell", $"SaveMediaRotationAsync completed. MediaId='{mediaId}', Rotation={normalized}.");
    }

    private async Task<bool> AddMediaInternalAsync(Func<CancellationToken, Task<int>> loader, string successMessage)
    {
        if (IsScanning)
        {
            AppTraceLogger.Log("LibraryShell", "AddMediaInternalAsync ignored. ScanAlreadyRunning=True.");
            StatusMessage = "已有扫描正在进行，请先取消或等待完成。";
            return false;
        }

        var stopwatch = Stopwatch.StartNew();
        var completed = false;
        var scanStartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var scanCancellation = new CancellationTokenSource();
        _scanCancellation = scanCancellation;
        Interlocked.Increment(ref _scanSessionId);
        Interlocked.Exchange(ref _pendingIncrementalScanRefresh, 0);
        _lastIncrementalScanRefreshAt = DateTimeOffset.MinValue;
        SetMediaLibraryScanState(MediaLibraryScanStatus.Running, successMessage, scanStartedAt);
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
            SetMediaLibraryScanState(MediaLibraryScanStatus.Completed, successMessage, scanStartedAt, added);
            completed = true;
        }
        catch (OperationCanceledException) when (scanCancellation.IsCancellationRequested)
        {
            Interlocked.Increment(ref _scanSessionId);
            await WaitForPendingScanRefreshAsync();
            await FinalizeScanRefreshAsync();
            await RunOnUiThreadAsync(() => StatusMessage = "扫描已取消，已保留已完成写入的媒体。");
            SetMediaLibraryScanState(MediaLibraryScanStatus.Canceled, successMessage, scanStartedAt);
            AppTraceLogger.Log("LibraryShell", $"AddMediaInternalAsync scan canceled. ElapsedMs={stopwatch.ElapsedMilliseconds}.");
        }
        catch (Exception ex)
        {
            SetMediaLibraryScanState(MediaLibraryScanStatus.Failed, successMessage, scanStartedAt, error: ex.Message);
            AppTraceLogger.LogException("LibraryShell", $"AddMediaInternalAsync scan failed. ElapsedMs={stopwatch.ElapsedMilliseconds}.", ex);
            throw;
        }
        finally
        {
            if (ReferenceEquals(_scanCancellation, scanCancellation))
            {
                _scanCancellation = null;
            }

            Interlocked.Increment(ref _scanProgressSequence);
            await RunOnUiThreadAsync(() =>
            {
                IsLoading = false;
                IsScanning = false;
                ScanCurrentPath = string.Empty;
                ScanProgressIsIndeterminate = false;
                ScanProgressValue = 0;
                ScanProgressMaximum = 0;
            });
            AppTraceLogger.Log(
                "LibraryShell",
                $"AddMediaInternalAsync scan UI finalized. IsScanning={IsScanning}, ScanValue={ScanProgressValue}, ScanMax={ScanProgressMaximum}, CurrentPathLength={ScanCurrentPath.Length}.");
        }

        return completed;
    }

    private void SetMediaLibraryScanState(
        MediaLibraryScanStatus status,
        string operation,
        long startedAt,
        int? addedOrUpdated = null,
        string? error = null)
    {
        try
        {
            _settingsAndState.SaveMediaLibraryScanState(status, operation, startedAt, addedOrUpdated, error);
            AppTraceLogger.LogSampled(
                "LibraryShell",
                "media-library-scan-state",
                $"Media library scan state saved. Status={status}, Operation='{operation}', AddedOrUpdated={addedOrUpdated?.ToString() ?? "<null>"}.",
                TimeSpan.FromSeconds(1));
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("LibraryShell", "SetMediaLibraryScanState failed.", ex);
        }
    }

    private void ReportInterruptedScanIfNeeded()
    {
        var scanState = _settingsAndState.LoadMediaLibraryScanState();
        if (scanState.State != MediaLibraryScanStatus.Running)
        {
            AppTraceLogger.LogSampled(
                "LibraryShell",
                "startup-scan-state",
                $"Startup scan state checked. Status='{scanState.Status}', Operation='{scanState.Operation}'.",
                TimeSpan.FromMinutes(1));
            return;
        }

        StatusMessage = "媒体库已从缓存打开，上次扫描未正常完成，可手动刷新。";
        AppTraceLogger.Log(
            "LibraryShell",
            $"Detected interrupted media library scan on startup. Operation='{scanState.Operation}', StartedAt={scanState.StartedAt}.");
    }

    private void QueueStartupCacheValidation()
    {
        var folders = WatchedFolders
            .Where(folder => folder.Visible)
            .Select(folder => PathHelpers.NormalizeFolderPath(folder.Path))
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxStartupCacheCheckFolders)
            .ToList();
        if (folders.Count == 0)
        {
            AppTraceLogger.Log("LibraryShell", "Startup cache validation skipped. No visible watched folders.");
            return;
        }

        var previousSnapshots = _settings.WatchedFolderScanSnapshots;
        _ = Task.Run(() => ValidateStartupCacheAsync(folders, previousSnapshots));
    }

    private async Task ValidateStartupCacheAsync(
        IReadOnlyList<string> folders,
        IReadOnlyDictionary<string, WatchedFolderScanSnapshot> previousSnapshots)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var changedFolders = new List<string>();
            foreach (var folder in folders)
            {
                var snapshot = CreateWatchedFolderScanSnapshot(folder);
                var hasPrevious = previousSnapshots.TryGetValue(folder, out var previous);
                if (!hasPrevious || !IsWatchedFolderSnapshotEquivalent(previous!, snapshot))
                {
                    changedFolders.Add(folder);
                }
            }

            if (changedFolders.Count > 0)
            {
                await RunOnUiThreadAsync(() =>
                {
                    if (string.IsNullOrWhiteSpace(StatusMessage))
                    {
                        StatusMessage = "媒体库已从缓存打开，部分文件夹可能有变化，可手动刷新。";
                    }
                });
            }

            AppTraceLogger.Log(
                "LibraryShell",
                $"Startup cache validation completed. Checked={folders.Count}, Changed={changedFolders.Count}, MissingPrevious={folders.Count(folder => !previousSnapshots.ContainsKey(folder))}, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("LibraryShell", "Startup cache validation failed.", ex);
        }
    }

    private void SaveWatchedFolderScanSnapshots(IEnumerable<string> folders)
    {
        var normalizedFolders = folders
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Select(PathHelpers.NormalizeFolderPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedFolders.Count == 0)
        {
            return;
        }

        try
        {
            var existingSnapshots = _settings.WatchedFolderScanSnapshots.Values
                .Where(snapshot => !normalizedFolders.Contains(snapshot.Path, StringComparer.OrdinalIgnoreCase))
                .ToList();
            var nextSnapshots = existingSnapshots
                .Concat(normalizedFolders.Select(CreateWatchedFolderScanSnapshot))
                .ToList();
            _settings.SetWatchedFolderScanSnapshots(nextSnapshots);
            AppTraceLogger.Log("LibraryShell", $"Saved watched folder scan snapshots. Updated={normalizedFolders.Count}, Total={nextSnapshots.Count}.");
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("LibraryShell", "SaveWatchedFolderScanSnapshots failed.", ex);
        }
    }

    private static bool IsWatchedFolderSnapshotEquivalent(WatchedFolderScanSnapshot previous, WatchedFolderScanSnapshot current)
    {
        return previous.Exists == current.Exists
            && previous.TopLevelMediaCount == current.TopLevelMediaCount
            && previous.LatestTopLevelMediaModifiedAt == current.LatestTopLevelMediaModifiedAt
            && previous.TopLevelDirectoryCount == current.TopLevelDirectoryCount
            && previous.LatestTopLevelDirectoryModifiedAt == current.LatestTopLevelDirectoryModifiedAt
            && previous.HadErrors == current.HadErrors;
    }

    private static WatchedFolderScanSnapshot CreateWatchedFolderScanSnapshot(string folderPath)
    {
        var normalized = PathHelpers.NormalizeFolderPath(folderPath);
        var nativePath = PathHelpers.ToNativePath(normalized);
        var snapshot = new WatchedFolderScanSnapshot
        {
            Path = normalized,
            Exists = Directory.Exists(nativePath),
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        if (!snapshot.Exists)
        {
            return snapshot;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(nativePath))
            {
                if (!MediaExtensions.IsSupported(file))
                {
                    continue;
                }

                snapshot.TopLevelMediaCount++;
                var modifiedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeSeconds();
                snapshot.LatestTopLevelMediaModifiedAt = Math.Max(snapshot.LatestTopLevelMediaModifiedAt, modifiedAt);
            }

            foreach (var directory in Directory.EnumerateDirectories(nativePath))
            {
                snapshot.TopLevelDirectoryCount++;
                var modifiedAt = new DateTimeOffset(Directory.GetLastWriteTimeUtc(directory)).ToUnixTimeSeconds();
                snapshot.LatestTopLevelDirectoryModifiedAt = Math.Max(snapshot.LatestTopLevelDirectoryModifiedAt, modifiedAt);
            }
        }
        catch (Exception ex)
        {
            snapshot.HadErrors = true;
            AppTraceLogger.LogSampled(
                "LibraryShell",
                "watched-folder-snapshot-failed",
                $"CreateWatchedFolderScanSnapshot failed. Folder='{normalized}', ErrorType={ex.GetType().Name}, Message='{ex.Message}'.",
                TimeSpan.FromSeconds(5));
        }

        return snapshot;
    }

    private Task<IReadOnlyList<MediaFolderSummary>> QueryMediaFolderSummariesAsync()
    {
        var queryState = CreateCurrentQueryState();
        AppTraceLogger.LogSampled(
            "LibraryShell",
            "load-media-folder-summaries",
            $"QueryMediaFolderSummaries requested. VisibleFolders={queryState.VisibleFolderPaths.Count}, PlaylistId='{queryState.SelectedPlaylistId ?? "<null>"}'.",
            TimeSpan.FromSeconds(1));
        return _library.QueryFolderSummariesAsync(queryState.ToMediaQuery(0, 0));
    }

    private MediaQuery CreateCurrentMediaQuery(
        int offset,
        int limit,
        IReadOnlyList<string>? visibleFolders = null,
        MediaType? typeFilter = null,
        IReadOnlyCollection<string>? excludedMediaIds = null,
        bool randomOrder = false)
    {
        return CreateCurrentQueryState(visibleFolders)
            .ToMediaQuery(offset, limit, typeFilter, excludedMediaIds, randomOrder);
    }

    private MediaLibraryQueryState CreateCurrentQueryState(IReadOnlyList<string>? visibleFolders = null)
    {
        return new MediaLibraryQueryState(
            SearchQuery,
            SelectedTags.ToList(),
            SelectedPlaylist?.Id,
            MediaTypeFilter,
            SortField,
            SortOrder,
            visibleFolders ?? GetVisibleWatchedFolderPaths(),
            GetActiveLockedFolderPaths(),
            GetPendingDeletedMediaIdsSnapshot());
    }

    private IReadOnlyList<string> GetPendingDeletedMediaIdsSnapshot()
    {
        lock (_pendingDeletedMediaIdsSync)
        {
            return _pendingDeletedMediaIds.ToList();
        }
    }

    private int PruneMediaItemStoreAfterRefresh()
    {
        var retainedIds = FilteredMediaItems
            .Select(item => item.Id)
            .ToList();
        var selectedId = SelectedMedia?.Id;
        if (!string.IsNullOrWhiteSpace(selectedId))
        {
            retainedIds.Add(selectedId);
        }

        return _mediaItemStore.PruneExcept(retainedIds);
    }

    private void RebuildMediaFolderGroups(bool allowLoadedMediaVisibilityRecovery = false)
    {
        var stopwatch = Stopwatch.StartNew();
        Interlocked.Exchange(ref _mediaFolderGroupRebuildQueued, 0);
        var existingGroups = MediaFolderGroups.ToDictionary(group => group.FolderPath, StringComparer.OrdinalIgnoreCase);

        var loadedGroups = FilteredMediaItems
            .GroupBy(item => item.FolderPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var directGroup in _folderGroupDirectItemsByPath)
        {
            if (!loadedGroups.TryGetValue(directGroup.Key, out var loadedItems) || loadedItems.Count == 0)
            {
                loadedGroups[directGroup.Key] = directGroup.Value.ToList();
                continue;
            }

            var loadedIds = loadedItems
                .Select(item => item.Id)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var item in directGroup.Value)
            {
                if (loadedIds.Add(item.Id))
                {
                    loadedItems.Add(item);
                }
            }
        }

        var hiddenLoadedFolderCount = CountCollapsedLoadedMediaFolders(loadedGroups.Keys);
        var recoveredCollapseStateCount = 0;

        var directCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var summary in _mediaFolderSummaries)
        {
            var folderPath = PathHelpers.NormalizeFolderPath(summary.FolderPath);
            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                directCounts[folderPath] = summary.Count;
            }
        }

        foreach (var folderGroup in loadedGroups)
        {
            directCounts.TryAdd(folderGroup.Key, folderGroup.Value.Count);
        }

        var folderNodes = _folderHierarchyManager.BuildVisibleNodes(directCounts, GetVisibleWatchedFolderPaths(), _folderCollapseStates);
        var nextGroups = new List<MediaFolderGroupViewModel>();
        foreach (var node in folderNodes)
        {
            loadedGroups.TryGetValue(node.FolderPath, out var loadedItems);
            var hasLoadedItems = loadedItems is { Count: > 0 };
            var hasDirectMedia = directCounts.ContainsKey(node.FolderPath);
            var isCollapsed = ResolveFolderGroupCollapsed(node.FolderPath, hasLoadedItems);
            var group = existingGroups.TryGetValue(node.FolderPath, out var existingGroup)
                ? existingGroup
                : CreateMediaFolderGroup(node.FolderPath, node.DisplayName, isCollapsed, node.TotalCount);
            group.SetTreeDisplay(node.DisplayName, node.TreePath, node.Depth);
            group.SetTotalCount(node.TotalCount, hasDirectMedia);
            group.SetCollapsedFromProjection(isCollapsed);
            if (loadedItems is { Count: > 0 } || !group.HasLoadedItems)
            {
                group.SetItems(loadedItems ?? Enumerable.Empty<MediaItemViewModel>());
            }

            nextGroups.Add(group);
        }

        var nextGroupPaths = nextGroups
            .Select(group => group.FolderPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removedGroups = existingGroups.Values
            .Where(group => !nextGroupPaths.Contains(group.FolderPath))
            .ToList();

        if (MediaFolderGroups is MediaFolderGroupCollection collection)
        {
            collection.ReplaceAll(nextGroups);
        }
        else
        {
            MediaFolderGroups.Clear();
            foreach (var group in nextGroups)
            {
                MediaFolderGroups.Add(group);
            }
        }

        DetachMediaFolderGroups(removedGroups);
        _mediaFolderGroupsByPath.Clear();
        foreach (var group in nextGroups)
        {
            _mediaFolderGroupsByPath[group.FolderPath] = group;
        }

        RaiseMediaFolderGroupToggleProperties();
        var visibleItemCount = nextGroups.Sum(group => group.VisibleItems.Count);
        var loadedVisibleGroupCount = nextGroups.Count(group => group.VisibleItems.Count > 0);
        AppTraceLogger.LogSampled(
            "LibraryShell",
            "media-folder-groups-rebuild",
            $"Rebuilt media folder groups. SummaryCount={_mediaFolderSummaries.Count}, LoadedFolderCount={loadedGroups.Count}, DirectFolderCount={directCounts.Count}, OutputGroupCount={nextGroups.Count}, ExistingGroupCount={existingGroups.Count}, DetachedGroupCount={removedGroups.Count}, VisibleItemCount={visibleItemCount}, LoadedVisibleGroupCount={loadedVisibleGroupCount}, HiddenLoadedFolderCount={hiddenLoadedFolderCount}, RecoveryAllowed={allowLoadedMediaVisibilityRecovery}, RecoveredCollapseStates={recoveredCollapseStateCount}, ElapsedMs={stopwatch.ElapsedMilliseconds}.",
            TimeSpan.FromSeconds(1));
        _lastMediaFolderGroupRebuildCompletedAt = DateTimeOffset.UtcNow;
    }

    private int CountCollapsedLoadedMediaFolders(IEnumerable<string> loadedFolderPaths)
    {
        var folders = loadedFolderPaths
            .Select(PathHelpers.NormalizeFolderPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (folders.Count == 0)
        {
            return 0;
        }

        return folders.Count(IsFolderPathCollapsedForMedia);
    }

    private bool IsFolderPathCollapsedForMedia(string folderPath)
    {
        return _folderHierarchyManager.IsCollapsedForMedia(folderPath, _folderCollapseStates, GetFolderPathAndVisibleAncestors);
    }

    private MediaFolderGroupViewModel CreateMediaFolderGroup(string folderPath, string displayName, bool isCollapsed, int totalCount)
    {
        var group = new MediaFolderGroupViewModel(folderPath, displayName, isCollapsed, totalCount, RescanFolderAsync);
        group.CollapseChanged += MediaFolderGroup_CollapseChanged;
        return group;
    }

    private void DetachMediaFolderGroups(IEnumerable<MediaFolderGroupViewModel> groups)
    {
        foreach (var group in groups)
        {
            group.CollapseChanged -= MediaFolderGroup_CollapseChanged;
        }
    }

    private void FilteredMediaItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (Volatile.Read(ref _suppressMediaFolderGroupRebuildSchedule) > 0)
        {
            AppTraceLogger.LogSampled(
                "LibraryShell",
                "media-folder-groups-rebuild-suppressed",
                $"Suppressed collection-triggered folder group rebuild during explicit refresh. Action={e.Action}, LoadedCount={FilteredMediaItems.Count}.",
                TimeSpan.FromSeconds(1));
            return;
        }

        // H5: page appends are the hot path while scrolling; append the new
        // items straight into their existing folder groups instead of
        // re-grouping the whole loaded window (O(loaded)) per page. Any shape
        // change (new folder, removal, move, reset) still takes the full
        // rebuild, which also reconciles any fast-path state.
        if (e.Action == NotifyCollectionChangedAction.Add
            && e.NewItems != null
            && TryAppendToExistingFolderGroups(e.NewItems))
        {
            return;
        }

        ScheduleRebuildMediaFolderGroups();
    }

    private bool TryAppendToExistingFolderGroups(System.Collections.IList newItems)
    {
        var additionsByPath = new Dictionary<string, List<MediaItemViewModel>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in newItems)
        {
            if (entry is not MediaItemViewModel item
                || string.IsNullOrWhiteSpace(item.FolderPath))
            {
                return false;
            }

            // Group keys are normalized folder paths, same as the lookups used
            // by folder-group navigation elsewhere.
            var normalizedPath = PathHelpers.NormalizeFolderPath(item.FolderPath);
            if (!additionsByPath.TryGetValue(normalizedPath, out var items))
            {
                items = new List<MediaItemViewModel>();
                additionsByPath[normalizedPath] = items;
            }

            items.Add(item);
        }

        // Every folder must already have a group; otherwise a new folder
        // appeared and the hierarchy projection needs a full rebuild.
        foreach (var folderPath in additionsByPath.Keys)
        {
            if (!_mediaFolderGroupsByPath.ContainsKey(folderPath))
            {
                return false;
            }
        }

        foreach (var (folderPath, items) in additionsByPath)
        {
            _mediaFolderGroupsByPath[folderPath].AppendLoadedItems(items);
        }

        AppTraceLogger.LogSampled(
            "LibraryShell",
            "media-folder-groups-incremental-append",
            $"Appended loaded media to existing folder groups incrementally. ItemCount={newItems.Count}, AffectedFolderCount={additionsByPath.Count}, LoadedCount={FilteredMediaItems.Count}.",
            TimeSpan.FromSeconds(1));
        return true;
    }

    private void ScheduleRebuildMediaFolderGroups()
    {
        if (Interlocked.Exchange(ref _mediaFolderGroupRebuildQueued, 1) == 1)
        {
            return;
        }

        // WP-D: rate-limit scheduled rebuilds so page-load bursts and watcher
        // batches coalesce into one O(n) rebuild instead of several back-to-back
        // ones. The queued flag guarantees the latest state is always rebuilt.
        var wait = _lastMediaFolderGroupRebuildCompletedAt + MediaFolderGroupRebuildMinInterval - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            _ = EnqueueMediaFolderGroupRebuildAfterDelayAsync(wait);
            return;
        }

        EnqueueMediaFolderGroupRebuild();
    }

    private async Task EnqueueMediaFolderGroupRebuildAfterDelayAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay);
            if (_isDisposed || Volatile.Read(ref _mediaFolderGroupRebuildQueued) == 0)
            {
                return;
            }

            EnqueueMediaFolderGroupRebuild();
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("LibraryShell", "Deferred media folder group rebuild failed.", ex);
        }
    }

    private void EnqueueMediaFolderGroupRebuild()
    {
        if (!_dispatcher.Enqueue(
            () =>
            {
                if (_isDisposed)
                {
                    Interlocked.Exchange(ref _mediaFolderGroupRebuildQueued, 0);
                    return;
                }

                RebuildMediaFolderGroups();
            },
            DispatcherQueuePriority.Low,
            "ScheduleRebuildMediaFolderGroups failed."))
        {
            Interlocked.Exchange(ref _mediaFolderGroupRebuildQueued, 0);
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

        if (group.IsCollapsed)
        {
            _recentUserCollapsedFolders[group.FolderPath] = DateTimeOffset.UtcNow;
        }
        else
        {
            _recentUserCollapsedFolders.Remove(group.FolderPath);
        }

        RaiseMediaFolderGroupToggleProperties();
        SaveMediaFolderCollapseStates(new[] { group.FolderPath });
        ScheduleRebuildMediaFolderGroups();
        AppTraceLogger.LogSampled(
            "LibraryShell",
            $"media-folder-collapse:{group.FolderPath}",
            $"Media folder collapse changed. Folder='{group.FolderPath}', Collapsed={group.IsCollapsed}, Count={group.TotalCount}.",
            TimeSpan.FromSeconds(1));

        if (!group.IsCollapsed && !group.HasLoadedItems && group.HasDirectMedia)
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

    private async Task EnsureExpandedFolderGroupsLoadedAsync(int? maxGroupsToLoad = null)
    {
        var folderPaths = MediaFolderGroups
            .Where(group => !group.IsCollapsed && group.HasDirectMedia && !group.HasLoadedItems && group.TotalCount > 0)
            .Select(group => group.FolderPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (folderPaths.Count == 0)
        {
            return;
        }

        var requestedCount = folderPaths.Count;
        if (maxGroupsToLoad.HasValue && folderPaths.Count > maxGroupsToLoad.Value)
        {
            folderPaths = folderPaths.Take(maxGroupsToLoad.Value).ToList();
        }

        AppTraceLogger.LogSampled(
            "LibraryShell",
            "expanded-folder-groups-load",
            $"Expanded folder group load requested. GroupCount={folderPaths.Count}, RequestedCount={requestedCount}, LoadedCount={_mediaPageStore.Count}, HasMore={_mediaPageStore.HasMoreItems}.",
            TimeSpan.FromSeconds(1));

        foreach (var folderPath in folderPaths)
        {
            if (!_mediaPageStore.HasMoreItems)
            {
                break;
            }

            if (FilteredMediaItems.Any(item => string.Equals(item.FolderPath, folderPath, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            await EnsureFolderGroupLoadedAsync(folderPath);
        }

        await RunOnUiThreadAsync(() => RebuildMediaFolderGroups());
    }

    private bool ResolveFolderGroupCollapsed(string folderPath, bool hasLoadedItems)
    {
        if (_folderCollapseStates.TryGetValue(folderPath, out var collapsed))
        {
            return collapsed;
        }

        return !hasLoadedItems;
    }

    private async Task EnsureFolderGroupLoadedAsync(string folderPath)
    {
        var normalizedFolderPath = PathHelpers.NormalizeFolderPath(folderPath);
        if (string.IsNullOrWhiteSpace(normalizedFolderPath)
            || !_folderGroupLoadRequests.Add(normalizedFolderPath))
        {
            return;
        }

        try
        {
            if (!HasDirectMediaForFolder(normalizedFolderPath))
            {
                AppTraceLogger.LogSampled(
                    "LibraryShell",
                    $"folder-group-load-skip:{normalizedFolderPath}",
                    $"Skipped folder group load because the group only contains child folder media. Folder='{normalizedFolderPath}', LoadedCount={_mediaPageStore.Count}.",
                    TimeSpan.FromSeconds(1));
                return;
            }

            AppTraceLogger.LogSampled(
                "LibraryShell",
                $"folder-group-load:{normalizedFolderPath}",
                $"Folder group load requested. Folder='{normalizedFolderPath}', LoadedCount={_mediaPageStore.Count}, HasMore={_mediaPageStore.HasMoreItems}.",
                TimeSpan.FromSeconds(1));

            var baseQuery = CreateCurrentQueryState().ToMediaQuery(offset: 0, limit: _mediaPageStore.PageSize);
            var query = new MediaQuery
            {
                SearchText = baseQuery.SearchText,
                SelectedTags = baseQuery.SelectedTags,
                PlaylistId = baseQuery.PlaylistId,
                IncludedFolderPaths = baseQuery.IncludedFolderPaths,
                ExactFolderPaths = new[] { normalizedFolderPath },
                ExcludedFolderPaths = baseQuery.ExcludedFolderPaths,
                TypeFilter = baseQuery.TypeFilter,
                ExcludedMediaIds = baseQuery.ExcludedMediaIds,
                RandomOrder = baseQuery.RandomOrder,
                SortField = baseQuery.SortField,
                SortOrder = baseQuery.SortOrder,
                Offset = 0,
                Limit = _mediaPageStore.PageSize
            };
            var result = await _library.QueryPageAsync(query);
            var loadedItems = await RunOnUiThreadAsync(() =>
            {
                var items = _mediaItemStore.GetOrUpdateRange(result.Items);
                _folderGroupDirectItemsByPath[normalizedFolderPath] = items;
                if (_mediaFolderGroupsByPath.TryGetValue(normalizedFolderPath, out var group))
                {
                    group.SetItems(items);
                }

                return items;
            });

            AppTraceLogger.LogSampled(
                "LibraryShell",
                $"folder-group-load-complete:{normalizedFolderPath}",
                $"Folder group load completed. Folder='{normalizedFolderPath}', DirectItems={loadedItems.Count}, HasMore={result.HasMore}, LoadedCount={_mediaPageStore.Count}.",
                TimeSpan.FromSeconds(1));
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("LibraryShell", $"Folder group load failed. Folder='{normalizedFolderPath}'.", ex);
        }
        finally
        {
            _folderGroupLoadRequests.Remove(normalizedFolderPath);
        }
    }

    private bool HasDirectMediaForFolder(string folderPath)
    {
        var normalizedFolderPath = PathHelpers.NormalizeFolderPath(folderPath);
        return _mediaFolderSummaries.Any(summary =>
                string.Equals(PathHelpers.NormalizeFolderPath(summary.FolderPath), normalizedFolderPath, StringComparison.OrdinalIgnoreCase))
            || HasLoadedMediaInFolder(normalizedFolderPath);
    }

    private bool HasLoadedMediaInFolder(string folderPath)
    {
        var normalizedFolderPath = PathHelpers.NormalizeFolderPath(folderPath);
        return FilteredMediaItems.Any(item => string.Equals(item.FolderPath, normalizedFolderPath, StringComparison.OrdinalIgnoreCase));
    }

    private async Task WaitForPendingScanRefreshAsync()
    {
        await _scanRefreshLock.WaitAsync();
        _scanRefreshLock.Release();
    }

    private void OnScanProgress(ScanProgress progress)
    {
        if (progress.IsBackgroundUpdate)
        {
            OnBackgroundLibraryProgress(progress);
            return;
        }

        var progressSequence = Interlocked.Increment(ref _scanProgressSequence);
        _ = RunOnUiThreadAsync(() =>
        {
            if (progressSequence != Volatile.Read(ref _scanProgressSequence))
            {
                return;
            }

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

    private void OnBackgroundLibraryProgress(ScanProgress progress)
    {
        if (progress.ShouldRefreshLibrary && progress.IsComplete)
        {
            QueueRefreshMedia(
                true,
                LibraryRefreshSource.BackgroundMetadata,
                LibraryRefreshKind.BackgroundMetadataRefresh,
                updateLoadingState: false);
        }
    }

    private void SetThumbnailSafe(MediaItemViewModel item, ImageSource source, int expectedIdentityVersion)
    {
        _dispatcher.Enqueue(
            () =>
            {
                if (item.ThumbnailIdentityVersion == expectedIdentityVersion)
                {
                    item.Thumbnail = source;
                }
            },
            failureContext: "SetThumbnailSafe failed.");
    }

    private void QueueRefreshMedia(
        bool preserveSelection,
        LibraryRefreshSource source,
        LibraryRefreshKind kind = LibraryRefreshKind.FullRefresh,
        bool updateLoadingState = true)
    {
        _refreshOrchestrator.Request(kind, source, preserveSelection, updateLoadingState);
    }

    private readonly record struct WatcherFileObservation(
        long Length,
        DateTime LastWriteTimeUtc,
        DateTimeOffset FirstObservedAt);

    private Task QueueRefreshMediaAsync(
        bool preserveSelection,
        LibraryRefreshSource source,
        LibraryRefreshKind kind = LibraryRefreshKind.FullRefresh,
        bool updateLoadingState = true)
    {
        return _refreshOrchestrator.RequestAsync(kind, source, preserveSelection, updateLoadingState);
    }

    private Task ExecuteRefreshRequestAsync(LibraryRefreshRequest request)
    {
        AppTraceLogger.LogSampled(
            "LibraryShell",
            "refresh-request-execute",
            $"Executing refresh request. Kind={request.Kind}, Source={request.Source}, PreserveSelection={request.PreserveSelection}, UpdateLoadingState={request.UpdateLoadingState}.",
            TimeSpan.FromSeconds(1));

        return request.Kind switch
        {
            LibraryRefreshKind.FolderSummaryRefresh => RefreshFolderSummariesAsync(),
            LibraryRefreshKind.LoadedWindowRefresh => RefreshLoadedWindowAndPruneStoreAsync(request.Kind),
            LibraryRefreshKind.BackgroundMetadataRefresh => RefreshLoadedWindowAndPruneStoreAsync(request.Kind),
            _ => RefreshMediaAsync(request.PreserveSelection, request.UpdateLoadingState)
        };
    }

    private async Task RefreshLoadedWindowAndPruneStoreAsync(LibraryRefreshKind kind)
    {
        var hadQuery = _mediaPageStore.HasQuery;
        var stopwatch = Stopwatch.StartNew();
        await _mediaPageStore.RefreshLoadedWindowAsync();
        if (!hadQuery)
        {
            return;
        }

        var prunedStoreItems = await RunOnUiThreadAsync(PruneMediaItemStoreAfterRefresh);
        AppTraceLogger.LogSampled(
            "LibraryShell",
            "refresh-loaded-window-prune",
            $"Refresh loaded window store prune completed. Kind={kind}, LoadedCount={FilteredMediaItems.Count}, SelectedId='{SelectedMedia?.Id ?? "<null>"}', PrunedStoreItems={prunedStoreItems}, ElapsedMs={stopwatch.ElapsedMilliseconds}.",
            TimeSpan.FromSeconds(1));
    }

    private async Task RefreshFolderSummariesAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        _mediaFolderSummaries = await QueryMediaFolderSummariesAsync();
        await RunOnUiThreadAsync(() => RebuildMediaFolderGroups());
        AppTraceLogger.LogSampled(
            "LibraryShell",
            "refresh-folder-summaries",
            $"Refresh folder summaries completed. SummaryCount={_mediaFolderSummaries.Count}, GroupCount={MediaFolderGroups.Count}, LoadedCount={FilteredMediaItems.Count}, ElapsedMs={stopwatch.ElapsedMilliseconds}.",
            TimeSpan.FromSeconds(1));
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

                await ExecuteRefreshRequestAsync(new LibraryRefreshRequest(
                    LibraryRefreshKind.LoadedWindowRefresh,
                    LibraryRefreshSource.ScanIncremental,
                    PreserveSelection: true,
                    UpdateLoadingState: false));
                await ExecuteRefreshRequestAsync(new LibraryRefreshRequest(
                    LibraryRefreshKind.FolderSummaryRefresh,
                    LibraryRefreshSource.ScanIncremental,
                    PreserveSelection: true,
                    UpdateLoadingState: false));
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
            await ExecuteRefreshRequestAsync(new LibraryRefreshRequest(
                LibraryRefreshKind.LoadedWindowRefresh,
                LibraryRefreshSource.ScanFinalize,
                PreserveSelection: true,
                UpdateLoadingState: false));
            await ExecuteRefreshRequestAsync(new LibraryRefreshRequest(
                LibraryRefreshKind.FolderSummaryRefresh,
                LibraryRefreshSource.ScanFinalize,
                PreserveSelection: true,
                UpdateLoadingState: false));
            AppTraceLogger.Log(
                "LibraryShell",
                $"FinalizeScanRefreshAsync applied incremental merge. LoadedCount={FilteredMediaItems.Count}, SelectedId='{SelectedMedia?.Id ?? "<null>"}'.");
            return;
        }

        AppTraceLogger.Log(
            "LibraryShell",
            $"FinalizeScanRefreshAsync fell back to full refresh. LoadedCount={FilteredMediaItems.Count}, SelectedId='{SelectedMedia?.Id ?? "<null>"}'.");
        await ExecuteRefreshRequestAsync(new LibraryRefreshRequest(
            LibraryRefreshKind.FullRefresh,
            LibraryRefreshSource.ScanFinalize,
            PreserveSelection: true,
            UpdateLoadingState: true));
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
            .Take(_mediaPageStore.PageSize)
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
        var protectedPaths = _watchedFolderManager.GetProtectedFolderPaths(WatchedFolders);
        _sessionUnlockedFolders.RemoveWhere(path => !protectedPaths.Contains(path));
    }

    private async Task ReloadPlaylistsAsync(string? selectedPlaylistId = null)
    {
        var preferredId = selectedPlaylistId ?? SelectedPlaylist?.Id;
        var playlists = await _playlistManager.LoadPlaylistsAsync();

        await RunOnUiThreadAsync(() =>
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

    private async Task RestoreSelectionAsync(int refreshVersion, string? selectedId)
    {
        if (string.IsNullOrWhiteSpace(selectedId))
        {
            await RunOnUiThreadAsync(() => SelectedMedia = null);
            AppTraceLogger.LogSampled(
                "LibraryShell",
                "restore-selection-clear",
                $"RestoreSelectionAsync cleared selection. Version={refreshVersion}, LoadedCount={FilteredMediaItems.Count}.",
                TimeSpan.FromSeconds(1));
            return;
        }

        var restored = await FindMediaByIdAsync(selectedId, refreshVersion);
        if (refreshVersion != _refreshVersion)
        {
            AppTraceLogger.LogSampled(
                "LibraryShell",
                "restore-selection-stale",
                $"RestoreSelectionAsync skipped stale restore. Version={refreshVersion}, CurrentVersion={_refreshVersion}, SelectedId='{selectedId}'.",
                TimeSpan.FromSeconds(1));
            return;
        }

        await RunOnUiThreadAsync(() => SelectedMedia = restored);
        AppTraceLogger.LogSampled(
            "LibraryShell",
            "restore-selection-complete",
            $"RestoreSelectionAsync completed. Version={refreshVersion}, RequestedId='{selectedId}', RestoredId='{restored?.Id ?? "<null>"}', LoadedCount={FilteredMediaItems.Count}, HasMore={_mediaPageStore.HasMoreItems}.",
            TimeSpan.FromSeconds(1));
    }

    private async Task<MediaItemViewModel?> FindMediaByIdAsync(string mediaId, int refreshVersion)
    {
        if (refreshVersion != _refreshVersion)
        {
            return null;
        }

        var existing = FilteredMediaItems.FirstOrDefault(item => string.Equals(item.Id, mediaId, StringComparison.Ordinal));
        if (existing != null)
        {
            return existing;
        }

        var media = await _library.GetMediaByIdAsync(mediaId);
        if (media == null || refreshVersion != _refreshVersion)
        {
            return null;
        }

        return await RunOnUiThreadAsync(() => _mediaItemStore.GetOrUpdate(media));
    }

    private Task RunOnUiThreadAsync(Action action)
    {
        return _dispatcher.EnqueueAsync(action, failureContext: "RunOnUiThreadAsync action failed.");
    }

    private Task<T> RunOnUiThreadAsync<T>(Func<T> action)
    {
        return _dispatcher.EnqueueAsync(action, failureContext: "RunOnUiThreadAsync action failed.");
    }

    private void RunOnUiThread(Action action)
    {
        _dispatcher.Enqueue(action, failureContext: "RunOnUiThread action failed.");
    }

    private void RaiseScanUiProperties()
    {
        OnPropertyChanged(nameof(ScanProgressVisibility));
        OnPropertyChanged(nameof(ScanCancelButtonVisibility));
        OnPropertyChanged(nameof(ScanPathVisibility));
    }

    private void SaveMediaBrowserViewMode()
    {
        QueueSaveMediaLibraryViewState();
    }

    private void SaveMediaBrowserSort()
    {
        QueueSaveMediaLibraryViewState();
    }

    private void SaveMediaBrowserTypeFilter()
    {
        QueueSaveMediaLibraryViewState();
    }

    private void SaveMediaBrowserIconSize()
    {
        QueueSaveMediaLibraryViewState();
    }

    private void SaveLibraryPaneLayout()
    {
        QueueSaveMediaLibraryViewState();
    }

    private void SaveMediaFolderCollapseStates(IEnumerable<string> changedFolderPaths)
    {
        foreach (var folderPath in changedFolderPaths)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                continue;
            }

            var normalized = PathHelpers.NormalizeFolderPath(folderPath);
            if (_folderCollapseStates.TryGetValue(folderPath, out var collapsed))
            {
                _folderCollapseStates[normalized] = collapsed;
            }
        }

        QueueSaveMediaLibraryViewState();
    }

    private void QueueSaveMediaLibraryViewState()
    {
        if (!_persistMediaLibraryViewState)
        {
            return;
        }

        if (_isDisposed)
        {
            return;
        }

        var previous = _viewStateSaveCancellation;
        var current = new CancellationTokenSource();
        _viewStateSaveCancellation = current;
        previous?.Cancel();
        _ = SaveMediaLibraryViewStateAfterDelayAsync(current);
    }

    private async Task SaveMediaLibraryViewStateAfterDelayAsync(CancellationTokenSource cancellationSource)
    {
        try
        {
            await Task.Delay(MediaLibraryViewStateSaveDelay, cancellationSource.Token);
            if (cancellationSource.IsCancellationRequested || _isDisposed)
            {
                return;
            }

            await RunOnUiThreadAsync(() =>
            {
                if (!cancellationSource.IsCancellationRequested && !_isDisposed)
                {
                    SaveMediaLibraryViewStateNow();
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("LibraryShell", "Delayed media library view state save failed.", ex);
        }
        finally
        {
            if (ReferenceEquals(_viewStateSaveCancellation, cancellationSource))
            {
                _viewStateSaveCancellation = null;
            }

            cancellationSource.Dispose();
        }
    }

    private void FlushMediaLibraryViewState()
    {
        if (!_persistMediaLibraryViewState || _isDisposed)
        {
            return;
        }

        var pending = _viewStateSaveCancellation;
        _viewStateSaveCancellation = null;
        pending?.Cancel();
        SaveMediaLibraryViewStateNow();
    }

    private void SaveMediaLibraryViewStateNow()
    {
        if (!_persistMediaLibraryViewState || _isDisposed)
        {
            return;
        }

        if (_libraryPaneHostWidth is > 0)
        {
            _libraryPaneRatio = Math.Clamp(LibraryPaneWidth / _libraryPaneHostWidth.Value, 0.15, 0.75);
        }

        _settingsAndState.SaveMediaLibraryViewState(new MediaLibraryViewState
        {
            ViewMode = ViewMode,
            SortField = SortField,
            SortOrder = SortOrder,
            TypeFilter = MediaTypeFilter,
            IconSize = IconSize,
            IsLibraryPaneOpen = IsLibraryPaneOpen,
            LibraryPaneWidth = LibraryPaneWidth,
            LibraryPaneRatio = _libraryPaneRatio,
            FolderCollapseStates = _folderCollapseStates.ToDictionary(
                pair => PathHelpers.NormalizeFolderPath(pair.Key),
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase)
        });
    }

    private static double ClampLibraryPaneWidth(double width, double? hostWidth)
    {
        var maxWidth = hostWidth is > 0
            ? Math.Max(MinLibraryPaneWidth, hostWidth.Value - MinPlayerPaneWidth)
            : double.PositiveInfinity;
        return Math.Clamp(width, MinLibraryPaneWidth, maxWidth);
    }

    private sealed class MediaFolderGroupCollection : ObservableCollection<MediaFolderGroupViewModel>
    {
        public void ReplaceAll(IEnumerable<MediaFolderGroupViewModel> groups)
        {
            var desiredGroups = groups.ToList();
            if (Items.Count == desiredGroups.Count
                && Items.Select(group => group.FolderPath).SequenceEqual(desiredGroups.Select(group => group.FolderPath), StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            var desiredPaths = desiredGroups
                .Select(group => group.FolderPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var removed = 0;
            var moved = 0;
            var inserted = 0;
            var replaced = 0;
            for (var index = Items.Count - 1; index >= 0; index--)
            {
                if (!desiredPaths.Contains(Items[index].FolderPath))
                {
                    RemoveItem(index);
                    removed++;
                }
            }

            for (var desiredIndex = 0; desiredIndex < desiredGroups.Count; desiredIndex++)
            {
                var desiredGroup = desiredGroups[desiredIndex];
                if (desiredIndex < Items.Count
                    && string.Equals(Items[desiredIndex].FolderPath, desiredGroup.FolderPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (!ReferenceEquals(Items[desiredIndex], desiredGroup))
                    {
                        SetItem(desiredIndex, desiredGroup);
                        replaced++;
                    }

                    continue;
                }

                var existingIndex = IndexOfFolderPath(desiredGroup.FolderPath, desiredIndex + 1);
                if (existingIndex >= 0)
                {
                    MoveItem(existingIndex, desiredIndex);
                    if (!ReferenceEquals(Items[desiredIndex], desiredGroup))
                    {
                        SetItem(desiredIndex, desiredGroup);
                        replaced++;
                    }

                    moved++;
                    continue;
                }

                InsertItem(desiredIndex, desiredGroup);
                inserted++;
            }

            AppTraceLogger.LogSampled(
                "LibraryShell",
                "media-folder-groups-diff",
                $"Applied media folder group diff. Count={Items.Count}, Inserted={inserted}, Removed={removed}, Moved={moved}, Replaced={replaced}.",
                TimeSpan.FromSeconds(1));
        }

        private int IndexOfFolderPath(string folderPath, int startIndex)
        {
            for (var index = Math.Max(0, startIndex); index < Items.Count; index++)
            {
                if (string.Equals(Items[index].FolderPath, folderPath, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            return -1;
        }
    }
}
