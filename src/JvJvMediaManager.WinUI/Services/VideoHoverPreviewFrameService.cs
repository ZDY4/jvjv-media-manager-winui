using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Editing;
using Windows.Storage;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.Services;

public sealed class VideoHoverPreviewFrameService
{
    private static readonly TimeSpan FrameGenerationTimeout = TimeSpan.FromSeconds(4);
    private readonly SemaphoreSlim _frameGate;
    private readonly ExternalProcessRunner _processRunner = new();
    private readonly string? _ffmpegPath;

    public VideoHoverPreviewFrameService(string? ffmpegPath = null, int maxConcurrentFrames = 1)
    {
        _ffmpegPath = ResolveToolPath("ffmpeg.exe", ffmpegPath);
        _frameGate = new SemaphoreSlim(Math.Max(1, maxConcurrentFrames), Math.Max(1, maxConcurrentFrames));
    }

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_ffmpegPath);

    public async Task<byte[]?> TranscodeStillImageAsync(
        string mediaPath,
        int maxLongEdge,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mediaPath)
            || !File.Exists(mediaPath)
            || maxLongEdge <= 0
            || string.IsNullOrWhiteSpace(_ffmpegPath))
        {
            return null;
        }

        await _frameGate.WaitAsync(cancellationToken);
        var startedAt = Stopwatch.GetTimestamp();
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
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(mediaPath);
            startInfo.ArgumentList.Add("-frames:v");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-vf");
            startInfo.ArgumentList.Add($"scale={maxLongEdge}:{maxLongEdge}:force_original_aspect_ratio=decrease");
            startInfo.ArgumentList.Add("-q:v");
            startInfo.ArgumentList.Add("3");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("image2pipe");
            startInfo.ArgumentList.Add("-vcodec");
            startInfo.ArgumentList.Add("mjpeg");
            startInfo.ArgumentList.Add("pipe:1");

            await using var output = new MemoryStream();
            var result = await _processRunner.RunAsync(
                startInfo,
                TimeSpan.FromSeconds(8),
                readStandardOutputAsync: async (reader, token) =>
                {
                    await reader.BaseStream.CopyToAsync(output, token);
                    return string.Empty;
                },
                cancellationToken: cancellationToken);

            if (result.ExitCode != 0 || output.Length == 0)
            {
                AppTraceLogger.LogSampled(
                    "VideoHoverPreview",
                    "ffmpeg-still-failed",
                    $"TranscodeStillImageAsync failed. ExitCode={result.ExitCode}, Bytes={output.Length}, ElapsedMs={Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:0}, Error='{result.StandardError}'.",
                    TimeSpan.FromSeconds(2));
                return null;
            }

            AppTraceLogger.LogSampled(
                "VideoHoverPreview",
                "ffmpeg-still-generated",
                $"TranscodeStillImageAsync completed. MaxLongEdge={maxLongEdge}, Bytes={output.Length}, ElapsedMs={Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:0}.",
                TimeSpan.FromSeconds(1));
            return output.ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException(
                "VideoHoverPreview",
                $"TranscodeStillImageAsync failed. Path='{mediaPath}'.",
                ex);
            return null;
        }
        finally
        {
            _frameGate.Release();
        }
    }

    public async Task<byte[]?> GenerateFrameAsync(
        string mediaPath,
        TimeSpan timestamp,
        int frameWidth,
        int frameHeight,
        CancellationToken cancellationToken = default,
        bool cropToFill = true)
    {
        if (string.IsNullOrWhiteSpace(mediaPath)
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

        if (string.IsNullOrWhiteSpace(_ffmpegPath))
        {
            return await GenerateFrameWithWindowsAsync(mediaPath, timestamp, frameWidth, frameHeight, cancellationToken);
        }

        await _frameGate.WaitAsync(cancellationToken);
        var startedAt = Stopwatch.GetTimestamp();
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
            startInfo.ArgumentList.Add(cropToFill
                ? $"scale={frameWidth}:{frameHeight}:force_original_aspect_ratio=increase,crop={frameWidth}:{frameHeight}"
                : $"scale={frameWidth}:{frameHeight}:force_original_aspect_ratio=decrease");
            startInfo.ArgumentList.Add("-q:v");
            startInfo.ArgumentList.Add("4");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("image2pipe");
            startInfo.ArgumentList.Add("-vcodec");
            startInfo.ArgumentList.Add("mjpeg");
            startInfo.ArgumentList.Add("pipe:1");

            await using var output = new MemoryStream();
            var result = await _processRunner.RunAsync(
                startInfo,
                FrameGenerationTimeout,
                readStandardOutputAsync: async (reader, token) =>
                {
                    await reader.BaseStream.CopyToAsync(output, token);
                    return string.Empty;
                },
                cancellationToken: cancellationToken);

            if (result.ExitCode != 0 || output.Length == 0)
            {
                AppTraceLogger.LogSampled(
                    "VideoHoverPreview",
                    "ffmpeg-frame-failed",
                    $"GenerateFrameAsync failed. ExitCode={result.ExitCode}, Bytes={output.Length}, ElapsedMs={Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:0}, Error='{result.StandardError}'.",
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
            _frameGate.Release();
        }
    }

    private async Task<byte[]?> GenerateFrameWithWindowsAsync(
        string mediaPath,
        TimeSpan timestamp,
        int frameWidth,
        int frameHeight,
        CancellationToken cancellationToken)
    {
        await _frameGate.WaitAsync(cancellationToken);
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = await StorageFile.GetFileFromPathAsync(mediaPath);
            var clip = await MediaClip.CreateFromFileAsync(file);
            var composition = new MediaComposition();
            composition.Clips.Add(clip);

            cancellationToken.ThrowIfCancellationRequested();
            using var thumbnail = await composition.GetThumbnailAsync(
                TimeSpan.FromSeconds(Math.Max(0, timestamp.TotalSeconds)),
                frameWidth,
                frameHeight,
                VideoFramePrecision.NearestFrame);

            cancellationToken.ThrowIfCancellationRequested();
            await using var input = thumbnail.AsStreamForRead();
            using var output = new MemoryStream();
            await input.CopyToAsync(output, cancellationToken);
            if (output.Length == 0)
            {
                AppTraceLogger.LogSampled(
                    "VideoHoverPreview",
                    "windows-frame-empty",
                    $"Windows frame extraction returned no frame. Timestamp={timestamp.TotalSeconds:F3}, Size={frameWidth}x{frameHeight}, ElapsedMs={Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:0}.",
                    TimeSpan.FromSeconds(2));
                return null;
            }

            AppTraceLogger.LogSampled(
                "VideoHoverPreview",
                "windows-frame-generated",
                $"Windows frame extraction completed. Timestamp={timestamp.TotalSeconds:F3}, Size={frameWidth}x{frameHeight}, Bytes={output.Length}, ElapsedMs={Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:0}.",
                TimeSpan.FromSeconds(1));
            return output.ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogSampled(
                "VideoHoverPreview",
                "windows-frame-failed",
                $"Windows frame extraction failed. Path='{mediaPath}', Timestamp={timestamp.TotalSeconds:F3}. ErrorType={ex.GetType().Name}, Message='{ex.Message}'.",
                TimeSpan.FromSeconds(2));
            return null;
        }
        finally
        {
            _frameGate.Release();
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
