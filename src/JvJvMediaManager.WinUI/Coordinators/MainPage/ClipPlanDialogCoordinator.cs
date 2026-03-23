using System.Collections.ObjectModel;
using System.Globalization;
using JvJvMediaManager.Services;
using JvJvMediaManager.Services.MainPage;
using JvJvMediaManager.Utilities;
using JvJvMediaManager.Views.Controls;
using Microsoft.UI.Xaml.Controls;

namespace JvJvMediaManager.Coordinators.MainPage;

public sealed class ClipPlanDialogCoordinator
{
    private readonly IContentDialogService _dialogService;

    public ClipPlanDialogCoordinator(IContentDialogService dialogService)
    {
        _dialogService = dialogService;
    }

    public async Task<ClipPlanResult?> ShowAsync(ClipPlanDialogRequest request)
    {
        if (request.Duration <= TimeSpan.Zero)
        {
            await _dialogService.ShowInfoAsync("提示", "视频时长尚未准备好，请开始播放或稍后再试。");
            return null;
        }

        var workingSegments = new ObservableCollection<ClipSegmentDisplayItem>(
            request.Segments.Select(segment => new ClipSegmentDisplayItem(segment.Start, segment.End)));
        var panel = new ClipPlanPanel(
            request.Duration,
            request.Mode,
            request.StartText,
            request.EndText,
            request.OutputDirectory);
        panel.SegmentsListView.ItemsSource = workingSegments;
        panel.SegmentsListView.DisplayMemberPath = nameof(ClipSegmentDisplayItem.DisplayText);

        void RefreshSummary()
        {
            var segments = NormalizeSegments(workingSegments.Select(item => item.ToSegment()), request.Duration);
            var mode = (VideoClipMode?)((panel.ModeComboBox.SelectedItem as ComboBoxItem)?.Tag ?? VideoClipMode.Keep) ?? VideoClipMode.Keep;
            var outputDuration = CalculateEffectiveOutputDuration(segments, request.Duration, mode);
            panel.SummaryText.Text = segments.Count == 0
                ? $"总时长：{FormatTime(request.Duration)}。请先添加至少一个片段。"
                : $"共 {segments.Count} 段，模式：{(mode == VideoClipMode.Keep ? "保留片段" : "删除片段")}，导出后预计时长：{FormatTime(outputDuration)}";
        }

        panel.UseCurrentButton.Click += (_, _) =>
        {
            panel.StartBox.Text = request.StartText;
            panel.EndBox.Text = request.EndText;
        };

        panel.AddButton.Click += (_, _) =>
        {
            if (!TryParseTimeInput(panel.StartBox.Text, out var start) || !TryParseTimeInput(panel.EndBox.Text, out var end))
            {
                panel.SummaryText.Text = "时间格式无效，请使用 mm:ss、hh:mm:ss 或 hh:mm:ss.fff。";
                return;
            }

            if (end <= start)
            {
                panel.SummaryText.Text = "结束时间必须晚于开始时间。";
                return;
            }

            workingSegments.Add(new ClipSegmentDisplayItem(start, end));
            RefreshSummary();
        };

        panel.RemoveButton.Click += (_, _) =>
        {
            if (panel.SegmentsListView.SelectedItem is ClipSegmentDisplayItem selected)
            {
                workingSegments.Remove(selected);
                RefreshSummary();
            }
        };

        panel.ClearButton.Click += (_, _) =>
        {
            workingSegments.Clear();
            RefreshSummary();
        };

        panel.PickOutputButton.Click += async (_, _) =>
        {
            var window = App.MainWindow;
            if (window == null)
            {
                return;
            }

            var folder = await PickerHelpers.PickFolderAsync(window);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                panel.OutputDirBox.Text = folder;
            }
        };

        panel.ModeComboBox.SelectionChanged += (_, _) => RefreshSummary();
        workingSegments.CollectionChanged += (_, _) => RefreshSummary();
        RefreshSummary();

        var dialog = new ContentDialog
        {
            Title = "片段方案",
            Content = panel,
            PrimaryButtonText = "保存方案",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await _dialogService.ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return null;
        }

        var normalized = NormalizeSegments(workingSegments.Select(item => item.ToSegment()), request.Duration);
        var mode = (VideoClipMode?)((panel.ModeComboBox.SelectedItem as ComboBoxItem)?.Tag ?? VideoClipMode.Keep) ?? VideoClipMode.Keep;
        return new ClipPlanResult
        {
            Segments = normalized,
            Mode = mode,
            OutputDirectory = string.IsNullOrWhiteSpace(panel.OutputDirBox.Text) ? null : panel.OutputDirBox.Text.Trim()
        };
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

    private static TimeSpan CalculateEffectiveOutputDuration(IReadOnlyList<VideoClipSegment> segments, TimeSpan totalDuration, VideoClipMode mode)
    {
        if (segments.Count == 0)
        {
            return TimeSpan.Zero;
        }

        if (mode == VideoClipMode.Keep)
        {
            return segments.Aggregate(TimeSpan.Zero, (current, segment) => current + segment.Duration);
        }

        var removed = segments.Aggregate(TimeSpan.Zero, (current, segment) => current + segment.Duration);
        var remaining = totalDuration - removed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static bool TryParseTimeInput(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
        {
            value = TimeSpan.FromSeconds(seconds);
            return true;
        }

        var formats = new[]
        {
            @"m\:ss",
            @"mm\:ss",
            @"m\:ss\.fff",
            @"mm\:ss\.fff",
            @"h\:mm\:ss",
            @"hh\:mm\:ss",
            @"h\:mm\:ss\.fff",
            @"hh\:mm\:ss\.fff"
        };

        return TimeSpan.TryParseExact(trimmed, formats, CultureInfo.InvariantCulture, out value)
            || TimeSpan.TryParse(trimmed, CultureInfo.InvariantCulture, out value);
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

    private sealed class ClipSegmentDisplayItem
    {
        public ClipSegmentDisplayItem(TimeSpan start, TimeSpan end)
        {
            Start = start;
            End = end;
        }

        public TimeSpan Start { get; }

        public TimeSpan End { get; }

        public string DisplayText => $"{FormatEditableTime(Start)} - {FormatEditableTime(End)}";

        public VideoClipSegment ToSegment()
        {
            return new VideoClipSegment
            {
                Start = Start,
                End = End
            };
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
    }
}
