using System.Text.Json;
using JvJvMediaManager.Models;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.Services;

public sealed class SettingsService
{
    private const int NumpadShortcutCount = 9;
    private readonly string _settingsPath;
    private SettingsModel _settings;

    public SettingsService()
    {
        _settingsPath = AppDataPaths.GetSettingsPath();
        _settings = LoadInternal();
    }

    public string DataDir
    {
        get
        {
            var dir = PortableMode
                ? GetPortableDataDir()
                : string.IsNullOrWhiteSpace(_settings.DataDir) ? GetDefaultDataDir() : _settings.DataDir!;
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public string? ConfiguredDataDir => _settings.DataDir;

    public bool PortableMode => _settings.PortableMode ?? true;

    public IReadOnlyList<WatchedFolder> WatchedFolders => _settings.WatchedFolders;

    public string LockPassword => _settings.LockPassword ?? string.Empty;

    public IReadOnlyList<string> NumpadTagShortcuts => NormalizeNumpadTagShortcuts(_settings.NumpadTagShortcuts);

    public void SetDataDir(string path)
    {
        _settings.DataDir = string.IsNullOrWhiteSpace(path)
            ? null
            : path.Trim();
        Save();
    }

    public void SetPortableMode(bool enabled)
    {
        _settings.PortableMode = enabled;
        Save();
    }

    public void SetWatchedFolders(List<WatchedFolder> folders)
    {
        _settings.WatchedFolders = folders;
        Save();
    }

    public void SetLockPassword(string password)
    {
        _settings.LockPassword = password;
        Save();
    }

    public void SetNumpadTagShortcuts(IReadOnlyList<string> shortcuts)
    {
        _settings.NumpadTagShortcuts = NormalizeNumpadTagShortcuts(shortcuts);
        Save();
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
            if (!File.Exists(_settingsPath))
            {
                return new SettingsModel();
            }

            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<SettingsModel>(json) ?? new SettingsModel();
        }
        catch
        {
            return new SettingsModel();
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsPath, json);
    }

    private static List<string> NormalizeNumpadTagShortcuts(IReadOnlyList<string>? shortcuts)
    {
        var normalized = new List<string>(NumpadShortcutCount);
        for (var i = 0; i < NumpadShortcutCount; i++)
        {
            var value = shortcuts != null && i < shortcuts.Count
                ? shortcuts[i]?.Trim()
                : string.Empty;
            normalized.Add(string.IsNullOrWhiteSpace(value) ? string.Empty : value);
        }

        return normalized;
    }

    private sealed class SettingsModel
    {
        public string? DataDir { get; set; }
        public bool? PortableMode { get; set; } = true;
        public List<WatchedFolder> WatchedFolders { get; set; } = new();
        public string? LockPassword { get; set; }
        public List<string> NumpadTagShortcuts { get; set; } = new();
    }
}
