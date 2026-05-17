using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace JvJvMediaManager.Utilities;

public static class AppTraceLogger
{
    private static readonly ConcurrentQueue<string> PendingLines = new();
    private static readonly ConcurrentQueue<string> RecentLines = new();
    private static readonly ConcurrentDictionary<string, long> LastLogTicksByKey = new();
    private static readonly SemaphoreSlim FlushGate = new(1, 1);
    private static readonly string SessionId = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}";
    private const int MaxRecentLines = 256;
    private const int MaxMessageLength = 12000;
    private static int _flushScheduled;
    private static int _recentLineCount;
    private static long _sequence;

    public static void Log(string category, string message)
    {
        try
        {
            var sequence = Interlocked.Increment(ref _sequence);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{category}] [session:{SessionId}] [seq:{sequence}] {NormalizeMessage(message)}";
            PendingLines.Enqueue(line);
            RecentLines.Enqueue(line);
            var recentCount = Interlocked.Increment(ref _recentLineCount);
            while (recentCount > MaxRecentLines && RecentLines.TryDequeue(out _))
            {
                recentCount = Interlocked.Decrement(ref _recentLineCount);
            }

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

    public static void LogException(string category, string message, Exception exception)
    {
        Log(category, $"{message} {FormatException(exception)}");
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

    public static void LogSessionStart()
    {
        try
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(AppTraceLogger).Assembly;
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString()
                ?? "<unknown>";
            using var process = Process.GetCurrentProcess();
            Log(
                "App",
                $"Session started. SessionId={SessionId}, Version={version}, ProcessId={Environment.ProcessId}, ProcessName={process.ProcessName}, BaseDirectory='{AppContext.BaseDirectory}', CurrentDirectory='{Environment.CurrentDirectory}', StorageRoot='{AppDataPaths.GetStorageRoot()}', Runtime='{RuntimeInformation.FrameworkDescription}', OS='{RuntimeInformation.OSDescription}', Architecture={RuntimeInformation.ProcessArchitecture}.");
        }
        catch
        {
            // Best-effort diagnostics only.
        }
    }

    public static IReadOnlyList<string> GetRecentLines(int maxLines = 80)
    {
        try
        {
            var lines = RecentLines.ToArray();
            if (lines.Length <= maxLines)
            {
                return lines;
            }

            return lines[^maxLines..];
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static void FlushNow()
    {
        try
        {
            FlushPendingAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Best-effort diagnostics only.
        }
    }

    private static async Task FlushPendingAsync()
    {
        await FlushGate.WaitAsync();
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
            FlushGate.Release();
            Interlocked.Exchange(ref _flushScheduled, 0);
            if (!PendingLines.IsEmpty && Interlocked.Exchange(ref _flushScheduled, 1) == 0)
            {
                _ = Task.Run(FlushPendingAsync);
            }
        }
    }

    private static string NormalizeMessage(string message)
    {
        var normalized = (message ?? string.Empty)
            .Replace("\r\n", " | ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ');

        return normalized.Length <= MaxMessageLength
            ? normalized
            : $"{normalized[..MaxMessageLength]}... <truncated>";
    }

    private static string FormatException(Exception exception)
    {
        var stackTop = exception.StackTrace?
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()
            ?.Trim();

        var builder = new StringBuilder();
        builder.Append("ExceptionType=").Append(exception.GetType().FullName);
        builder.Append(", HResult=0x").Append(exception.HResult.ToString("X8"));
        builder.Append(", Message='").Append(NormalizeMessage(exception.Message)).Append('\'');
        if (!string.IsNullOrWhiteSpace(stackTop))
        {
            builder.Append(", StackTop='").Append(NormalizeMessage(stackTop)).Append('\'');
        }

        if (exception.InnerException != null)
        {
            builder.Append(", Inner=(").Append(FormatException(exception.InnerException)).Append(')');
        }

        return builder.ToString();
    }
}
