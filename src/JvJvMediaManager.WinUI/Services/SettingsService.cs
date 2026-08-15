using System.Text.Json;
using JvJvMediaManager.Models;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.Services;

public sealed class SettingsService : IAsyncDisposable, IDisposable
{
    private const int NumpadShortcutCount = 9;
    private static readonly TimeSpan SaveDebounceDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _settingsSync = new();
    private readonly string _settingsPath;
    private CancellationTokenSource? _saveDebounceCancellation;
    private SettingsModel _settings;
    private long _saveGeneration;
    private long _persistedGeneration;
    private string? _pendingJson;
    private bool _disposed;

    public SettingsService()
    {
        _settingsPath = AppDataPaths.GetSettingsPath();
        _settings = LoadInternal();
    }

    public string DataDir
    {
        get
        {
            var portableMode = ReadSettings(settings => settings.PortableMode ?? true);
            var configuredDataDir = ReadSettings(settings => settings.DataDir);
            var dir = portableMode
                ? GetPortableDataDir()
                : string.IsNullOrWhiteSpace(configuredDataDir) ? GetDefaultDataDir() : configuredDataDir!;
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public string? ConfiguredDataDir => ReadSettings(settings => settings.DataDir);

    public bool PortableMode => ReadSettings(settings => settings.PortableMode ?? true);

    public IReadOnlyList<WatchedFolder> WatchedFolders => ReadSettings(settings => NormalizeWatchedFolders(settings.WatchedFolders));

    public string LockPassword => ReadSettings(settings => settings.LockPassword ?? string.Empty);

    public IReadOnlyList<string> NumpadTagShortcuts => ReadSettings(settings => NormalizeNumpadTagShortcuts(settings.NumpadTagShortcuts));

    public string PlaybackMode => ReadSettings(settings => settings.PlaybackMode ?? string.Empty);

    public bool PlaybackAutoAdvanceEnabled => ReadSettings(settings => settings.PlaybackAutoAdvanceEnabled ?? false);

    public bool ShuffleVideoOnly => ReadSettings(settings => settings.ShuffleVideoOnly ?? false);

    public MediaLibraryViewState MediaLibraryViewState => ReadSettings(settings => new MediaLibraryViewState
    {
        ViewMode = ParseEnum(settings.MediaBrowserViewMode, MediaViewMode.List),
        SortField = ParseEnum(settings.MediaBrowserSortField, MediaSortField.ModifiedAt),
        SortOrder = ParseEnum(settings.MediaBrowserSortOrder, MediaSortOrder.Desc),
        TypeFilter = ParseOptionalEnum<MediaType>(settings.MediaBrowserTypeFilter),
        IconSize = Math.Clamp(settings.MediaBrowserIconSize ?? 120, 72, 260),
        IsLibraryPaneOpen = settings.IsLibraryPaneOpen ?? true,
        LibraryPaneWidth = Math.Max(240, settings.LibraryPaneWidth ?? 360),
        LibraryPaneRatio = settings.LibraryPaneRatio is > 0
            ? Math.Clamp(settings.LibraryPaneRatio.Value, 0.15, 0.75)
            : null,
        FolderCollapseStates = NormalizeFolderCollapseStates(settings.MediaFolderCollapseStates ?? new(StringComparer.OrdinalIgnoreCase))
    });

    public MediaViewMode MediaBrowserViewMode => ReadSettings(settings => ParseEnum(settings.MediaBrowserViewMode, MediaViewMode.List));

    public MediaSortField MediaBrowserSortField => ReadSettings(settings => ParseEnum(settings.MediaBrowserSortField, MediaSortField.ModifiedAt));

    public MediaSortOrder MediaBrowserSortOrder => ReadSettings(settings => ParseEnum(settings.MediaBrowserSortOrder, MediaSortOrder.Desc));

    public MediaType? MediaBrowserTypeFilter => ReadSettings(settings => ParseOptionalEnum<MediaType>(settings.MediaBrowserTypeFilter));

    public int MediaBrowserIconSize => ReadSettings(settings => Math.Clamp(settings.MediaBrowserIconSize ?? 120, 72, 260));

    public bool IsLibraryPaneOpen => ReadSettings(settings => settings.IsLibraryPaneOpen ?? true);

    public double LibraryPaneWidth => ReadSettings(settings => Math.Max(240, settings.LibraryPaneWidth ?? 360));

    public double? LibraryPaneRatio => ReadSettings<double?>(settings => settings.LibraryPaneRatio is > 0
        ? Math.Clamp(settings.LibraryPaneRatio.Value, 0.15, 0.75)
        : null);

    public IReadOnlyDictionary<string, bool> MediaFolderCollapseStates
        => ReadSettings(settings => NormalizeFolderCollapseStates(settings.MediaFolderCollapseStates ?? new(StringComparer.OrdinalIgnoreCase)));

    public IReadOnlyDictionary<string, WatchedFolderScanSnapshot> WatchedFolderScanSnapshots
        => ReadSettings(settings => NormalizeWatchedFolderScanSnapshots(settings.WatchedFolderScanSnapshots ?? new(StringComparer.OrdinalIgnoreCase)));

    public MediaLibraryScanState MediaLibraryScanState => ReadSettings(settings => NormalizeMediaLibraryScanState(settings.MediaLibraryScanState));

    public void SetDataDir(string path)
    {
        UpdateSettings(settings =>
        {
            settings.DataDir = string.IsNullOrWhiteSpace(path)
                ? null
                : path.Trim();
        });
    }

    public void SetPortableMode(bool enabled)
    {
        UpdateSettings(settings => settings.PortableMode = enabled);
    }

    public void SetWatchedFolders(List<WatchedFolder> folders)
    {
        UpdateSettings(settings => settings.WatchedFolders = NormalizeWatchedFolders(folders));
    }

    public void SetWatchedFolderScanSnapshots(IEnumerable<WatchedFolderScanSnapshot> snapshots)
    {
        var snapshotList = snapshots.ToList();
        UpdateSettings(settings =>
        {
            settings.WatchedFolderScanSnapshots = NormalizeWatchedFolderScanSnapshots(
                snapshotList
                    .Where(snapshot => !string.IsNullOrWhiteSpace(snapshot.Path))
                    .Select(snapshot => new KeyValuePair<string, WatchedFolderScanSnapshot>(snapshot.Path, snapshot)));
        });
    }

    public void SetMediaLibraryScanState(MediaLibraryScanState scanState)
    {
        UpdateSettings(settings => settings.MediaLibraryScanState = NormalizeMediaLibraryScanState(scanState));
    }

    public void SetLockPassword(string password)
    {
        UpdateSettings(settings => settings.LockPassword = password);
    }

    public void SetNumpadTagShortcuts(IReadOnlyList<string> shortcuts)
    {
        UpdateSettings(settings => settings.NumpadTagShortcuts = NormalizeNumpadTagShortcuts(shortcuts));
    }

    public void SetPlaybackMode(string playbackMode)
    {
        UpdateSettings(settings =>
        {
            settings.PlaybackMode = string.IsNullOrWhiteSpace(playbackMode)
                ? null
                : playbackMode.Trim();
        });
    }

    public void SetPlaybackAutoAdvanceEnabled(bool enabled)
    {
        UpdateSettings(settings => settings.PlaybackAutoAdvanceEnabled = enabled);
    }

    public void SetShuffleVideoOnly(bool enabled)
    {
        UpdateSettings(settings => settings.ShuffleVideoOnly = enabled);
    }

    public void SetMediaLibraryViewState(MediaLibraryViewState viewState)
    {
        ArgumentNullException.ThrowIfNull(viewState);

        UpdateSettings(settings =>
        {
            settings.MediaBrowserViewMode = viewState.ViewMode.ToString();
            settings.MediaBrowserSortField = viewState.SortField.ToString();
            settings.MediaBrowserSortOrder = viewState.SortOrder.ToString();
            settings.MediaBrowserTypeFilter = viewState.TypeFilter?.ToString();
            settings.MediaBrowserIconSize = Math.Clamp(viewState.IconSize, 72, 260);
            settings.IsLibraryPaneOpen = viewState.IsLibraryPaneOpen;
            settings.LibraryPaneWidth = Math.Max(240, viewState.LibraryPaneWidth);
            settings.LibraryPaneRatio = viewState.LibraryPaneRatio is > 0
                ? Math.Clamp(viewState.LibraryPaneRatio.Value, 0.15, 0.75)
                : null;
            settings.MediaFolderCollapseStates = NormalizeFolderCollapseStates(viewState.FolderCollapseStates);
        });
    }

    public void SetMediaBrowserViewMode(MediaViewMode viewMode)
    {
        UpdateSettings(settings => settings.MediaBrowserViewMode = viewMode.ToString());
    }

    public void SetMediaBrowserSort(MediaSortField sortField, MediaSortOrder sortOrder)
    {
        UpdateSettings(settings =>
        {
            settings.MediaBrowserSortField = sortField.ToString();
            settings.MediaBrowserSortOrder = sortOrder.ToString();
        });
    }

    public void SetMediaBrowserTypeFilter(MediaType? typeFilter)
    {
        UpdateSettings(settings => settings.MediaBrowserTypeFilter = typeFilter?.ToString());
    }

    public void SetMediaBrowserIconSize(int iconSize)
    {
        UpdateSettings(settings => settings.MediaBrowserIconSize = Math.Clamp(iconSize, 72, 260));
    }

    public void SetLibraryPaneLayout(bool isOpen, double width, double? hostWidth = null)
    {
        UpdateSettings(settings =>
        {
            settings.IsLibraryPaneOpen = isOpen;
            settings.LibraryPaneWidth = Math.Max(240, width);
            if (hostWidth is > 0)
            {
                settings.LibraryPaneRatio = Math.Clamp(settings.LibraryPaneWidth.Value / hostWidth.Value, 0.15, 0.75);
            }
        });

    }

    public void SetMediaFolderCollapseStates(IReadOnlyDictionary<string, bool> states)
    {
        UpdateSettings(settings => settings.MediaFolderCollapseStates = NormalizeFolderCollapseStates(states));
    }

    public string GetDefaultDataDir()
    {
        return AppDataPaths.GetStorageRoot();
    }

    public string GetPortableDataDir()
    {
        return AppDataPaths.GetStorageRoot();
    }

    public string GetThumbnailCacheDir()
    {
        var dir = Path.Combine(DataDir, "thumb-cache");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string GetTimelineThumbnailCacheDir()
    {
        var dir = Path.Combine(DataDir, "timeline-thumb-cache");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private SettingsModel LoadInternal()
    {
        try
        {
            var tempSettings = TryReadSettings($"{_settingsPath}.tmp", out var tempLastWriteUtc);
            var settings = TryReadSettings(_settingsPath, out var settingsLastWriteUtc);
            if (tempSettings != null && (settings == null || tempLastWriteUtc > settingsLastWriteUtc))
            {
                TryPromoteTempSettingsFile();
                return tempSettings;
            }

            TryDeleteTempSettingsFile();
            if (settings != null)
            {
                return settings;
            }

            return new SettingsModel();
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogSampled(
                "SettingsService",
                "load-failed",
                $"Settings load failed; using defaults. Path='{_settingsPath}', ErrorType={ex.GetType().Name}, Message='{ex.Message}'.",
                TimeSpan.FromMinutes(1));
            return new SettingsModel();
        }
    }

    private SettingsModel? TryReadSettings(string path, out DateTime lastWriteUtc)
    {
        lastWriteUtc = DateTime.MinValue;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            lastWriteUtc = File.GetLastWriteTimeUtc(path);
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SettingsModel>(json);
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogSampled(
                "SettingsService",
                $"read-failed:{path}",
                $"Failed to read settings file. Path='{path}', ErrorType={ex.GetType().Name}, Message='{ex.Message}'.",
                TimeSpan.FromMinutes(5));
            return null;
        }
    }

    private void TryPromoteTempSettingsFile()
    {
        var tempPath = $"{_settingsPath}.tmp";
        try
        {
            File.Move(tempPath, _settingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogSampled(
                "SettingsService",
                "promote-temp-failed",
                $"Failed to promote temp settings file; deleting temp. Path='{tempPath}', ErrorType={ex.GetType().Name}, Message='{ex.Message}'.",
                TimeSpan.FromMinutes(5));
            TryDeleteTempSettingsFile();
        }
    }

    private void TryDeleteTempSettingsFile()
    {
        var tempPath = $"{_settingsPath}.tmp";
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private T ReadSettings<T>(Func<SettingsModel, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        lock (_settingsSync)
        {
            return read(_settings);
        }
    }

    private void UpdateSettings(Action<SettingsModel> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        lock (_settingsSync)
        {
            if (_disposed)
            {
                AppTraceLogger.LogSampled(
                    "SettingsService",
                    "update-after-dispose",
                    "Ignored settings update after dispose.",
                    TimeSpan.FromSeconds(5));
                return;
            }

            var previousJson = SerializeSettings(_settings);
            update(_settings);
            var nextJson = SerializeSettings(_settings);
            if (string.Equals(previousJson, nextJson, StringComparison.Ordinal))
            {
                AppTraceLogger.LogSampled(
                    "SettingsService",
                    "save-skipped-unchanged",
                    "Skipped settings save because the serialized settings did not change.",
                    TimeSpan.FromSeconds(5));
                return;
            }

            _pendingJson = nextJson;
            _saveGeneration++;
            ScheduleSaveLocked(nextJson, _saveGeneration);
        }
    }

    /// <summary>
    /// Cancels any pending debounce and immediately persists the latest settings
    /// snapshot. Safe to call during shutdown; bounded by <see cref="FlushTimeout"/>.
    /// </summary>
    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        string? json;
        long generation;
        lock (_settingsSync)
        {
            _saveDebounceCancellation?.Cancel();
            _saveDebounceCancellation?.Dispose();
            _saveDebounceCancellation = null;

            if (_pendingJson == null || _persistedGeneration >= _saveGeneration)
            {
                return Task.CompletedTask;
            }

            json = _pendingJson;
            generation = _saveGeneration;
        }

        return Task.Run(() =>
        {
            try
            {
                lock (_settingsSync)
                {
                    if (_persistedGeneration >= generation)
                    {
                        return;
                    }

                    SaveLocked(json);
                    _persistedGeneration = generation;
                }
            }
            catch (Exception ex)
            {
                AppTraceLogger.LogException("SettingsService", "Settings flush failed.", ex);
            }
        }, cancellationToken).WaitAsync(FlushTimeout, cancellationToken);
    }

    private void ScheduleSaveLocked(string json, long generation)
    {
        _saveDebounceCancellation?.Cancel();
        _saveDebounceCancellation?.Dispose();
        _saveDebounceCancellation = new CancellationTokenSource();
        var debounce = _saveDebounceCancellation;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SaveDebounceDelay, debounce.Token);
                lock (_settingsSync)
                {
                    if (debounce.IsCancellationRequested || !ReferenceEquals(_saveDebounceCancellation, debounce))
                    {
                        return;
                    }

                    if (_persistedGeneration < generation)
                    {
                        SaveLocked(json);
                        _persistedGeneration = generation;
                    }

                    _saveDebounceCancellation = null;
                }

                debounce.Dispose();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppTraceLogger.LogException("SettingsService", "Debounced settings save failed.", ex);
            }
            finally
            {
                if (!ReferenceEquals(_saveDebounceCancellation, debounce))
                {
                    debounce.Dispose();
                }
            }
        });
    }

