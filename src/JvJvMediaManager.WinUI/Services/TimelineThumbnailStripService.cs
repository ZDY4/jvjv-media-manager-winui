using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.Services;

public sealed class TimelineThumbnailStripRequest
{
    public string MediaId { get; init; } = string.Empty;
    public string MediaPath { get; init; } = string.Empty;
    public string CacheToken { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }
    public int FrameCount { get; init; }
    public int FrameWidth { get; init; }
    public int FrameHeight { get; init; }
}

public sealed class TimelineThumbnailStripService
{
    private static readonly TimeSpan FrameGenerationTimeout = TimeSpan.FromSeconds(8);

    // WP-06: timeline frame disk cache quota. Configurable defaults.
    private static readonly CacheQuota DiskQuota = new(
        MaxBytes: 4L * 1024 * 1024 * 1024,
        MaxFiles: 100_000,
        MaxAge: TimeSpan.FromDays(90),
        TargetBytesAfterTrim: 3L * 1024 * 1024 * 1024);
    private const int TrimTriggerFileInterval = 500;

    private readonly SemaphoreSlim _frameGate = new(2, 2);
    private readonly ExternalProcessRunner _processRunner = new();
    private readonly string _cacheDir;
    private readonly string? _ffmpegPath;
    private readonly DiskCacheTrimmer _diskTrimmer;
    private int _writesSinceTrim;
    private int _trimScheduled;
    private int _startupTrimDone;

    public TimelineThumbnailStripService(string? cacheDir = null, string? ffmpegPath = null)
    {
        _cacheDir = string.IsNullOrWhiteSpace(cacheDir)
            ? new SettingsService().GetTimelineThumbnailCacheDir()
            : cacheDir;
        _ffmpegPath = ResolveToolPath("ffmpeg.exe", ffmpegPath);
        Directory.CreateDirectory(_cacheDir);
        _diskTrimmer = new DiskCacheTrimmer(_cacheDir, "timeline");
    }

