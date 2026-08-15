using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace JvJvMediaManager.Utilities;

public static class AppTraceLogger
{
    private readonly record struct PendingLogEntry(string Line, bool HighValue);

    private static readonly ConcurrentQueue<PendingLogEntry> PendingLines = new();
    private static readonly ConcurrentQueue<string> RecentLines = new();
    private static readonly ConcurrentDictionary<string, long> LastLogTicksByKey = new();
    private static readonly SemaphoreSlim FlushGate = new(1, 1);
    private static readonly string SessionId = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}";
    private static readonly string LogDirectory = AppDataPaths.GetLogsDirectory();
    private const int MaxRecentLines = 256;
    private const int MaxMessageLength = 12000;

    // WP-09 capacity bounds (configurable defaults).
    private const int MaxPendingLines = 10_000;
    private const long MaxLogFileBytes = 32L * 1024 * 1024;
    private const long MaxTotalLogBytes = 512L * 1024 * 1024;
    private const int MaxRetentionDays = 14;
    private const int MaxSampleKeys = 4096;
    private static readonly TimeSpan SampleKeyMaxAge = TimeSpan.FromMinutes(30);

    private static int _flushScheduled;
    private static int _recentLineCount;
    private static int _pendingLineCount;
    private static int _sampleKeyPruneScheduled;
    private static long _sequence;
    private static long _droppedPendingLines;
    private static long _flushFailures;
    private static long _logBytesWritten;
    private static string? _currentLogDate;
    private static string? _currentLogPath;
    private static int _currentLogShard;
    private static long _currentLogBytes;

    public static long DroppedPendingLines => Interlocked.Read(ref _droppedPendingLines);
    public static long FlushFailures => Interlocked.Read(ref _flushFailures);
    public static long LogBytesWritten => Interlocked.Read(ref _logBytesWritten);
    public static int PendingQueueLength => Volatile.Read(ref _pendingLineCount);
    public static int SampleKeyCount => LastLogTicksByKey.Count;

    public static void Log(string category, string message)
    {
        Enqueue(category, message, highValue: true);
    }

    public static void LogException(string category, string message, Exception exception)
    {
        Log(category, $"{message} {FormatException(exception)}");
    }

    public static void LogSampled(string category, string sampleKey, string message, TimeSpan minimumInterval)
        => LogSampled(category, sampleKey, () => message, minimumInterval);

    public static void LogSampled(string category, string sampleKey, Func<string> messageFactory, TimeSpan minimumInterval)
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
            MaybePruneSampleKeys();
            Enqueue(category, messageFactory(), highValue: false);
        }
        catch
        {
            // Best-effort diagnostics only.
        }
    }

    private static void Enqueue(string category, string message, bool highValue)
    {
        try
        {
            var sequence = Interlocked.Increment(ref _sequence);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{category}] [session:{SessionId}] [seq:{sequence}] {NormalizeMessage(message)}";

            RecentLines.Enqueue(line);
            var recentCount = Interlocked.Increment(ref _recentLineCount);
            while (recentCount > MaxRecentLines && RecentLines.TryDequeue(out _))
            {
                recentCount = Interlocked.Decrement(ref _recentLineCount);
            }

            var pendingCount = Volatile.Read(ref _pendingLineCount);
            if (pendingCount >= MaxPendingLines)
            {
                if (!highValue)
                {
                    // Drop low-value (sampled) lines first when the queue is saturated.
                    Interlocked.Increment(ref _droppedPendingLines);
                    return;
                }

                // Make room for a high-value line by discarding the oldest entry.
                if (PendingLines.TryDequeue(out _))
                {
                    Interlocked.Decrement(ref _pendingLineCount);
                    Interlocked.Increment(ref _droppedPendingLines);
                }
            }

            PendingLines.Enqueue(new PendingLogEntry(line, highValue));
            Interlocked.Increment(ref _pendingLineCount);

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
            ScheduleRetentionCleanup();
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
            // WP-09: bound the crash-path flush so crash handling never deadlocks.
            if (!FlushPendingAsync().Wait(TimeSpan.FromSeconds(2)))
            {
                Interlocked.Increment(ref _flushFailures);
            }
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
                while (lines.Count < 64 && PendingLines.TryDequeue(out var entry))
                {
                    Interlocked.Decrement(ref _pendingLineCount);
                    lines.Add(entry.Line);
                }

                if (lines.Count == 0)
                {
                    return;
                }

                var builder = new StringBuilder();
                foreach (var line in lines)
                {
                    builder.AppendLine(line);
                }

                var payload = builder.ToString();
                var byteCount = Encoding.UTF8.GetByteCount(payload);
                var logPath = GetLogPathForAppend(byteCount);

                try
                {
                    await File.AppendAllTextAsync(logPath, payload);
                    Interlocked.Add(ref _logBytesWritten, byteCount);
                    _currentLogBytes += byteCount;
                }
                catch
                {
                    Interlocked.Increment(ref _flushFailures);
                }
            }
        }
        catch
        {
            Interlocked.Increment(ref _flushFailures);
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

    private static string GetLogPathForAppend(int incomingBytes)
    {
        var date = DateTime.Now.ToString("yyyyMMdd");
        var dateChanged = !string.Equals(_currentLogDate, date, StringComparison.Ordinal);

        if (dateChanged || string.IsNullOrWhiteSpace(_currentLogPath))
        {
            Directory.CreateDirectory(LogDirectory);
            _currentLogDate = date;
            _currentLogShard = FindLatestShard(date);
            _currentLogPath = BuildLogPath(date, _currentLogShard);
            _currentLogBytes = SafeFileLength(_currentLogPath);
            if (dateChanged)
            {
                ScheduleRetentionCleanup();
            }
        }

        // Roll to a new shard when the current file would exceed the size cap.
        if (_currentLogBytes + incomingBytes > MaxLogFileBytes && _currentLogBytes > 0)
        {
            _currentLogShard++;
            _currentLogPath = BuildLogPath(date, _currentLogShard);
            _currentLogBytes = SafeFileLength(_currentLogPath);
        }

        return _currentLogPath!;
    }

    private static string BuildLogPath(string date, int shard)
    {
        return Path.Combine(LogDirectory, $"trace-{date}.{shard:000}.log");
    }

    private static int FindLatestShard(string date)
    {
        try
        {
            var prefix = $"trace-{date}.";
            var maxShard = 0;
            var found = false;
            foreach (var file in Directory.EnumerateFiles(LogDirectory, $"trace-{date}.*.log", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Length <= prefix.Length || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (int.TryParse(name[prefix.Length..], out var shard))
                {
                    found = true;
                    if (shard > maxShard)
                    {
                        maxShard = shard;
                    }
                }
            }

            return found ? maxShard : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static long SafeFileLength(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void ScheduleRetentionCleanup()
    {
        _ = Task.Run(() =>
        {
            try
            {
                RunRetentionCleanup();
            }
            catch
            {
                // Best-effort retention only.
            }
        });
    }

    private static void RunRetentionCleanup()
    {
        if (!Directory.Exists(LogDirectory))
        {
            return;
        }

        List<FileInfo> logFiles;
        try
        {
            logFiles = new DirectoryInfo(LogDirectory)
                .EnumerateFiles("trace-*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
        }
        catch
        {
            return;
        }

        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(MaxRetentionDays);
        long runningBytes = 0;
        foreach (var file in logFiles)
        {
            // Never delete files from the current session/date.
            var isCurrent = _currentLogPath != null
                && string.Equals(file.FullName, Path.GetFullPath(_currentLogPath), StringComparison.OrdinalIgnoreCase);

            long length;
            DateTime lastWriteUtc;
            try
            {
                length = file.Length;
                lastWriteUtc = file.LastWriteTimeUtc;
            }
            catch
            {
                continue;
            }

            runningBytes += length;
            if (isCurrent)
            {
                continue;
            }

            var tooOld = lastWriteUtc < cutoff;
            var overTotal = runningBytes > MaxTotalLogBytes;
            if (tooOld || overTotal)
            {
                try
                {
                    file.Delete();
                    runningBytes -= length;
                }
                catch
                {
                    // Best-effort retention only.
                }
            }
        }
    }

    private static void MaybePruneSampleKeys()
    {
        if (LastLogTicksByKey.Count <= MaxSampleKeys)
        {
            return;
        }

        if (Interlocked.Exchange(ref _sampleKeyPruneScheduled, 1) == 1)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var expiredBefore = DateTime.UtcNow.Ticks - SampleKeyMaxAge.Ticks;
                foreach (var pair in LastLogTicksByKey)
                {
                    if (pair.Value < expiredBefore)
                    {
                        LastLogTicksByKey.TryRemove(pair.Key, out _);
                    }
                }

                // If still over budget, drop the oldest keys.
                if (LastLogTicksByKey.Count > MaxSampleKeys)
                {
                    foreach (var pair in LastLogTicksByKey.OrderBy(p => p.Value).Take(LastLogTicksByKey.Count - MaxSampleKeys))
                    {
                        LastLogTicksByKey.TryRemove(pair.Key, out _);
                    }
                }
            }
            catch
            {
                // Best-effort prune only.
            }
            finally
            {
                Interlocked.Exchange(ref _sampleKeyPruneScheduled, 0);
            }
        });
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
