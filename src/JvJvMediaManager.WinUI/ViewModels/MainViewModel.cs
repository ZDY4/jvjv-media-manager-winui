using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using JvJvMediaManager.Data;
using JvJvMediaManager.Models;
using JvJvMediaManager.Services;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.ViewModels;

public enum MediaViewMode
{
    List,
    Grid
}

public sealed class MainViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly MediaDb _db;
    private readonly MediaLibraryService _library;
    private readonly ThumbnailService _thumbnails;

    private DispatcherQueue? _dispatcher;
    private int _refreshVersion;

    private MediaItemViewModel? _selectedMedia;
    private string _searchQuery = string.Empty;
    private bool _isLoading;
    private string _statusMessage = string.Empty;
    private MediaViewMode _viewMode = MediaViewMode.List;
    private MediaSortField _sortField = MediaSortField.ModifiedAt;
    private MediaSortOrder _sortOrder = MediaSortOrder.Desc;
    private int _iconSize = 120;

    public MainViewModel()
    {
        _settings = new SettingsService();
        _db = new MediaDb(_settings.DataDir);
        _db.Initialize();
        _library = new MediaLibraryService(_db);
        _thumbnails = new ThumbnailService();
        FilteredMediaItems = new IncrementalMediaCollection(LoadMediaPageAsync);
        WatchedFolders = new ObservableCollection<WatchedFolder>(_settings.WatchedFolders);
    }

    public IncrementalMediaCollection FilteredMediaItems { get; }

    public ObservableCollection<WatchedFolder> WatchedFolders { get; }

    public ObservableCollection<string> SelectedTags { get; } = new();

    public MediaItemViewModel? SelectedMedia
    {
        get => _selectedMedia;
        set => SetProperty(ref _selectedMedia, value);
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
        set => SetProperty(ref _viewMode, value);
    }

    public MediaSortField SortField
    {
        get => _sortField;
        set
        {
            if (SetProperty(ref _sortField, value))
            {
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
                QueueRefreshMedia(true);
            }
        }
    }

    public int IconSize
    {
        get => _iconSize;
        set => SetProperty(ref _iconSize, value);
    }

    public string DataDir => _settings.DataDir;

    public void SetDispatcher(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public async Task InitializeAsync()
    {
        await RefreshMediaAsync(false);
    }

    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        await AddMediaInternalAsync(() => _library.AddFilesAsync(paths, new Progress<ScanProgress>(OnScanProgress)));
    }

    public async Task AddFolderAsync(string path)
    {
        await AddMediaInternalAsync(() => _library.AddFolderAsync(path, new Progress<ScanProgress>(OnScanProgress)));
    }

    public async Task RescanFoldersAsync()
    {
        var folders = WatchedFolders.Select(f => f.Path).ToList();
        await AddMediaInternalAsync(() => _library.RescanFoldersAsync(folders, new Progress<ScanProgress>(OnScanProgress)));
    }

    public void UpdateWatchedFolders(IEnumerable<WatchedFolder> folders)
    {
        WatchedFolders.Clear();
        foreach (var folder in folders)
        {
            WatchedFolders.Add(folder);
        }
        _settings.SetWatchedFolders(WatchedFolders.ToList());
    }

    public void ToggleSort(MediaSortField field)
    {
        if (SortField == field)
        {
            _sortOrder = SortOrder == MediaSortOrder.Asc ? MediaSortOrder.Desc : MediaSortOrder.Asc;
            OnPropertyChanged(nameof(SortOrder));
        }
        else
        {
            _sortField = field;
            _sortOrder = MediaSortOrder.Asc;
            OnPropertyChanged(nameof(SortField));
            OnPropertyChanged(nameof(SortOrder));
        }

        QueueRefreshMedia(true);
    }

    public async Task UpdateTagsAsync(MediaItemViewModel media, IEnumerable<string> tags)
    {
        var normalized = tags.Select(t => t.Trim()).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var tag in media.Tags.ToList())
        {
            _db.RemoveTag(media.Id, tag);
        }
        foreach (var tag in normalized)
        {
            _db.AddTag(media.Id, tag);
        }

        media.UpdateTags(normalized);
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

        foreach (var item in list)
        {
            FilteredMediaItems.Remove(item);
        }

        if (!string.IsNullOrWhiteSpace(selectedId) && !removedIds.Contains(selectedId))
        {
            var existingSelection = FilteredMediaItems.FirstOrDefault(item => item.Id == selectedId);
            if (existingSelection != null)
            {
                SelectedMedia = existingSelection;
                return existingSelection;
            }
        }

        await FilteredMediaItems.EnsureItemAvailableAsync(Math.Max(loadedCount - 1, 0));
        if (FilteredMediaItems.Count == 0)
        {
            SelectedMedia = null;
            return null;
        }

        var fallbackIndex = Math.Clamp(firstRemovedIndex, 0, FilteredMediaItems.Count - 1);
        var replacement = FilteredMediaItems[fallbackIndex];
        SelectedMedia = replacement;
        return replacement;
    }

    public async Task RefreshMediaAsync(bool preserveSelection)
    {
        var refreshVersion = Interlocked.Increment(ref _refreshVersion);
        var selectedId = preserveSelection ? SelectedMedia?.Id : null;
        IsLoading = true;
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
                IsLoading = false;
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

        var source = await _thumbnails.GetThumbnailAsync(item.Media.Path, item.Media.Type == MediaType.Video);
        if (source != null)
        {
            SetThumbnailSafe(item, source);
        }
    }

    private async Task AddMediaInternalAsync(Func<Task<int>> loader)
    {
        IsLoading = true;
        try
        {
            await loader();
            await RefreshMediaAsync(true);
        }
        finally
        {
            IsLoading = false;
            StatusMessage = string.Empty;
        }
    }

    private Task<MediaPageResult> LoadMediaPageAsync(int offset, int limit)
    {
        return _library.QueryPageAsync(new MediaQuery
        {
            SearchText = SearchQuery,
            SelectedTags = SelectedTags.ToList(),
            SortField = SortField,
            SortOrder = SortOrder,
            Offset = offset,
            Limit = limit
        });
    }

    private void OnScanProgress(ScanProgress progress)
    {
        StatusMessage = progress.Total > 0
            ? $"正在扫描 {progress.Scanned}/{progress.Total}"
            : "准备扫描...";
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

    private async Task RestoreSelectionAsync(int refreshVersion, string? selectedId)
    {
        if (string.IsNullOrWhiteSpace(selectedId))
        {
            SelectedMedia = null;
            return;
        }

        var restored = await FindMediaByIdAsync(selectedId, refreshVersion);
        if (refreshVersion != _refreshVersion)
        {
            return;
        }

        SelectedMedia = restored;
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
}
