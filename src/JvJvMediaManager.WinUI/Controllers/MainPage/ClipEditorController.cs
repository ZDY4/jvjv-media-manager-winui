using System.Collections.ObjectModel;
using JvJvMediaManager.Coordinators.MainPage;
using JvJvMediaManager.Models;
using JvJvMediaManager.Services;
using JvJvMediaManager.Services.MainPage;
using JvJvMediaManager.ViewModels;
using JvJvMediaManager.ViewModels.MainPage;
using JvJvMediaManager.Views.MainPageParts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JvJvMediaManager.Controllers.MainPage;

public sealed class ClipEditorController
{
    private readonly LibraryShellViewModel _libraryViewModel;
    private readonly ClipEditorViewModel _viewModel;
    private readonly DialogWorkflowCoordinator _dialogCoordinator;
    private readonly VideoClipService _clipService = new();
    private readonly Button _clipModeToggleButton;
    private readonly Button _setClipStartButton;
    private readonly Button _setClipEndButton;
    private readonly Button _clipPlanButton;
    private readonly Button _clearClipButton;
    private readonly Button _exportClipButton;
    private readonly Func<TimeSpan> _getCurrentPlaybackPosition;
    private readonly Func<TimeSpan> _getCurrentVideoDuration;
    private readonly Action<IEnumerable<string>> _registerOutputPaths;
    private readonly Action _showControls;

    private bool _isClipModeActive;
    private string? _clipMediaId;
    private TimeSpan? _clipStart;
    private TimeSpan? _clipEnd;
    private bool _isExportingClip;
    private string _clipStatusMessage = string.Empty;
    private readonly ObservableCollection<VideoClipSegment> _clipSegments = new();
    private VideoClipMode _clipMode = VideoClipMode.Keep;
    private string? _clipOutputDirectory;

    public ClipEditorController(
        LibraryShellViewModel libraryViewModel,
        ClipEditorViewModel viewModel,
        Button clipModeToggleButton,
        ClipEditorBarView clipBarView,
        DialogWorkflowCoordinator dialogCoordinator,
        Func<TimeSpan> getCurrentPlaybackPosition,
        Func<TimeSpan> getCurrentVideoDuration,
        Action<IEnumerable<string>> registerOutputPaths,
        Action showControls)
    {
        _libraryViewModel = libraryViewModel;
        _viewModel = viewModel;
        _clipModeToggleButton = clipModeToggleButton;
        _setClipStartButton = clipBarView.SetClipStartButton;
        _setClipEndButton = clipBarView.SetClipEndButton;
        _clipPlanButton = clipBarView.ClipPlanButton;
        _clearClipButton = clipBarView.ClearClipButton;
        _exportClipButton = clipBarView.ExportClipButton;
        _dialogCoordinator = dialogCoordinator;
        _getCurrentPlaybackPosition = getCurrentPlaybackPosition;
        _getCurrentVideoDuration = getCurrentVideoDuration;
        _registerOutputPaths = registerOutputPaths;
        _showControls = showControls;
    }

    public bool IsClipModeActive => _isClipModeActive;

    public void Refresh()
    {
        UpdateUi();
    }

    public void HandleMediaChanged(MediaItemViewModel? media)
    {
        if (media?.Type != MediaType.Video)
        {
            _isClipModeActive = false;
            _clipMediaId = null;
            _clipStart = null;
            _clipEnd = null;
            _clipSegments.Clear();
            _clipOutputDirectory = null;
            _clipMode = VideoClipMode.Keep;
            _clipStatusMessage = string.Empty;
            UpdateUi();
            return;
        }

        if (string.Equals(_clipMediaId, media.Id, StringComparison.Ordinal))
        {
            UpdateUi();
            return;
        }

        _clipMediaId = media.Id;
        _isClipModeActive = false;
        _clipStart = TimeSpan.Zero;
        _clipEnd = null;
        _clipSegments.Clear();
        _clipOutputDirectory = Path.GetDirectoryName(media.FileSystemPath);
        _clipMode = VideoClipMode.Keep;
        _clipStatusMessage = _clipService.IsAvailable
            ? "使用 I / O 标记区间，或打开“片段方案...”配置多段剪辑。"
            : _clipService.UnavailableReason;
        UpdateUi();
    }

