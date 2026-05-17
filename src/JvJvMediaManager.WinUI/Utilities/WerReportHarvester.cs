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
                var destinationDirectory = Path.Combine(destinationRoot, reportDirectory.Name);
                if (Directory.Exists(destinationDirectory))
                {
                    continue;
                }

                CopyDirectory(reportDirectory.FullName, destinationDirectory);
                WriteMetadataFile(reportDirectory.FullName, destinationDirectory);
                copiedReports.Add(reportDirectory.Name);
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

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relativePath));
        }

        foreach (var filePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, filePath);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            var parentDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }

            File.Copy(filePath, destinationPath, overwrite: false);
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
}
