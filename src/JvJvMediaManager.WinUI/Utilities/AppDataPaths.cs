namespace JvJvMediaManager.Utilities;

public static class AppDataPaths
{
    public const string StorageRootEnvironmentVariable = "JVJVMM_STORAGE_ROOT";

    public static string GetStorageRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable(StorageRootEnvironmentVariable);
        var root = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(AppContext.BaseDirectory, "data")
            : configuredRoot.Trim();

        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        return root;
    }

    public static string GetSettingsPath()
    {
        return Path.Combine(GetStorageRoot(), "settings.json");
    }

    public static string GetLogsDirectory()
    {
        return Path.Combine(GetStorageRoot(), "logs");
    }
}
