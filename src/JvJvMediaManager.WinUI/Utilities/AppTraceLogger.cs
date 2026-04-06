using System.Collections.Concurrent;
using System.Text;

namespace JvJvMediaManager.Utilities;

public static class AppTraceLogger
{
    private static readonly ConcurrentQueue<string> PendingLines = new();
    private static readonly ConcurrentDictionary<string, long> LastLogTicksByKey = new();
    private static int _flushScheduled;

    public static void Log(string category, string message)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{category}] {message}";
            PendingLines.Enqueue(line);

            if (Interlocked.Exchange(ref _flushScheduled, 1) == 0)
            {
                _ = Task.Run(FlushPendingAsync);
            }
        }
        catch
        {
            // Best-effort diagnostics only.
        }
    }

    public static void LogSampled(string category, string sampleKey, string message, TimeSpan minimumInterval)
    {
        try
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            var intervalTicks = Math.Max(minimumInterval.Ticks, TimeSpan.FromMilliseconds(100).Ticks);
            var cacheKey = $"{category}:{sampleKey}";

            if (LastLogTicksByKey.TryGetValue(cacheKey, out var lastTicks)
                && nowTicks - lastTicks < intervalTicks)
            {
                return;
            }

            LastLogTicksByKey[cacheKey] = nowTicks;
            Log(category, message);
        }
        catch
        {
            // Best-effort diagnostics only.
        }
    }

    private static async Task FlushPendingAsync()
    {
        try
        {
            while (true)
            {
                var lines = new List<string>(64);
                while (lines.Count < 64 && PendingLines.TryDequeue(out var line))
                {
                    lines.Add(line);
                }

                if (lines.Count == 0)
                {
                    return;
                }

                var logDirectory = AppDataPaths.GetLogsDirectory();
                Directory.CreateDirectory(logDirectory);
                var logPath = Path.Combine(logDirectory, $"trace-{DateTime.Now:yyyyMMdd}.log");
                var builder = new StringBuilder();
                foreach (var line in lines)
                {
                    builder.AppendLine(line);
                }

                await File.AppendAllTextAsync(logPath, builder.ToString());
            }
        }
        catch
        {
            // Best-effort diagnostics only.
        }
        finally
        {
            Interlocked.Exchange(ref _flushScheduled, 0);
            if (!PendingLines.IsEmpty && Interlocked.Exchange(ref _flushScheduled, 1) == 0)
            {
                _ = Task.Run(FlushPendingAsync);
            }
        }
    }
}