    public void ScheduleStartupQuotaCheck()
    {
        if (Interlocked.Exchange(ref _startupTrimDone, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(6));
                _diskTrimmer.Trim(DiskQuota, "*.jpg");
            }
            catch
            {
                // Best-effort startup trim only.
            }
        });
    }

    private void NotifyDiskCacheWrite()
    {
        if (Interlocked.Increment(ref _writesSinceTrim) < TrimTriggerFileInterval)
        {
            return;
        }

        Interlocked.Exchange(ref _writesSinceTrim, 0);
        if (Interlocked.Exchange(ref _trimScheduled, 1) == 1)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                _diskTrimmer.Trim(DiskQuota, "*.jpg");
            }
            catch
            {
                // Best-effort incremental trim only.
            }
            finally
            {
                Interlocked.Exchange(ref _trimScheduled, 0);
            }
        });
    }

    public async Task<IReadOnlyList<ImageSource?>> GetStripAsync(
        TimelineThumbnailStripRequest request,
        ImageSource? fallbackSource,
        Action<int, ImageSource?>? frameLoaded = null,
        CancellationToken cancellationToken = default)
    {
        var frameCount = Math.Clamp(request.FrameCount, 1, 64);
        if (frameCount <= 0)
        {
            return Array.Empty<ImageSource?>();
        }

        if (request.Duration <= TimeSpan.Zero
            || string.IsNullOrWhiteSpace(request.MediaPath)
            || !File.Exists(request.MediaPath)
            || string.IsNullOrWhiteSpace(_ffmpegPath))
        {
            var fallbackStrip = CreateFallbackStrip(frameCount, fallbackSource);
            for (var index = 0; index < fallbackStrip.Count; index++)
            {
                frameLoaded?.Invoke(index, fallbackStrip[index]);
            }

            return fallbackStrip;
        }

        var result = CreateFallbackStrip(frameCount, fallbackSource).ToArray();
        var cachePrefix = BuildCachePrefix(request, frameCount);
        var frameTasks = Enumerable.Range(0, frameCount)
            .Select(index => LoadFrameAsync(
                request,
                cachePrefix,
                index,
                fallbackSource,
                frameLoaded,
                cancellationToken))
            .ToArray();

        var frames = await Task.WhenAll(frameTasks);
        foreach (var frame in frames)
        {
            result[frame.Index] = frame.Source;
        }

        return result;
    }

    public void ClearCache()
    {
        if (!Directory.Exists(_cacheDir))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_cacheDir, "*", SearchOption.TopDirectoryOnly))
        {
            TryDelete(file);
        }
    }

    public void ClearCacheForMediaIds(IEnumerable<string> mediaIds)
    {
        var ids = mediaIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0 || !Directory.Exists(_cacheDir))
        {
            return;
        }

        foreach (var id in ids)
        {
            var prefix = $"{SanitizeToken(id)}_";
            foreach (var file in Directory.EnumerateFiles(_cacheDir, $"{prefix}*.jpg", SearchOption.TopDirectoryOnly))
            {
                TryDelete(file);
            }
        }
    }

    private async Task GenerateFrameAsync(
        TimelineThumbnailStripRequest request,
        string cachePath,
        TimeSpan timestamp,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_ffmpegPath))
        {
            return;
        }

        await _frameGate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(cachePath))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

            // WP-06: render to a temp file then rename so cancelled/failed ffmpeg
            // runs never leave a half-written frame that reads as a valid image.
            var tempPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";

            var startInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-loglevel");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-nostdin");
            startInfo.ArgumentList.Add("-ss");
            startInfo.ArgumentList.Add($"{timestamp.TotalSeconds:F3}");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(request.MediaPath);
            startInfo.ArgumentList.Add("-frames:v");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-vf");
            startInfo.ArgumentList.Add($"scale={request.FrameWidth}:{request.FrameHeight}:force_original_aspect_ratio=increase,crop={request.FrameWidth}:{request.FrameHeight}");
            startInfo.ArgumentList.Add("-q:v");
            startInfo.ArgumentList.Add("4");
            startInfo.ArgumentList.Add("-y");
            startInfo.ArgumentList.Add(tempPath);

            try
            {
                var result = await _processRunner.RunAsync(
                    startInfo,
                    FrameGenerationTimeout,
                    readStandardOutputAsync: async (reader, token) =>
                    {
                        await reader.BaseStream.CopyToAsync(Stream.Null, token);
                        return string.Empty;
                    },
                    cancellationToken: cancellationToken);

                if (result.ExitCode != 0)
                {
                    TryDelete(tempPath);
                    return;
                }

                try
                {
                    File.Move(tempPath, cachePath, overwrite: true);
                    NotifyDiskCacheWrite();
                }
                catch
                {
                    TryDelete(tempPath);
                    throw;
                }
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }
        finally
        {
            _frameGate.Release();
        }
    }

    private async Task<(int Index, ImageSource? Source)> LoadFrameAsync(
        TimelineThumbnailStripRequest request,
        string cachePrefix,
        int index,
        ImageSource? fallbackSource,
        Action<int, ImageSource?>? frameLoaded,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cachePath = Path.Combine(_cacheDir, $"{cachePrefix}_{index:00}.jpg");
        var source = await TryLoadBitmapAsync(cachePath);
        if (source == null)
        {
            var timestamp = GetFrameTimestamp(request.Duration, Math.Clamp(request.FrameCount, 1, 64), index);
            await GenerateFrameAsync(request, cachePath, timestamp, cancellationToken);
            source = await TryLoadBitmapAsync(cachePath);
        }

        source ??= fallbackSource;
        frameLoaded?.Invoke(index, source);
        return (index, source);
    }

    private static TimeSpan GetFrameTimestamp(TimeSpan duration, int frameCount, int index)
    {
        if (duration <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var ratio = (index + 0.5) / frameCount;
        var seconds = Math.Clamp(duration.TotalSeconds * ratio, 0, Math.Max(0, duration.TotalSeconds - 0.05));
        return TimeSpan.FromSeconds(seconds);
    }

    private static IReadOnlyList<ImageSource?> CreateFallbackStrip(int frameCount, ImageSource? fallbackSource)
    {
        var result = new List<ImageSource?>(frameCount);
        for (var i = 0; i < frameCount; i++)
        {
            result.Add(fallbackSource);
        }

        return result;
    }

    private static async Task<ImageSource?> TryLoadBitmapAsync(string cachePath)
    {
        if (!File.Exists(cachePath))
        {
            return null;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(cachePath);
            using var stream = await file.OpenReadAsync();
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch
        {
            TryDelete(cachePath);
            return null;
        }
    }

    private static string BuildCachePrefix(TimelineThumbnailStripRequest request, int frameCount)
    {
        var input = $"{request.MediaId}|{request.CacheToken}|{request.FrameWidth}|{request.FrameHeight}|{frameCount}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var hash = Convert.ToHexString(hashBytes[..8]).ToLowerInvariant();
        return $"{SanitizeToken(request.MediaId)}_{hash}";
    }

    private static string SanitizeToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "timeline";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }

        return builder.ToString();
    }

    private static string? ResolveToolPath(string fileName, string? explicitPath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            candidates.Add(explicitPath);
        }

        candidates.AddRange(GetDefaultToolCandidates(fileName));

        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> GetDefaultToolCandidates(string fileName)
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "bin", fileName),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName),
            Path.Combine(Environment.CurrentDirectory, "tools", "ffmpeg", "bin", fileName)
        };

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var segment in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            candidates.Add(Path.Combine(segment, fileName));
        }

        return candidates;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

}