    private void SaveLocked(string? json = null)
    {
        json ??= SerializeSettings(_settings);
        var settingsDirectory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(settingsDirectory))
        {
            Directory.CreateDirectory(settingsDirectory);
        }

        var tempPath = $"{_settingsPath}.tmp";
        try
        {
            File.WriteAllText(tempPath, json);
            var persistedJson = File.ReadAllText(tempPath);
            _ = JsonSerializer.Deserialize<SettingsModel>(persistedJson)
                ?? throw new InvalidOperationException("Settings write verification failed.");
            File.Move(tempPath, _settingsPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Best-effort cleanup only; a later save can overwrite the temp file.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_settingsSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        try
        {
            await FlushAsync();
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("SettingsService", "Settings dispose flush failed.", ex);
        }
    }

    public void Dispose()
    {
        try
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("SettingsService", "Settings synchronous dispose failed.", ex);
        }
    }

    private static string SerializeSettings(SettingsModel settings)
    {
        return JsonSerializer.Serialize(settings, JsonOptions);
    }

    private static List<string> NormalizeNumpadTagShortcuts(IReadOnlyList<string>? shortcuts)
    {
        var normalized = new List<string>(Math.Max(NumpadShortcutCount, shortcuts?.Count ?? 0));
        for (var i = 0; i < Math.Max(NumpadShortcutCount, shortcuts?.Count ?? 0); i++)
        {
            var value = shortcuts != null && i < shortcuts.Count
                ? shortcuts[i]?.Trim()
                : string.Empty;
            normalized.Add(string.IsNullOrWhiteSpace(value) ? string.Empty : value);
        }

        return normalized;
    }

