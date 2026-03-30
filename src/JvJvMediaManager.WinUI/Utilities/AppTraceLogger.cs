namespace JvJvMediaManager.Utilities;

public static class AppTraceLogger
{
    private static readonly object SyncRoot = new();

    public static void Log(string category, string message)
    {
        try
        {
            var logDirectory = AppDataPaths.GetLogsDirectory();
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, $"trace-{DateTime.Now:yyyyMMdd}.log");
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{category}] {message}{Environment.NewLine}";
            lock (SyncRoot)
            {
                File.AppendAllText(logPath, line);
            }
        }
        catch
        {
            // Best-effort diagnostics only.
        }
    }
}
