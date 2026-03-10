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

public enum MediaSortField
{
    FileName,
    ModifiedAt
}

public enum MediaSortOrder
{
    Asc,
    Desc
}

public sealed class MainViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly MediaDb _db;
    private readonly MediaLibraryService _library;
    private readonly ThumbnailService _thumbnails;

    private DispatcherQueue? _dispatcher;

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
        WatchedFolders = new ObservableCollection<WatchedFolder>(_settings.WatchedFolders);
    }

    public ObservableCollection<MediaItemViewModel> MediaItems { get; } = new();
    public ObservableCollection<MediaItemViewModel> FilteredMediaItems { get; } = new();
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
                ApplyFilters();
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
                ApplyFilters();
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
                ApplyFilters();
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
        IsLoading = true;
        try
        {
            var media = await _library.LoadAllAsync();
            ReplaceMediaItems(media);
            ApplyFilters();
            _ = LoadThumbnailsAsync(MediaItems.ToList());
        }
        finally
        {
            IsLoading = false;
        }
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
            SortOrder = SortOrder == MediaSortOrder.Asc ? MediaSortOrder.Desc : MediaSortOrder.Asc;
        }
        else
        {
            SortField = field;
            SortOrder = MediaSortOrder.Asc;
        }
    }

    public void ApplyFilters()
    {
        var query = SearchQuery.Trim().ToLowerInvariant();
        var tags = SelectedTags.Select(t => t.ToLowerInvariant()).ToList();

        IEnumerable<MediaItemViewModel> filtered = MediaItems;

        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = filtered.Where(m => m.FileName.ToLowerInvariant().Contains(query) || m.Tags.Any(t => t.ToLowerInvariant().Contains(query)));
        }

        if (tags.Count > 0)
        {
            filtered = filtered.Where(m => tags.All(tag => m.Tags.Any(t => t.ToLowerInvariant().Contains(tag))));
        }

        filtered = SortField switch
        {
            MediaSortField.FileName => SortOrder == MediaSortOrder.Asc
                ? filtered.OrderBy(m => m.FileName, StringComparer.OrdinalIgnoreCase)
                : filtered.OrderByDescending(m => m.FileName, StringComparer.OrdinalIgnoreCase),
            _ => SortOrder == MediaSortOrder.Asc
                ? filtered.OrderBy(m => m.Media.ModifiedAt)
                : filtered.OrderByDescending(m => m.Media.ModifiedAt)
        };

        ReplaceCollection(FilteredMediaItems, filtered);
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
        ApplyFilters();
        await Task.CompletedTask;
    }

    public void DeleteMedia(IEnumerable<MediaItemViewModel> items)
    {
        var list = items.ToList();
        foreach (var item in list)
        {
            _db.DeleteMedia(item.Id);
            MediaItems.Remove(item);
            FilteredMediaItems.Remove(item);
        }
    }

    private async Task AddMediaInternalAsync(Func<Task<IReadOnlyList<MediaFile>>> loader)
    {
        IsLoading = true;
        try
        {
            var media = await loader();
            if (media.Count > 0)
            {
                var newItems = media.Select(m => new MediaItemViewModel(m)).ToList();
                foreach (var item in newItems)
                {
                    MediaItems.Add(item);
                }
                ApplyFilters();
                _ = LoadThumbnailsAsync(newItems);
            }
        }
        finally
        {
            IsLoading = false;
            StatusMessage = string.Empty;
        }
    }

    private void ReplaceMediaItems(IReadOnlyList<MediaFile> items)
    {
        MediaItems.Clear();
        foreach (var media in items)
        {
            MediaItems.Add(new MediaItemViewModel(media));
        }
    }

    private void OnScanProgress(ScanProgress progress)
    {
        StatusMessage = progress.Total > 0
            ? $"正在扫描 {progress.Scanned}/{progress.Total}"
            : "准备扫描...";
    }

    private async Task LoadThumbnailsAsync(IEnumerable<MediaItemViewModel> items)
    {
        foreach (var item in items)
        {
            var source = await _thumbnails.GetThumbnailAsync(item.Media.Path, item.Media.Type == MediaType.Video);
            if (source != null)
            {
                SetThumbnailSafe(item, source);
            }
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

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
