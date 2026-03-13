using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace JvJvMediaManager.Services;

public enum VideoClipMode
{
    Keep,
    Delete
}

public sealed class VideoClipSegment
{
    public TimeSpan Start { get; init; }
    public TimeSpan End { get; init; }

    public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;
}

public sealed class VideoClipRequest
{
    public string InputPath { get; init; } = string.Empty;
    public IReadOnlyList<VideoClipSegment> Segments { get; init; } = Array.Empty<VideoClipSegment>();
    public VideoClipMode Mode { get; init; } = VideoClipMode.Keep;
    public TimeSpan? SourceDuration { get; init; }
    public string? OutputDirectory { get; init; }
    public string? OutputPath { get; init; }
}

public sealed class VideoClipExportResult
{
    public string OutputPath { get; init; } = string.Empty;
    public IReadOnlyList<VideoClipSegment> KeptSegments { get; init; } = Array.Empty<VideoClipSegment>();
}

public sealed class VideoProbeInfo
{
    public TimeSpan Duration { get; init; }
    public bool HasAudio { get; init; }
}

public sealed class VideoClipService
{
    private readonly string? _ffmpegPath;
    private readonly string? _ffprobePath;

    public VideoClipService(string? ffmpegPath = null, string? ffprobePath = null)
    {
        _ffmpegPath = ResolveFfmpegPath(ffmpegPath);
        _ffprobePath = ResolveFfprobePath(ffprobePath, _ffmpegPath);
    }

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_ffmpegPath) && !string.IsNullOrWhiteSpace(_ffprobePath);

    public string UnavailableReason => IsAvailable
        ? string.Empty
        : "未检测到 ffmpeg.exe 与 ffprobe.exe，请将它们加入 PATH，或放到 tools\\ffmpeg\\bin\\。";

    public string CreateOutputPath(string inputPath, string? outputDirectory, VideoClipMode mode, IReadOnlyList<VideoClipSegment> segments)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            throw new ArgumentException("输入文件不能为空。", nameof(inputPath));
        }

        var directory = string.IsNullOrWhiteSpace(outputDirectory)
            ? Path.GetDirectoryName(inputPath)
            : outputDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("无法确定输出目录。");
        }

        Directory.CreateDirectory(directory);

        var fileName = Path.GetFileNameWithoutExtension(inputPath);
        var extension = Path.GetExtension(inputPath);
        var segmentSuffix = segments.Count switch
        {
            <= 0 => "segment",
            1 => $"{FormatFileToken(segments[0].Start)}_{FormatFileToken(segments[0].End)}",
            _ => $"{segments.Count}segments"
        };
        var modeToken = mode == VideoClipMode.Keep ? "keep" : "delete";
        var baseName = $"{fileName}.{modeToken}_{segmentSuffix}";
        var outputPath = Path.Combine(directory, $"{baseName}{extension}");
        var suffix = 1;

        while (File.Exists(outputPath))
        {
            outputPath = Path.Combine(directory, $"{baseName}_{suffix}{extension}");
            suffix++;
        }

        return outputPath;
    }

    public async Task<VideoProbeInfo> ProbeAsync(string inputPath, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(_ffprobePath))
        {
            throw new InvalidOperationException(UnavailableReason);
        }

        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        {
            throw new FileNotFoundException("原视频文件不存在。", inputPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffprobePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-show_entries");
        startInfo.ArgumentList.Add("format=duration:stream=codec_type");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add(inputPath);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "ffprobe 读取媒体信息失败。" : stderr.Trim());
        }

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;

        var duration = TimeSpan.Zero;
        if (root.TryGetProperty("format", out var formatElement)
            && formatElement.TryGetProperty("duration", out var durationElement)
            && durationElement.ValueKind == JsonValueKind.String
            && double.TryParse(durationElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0)
        {
            duration = TimeSpan.FromSeconds(seconds);
        }

        var hasAudio = false;
        if (root.TryGetProperty("streams", out var streamsElement) && streamsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streamsElement.EnumerateArray())
            {
                if (stream.TryGetProperty("codec_type", out var codecType)
                    && string.Equals(codecType.GetString(), "audio", StringComparison.OrdinalIgnoreCase))
                {
                    hasAudio = true;
                    break;
                }
            }
        }

        return new VideoProbeInfo
        {
            Duration = duration,
            HasAudio = hasAudio
        };
    }

    public async Task<VideoClipExportResult> ExportClipAsync(
        VideoClipRequest request,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(_ffmpegPath))
        {
            throw new InvalidOperationException(UnavailableReason);
        }

        if (string.IsNullOrWhiteSpace(request.InputPath) || !File.Exists(request.InputPath))
        {
            throw new FileNotFoundException("原视频文件不存在。", request.InputPath);
        }

        if (request.Segments.Count == 0)
        {
            throw new InvalidOperationException("请至少提供一个剪辑片段。");
        }

        var probe = await ProbeAsync(request.InputPath, cancellationToken);
        var sourceDuration = request.SourceDuration.GetValueOrDefault(probe.Duration);
        if (sourceDuration <= TimeSpan.Zero)
        {
            sourceDuration = probe.Duration;
        }

        if (sourceDuration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("无法识别视频总时长，无法执行多段剪辑。");
        }

        var normalized = NormalizeSegments(request.Segments, sourceDuration);
        if (normalized.Count == 0)
        {
            throw new InvalidOperationException("没有可导出的有效片段。");
        }

        var keptSegments = request.Mode == VideoClipMode.Keep
            ? normalized
            : InvertSegments(normalized, sourceDuration);
        if (keptSegments.Count == 0)
        {
            throw new InvalidOperationException("删除模式下没有剩余可导出的内容。");
        }

        var outputPath = string.IsNullOrWhiteSpace(request.OutputPath)
            ? CreateOutputPath(request.InputPath, request.OutputDirectory, request.Mode, normalized)
            : request.OutputPath!;
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var totalOutputDuration = keptSegments.Aggregate(TimeSpan.Zero, (current, segment) => current + segment.Duration);

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        BuildFfmpegArguments(startInfo.ArgumentList, request.InputPath, outputPath, keptSegments, probe.HasAudio);

        using var process = new Process { StartInfo = startInfo };
        var errorOutput = new StringBuilder();

        process.Start();

        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch
            {
                // Ignore process-kill races during cancellation.
            }
        });

        var progressTask = ReadProgressAsync(process.StandardOutput, totalOutputDuration, progress, cancellationToken);
        var errorTask = ReadErrorsAsync(process.StandardError, errorOutput, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(progressTask, errorTask);

            if (process.ExitCode != 0)
            {
                TryDeletePartialOutput(outputPath);
                throw new InvalidOperationException(errorOutput.Length > 0
                    ? errorOutput.ToString().Trim()
                    : "ffmpeg 导出失败。");
            }
        }
        catch
        {
            TryDeletePartialOutput(outputPath);
            throw;
        }

        progress?.Report(1);

        return new VideoClipExportResult
        {
            OutputPath = outputPath,
            KeptSegments = keptSegments
        };
    }

    private static void BuildFfmpegArguments(
        ICollection<string> args,
        string inputPath,
        string outputPath,
        IReadOnlyList<VideoClipSegment> keptSegments,
        bool hasAudio)
    {
        args.Add("-hide_banner");
        args.Add("-loglevel");
        args.Add("error");
        args.Add("-nostdin");
        args.Add("-i");
        args.Add(inputPath);
        args.Add("-filter_complex");
        args.Add(BuildFilterComplex(keptSegments, hasAudio));
        args.Add("-map");
        args.Add("[vout]");
        if (hasAudio)
        {
            args.Add("-map");
            args.Add("[aout]");
        }

        args.Add("-c:v");
        args.Add("libx264");
        args.Add("-preset");
        args.Add("fast");
        args.Add("-crf");
        args.Add("18");
        args.Add("-pix_fmt");
        args.Add("yuv420p");
        if (hasAudio)
        {
            args.Add("-c:a");
            args.Add("aac");
            args.Add("-b:a");
            args.Add("192k");
        }
        else
        {
            args.Add("-an");
        }

        args.Add("-movflags");
        args.Add("+faststart");
        args.Add("-progress");
        args.Add("pipe:1");
        args.Add("-y");
        args.Add(outputPath);
    }

    private static string BuildFilterComplex(IReadOnlyList<VideoClipSegment> keptSegments, bool hasAudio)
    {
        var filter = new StringBuilder();

        for (var i = 0; i < keptSegments.Count; i++)
        {
            var segment = keptSegments[i];
            filter.Append(CultureInfo.InvariantCulture, $"[0:v:0]trim=start={segment.Start.TotalSeconds:F3}:end={segment.End.TotalSeconds:F3},setpts=PTS-STARTPTS[v{i}];");
            if (hasAudio)
            {
                filter.Append(CultureInfo.InvariantCulture, $"[0:a:0]atrim=start={segment.Start.TotalSeconds:F3}:end={segment.End.TotalSeconds:F3},asetpts=PTS-STARTPTS[a{i}];");
            }
        }

        if (keptSegments.Count == 1)
        {
            filter.Append("[v0]null[vout];");
            if (hasAudio)
            {
                filter.Append("[a0]anull[aout]");
            }
            else
            {
                filter.Length--;
            }

            return filter.ToString();
        }

        for (var i = 0; i < keptSegments.Count; i++)
        {
            filter.Append($"[v{i}]");
            if (hasAudio)
            {
                filter.Append($"[a{i}]");
            }
        }

        filter.Append($"concat=n={keptSegments.Count}:v=1:a={(hasAudio ? 1 : 0)}[vout]");
        if (hasAudio)
        {
            filter.Append("[aout]");
        }

        return filter.ToString();
    }

    private static List<VideoClipSegment> NormalizeSegments(IReadOnlyList<VideoClipSegment> segments, TimeSpan duration)
    {
        var normalized = segments
            .Select(segment => new VideoClipSegment
            {
                Start = Clamp(segment.Start, duration),
                End = Clamp(segment.End, duration)
            })
            .Where(segment => segment.End > segment.Start)
            .OrderBy(segment => segment.Start)
            .ToList();
        if (normalized.Count == 0)
        {
            return normalized;
        }

        var merged = new List<VideoClipSegment> { normalized[0] };
        for (var i = 1; i < normalized.Count; i++)
        {
            var current = normalized[i];
            var previous = merged[^1];
            if (current.Start <= previous.End)
            {
                merged[^1] = new VideoClipSegment
                {
                    Start = previous.Start,
                    End = current.End > previous.End ? current.End : previous.End
                };
                continue;
            }

            merged.Add(current);
        }

        return merged;
    }

    private static List<VideoClipSegment> InvertSegments(IReadOnlyList<VideoClipSegment> segments, TimeSpan duration)
    {
        var kept = new List<VideoClipSegment>();
        var current = TimeSpan.Zero;

        foreach (var segment in segments)
        {
            if (segment.Start > current)
            {
                kept.Add(new VideoClipSegment
                {
                    Start = current,
                    End = segment.Start
                });
            }

            if (segment.End > current)
            {
                current = segment.End;
            }
        }

        if (current < duration)
        {
            kept.Add(new VideoClipSegment
            {
                Start = current,
                End = duration
            });
        }

        return kept.Where(segment => segment.End > segment.Start).ToList();
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan duration)
    {
        if (value < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (value > duration)
        {
            return duration;
        }

        return value;
    }

    private static async Task ReadProgressAsync(
        StreamReader reader,
        TimeSpan duration,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("out_time=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line["out_time=".Length..].Trim();
            if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var current))
            {
                continue;
            }

            if (duration <= TimeSpan.Zero)
            {
                progress?.Report(0);
                continue;
            }

            progress?.Report(Math.Clamp(current.TotalSeconds / duration.TotalSeconds, 0, 1));
        }
    }

    private static async Task ReadErrorsAsync(StreamReader reader, StringBuilder buffer, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (buffer.Length > 0)
            {
                buffer.AppendLine();
            }

            buffer.Append(line.Trim());
        }
    }

    private static string? ResolveFfmpegPath(string? explicitPath)
    {
        return ResolveToolPath("ffmpeg.exe", explicitPath);
    }

    private static string? ResolveFfprobePath(string? explicitPath, string? ffmpegPath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            candidates.Add(explicitPath);
        }

        if (!string.IsNullOrWhiteSpace(ffmpegPath))
        {
            candidates.Add(Path.Combine(Path.GetDirectoryName(ffmpegPath)!, "ffprobe.exe"));
        }

        candidates.AddRange(GetDefaultToolCandidates("ffprobe.exe"));

        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);
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

    private static string FormatFileToken(TimeSpan value)
    {
        var safe = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        var hours = (int)safe.TotalHours;
        return $"{hours:00}-{safe.Minutes:00}-{safe.Seconds:00}-{safe.Milliseconds:000}";
    }

    private static void TryDeletePartialOutput(string path)
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