    public void HandleMediaOpened(TimeSpan duration)
    {
        InitializeClipRange(duration);
        UpdateUi();
    }

    public void ToggleClipMode()
    {
        if (_libraryViewModel.SelectedMedia?.Type != MediaType.Video || _isExportingClip)
        {
            return;
        }

        _isClipModeActive = !_isClipModeActive;
        UpdateUi();
        _showControls();
    }

    public void SetClipStartToCurrent()
    {
        var duration = _getCurrentVideoDuration();
        if (duration <= TimeSpan.Zero)
        {
            _clipStatusMessage = "视频时长尚未就绪，请稍后再试。";
            UpdateUi();
            return;
        }

        var position = ClampToDuration(_getCurrentPlaybackPosition(), duration);
        _clipStart = position;

        if (!_clipEnd.HasValue || _clipEnd.Value <= position)
        {
            _clipEnd = duration;
        }

        _clipStatusMessage = $"入点已设置为 {FormatTime(position)}。";
        UpdateUi();
        _showControls();
    }

    public void SetClipEndToCurrent()
    {
        var duration = _getCurrentVideoDuration();
        if (duration <= TimeSpan.Zero)
        {
            _clipStatusMessage = "视频时长尚未就绪，请稍后再试。";
            UpdateUi();
            return;
        }

        var position = ClampToDuration(_getCurrentPlaybackPosition(), duration);
        _clipEnd = position;

        if (!_clipStart.HasValue || _clipStart.Value >= position)
        {
            _clipStart = TimeSpan.Zero;
        }

        _clipStatusMessage = $"出点已设置为 {FormatTime(position)}。";
        UpdateUi();
        _showControls();
    }

    public void Clear()
    {
        ResetClipRangeToFullDuration();
        _clipSegments.Clear();
        _clipMode = VideoClipMode.Keep;
        _clipStatusMessage = _clipService.IsAvailable
            ? "剪辑区间已重置为整段视频。"
            : _clipService.UnavailableReason;
        UpdateUi();
        _showControls();
    }

    public async Task ExportCurrentClipAsync()
    {
        var media = _libraryViewModel.SelectedMedia;
        if (media?.Type != MediaType.Video)
        {
            return;
        }

        if (!_clipService.IsAvailable)
        {
            _clipStatusMessage = _clipService.UnavailableReason;
            UpdateUi();
            return;
        }

        var segments = GetConfiguredSegments();
        if (segments.Count == 0)
        {
            _clipStatusMessage = "请先设置有效的片段，或打开“片段方案...”配置多段剪辑。";
            UpdateUi();
            return;
        }

        var outputPath = _clipService.CreateOutputPath(media.FileSystemPath, _clipOutputDirectory, _clipMode, segments);
        _isExportingClip = true;
        _clipStatusMessage = $"正在导出到 {Path.GetFileName(outputPath)}";
        _viewModel.ProgressValue = 0;
        UpdateUi();

        try
        {
            var progress = new Progress<double>(value =>
            {
                _viewModel.ProgressValue = value * 100;
                _clipStatusMessage = value >= 1
                    ? $"正在收尾 {Path.GetFileName(outputPath)}"
                    : $"正在导出 {Path.GetFileName(outputPath)} ({value:P0})";
                UpdateUi();
            });

            var result = await _clipService.ExportClipAsync(new VideoClipRequest
            {
                InputPath = media.FileSystemPath,
                Segments = segments,
                Mode = _clipMode,
                SourceDuration = GetBestKnownVideoDuration(media),
                OutputDirectory = _clipOutputDirectory,
                OutputPath = outputPath
            }, progress);

            _clipStatusMessage = $"导出完成：{Path.GetFileName(result.OutputPath)}";
            _viewModel.ProgressValue = 100;

            await _libraryViewModel.AddFilesAsync(new[] { result.OutputPath });
            _registerOutputPaths(new[] { result.OutputPath });
        }
        catch (Exception ex)
        {
            _clipStatusMessage = $"导出失败：{ex.Message}";
        }
        finally
        {
            _isExportingClip = false;
            UpdateUi();
        }
    }

