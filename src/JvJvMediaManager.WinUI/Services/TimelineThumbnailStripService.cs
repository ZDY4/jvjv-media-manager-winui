using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;

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
    private readonly SemaphoreSlim _frameGate = new(2, 2);
    private readonly string _cacheDir;
    private readonly string? _ffmpegPath;

    public TimelineThumbnailStripService(string? cacheDir = null, string? ffmpegPath = null)
    {
        _cacheDir = string.IsNullOrWhiteSpace(cacheDir)
            ? new SettingsService().GetTimelineThumbnailCacheDir()
            : cacheDir;
        _ffmpegPath = ResolveToolPath("ffmpeg.exe", ffmpegPath);
        Directory.CreateDirectory(_cacheDir);
    }

    public async Task<IReadOnlyList<ImageSource?>> GetStripAsync(
        TimelineThumbnailStripRequest request,
        ImageSource? fallbackSource,
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
            return CreateFallbackStrip(frameCount, fallbackSource);
        }

        var result = new List<ImageSource?>(frameCount);
        var cachePrefix = BuildCachePrefix(request, frameCount);

        for (var index = 0; index < frameCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cachePath = Path.Combine(_cacheDir, $"{cachePrefix}_{index:00}.jpg");
            var source = await TryLoadBitmapAsync(cachePath);
            if (source == null)
            {
                var timestamp = GetFrameTimestamp(request.Duration, frameCount, index);
                await GenerateFrameAsync(request, cachePath, timestamp, cancellationToken);
                source = await TryLoadBitmapAsync(cachePath);
            }

            result.Add(source ?? fallbackSource);
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
            startInfo.ArgumentList.Add(cachePath);

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
            _ = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                TryDelete(cachePath);
            }
        }
        finally
        {
            _frameGate.Release();
        }
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
