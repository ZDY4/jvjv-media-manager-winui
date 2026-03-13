using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace JvJvMediaManager.Services;

public sealed class VideoClipRequest
{
    public string InputPath { get; init; } = string.Empty;
    public TimeSpan Start { get; init; }
    public TimeSpan End { get; init; }
    public string? OutputPath { get; init; }
}

public sealed class VideoClipExportResult
{
    public string OutputPath { get; init; } = string.Empty;
}

public sealed class VideoClipService
{
    private readonly string? _ffmpegPath;

    public VideoClipService(string? ffmpegPath = null)
    {
        _ffmpegPath = ResolveFfmpegPath(ffmpegPath);
    }

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_ffmpegPath);

    public string UnavailableReason => IsAvailable
        ? string.Empty
        : "未检测到 ffmpeg.exe，请将 ffmpeg 加入 PATH，或放到 tools\\ffmpeg\\bin\\ffmpeg.exe。";

    public string CreateOutputPath(string inputPath, TimeSpan start, TimeSpan end)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            throw new ArgumentException("输入文件不能为空。", nameof(inputPath));
        }

        var directory = Path.GetDirectoryName(inputPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("无法确定原视频目录。");
        }

        var fileName = Path.GetFileNameWithoutExtension(inputPath);
        var extension = Path.GetExtension(inputPath);
        var baseName = $"{fileName}.clip_{FormatFileToken(start)}_{FormatFileToken(end)}";
        var outputPath = Path.Combine(directory, $"{baseName}{extension}");
        var suffix = 1;

        while (File.Exists(outputPath))
        {
            outputPath = Path.Combine(directory, $"{baseName}_{suffix}{extension}");
            suffix++;
        }

        return outputPath;
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

        if (request.Start < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "剪辑开始时间不能小于 0。");
        }

        if (request.End <= request.Start)
        {
            throw new InvalidOperationException("出点必须晚于入点。");
        }

        var outputPath = string.IsNullOrWhiteSpace(request.OutputPath)
            ? CreateOutputPath(request.InputPath, request.Start, request.End)
            : request.OutputPath;

        var duration = request.End - request.Start;
        var start = FormatArgumentTime(request.Start);
        var clipLength = FormatArgumentTime(duration);

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-ss");
        startInfo.ArgumentList.Add(start);
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(request.InputPath);
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add(clipLength);
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("-map_metadata");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("-map_chapters");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("copy");
        startInfo.ArgumentList.Add("-copyinkf");
        startInfo.ArgumentList.Add("-avoid_negative_ts");
        startInfo.ArgumentList.Add("make_zero");
        startInfo.ArgumentList.Add("-movflags");
        startInfo.ArgumentList.Add("use_metadata_tags");
        startInfo.ArgumentList.Add("-progress");
        startInfo.ArgumentList.Add("pipe:1");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add(outputPath);

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
                // Ignore process-kill races during cancellation
            }
        });

        var progressTask = ReadProgressAsync(process.StandardOutput, duration, progress, cancellationToken);
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
            OutputPath = outputPath
        };
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

            var line = await reader.ReadLineAsync();
            if (line == null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!line.StartsWith("out_time=", StringComparison.OrdinalIgnoreCase))
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

            var ratio = Math.Clamp(current.TotalSeconds / duration.TotalSeconds, 0, 1);
            progress?.Report(ratio);
        }
    }

    private static async Task ReadErrorsAsync(StreamReader reader, StringBuilder buffer, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync();
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
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            candidates.Add(explicitPath);
        }

        var baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, "ffmpeg.exe"));
        candidates.Add(Path.Combine(baseDir, "tools", "ffmpeg", "bin", "ffmpeg.exe"));
        candidates.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe"));
        candidates.Add(Path.Combine(Environment.CurrentDirectory, "tools", "ffmpeg", "bin", "ffmpeg.exe"));

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var segment in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            candidates.Add(Path.Combine(segment, "ffmpeg.exe"));
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);
    }

    private static string FormatArgumentTime(TimeSpan value)
    {
        var safe = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        var hours = (int)safe.TotalHours;
        return $"{hours:00}:{safe.Minutes:00}:{safe.Seconds:00}.{safe.Milliseconds:000}";
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
            // Best-effort cleanup only
        }
    }
}