    public async Task ShowClipPlanDialogAsync()
    {
        var media = _libraryViewModel.SelectedMedia;
        if (media?.Type != MediaType.Video)
        {
            return;
        }

        var duration = GetBestKnownVideoDuration(media);
        var result = await _dialogCoordinator.ShowClipPlanDialogAsync(new ClipPlanDialogRequest
        {
            Duration = duration,
            Mode = _clipMode,
            Segments = _clipSegments.ToList(),
            StartText = FormatEditableTime(_clipStart ?? TimeSpan.Zero),
            EndText = FormatEditableTime(_clipEnd ?? duration),
            OutputDirectory = _clipOutputDirectory ?? Path.GetDirectoryName(media.FileSystemPath) ?? string.Empty
        });
        if (result == null)
        {
            return;
        }

        _clipSegments.Clear();
        foreach (var segment in result.Segments)
        {
            _clipSegments.Add(segment);
        }

        _clipMode = result.Mode;
        _clipOutputDirectory = string.IsNullOrWhiteSpace(result.OutputDirectory)
            ? Path.GetDirectoryName(media.FileSystemPath)
            : result.OutputDirectory;
        _clipStatusMessage = result.Segments.Count == 0
            ? "片段方案已清空，导出时将使用当前入点/出点。"
            : $"片段方案已保存，共 {result.Segments.Count} 段。";
        UpdateUi();
        _showControls();
    }

    private void ResetClipRangeToFullDuration()
    {
        _clipStart = TimeSpan.Zero;
        var duration = _getCurrentVideoDuration();
        _clipEnd = duration > TimeSpan.Zero ? duration : null;
    }

    private void InitializeClipRange(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        _clipStart ??= TimeSpan.Zero;
        if (!_clipEnd.HasValue || _clipEnd.Value > duration)
        {
            _clipEnd = duration;
        }
    }

    private void UpdateUi()
    {
        var isVideo = _libraryViewModel.SelectedMedia?.Type == MediaType.Video;
        SetButtonGlyph(_clipModeToggleButton, _isClipModeActive ? "\uE711" : "\uE7C8");
        ToolTipService.SetToolTip(_clipModeToggleButton, _isClipModeActive ? "退出剪辑" : "进入剪辑");
        _clipModeToggleButton.IsEnabled = isVideo && !_isExportingClip;

        var showClipBar = isVideo && _isClipModeActive;
        _viewModel.Visibility = showClipBar ? Visibility.Visible : Visibility.Collapsed;
        if (!showClipBar)
        {
            _viewModel.ProgressVisibility = Visibility.Collapsed;
            _viewModel.StatusText = _clipStatusMessage;
            return;
        }

        var duration = _getCurrentVideoDuration();
        InitializeClipRange(duration);

        var clipStart = _clipStart ?? TimeSpan.Zero;
        var clipEnd = _clipEnd ?? duration;
        var clipLength = clipEnd > clipStart ? clipEnd - clipStart : TimeSpan.Zero;
        var configuredSegments = GetConfiguredSegments();
        var summaryDuration = CalculateEffectiveOutputDuration(configuredSegments, _getCurrentVideoDuration());

        _viewModel.ClipStartText = $"入点：{FormatTime(clipStart)}";
        _viewModel.ClipEndText = clipEnd > TimeSpan.Zero ? $"出点：{FormatTime(clipEnd)}" : "出点：--";
        _viewModel.ClipDurationText = summaryDuration > TimeSpan.Zero
            ? $"时长：{FormatTime(summaryDuration)}"
            : clipLength > TimeSpan.Zero
                ? $"时长：{FormatTime(clipLength)}"
                : "时长：--";
        _viewModel.ClipModeText = $"模式：{(_clipMode == VideoClipMode.Keep ? "保留片段" : "删除片段")}";
        _viewModel.ClipSegmentCountText = $"片段：{configuredSegments.Count}";
        _viewModel.ClipOutputText = $"输出：{(string.IsNullOrWhiteSpace(_clipOutputDirectory) ? "原目录" : Path.GetFileName(_clipOutputDirectory))}";
        _viewModel.ProgressVisibility = _isExportingClip ? Visibility.Visible : Visibility.Collapsed;

        if (!_isExportingClip && string.IsNullOrWhiteSpace(_clipStatusMessage))
        {
            _viewModel.ProgressValue = 0;
        }

        _viewModel.StatusText = _clipStatusMessage;
        _setClipStartButton.IsEnabled = !_isExportingClip && duration > TimeSpan.Zero;
        _setClipEndButton.IsEnabled = !_isExportingClip && duration > TimeSpan.Zero;
        _clipPlanButton.IsEnabled = !_isExportingClip && duration > TimeSpan.Zero;
        _clearClipButton.IsEnabled = !_isExportingClip;
        _exportClipButton.IsEnabled = !_isExportingClip && _clipService.IsAvailable && configuredSegments.Count > 0;
        ToolTipService.SetToolTip(_exportClipButton, _isExportingClip ? "导出中..." : "导出剪辑 (E)");
    }

