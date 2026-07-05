using System.Text;

namespace JvJvMediaManager.Utilities;

internal static class WerReportHarvester
{
    private static readonly string[] WerRoots =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "WER", "ReportArchive"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "WER", "ReportQueue")
    };

    public static void HarvestPreviousCrashReports()
    {
        try
        {
            var destinationRoot = AppDataPaths.GetWerReportsDirectory();
            Directory.CreateDirectory(destinationRoot);

            var copiedReports = new List<string>();
            foreach (var reportDirectory in EnumerateCandidateReportDirectories())
            {
                TryHarvestReportDirectory(reportDirectory, destinationRoot, copiedReports);
            }

            if (copiedReports.Count == 0)
            {
                return;
            }

            AppTraceLogger.Log(
                "WerHarvester",
                $"Harvested {copiedReports.Count} WER report(s): {string.Join(", ", copiedReports)}.");
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("WerHarvester", "HarvestPreviousCrashReports failed.", ex);
        }
    }

    private static IEnumerable<DirectoryInfo> EnumerateCandidateReportDirectories()
    {
        foreach (var root in WerRoots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            IEnumerable<DirectoryInfo> directories;
            try
            {
                directories = new DirectoryInfo(root)
                    .EnumerateDirectories("AppCrash_*JvJvMediaManager*")
                    .OrderByDescending(directory => directory.LastWriteTimeUtc)
                    .Take(8)
                    .ToArray();
            }
            catch (UnauthorizedAccessException ex)
            {
                AppTraceLogger.LogSampled(
                    "WerHarvester",
                    $"enumerate-access-denied:{root}",
                    $"Skipped WER root because access was denied. Root='{root}', HResult=0x{ex.HResult:X8}.",
                    TimeSpan.FromHours(6));
                continue;
            }
            catch
            {
                continue;
            }

            foreach (var directory in directories)
            {
                yield return directory;
            }
        }
    }

    private static void TryHarvestReportDirectory(
        DirectoryInfo reportDirectory,
        string destinationRoot,
        ICollection<string> copiedReports)
    {
        var destinationDirectory = Path.Combine(destinationRoot, reportDirectory.Name);
        if (Directory.Exists(destinationDirectory))
        {
            return;
        }

        var temporaryDirectory = $"{destinationDirectory}.tmp-{Guid.NewGuid():N}";
        try
        {
            CopyDirectory(reportDirectory.FullName, temporaryDirectory);
            WriteMetadataFile(reportDirectory.FullName, temporaryDirectory);
            Directory.Move(temporaryDirectory, destinationDirectory);
            copiedReports.Add(reportDirectory.Name);
        }
        catch (UnauthorizedAccessException ex)
        {
            TryDeleteDirectory(temporaryDirectory);
            AppTraceLogger.LogSampled(
                "WerHarvester",
                $"copy-access-denied:{reportDirectory.FullName}",
                $"Skipped WER report because access was denied. Report='{reportDirectory.FullName}', HResult=0x{ex.HResult:X8}.",
                TimeSpan.FromHours(6));
        }
        catch (IOException ex) when (IsAccessDenied(ex))
        {
            TryDeleteDirectory(temporaryDirectory);
            AppTraceLogger.LogSampled(
                "WerHarvester",
                $"copy-access-denied:{reportDirectory.FullName}",
                $"Skipped WER report because access was denied. Report='{reportDirectory.FullName}', HResult=0x{ex.HResult:X8}.",
                TimeSpan.FromHours(6));
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(temporaryDirectory);
            AppTraceLogger.LogException(
                "WerHarvester",
                $"Failed to harvest WER report. Report='{reportDirectory.FullName}'.",
                ex);
        }
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var entryPath in Directory.EnumerateFileSystemEntries(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, entryPath);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            if (Directory.Exists(entryPath))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            var parentDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }

            File.Copy(entryPath, destinationPath, overwrite: false);
        }
    }

    private static void WriteMetadataFile(string sourceDirectory, string destinationDirectory)
    {
        var metadataPath = Path.Combine(destinationDirectory, "_harvest.txt");
        var builder = new StringBuilder();
        builder.AppendLine($"HarvestedAt: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        builder.AppendLine($"SourceDirectory: {sourceDirectory}");
        builder.AppendLine($"DestinationDirectory: {destinationDirectory}");
        File.WriteAllText(metadataPath, builder.ToString());
    }

    private static bool IsAccessDenied(IOException exception)
    {
        return exception.HResult == unchecked((int)0x80070005);
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Best-effort diagnostic cleanup only.
        }
    }
}
