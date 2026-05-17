using System.Diagnostics;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.Services;

public sealed class VideoHoverPreviewFrameService
{
    private static readonly TimeSpan FrameGenerationTimeout = TimeSpan.FromSeconds(4);
    private readonly SemaphoreSlim _frameGate = new(1, 1);
    private readonly string? _ffmpegPath;

    public VideoHoverPreviewFrameService(string? ffmpegPath = null)
    {
        _ffmpegPath = ResolveToolPath("ffmpeg.exe", ffmpegPath);
    }

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_ffmpegPath);

    public async Task<byte[]?> GenerateFrameAsync(
        string mediaPath,
        TimeSpan timestamp,
        int frameWidth,
        int frameHeight,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_ffmpegPath)
            || string.IsNullOrWhiteSpace(mediaPath)
            || !File.Exists(mediaPath)
            || frameWidth <= 0
            || frameHeight <= 0)
        {
            AppTraceLogger.LogSampled(
                "VideoHoverPreview",
                "ffmpeg-frame-skipped",
                $"GenerateFrameAsync skipped. FfmpegAvailable={!string.IsNullOrWhiteSpace(_ffmpegPath)}, MediaPathEmpty={string.IsNullOrWhiteSpace(mediaPath)}, FileExists={File.Exists(mediaPath)}, Size={frameWidth}x{frameHeight}.",
                TimeSpan.FromSeconds(2));
            return null;
        }

        await _frameGate.WaitAsync(cancellationToken);
        Process? process = null;
        var startedAt = Stopwatch.GetTimestamp();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(FrameGenerationTimeout);
        var frameCancellationToken = timeoutCts.Token;
        try
        {
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
            startInfo.ArgumentList.Add($"{Math.Max(0, timestamp.TotalSeconds):F3}");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(mediaPath);
            startInfo.ArgumentList.Add("-frames:v");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-vf");
            startInfo.ArgumentList.Add($"scale={frameWidth}:{frameHeight}:force_original_aspect_ratio=increase,crop={frameWidth}:{frameHeight}");
            startInfo.ArgumentList.Add("-q:v");
            startInfo.ArgumentList.Add("4");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("image2pipe");
            startInfo.ArgumentList.Add("-vcodec");
            startInfo.ArgumentList.Add("mjpeg");
            startInfo.ArgumentList.Add("pipe:1");

            process = new Process { StartInfo = startInfo };
            process.Start();

            await using var output = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output, frameCancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(frameCancellationToken);
            await process.WaitForExitAsync(frameCancellationToken);
            await outputTask;
            var error = await errorTask;

            if (process.ExitCode != 0 || output.Length == 0)
            {
                AppTraceLogger.LogSampled(
                    "VideoHoverPreview",
                    "ffmpeg-frame-failed",
                    $"GenerateFrameAsync failed. ExitCode={process.ExitCode}, Bytes={output.Length}, ElapsedMs={Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:0}, Error='{error}'.",
                    TimeSpan.FromSeconds(2));
                return null;
            }

            AppTraceLogger.LogSampled(
                "VideoHoverPreview",
                "ffmpeg-frame-generated",
                $"GenerateFrameAsync completed. Timestamp={timestamp.TotalSeconds:F3}, Size={frameWidth}x{frameHeight}, Bytes={output.Length}, ElapsedMs={Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:0}.",
                TimeSpan.FromSeconds(1));
            return output.ToArray();
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            AppTraceLogger.LogSampled(
                "VideoHoverPreview",
                "ffmpeg-frame-timeout",
                $"GenerateFrameAsync timed out. TimeoutMs={FrameGenerationTimeout.TotalMilliseconds:0}, ElapsedMs={Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:0}, Path='{mediaPath}', Timestamp={timestamp.TotalSeconds:F3}.",
                TimeSpan.FromSeconds(2));
            return null;
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("VideoHoverPreview", $"GenerateFrameAsync failed. Path='{mediaPath}', Timestamp={timestamp.TotalSeconds:F3}.", ex);
            return null;
        }
        finally
        {
            process?.Dispose();
            _frameGate.Release();
        }
    }

    private static void TryKill(Process? process)
    {
        try
        {
            if (process != null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cancellation cleanup only.
        }
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
}