    private static List<WatchedFolder> NormalizeWatchedFolders(IEnumerable<WatchedFolder>? folders)
    {
        return (folders ?? Array.Empty<WatchedFolder>())
            .Where(folder => !string.IsNullOrWhiteSpace(folder.Path))
            .Select(folder => new
            {
                Path = PathHelpers.NormalizeFolderPath(folder.Path),
                folder.Locked,
                folder.Visible
            })
            .Where(folder => !string.IsNullOrWhiteSpace(folder.Path))
            .GroupBy(folder => folder.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var last = group.Last();
                return new WatchedFolder
                {
                    Path = group.Key,
                    Locked = last.Locked,
                    Visible = last.Visible
                };
            })
            .OrderBy(folder => folder.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback)
        where TEnum : struct, Enum
    {
        return Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed)
            ? parsed
            : fallback;
    }

    private static TEnum? ParseOptionalEnum<TEnum>(string? value)
        where TEnum : struct, Enum
    {
        return Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed)
            ? parsed
            : null;
    }

    private static Dictionary<string, bool> NormalizeFolderCollapseStates(IEnumerable<KeyValuePair<string, bool>> states)
    {
        return states
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .Select(pair => new KeyValuePair<string, bool>(PathHelpers.NormalizeFolderPath(pair.Key), pair.Value))
            .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Last().Value,
                StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, WatchedFolderScanSnapshot> NormalizeWatchedFolderScanSnapshots(IEnumerable<KeyValuePair<string, WatchedFolderScanSnapshot>> snapshots)
    {
        return snapshots
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .Select(pair => NormalizeWatchedFolderScanSnapshot(pair.Value, pair.Key))
            .GroupBy(snapshot => snapshot.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Last(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static WatchedFolderScanSnapshot NormalizeWatchedFolderScanSnapshot(WatchedFolderScanSnapshot? snapshot, string fallbackPath)
    {
        if (snapshot == null)
        {
            return new WatchedFolderScanSnapshot
            {
                Path = PathHelpers.NormalizeFolderPath(fallbackPath)
            };
        }

        var path = string.IsNullOrWhiteSpace(snapshot.Path)
            ? fallbackPath
            : snapshot.Path;
        return new WatchedFolderScanSnapshot
        {
            Path = PathHelpers.NormalizeFolderPath(path),
            Exists = snapshot.Exists,
            TopLevelMediaCount = Math.Max(0, snapshot.TopLevelMediaCount),
            LatestTopLevelMediaModifiedAt = Math.Max(0, snapshot.LatestTopLevelMediaModifiedAt),
            TopLevelDirectoryCount = Math.Max(0, snapshot.TopLevelDirectoryCount),
            LatestTopLevelDirectoryModifiedAt = Math.Max(0, snapshot.LatestTopLevelDirectoryModifiedAt),
            CompletedAt = Math.Max(0, snapshot.CompletedAt),
            HadErrors = snapshot.HadErrors
        };
    }

    private static MediaLibraryScanState NormalizeMediaLibraryScanState(MediaLibraryScanState? scanState)
    {
        if (scanState == null)
        {
            return new MediaLibraryScanState();
        }

        return new MediaLibraryScanState
        {
            State = MediaLibraryScanState.ParseStatus(scanState.Status),
            Operation = scanState.Operation?.Trim() ?? string.Empty,
            StartedAt = Math.Max(0, scanState.StartedAt),
            FinishedAt = scanState.FinishedAt is > 0 ? scanState.FinishedAt : null,
            AddedOrUpdated = scanState.AddedOrUpdated is >= 0 ? scanState.AddedOrUpdated : null,
            Error = string.IsNullOrWhiteSpace(scanState.Error) ? null : scanState.Error.Trim()
        };
    }

    private sealed class SettingsModel
    {
        public string? DataDir { get; set; }
        public bool? PortableMode { get; set; } = true;
        public List<WatchedFolder> WatchedFolders { get; set; } = new();
        public string? LockPassword { get; set; }
        public List<string> NumpadTagShortcuts { get; set; } = new();
        public string? PlaybackMode { get; set; }
        public bool? PlaybackAutoAdvanceEnabled { get; set; }
        public bool? ShuffleVideoOnly { get; set; }
        public string? MediaBrowserViewMode { get; set; }
        public string? MediaBrowserSortField { get; set; }
        public string? MediaBrowserSortOrder { get; set; }
        public string? MediaBrowserTypeFilter { get; set; }
        public int? MediaBrowserIconSize { get; set; }
        public bool? IsLibraryPaneOpen { get; set; }
        public double? LibraryPaneWidth { get; set; }
        public double? LibraryPaneRatio { get; set; }
        public Dictionary<string, bool> MediaFolderCollapseStates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, WatchedFolderScanSnapshot> WatchedFolderScanSnapshots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public MediaLibraryScanState? MediaLibraryScanState { get; set; }
    }
}
