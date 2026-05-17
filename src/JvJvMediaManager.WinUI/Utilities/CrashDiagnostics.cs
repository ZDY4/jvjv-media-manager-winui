using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace JvJvMediaManager.Utilities;

internal static class CrashDiagnostics
{
    public static void WriteCrashArtifacts(string source, Exception? exception)
    {
        try
        {
            AppTraceLogger.FlushNow();
            var timestamp = DateTime.Now;
            var logDirectory = AppDataPaths.GetLogsDirectory();
            var dumpDirectory = AppDataPaths.GetDumpsDirectory();
            Directory.CreateDirectory(logDirectory);
            Directory.CreateDirectory(dumpDirectory);

            var dumpPath = TryWriteMiniDump(source, timestamp, dumpDirectory, out var dumpError);
            var crashLogPath = Path.Combine(logDirectory, $"crash-{timestamp:yyyyMMdd}.log");
            var lines = BuildCrashLogLines(source, exception, dumpPath, dumpError);
            File.AppendAllLines(crashLogPath, lines);
        }
        catch
        {
            // Best-effort crash diagnostics only.
        }
    }

    private static IReadOnlyList<string> BuildCrashLogLines(string source, Exception? exception, string? dumpPath, string? dumpError)
    {
        using var process = Process.GetCurrentProcess();
        var lines = new List<string>
        {
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {source}",
            $"ProcessId: {Environment.ProcessId}",
            $"ProcessName: {process.ProcessName}",
            $"ProcessPath: {Environment.ProcessPath ?? "<unknown>"}",
            $"BaseDirectory: {AppContext.BaseDirectory}",
            $"CurrentDirectory: {Environment.CurrentDirectory}",
            $"StorageRoot: {AppDataPaths.GetStorageRoot()}",
            $"Runtime: {RuntimeInformation.FrameworkDescription}",
            $"OS: {RuntimeInformation.OSDescription}",
            $"ProcessArchitecture: {RuntimeInformation.ProcessArchitecture}",
            $"OSArchitecture: {RuntimeInformation.OSArchitecture}",
            $"Is64BitProcess: {Environment.Is64BitProcess}",
            $"ThreadId: {Environment.CurrentManagedThreadId}",
            $"WorkingSet64: {process.WorkingSet64}",
            $"PrivateMemorySize64: {process.PrivateMemorySize64}",
            $"ThreadCount: {process.Threads.Count}",
            $"DumpPath: {dumpPath ?? "<not-created>"}"
        };

        if (!string.IsNullOrWhiteSpace(dumpError))
        {
            lines.Add($"DumpError: {dumpError}");
        }

        lines.Add(string.Empty);
        lines.Add(exception?.ToString() ?? "No managed exception details were available.");

        var recentTraceLines = AppTraceLogger.GetRecentLines();
        if (recentTraceLines.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("RecentTraceTail:");
            lines.AddRange(recentTraceLines);
        }

        lines.Add(string.Empty);
        return lines;
    }

    private static string? TryWriteMiniDump(string source, DateTime timestamp, string dumpDirectory, out string? dumpError)
    {
        dumpError = null;

        try
        {
            var safeSource = SanitizeFileNameSegment(source);
            var dumpPath = Path.Combine(
                dumpDirectory,
                $"crash-{timestamp:yyyyMMdd-HHmmss-fff}-{safeSource}-{Environment.ProcessId}.dmp");

            using var process = Process.GetCurrentProcess();
            using var stream = new FileStream(dumpPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            var dumpType = MiniDumpType.MiniDumpWithThreadInfo
                | MiniDumpType.MiniDumpWithUnloadedModules
                | MiniDumpType.MiniDumpWithHandleData
                | MiniDumpType.MiniDumpWithDataSegs
                | MiniDumpType.MiniDumpIgnoreInaccessibleMemory;

            if (!MiniDumpWriteDump(
                    process.Handle,
                    process.Id,
                    stream.SafeFileHandle,
                    dumpType,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                dumpError = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return null;
            }

            return dumpPath;
        }
        catch (Exception ex)
        {
            dumpError = ex.ToString();
            return null;
        }
    }

    private static string SanitizeFileNameSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var buffer = value
            .Select(ch => invalidChars.Contains(ch) ? '-' : ch)
            .ToArray();

        return new string(buffer).Replace(' ', '-');
    }

    [Flags]
    private enum MiniDumpType : uint
    {
        MiniDumpWithDataSegs = 0x00000001,
        MiniDumpWithHandleData = 0x00000004,
        MiniDumpWithUnloadedModules = 0x00000020,
        MiniDumpIgnoreInaccessibleMemory = 0x00020000,
        MiniDumpWithThreadInfo = 0x00001000
    }

    [DllImport("Dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess,
        int processId,
        SafeFileHandle hFile,
        MiniDumpType dumpType,
        IntPtr exceptionParam,
        IntPtr userStreamParam,
        IntPtr callbackParam);
}
