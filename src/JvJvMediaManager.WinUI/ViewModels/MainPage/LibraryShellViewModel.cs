using System.Collections.ObjectModel;
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
    private static readonly Brush TransparentBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private readonly SettingsService _settings;
    private readonly MediaDb _db;
    private readonly MediaLibraryService _library;
    private readonly ThumbnailService _thumbnails;
    private readonly HashSet<string> _sessionUnlockedFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly SelectionViewModel _selection;

    private DispatcherQueue? _dispatcher;
    private int _refreshVersion;

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
    private bool _isLibraryPaneOpen = true;
    private double _libraryPaneWidth = 360;
    private double _libraryPaneResizerOpacity = 0.65;

    public LibraryShellViewModel(SelectionViewModel selection)
    {
        _selection = selection;
        _settings = new SettingsService();
        _db = new MediaDb(_settings.DataDir);
        _db.Initialize();
        _library = new MediaLibraryService(_db);
        _thumbnails = new ThumbnailService(_settings.GetThumbnailCacheDir());
        FilteredMediaItems = new IncrementalMediaCollection(LoadMediaPageAsync);
        WatchedFolders = new ObservableCollection<WatchedFolder>(_settings.WatchedFolders);
        Playlists = new ObservableCollection<Playlist>(_db.GetPlaylists());
        SelectedTags.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SelectedTagsVisibility));
    }

    public IncrementalMediaCollection FilteredMediaItems { get; }

    public ObservableCollection<WatchedFolder> WatchedFolders { get; }

    public ObservableCollection<string> SelectedTags { get; } = new();

    public ObservableCollection<Playlist> Playlists { get; }

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
            if (SetProperty(ref _selectedPlaylist, value))
            {
                OnPropertyChanged(nameof(CurrentScopeTitle));
                OnPropertyChanged(nameof(SelectedPlaylistTitle));
                OnPropertyChanged(nameof(SelectedPlaylistTitleVisibility));
                OnPropertyChanged(nameof(MediaTabBackground));
                OnPropertyChanged(nameof(MediaTabForeground));
                QueueRefreshMedia(false);
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

    public bool HasLockPassword => !string.IsNullOrWhiteSpace(LockPassword);

    public AppThemeMode ThemeMode => AppThemeMode.Dark;

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

    public Visibility ScanPathVisibility => !string.IsNullOrWhiteSpace(ScanCurrentPath)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility SelectedPlaylistTitleVisibility => SelectedPlaylist == null ? Visibility.Collapsed : Visibility.Visible;

    public string SelectedPlaylistTitle => SelectedPlaylist?.Name ?? string.Empty;

    public Brush MediaTabBackground => SelectedPlaylist == null
        ? ResolveBrush("SurfaceMutedBrush", Microsoft.UI.ColorHelper.FromArgb(255, 46, 46, 46))
        : TransparentBrush;

    public Brush MediaTabForeground => SelectedPlaylist == null
        ? ResolveBrush("TextBrush", Microsoft.UI.Colors.White)
        : ResolveBrush("MutedTextBrush", Microsoft.UI.ColorHelper.FromArgb(255, 170, 170, 170));

    public string ViewModeToggleGlyph => ViewMode == MediaViewMode.List ? "\uECA5" : "\uE8FD";

    public string ViewModeToggleToolTip => ViewMode == MediaViewMode.List ? "切换到网格" : "切换到列表";

    public string SortButtonText
    {
        get
        {
            return (SortField, SortOrder) switch
            {
                (MediaSortField.ModifiedAt, MediaSortOrder.Desc) => "时间 ↓",
                (MediaSortField.ModifiedAt, MediaSortOrder.Asc) => "时间 ↑",
                (MediaSortField.FileName, MediaSortOrder.Asc) => "名称 A-Z",
                _ => "名称 Z-A"
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
        ReloadPlaylists();
        await RefreshMediaAsync(false);
    }

    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        await AddMediaInternalAsync(() => _library.AddFilesAsync(paths, new Progress<ScanProgress>(OnScanProgress)), "文件导入完成。");
    }

    public async Task AddFolderAsync(string path)
    {
        await AddMediaInternalAsync(() => _library.AddFolderAsync(path, new Progress<ScanProgress>(OnScanProgress)), "文件夹导入完成。");
    }

    public async Task RescanFoldersAsync()
    {
        var folders = WatchedFolders.Select(f => f.Path).ToList();
        await AddMediaInternalAsync(() => _library.RescanFoldersAsync(folders, new Progress<ScanProgress>(OnScanProgress)), "媒体库刷新完成。");
    }

    public void UpdateWatchedFolders(IEnumerable<WatchedFolder> folders, bool refreshMedia = true)
    {
        var normalized = folders
            .Where(folder => !string.IsNullOrWhiteSpace(folder.Path))
            .GroupBy(folder => PathHelpers.NormalizeFolderPath(folder.Path), StringComparer.OrdinalIgnoreCase)
            .Select(group => new WatchedFolder
            {
                Path = group.First().Path,
                Locked = group.First().Locked
            })
            .OrderBy(folder => folder.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        WatchedFolders.Clear();
        foreach (var folder in normalized)
        {
            WatchedFolders.Add(folder);
        }

        _settings.SetWatchedFolders(WatchedFolders.ToList());
        CleanupUnlockedFolders();
        if (refreshMedia)
        {
            QueueRefreshMedia(false);
        }
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
        _db.DeleteMedia(list.Select(item => item.Id));

        await RunOnUiThreadAsync(() =>
        {
            foreach (var item in list)
            {
                FilteredMediaItems.Remove(item);
            }
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

        await FilteredMediaItems.EnsureItemAvailableAsync(Math.Max(loadedCount - 1, 0));
        if (FilteredMediaItems.Count == 0)
        {
            await RunOnUiThreadAsync(() => SelectedMedia = null);
            return null;
        }

        var fallbackIndex = Math.Clamp(firstRemovedIndex, 0, FilteredMediaItems.Count - 1);
        var replacement = FilteredMediaItems[fallbackIndex];
        await RunOnUiThreadAsync(() => SelectedMedia = replacement);
        return replacement;
    }

    public async Task RefreshMediaAsync(bool preserveSelection)
    {
        var refreshVersion = Interlocked.Increment(ref _refreshVersion);
        var selectedId = preserveSelection ? SelectedMedia?.Id : null;
        await RunOnUiThreadAsync(() => IsLoading = true);
        try
        {
            await FilteredMediaItems.RefreshAsync();
            if (refreshVersion == _refreshVersion)
            {
                await RestoreSelectionAsync(refreshVersion, selectedId);
            }
        }
        finally
        {
            if (refreshVersion == _refreshVersion)
            {
                await RunOnUiThreadAsync(() => IsLoading = false);
            }
        }
    }

    public Task EnsureMediaItemLoadedAsync(int index)
    {
        return FilteredMediaItems.EnsureItemAvailableAsync(index);
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
        }
    }

    public Playlist CreatePlaylist(string name)
    {
        var playlist = _db.CreatePlaylist(name);
        ReloadPlaylists(playlist.Id);
        return playlist;
    }

    public void RenamePlaylist(string playlistId, string name)
    {
        _db.RenamePlaylist(playlistId, name);
        ReloadPlaylists(playlistId);
    }

    public void SetPlaylistColor(string playlistId, string? colorHex)
    {
        _db.SetPlaylistColor(playlistId, colorHex);
        ReloadPlaylists(playlistId);
    }

    public async Task DeletePlaylistAsync(string playlistId)
    {
        var deletingCurrent = string.Equals(SelectedPlaylist?.Id, playlistId, StringComparison.Ordinal);
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
    }

    public void UpdatePlaylistOrder(IReadOnlyList<Playlist> playlists)
    {
        for (var i = 0; i < playlists.Count; i++)
        {
            playlists[i].SortOrder = i;
        }

        _db.UpdatePlaylistOrder(playlists.Select(item => item.Id).ToList());
        ReloadPlaylists(SelectedPlaylist?.Id);
    }

    public async Task AddMediaToPlaylistAsync(string playlistId, IEnumerable<MediaItemViewModel> items)
    {
        _db.AddMediaToPlaylist(playlistId, items.Select(item => item.Id));
        ReloadPlaylists(SelectedPlaylist?.Id ?? playlistId);

        if (string.Equals(SelectedPlaylist?.Id, playlistId, StringComparison.Ordinal))
        {
            await RefreshMediaAsync(true);
        }
    }

    public async Task RemoveMediaFromSelectedPlaylistAsync(IEnumerable<MediaItemViewModel> items)
    {
        if (SelectedPlaylist == null)
        {
            return;
        }

        _db.RemoveMediaFromPlaylist(SelectedPlaylist.Id, items.Select(item => item.Id));
        await RefreshMediaAsync(true);
    }

    public void SetDataDir(string path)
    {
        _settings.SetDataDir(path);
        OnPropertyChanged(nameof(DataDir));
        OnPropertyChanged(nameof(ConfiguredDataDir));
    }

    public void SetPortableMode(bool enabled)
    {
        _settings.SetPortableMode(enabled);
        OnPropertyChanged(nameof(PortableMode));
        OnPropertyChanged(nameof(DataDir));
    }

    public void SetLockPassword(string password)
    {
        _settings.SetLockPassword(password.Trim());
        OnPropertyChanged(nameof(LockPassword));
        OnPropertyChanged(nameof(HasLockPassword));
    }

    public void SetThemeMode(AppThemeMode themeMode)
    {
        _settings.SetThemeMode(AppThemeMode.Dark);
        OnPropertyChanged(nameof(ThemeMode));
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
            return false;
        }

        _sessionUnlockedFolders.Add(PathHelpers.NormalizeFolderPath(folderPath));
        StatusMessage = $"已解锁 {Path.GetFileName(folderPath)}";
        await RefreshMediaAsync(false);
        return true;
    }

    public async Task LockFolderAsync(string folderPath)
    {
        _sessionUnlockedFolders.Remove(PathHelpers.NormalizeFolderPath(folderPath));
        StatusMessage = $"已重新锁定 {Path.GetFileName(folderPath)}";
        await RefreshMediaAsync(false);
    }

    public async Task RelockAllFoldersAsync()
    {
        _sessionUnlockedFolders.Clear();
        StatusMessage = "所有受保护文件夹已重新锁定。";
        await RefreshMediaAsync(false);
    }

    public async Task ResetLibraryAsync(bool includePlaylists)
    {
        _db.ClearAllMedia(includePlaylists);
        _thumbnails.ClearCache();
        _sessionUnlockedFolders.Clear();
        ReloadPlaylists();
        SelectedMedia = null;
        if (includePlaylists)
        {
            SelectedPlaylist = null;
        }

        await RefreshMediaAsync(false);
        StatusMessage = includePlaylists ? "媒体库、标签和播放列表已清空。" : "媒体库和标签已清空。";
    }

    public void ClearThumbnailCache()
    {
        _thumbnails.ClearCache();
        foreach (var item in FilteredMediaItems)
        {
            item.ResetThumbnailLoadState();
            item.Thumbnail = null;
        }
    }

    public IReadOnlyList<string> GetAllTags()
    {
        return _db.GetAllTags();
    }

    private async Task AddMediaInternalAsync(Func<Task<int>> loader, string successMessage)
    {
        IsLoading = true;
        await RunOnUiThreadAsync(() =>
        {
            IsLoading = true;
            IsScanning = true;
            ScanProgressValue = 0;
            ScanProgressMaximum = 0;
            ScanCurrentPath = string.Empty;
        });

        try
        {
            var added = await loader();
            await RefreshMediaAsync(true);
            await RunOnUiThreadAsync(() => StatusMessage = $"{successMessage} 新增或更新 {added} 个媒体。");
        }
        finally
        {
            await RunOnUiThreadAsync(() =>
            {
                IsLoading = false;
                IsScanning = false;
                ScanCurrentPath = string.Empty;
                if (ScanProgressMaximum > 0)
                {
                    ScanProgressValue = ScanProgressMaximum;
                }
            });
        }
    }

    private Task<MediaPageResult> LoadMediaPageAsync(int offset, int limit)
    {
        return _library.QueryPageAsync(new MediaQuery
        {
            SearchText = SearchQuery,
            SelectedTags = SelectedTags.ToList(),
            PlaylistId = SelectedPlaylist?.Id,
            ExcludedFolderPaths = GetActiveLockedFolderPaths(),
            SortField = SortField,
            SortOrder = SortOrder,
            Offset = offset,
            Limit = limit
        });
    }

    private void OnScanProgress(ScanProgress progress)
    {
        _ = RunOnUiThreadAsync(() =>
        {
            IsScanning = !progress.IsComplete;
            ScanProgressMaximum = progress.Total;
            ScanProgressValue = progress.Scanned;
            ScanCurrentPath = progress.CurrentPath;

            if (progress.Total > 0)
            {
                StatusMessage = progress.IsComplete
                    ? $"扫描完成 {progress.Scanned}/{progress.Total}"
                    : $"正在扫描 {progress.Scanned}/{progress.Total}";
                return;
            }

            StatusMessage = progress.IsComplete ? "扫描完成。" : "准备扫描...";
        });
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

    private IReadOnlyList<string> GetActiveLockedFolderPaths()
    {
        return WatchedFolders
            .Where(folder => folder.Locked)
            .Select(folder => PathHelpers.NormalizeFolderPath(folder.Path))
            .Where(path => !_sessionUnlockedFolders.Contains(path))
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
                    tcs.SetException(ex);
                }
            }))
        {
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
        OnPropertyChanged(nameof(ScanPathVisibility));
    }

    private static Brush ResolveBrush(string resourceKey, Windows.UI.Color fallbackColor)
    {
        return Application.Current.Resources[resourceKey] as Brush
            ?? new SolidColorBrush(fallbackColor);
    }
}