    private IReadOnlyList<VideoClipSegment> GetConfiguredSegments()
    {
        var duration = GetBestKnownVideoDuration(_libraryViewModel.SelectedMedia);
        if (_clipSegments.Count > 0)
        {
            return NormalizeSegments(_clipSegments, duration);
        }

        if (_clipStart.HasValue && _clipEnd.HasValue && _clipEnd.Value > _clipStart.Value)
        {
            return NormalizeSegments(
                new[]
                {
                    new VideoClipSegment
                    {
                        Start = _clipStart.Value,
                        End = _clipEnd.Value
                    }
                },
                duration);
        }

        return Array.Empty<VideoClipSegment>();
    }

    private TimeSpan GetBestKnownVideoDuration(MediaItemViewModel? media)
    {
        if (media?.Media.Duration is > 0)
        {
            return TimeSpan.FromSeconds(media.Media.Duration.Value);
        }

        return _getCurrentVideoDuration();
    }

    private static List<VideoClipSegment> NormalizeSegments(IEnumerable<VideoClipSegment> segments, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return segments
                .Where(segment => segment.End > segment.Start)
                .OrderBy(segment => segment.Start)
                .ToList();
        }

        var normalized = segments
            .Select(segment => new VideoClipSegment
            {
                Start = segment.Start < TimeSpan.Zero ? TimeSpan.Zero : segment.Start,
                End = segment.End > duration ? duration : segment.End
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

    private static TimeSpan CalculateEffectiveOutputDuration(IReadOnlyList<VideoClipSegment> segments, TimeSpan totalDuration, VideoClipMode? mode = null)
    {
        if (segments.Count == 0)
        {
            return TimeSpan.Zero;
        }

        var selectedMode = mode ?? VideoClipMode.Keep;
        if (selectedMode == VideoClipMode.Keep)
        {
            return segments.Aggregate(TimeSpan.Zero, (current, segment) => current + segment.Duration);
        }

        var removed = segments.Aggregate(TimeSpan.Zero, (current, segment) => current + segment.Duration);
        var remaining = totalDuration - removed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static string FormatEditableTime(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        var hours = (int)value.TotalHours;
        return $"{hours:00}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}";
    }

    private static TimeSpan ClampToDuration(TimeSpan value, TimeSpan duration)
    {
        if (value < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (duration > TimeSpan.Zero && value > duration)
        {
            return duration;
        }

        return value;
    }

    private static string FormatTime(TimeSpan value)
    {
        var totalSeconds = (int)Math.Max(0, value.TotalSeconds);
        var hours = totalSeconds / 3600;
        var minutes = (totalSeconds % 3600) / 60;
        var seconds = totalSeconds % 60;

        return hours > 0
            ? $"{hours}:{minutes:00}:{seconds:00}"
            : $"{minutes}:{seconds:00}";
    }

    private static void SetButtonGlyph(Button button, string glyph)
    {
        if (button.Content is FontIcon icon)
        {
            icon.Glyph = glyph;
            return;
        }

        button.Content = new FontIcon
        {
            Glyph = glyph
        };
    }
}
