using System.Diagnostics;

namespace JvJvMediaManager.Utilities;

/// <summary>
/// Quota policy for a disk cache directory. Values should be configurable per
/// version/user rather than treated as permanent product rules.
/// </summary>
public sealed record CacheQuota(
    long MaxBytes,
    int MaxFiles,
    TimeSpan MaxAge,
    long TargetBytesAfterTrim);

public readonly record struct CacheTrimResult(
    int ScannedFiles,
    long ScannedBytes,
    int DeletedFiles,
    long DeletedBytes,
    long ElapsedMilliseconds);

/// <summary>
/// Shared, allocation-light disk cache trimmer. Applies a <see cref="CacheQuota"/>
/// by removing expired files first (age), then evicting least-recently-accessed
/// files until the directory fits under the target size/file budget. Skips
/// in-flight/temp files and never recurses outside the target directory.
/// </summary>
public sealed class DiskCacheTrimmer
{
    private readonly string _cacheDir;
    private readonly string _category;
    private readonly Func<string, bool>? _isProtected;

    public DiskCacheTrimmer(string cacheDir, string category, Func<string, bool>? isProtected = null)
    {
        _cacheDir = cacheDir;
        _category = category;
        _isProtected = isProtected;
    }

    public CacheTrimResult Trim(CacheQuota quota, string searchPattern = "*")
    {
        var stopwatch = Stopwatch.StartNew();
        if (!Directory.Exists(_cacheDir))
        {
            return new CacheTrimResult(0, 0, 0, 0, stopwatch.ElapsedMilliseconds);
        }

        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(_cacheDir)
                .EnumerateFiles(searchPattern, SearchOption.TopDirectoryOnly)
                .ToList();
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogSampled(
                "DiskCacheTrimmer",
                $"enumerate-failed-{_category}",
                $"Cache enumeration failed. Category={_category}, Dir='{_cacheDir}'. ErrorType={ex.GetType().Name}, Message='{ex.Message}'.",
                TimeSpan.FromSeconds(30));
            return new CacheTrimResult(0, 0, 0, 0, stopwatch.ElapsedMilliseconds);
        }

        var now = DateTime.UtcNow;
        long totalBytes = 0;
        var deletedFiles = 0;
        long deletedBytes = 0;
        var scannedFiles = files.Count;

        var candidates = new List<(FileInfo File, long Length, DateTime LastAccessUtc)>(files.Count);
        foreach (var file in files)
        {
            long length;
            DateTime lastAccessUtc;
            try
            {
                length = file.Length;
                lastAccessUtc = file.LastAccessTimeUtc;
            }
            catch
            {
                continue;
            }

            totalBytes += length;

            if (IsSkippable(file.Name))
            {
                continue;
            }

            // Age-based eviction first.
            if (quota.MaxAge > TimeSpan.Zero && now - lastAccessUtc > quota.MaxAge)
            {
                if (TryDelete(file))
                {
                    deletedFiles++;
                    deletedBytes += length;
                    totalBytes -= length;
                    continue;
                }
            }

            candidates.Add((file, length, lastAccessUtc));
        }

        var overSize = quota.MaxBytes > 0 && totalBytes > quota.MaxBytes;
        var remainingFiles = candidates.Count;
        var overFiles = quota.MaxFiles > 0 && remainingFiles > quota.MaxFiles;

        if (overSize || overFiles)
        {
            // Evict least-recently-accessed first.
            candidates.Sort((a, b) => a.LastAccessUtc.CompareTo(b.LastAccessUtc));
            var target = quota.TargetBytesAfterTrim > 0 ? quota.TargetBytesAfterTrim : quota.MaxBytes;

            foreach (var candidate in candidates)
            {
                var underSize = !overSize || totalBytes <= target;
                var underFiles = !overFiles || remainingFiles <= quota.MaxFiles;
                if (underSize && underFiles)
                {
                    break;
                }

                if (TryDelete(candidate.File))
                {
                    deletedFiles++;
                    deletedBytes += candidate.Length;
                    totalBytes -= candidate.Length;
                    remainingFiles--;
                }
            }
        }

        stopwatch.Stop();
        if (deletedFiles > 0)
        {
            AppTraceLogger.Log(
                "DiskCacheTrimmer",
                $"Cache trimmed. Category={_category}, ScannedFiles={scannedFiles}, ScannedBytes={totalBytes + deletedBytes}, DeletedFiles={deletedFiles}, DeletedBytes={deletedBytes}, RemainingBytes={totalBytes}, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
        }

        return new CacheTrimResult(scannedFiles, totalBytes + deletedBytes, deletedFiles, deletedBytes, stopwatch.ElapsedMilliseconds);
    }

    private bool IsSkippable(string fileName)
    {
        if (fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return _isProtected?.Invoke(fileName) ?? false;
    }

    private bool TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
