using System.Text.Json;
using JvJvMediaManager.Models;

namespace JvJvMediaManager.Services;

public sealed class SettingsService
{
    private readonly string _settingsPath;
    private SettingsModel _settings;

    public SettingsService()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JvJvMediaManager");
        Directory.CreateDirectory(root);
        _settingsPath = Path.Combine(root, "settings.json");
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

    public bool PortableMode => _settings.PortableMode;

    public IReadOnlyList<WatchedFolder> WatchedFolders => _settings.WatchedFolders;

    public string LockPassword => _settings.LockPassword ?? string.Empty;

    public void SetDataDir(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        _settings.DataDir = path;
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

    public string GetDefaultDataDir()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JvJvMediaManager");
        return Path.Combine(root, "data");
    }

    public string GetPortableDataDir()
    {
        return Path.Combine(AppContext.BaseDirectory, "data");
    }

    public string GetThumbnailCacheDir()
    {
        var dir = Path.Combine(DataDir, "thumb-cache");
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

    private sealed class SettingsModel
    {
        public string? DataDir { get; set; }
        public bool PortableMode { get; set; }
        public List<WatchedFolder> WatchedFolders { get; set; } = new();
        public string? LockPassword { get; set; }
    }
}
