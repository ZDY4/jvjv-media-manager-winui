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
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace JvJvMediaManager.Controllers.MainPage;

public sealed class ClipEditorController : IDisposable
{
    private const double TimelineTrackHeight = 10;
    private const double TimelineHandleWidth = 16;
    private const double TimelineHandleHeight = 34;
    private const double TimelinePlayheadWidth = 2;
    private const double TimelineThumbnailTop = 6;
    private const double TimelineThumbnailHeight = 42;
    private const double TimelineTrackTop = 60;
    private const double TimelinePlayheadTop = 4;
    private const double TimelineZoomMin = 1;
    private const double TimelineZoomMax = 12;
    private const double TimelineZoomStep = 1.25;
    private const double TimelineThumbnailTileWidth = 96;
    private const int TimelineThumbnailMinCount = 6;
    private const int TimelineThumbnailMaxCount = 48;
    private static readonly TimeSpan MinTimelineSelectionDuration = TimeSpan.FromMilliseconds(100);

    private enum TimelineInteractionMode
    {
        None,
        Seek,
        StartHandle,
        EndHandle,
        Pan
    }

    private readonly record struct SegmentHandleTag(int SegmentIndex, bool IsStart);
    private readonly record struct SegmentBodyTag(int SegmentIndex);

    private readonly LibraryShellViewModel _libraryViewModel;
    private readonly ClipEditorViewModel _viewModel;
    private readonly DialogWorkflowCoordinator _dialogCoordinator;
    private readonly VideoClipService _clipService = new();
    private readonly TimelineThumbnailStripService _timelineThumbnailStripService = new();
    private readonly ClipEditorBarView _clipBarView;
    private readonly Button _clipModeToggleButton;
    private readonly Button _exitClipModeButton;
    private readonly Button _clipPlayPauseButton;
    private readonly Button _splitClipButton;
    private readonly Button _setClipStartButton;
    private readonly Button _setClipEndButton;
    private readonly Button _clipPlanButton;
    private readonly Button _clearClipButton;
    private readonly Button _exportClipButton;
    private readonly Button _timelineZoomOutButton;
    private readonly Button _timelineZoomInButton;
    private readonly Button _timelineZoomResetButton;
    private readonly Func<TimeSpan> _getCurrentPlaybackPosition;
    private readonly Func<TimeSpan> _getCurrentVideoDuration;
    private readonly Action<TimeSpan> _seekPlaybackPosition;
    private readonly Action _togglePlayPause;
    private readonly Func<bool> _isPlaybackPlaying;
    private readonly Action<bool> _setTransportSuppressed;
    private readonly Action<IEnumerable<string>> _registerOutputPaths;
    private readonly Action _showControls;
    private readonly Brush? _keepSegmentBrush;
    private readonly Brush? _deleteSegmentBrush;

    private bool _isClipModeActive;
    private string? _clipMediaId;
    private TimeSpan? _clipStart;
    private TimeSpan? _clipEnd;
    private bool _isExportingClip;
    private string _clipStatusMessage = string.Empty;
    private readonly ObservableCollection<VideoClipSegment> _clipSegments = new();
    private VideoClipMode _clipMode = VideoClipMode.Keep;
    private string? _clipOutputDirectory;
    private TimelineInteractionMode _timelineInteractionMode;
    private UIElement? _timelineCaptureOwner;
    private int _activeSegmentIndex = -1;
    private int _selectedSegmentIndex = -1;
    private bool _timelineInteractionSwitchedFromPlan;
    private double _timelineZoomFactor = TimelineZoomMin;
    private Point _timelinePanStartPoint;
    private double _timelinePanStartHorizontalOffset;
    private CancellationTokenSource? _timelineThumbnailLoadCts;
    private string? _timelineThumbnailRequestKey;
    private string? _timelineThumbnailRenderKey;
    private readonly List<ImageSource?> _timelineThumbnails = new();

    public ClipEditorController(
        LibraryShellViewModel libraryViewModel,
        ClipEditorViewModel viewModel,
        Button clipModeToggleButton,
        ClipEditorBarView clipBarView,
        DialogWorkflowCoordinator dialogCoordinator,
        Func<TimeSpan> getCurrentPlaybackPosition,
        Func<TimeSpan> getCurrentVideoDuration,
        Action<TimeSpan> seekPlaybackPosition,
        Action togglePlayPause,
        Func<bool> isPlaybackPlaying,
        Action<bool> setTransportSuppressed,
        Action<IEnumerable<string>> registerOutputPaths,
        Action showControls)
    {
        _libraryViewModel = libraryViewModel;
        _viewModel = viewModel;
        _clipModeToggleButton = clipModeToggleButton;
        _clipBarView = clipBarView;
        _exitClipModeButton = clipBarView.ExitClipModeButton;
        _clipPlayPauseButton = clipBarView.ClipPlayPauseButton;
        _splitClipButton = clipBarView.SplitClipButton;
        _setClipStartButton = clipBarView.SetClipStartButton;
        _setClipEndButton = clipBarView.SetClipEndButton;
        _clipPlanButton = clipBarView.ClipPlanButton;
        _clearClipButton = clipBarView.ClearClipButton;
        _exportClipButton = clipBarView.ExportClipButton;
        _timelineZoomOutButton = clipBarView.TimelineZoomOutButton;
        _timelineZoomInButton = clipBarView.TimelineZoomInButton;
        _timelineZoomResetButton = clipBarView.TimelineZoomResetButton;
        _dialogCoordinator = dialogCoordinator;
        _getCurrentPlaybackPosition = getCurrentPlaybackPosition;
        _getCurrentVideoDuration = getCurrentVideoDuration;
        _seekPlaybackPosition = seekPlaybackPosition;
        _togglePlayPause = togglePlayPause;
        _isPlaybackPlaying = isPlaybackPlaying;
        _setTransportSuppressed = setTransportSuppressed;
        _registerOutputPaths = registerOutputPaths;
        _showControls = showControls;
        _keepSegmentBrush = ResolveBrush("AccentBrush");
        _deleteSegmentBrush = ResolveBrush("LibrarySelectionStrongBrush") ?? _keepSegmentBrush;

        AttachTimelineEvents();
    }

    public bool IsClipModeActive => _isClipModeActive;

    public void Dispose()
    {
        DetachTimelineEvents();
        EndTimelineInteraction();
        ClearTimelineThumbnailState(clearCanvas: true);
        _setTransportSuppressed(false);
    }

    public void Refresh()
    {
        UpdateUi();
    }

    public void HandleMediaChanged(MediaItemViewModel? media)
    {
        EndTimelineInteraction();
        ClearTimelineThumbnailState(clearCanvas: true);

        if (media?.Type != MediaType.Video)
        {
            _isClipModeActive = false;
            _clipMediaId = null;
            _clipStart = null;
            _clipEnd = null;
            _clipSegments.Clear();
            _selectedSegmentIndex = -1;
            _clipOutputDirectory = null;
            _clipMode = VideoClipMode.Keep;
            _clipStatusMessage = string.Empty;
            _timelineZoomFactor = TimelineZoomMin;
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
        _selectedSegmentIndex = -1;
        _clipOutputDirectory = Path.GetDirectoryName(media.FileSystemPath);
        _clipMode = VideoClipMode.Keep;
        _clipStatusMessage = _clipService.IsAvailable
            ? "拖动片段左右边界微调范围，单击片段选中后可按 Delete 删除，可在游标处切开片段，Ctrl+滚轮缩放，右键按住平移。"
            : _clipService.UnavailableReason;
        _timelineZoomFactor = TimelineZoomMin;
        UpdateUi();
    }

    public void HandleMediaOpened(TimeSpan duration)
    {
        EnsureClipSegments(duration);
        UpdateUi();
    }

    public void ToggleClipMode()
    {
        if (_libraryViewModel.SelectedMedia?.Type != MediaType.Video || _isExportingClip)
        {
            return;
        }

        if (_isClipModeActive)
        {
            EndTimelineInteraction();
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

        var switchedFromPlan = PrepareSingleSegmentEditing(duration);
        var position = ClampToDuration(_getCurrentPlaybackPosition(), duration);
        var minimumGap = GetMinimumSelectionGap(duration);
        var maxStart = _clipEnd.HasValue
            ? Max(TimeSpan.Zero, _clipEnd.Value - minimumGap)
            : Max(TimeSpan.Zero, duration - minimumGap);
        _clipStart = position > maxStart ? maxStart : position;

        if (!_clipEnd.HasValue || _clipEnd.Value <= _clipStart.Value)
        {
            _clipEnd = ClampToDuration(_clipStart.Value + minimumGap, duration);
        }

        var prefix = switchedFromPlan ? "已切换为单段时间线编辑，" : string.Empty;
        _clipStatusMessage = $"{prefix}入点已设置为 {FormatTime(_clipStart.Value)}。";
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

        var switchedFromPlan = PrepareSingleSegmentEditing(duration);
        var position = ClampToDuration(_getCurrentPlaybackPosition(), duration);
        var minimumGap = GetMinimumSelectionGap(duration);
        var minEnd = (_clipStart ?? TimeSpan.Zero) + minimumGap;
        _clipEnd = position < minEnd ? ClampToDuration(minEnd, duration) : position;

        if (!_clipStart.HasValue || _clipStart.Value >= _clipEnd.Value)
        {
            _clipStart = Max(TimeSpan.Zero, _clipEnd.Value - minimumGap);
        }

        var prefix = switchedFromPlan ? "已切换为单段时间线编辑，" : string.Empty;
        _clipStatusMessage = $"{prefix}出点已设置为 {FormatTime(_clipEnd.Value)}。";
        UpdateUi();
        _showControls();
    }

    public void Clear()
    {
        EndTimelineInteraction();
        ResetClipRangeToFullDuration();
        _clipMode = VideoClipMode.Keep;
        _clipStatusMessage = _clipService.IsAvailable
            ? "剪辑区间已重置为整段视频。"
            : _clipService.UnavailableReason;
        UpdateUi();
        _showControls();
    }

    public void SplitSegmentAtCurrentPosition()
    {
        var duration = GetBestKnownVideoDuration(_libraryViewModel.SelectedMedia);
        if (duration <= TimeSpan.Zero || _isExportingClip)
        {
            return;
        }

        var segments = GetEditableSegments(duration);
        if (segments.Count == 0)
        {
            return;
        }

        var gap = GetMinimumSelectionGap(duration);
        var position = ClampToDuration(_getCurrentPlaybackPosition(), duration);
        var targetIndex = segments.FindIndex(segment =>
            position > segment.Start + gap
            && position < segment.End - gap);
        if (targetIndex < 0)
        {
            _clipStatusMessage = "游标需要落在某个片段内部，且离边界稍微远一点，才能切开。";
            UpdateUi();
            return;
        }

        var segment = segments[targetIndex];
        segments[targetIndex] = new VideoClipSegment
        {
            Start = segment.Start,
            End = position
        };
        segments.Insert(targetIndex + 1, new VideoClipSegment
        {
            Start = position,
            End = segment.End
        });

        SetEditableSegments(segments, duration);
        _selectedSegmentIndex = Math.Clamp(targetIndex + 1, 0, segments.Count - 1);
        _clipStatusMessage = $"已在 {FormatTime(position)} 切开当前片段，共 {segments.Count} 段。";
        UpdateUi();
        _showControls();
    }

    public bool DeleteSelectedSegment()
    {
        if (!_isClipModeActive)
        {
            return false;
        }

        if (_isExportingClip)
        {
            _clipStatusMessage = "导出进行中，暂时不能删除片段。";
            UpdateUi();
            return true;
        }

        var duration = GetBestKnownVideoDuration(_libraryViewModel.SelectedMedia);
        if (duration <= TimeSpan.Zero)
        {
            _clipStatusMessage = "视频时长尚未就绪，请稍后再试。";
            UpdateUi();
            return true;
        }

        var segments = GetEditableSegments(duration);
        if (segments.Count == 0)
        {
            _clipStatusMessage = "当前没有可删除的片段。";
            UpdateUi();
            return true;
        }

        var selectedIndex = _selectedSegmentIndex >= 0 && _selectedSegmentIndex < segments.Count
            ? _selectedSegmentIndex
            : FindSegmentIndexContaining(ClampToDuration(_getCurrentPlaybackPosition(), duration), segments);
        if (selectedIndex < 0)
        {
            _clipStatusMessage = "先单击时间线上的片段，再按 Delete 删除。";
            UpdateUi();
            return true;
        }

        var removedSegment = segments[selectedIndex];
        segments.RemoveAt(selectedIndex);
        SetEditableSegments(segments, duration);
        _selectedSegmentIndex = segments.Count == 0 ? -1 : Math.Min(selectedIndex, segments.Count - 1);

        if (segments.Count > 0)
        {
            SeekPlayback(segments[_selectedSegmentIndex].Start);
            _clipStatusMessage = $"已删除片段 {selectedIndex + 1}，剩余 {segments.Count} 段。";
        }
        else
        {
            SeekPlayback(removedSegment.Start);
            _clipStatusMessage = "已删除最后一个片段，当前没有可导出的片段。";
        }

        UpdateUi();
        _showControls();
        return true;
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
            _clipStatusMessage = "请先保留至少一个有效片段。";
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

        var normalized = NormalizeSegments(result.Segments, duration);
        if (normalized.Count > 0)
        {
            _clipStart = normalized[0].Start;
            _clipEnd = normalized[^1].End;
        }
        else
        {
            ResetClipRangeToFullDuration();
        }

        _clipStatusMessage = result.Segments.Count == 0
            ? "片段配置已清空，当前没有可导出的片段。"
            : $"片段配置已保存，共 {result.Segments.Count} 段。";
        UpdateUi();
        _showControls();
    }

    private void AttachTimelineEvents()
    {
        _exitClipModeButton.Click += ExitClipModeButton_Click;
        _clipPlayPauseButton.Click += ClipPlayPauseButton_Click;
        _splitClipButton.Click += SplitClipButton_Click;
        _timelineZoomOutButton.Click += TimelineZoomOutButton_Click;
        _timelineZoomInButton.Click += TimelineZoomInButton_Click;
        _timelineZoomResetButton.Click += TimelineZoomResetButton_Click;
        _clipBarView.TimelineViewport.SizeChanged += TimelineViewport_SizeChanged;
        _clipBarView.TimelineScrollViewer.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(TimelineViewport_PointerWheelChanged), true);
        _clipBarView.TimelineScrubSurface.PointerPressed += TimelineScrubSurface_PointerPressed;
        _clipBarView.TimelineScrubSurface.PointerMoved += TimelineInteraction_PointerMoved;
        _clipBarView.TimelineScrubSurface.PointerReleased += TimelineInteraction_PointerReleased;
        _clipBarView.TimelineScrubSurface.PointerCaptureLost += TimelineInteraction_PointerCaptureLost;
        _clipBarView.TimelineScrubSurface.RightTapped += TimelineScrubSurface_RightTapped;
    }

    private void DetachTimelineEvents()
    {
        _exitClipModeButton.Click -= ExitClipModeButton_Click;
        _clipPlayPauseButton.Click -= ClipPlayPauseButton_Click;
        _splitClipButton.Click -= SplitClipButton_Click;
        _timelineZoomOutButton.Click -= TimelineZoomOutButton_Click;
        _timelineZoomInButton.Click -= TimelineZoomInButton_Click;
        _timelineZoomResetButton.Click -= TimelineZoomResetButton_Click;
        _clipBarView.TimelineViewport.SizeChanged -= TimelineViewport_SizeChanged;
        _clipBarView.TimelineScrollViewer.RemoveHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(TimelineViewport_PointerWheelChanged));
        _clipBarView.TimelineScrubSurface.PointerPressed -= TimelineScrubSurface_PointerPressed;
        _clipBarView.TimelineScrubSurface.PointerMoved -= TimelineInteraction_PointerMoved;
        _clipBarView.TimelineScrubSurface.PointerReleased -= TimelineInteraction_PointerReleased;
        _clipBarView.TimelineScrubSurface.PointerCaptureLost -= TimelineInteraction_PointerCaptureLost;
        _clipBarView.TimelineScrubSurface.RightTapped -= TimelineScrubSurface_RightTapped;
    }

    private void ExitClipModeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleClipMode();
    }

    private void ClipPlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        _togglePlayPause();
        UpdateUi();
        _showControls();
    }

    private void SplitClipButton_Click(object sender, RoutedEventArgs e)
    {
        SplitSegmentAtCurrentPosition();
    }

    private void TimelineZoomOutButton_Click(object sender, RoutedEventArgs e)
    {
        SetTimelineZoom(_timelineZoomFactor / TimelineZoomStep);
    }

    private void TimelineZoomInButton_Click(object sender, RoutedEventArgs e)
    {
        SetTimelineZoom(_timelineZoomFactor * TimelineZoomStep);
    }

    private void TimelineZoomResetButton_Click(object sender, RoutedEventArgs e)
    {
        SetTimelineZoom(TimelineZoomMin);
    }

    private void TimelineViewport_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!_isClipModeActive)
        {
            return;
        }

        var ctrlDown = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (!ctrlDown)
        {
            return;
        }

        var delta = e.GetCurrentPoint(_clipBarView.TimelineViewport).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        SetTimelineZoom(delta > 0 ? _timelineZoomFactor * TimelineZoomStep : _timelineZoomFactor / TimelineZoomStep);
        e.Handled = true;
    }

    private void TimelineViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0)
        {
            return;
        }

        _timelineThumbnailRenderKey = null;
        UpdateUi();
    }

    private void TimelineScrubSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_isClipModeActive || _isExportingClip)
        {
            return;
        }

        var point = e.GetCurrentPoint(_clipBarView.TimelineScrubSurface);
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && point.Properties.IsRightButtonPressed)
        {
            BeginTimelinePan(sender as UIElement, e);
            e.Handled = true;
            return;
        }

        if (!CanInteractWithTimeline(sender as UIElement, e, requireLeftButtonForMouse: true))
        {
            return;
        }

        _selectedSegmentIndex = -1;
        BeginTimelineInteraction(TimelineInteractionMode.Seek, sender as UIElement, e);
        UpdateTimelineInteraction(e.GetCurrentPoint(_clipBarView.TimelineContentCanvas).Position, finalize: false);
        e.Handled = true;
    }

    private void TimelineStartHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!CanInteractWithTimeline(sender as UIElement, e, requireLeftButtonForMouse: true))
        {
            return;
        }

        _timelineInteractionSwitchedFromPlan = PrepareSingleSegmentEditing(_getCurrentVideoDuration());
        BeginTimelineInteraction(TimelineInteractionMode.StartHandle, sender as UIElement, e);
        UpdateTimelineInteraction(e.GetCurrentPoint(_clipBarView.TimelineContentCanvas).Position, finalize: false);
        e.Handled = true;
    }

    private void TimelineEndHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!CanInteractWithTimeline(sender as UIElement, e, requireLeftButtonForMouse: true))
        {
            return;
        }

        _timelineInteractionSwitchedFromPlan = PrepareSingleSegmentEditing(_getCurrentVideoDuration());
        BeginTimelineInteraction(TimelineInteractionMode.EndHandle, sender as UIElement, e);
        UpdateTimelineInteraction(e.GetCurrentPoint(_clipBarView.TimelineContentCanvas).Position, finalize: false);
        e.Handled = true;
    }

    private void SegmentHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element
            || element.Tag is not SegmentHandleTag tag
            || !CanInteractWithTimeline(element, e, requireLeftButtonForMouse: true))
        {
            return;
        }

        _activeSegmentIndex = tag.SegmentIndex;
        _selectedSegmentIndex = tag.SegmentIndex;
        BeginTimelineInteraction(tag.IsStart ? TimelineInteractionMode.StartHandle : TimelineInteractionMode.EndHandle, element, e);
        UpdateTimelineInteraction(e.GetCurrentPoint(_clipBarView.TimelineContentCanvas).Position, finalize: false);
        e.Handled = true;
    }

    private void SegmentBody_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element
            || element.Tag is not SegmentBodyTag tag
            || !CanInteractWithTimeline(element, e, requireLeftButtonForMouse: true))
        {
            return;
        }

        _selectedSegmentIndex = tag.SegmentIndex;
        _clipStatusMessage = $"已选中片段 {tag.SegmentIndex + 1}。拖动左右边界微调，按 Delete 删除。";
        BeginTimelineInteraction(TimelineInteractionMode.Seek, element, e);
        UpdateTimelineInteraction(e.GetCurrentPoint(_clipBarView.TimelineContentCanvas).Position, finalize: false);
        e.Handled = true;
    }

    private void TimelineInteraction_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_timelineInteractionMode == TimelineInteractionMode.None)
        {
            return;
        }

        if (_timelineInteractionMode == TimelineInteractionMode.Pan)
        {
            UpdateTimelinePan(e.GetCurrentPoint(_clipBarView.TimelineViewport).Position);
        }
        else
        {
            UpdateTimelineInteraction(e.GetCurrentPoint(_clipBarView.TimelineContentCanvas).Position, finalize: false);
        }

        e.Handled = true;
    }

    private void TimelineInteraction_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_timelineInteractionMode == TimelineInteractionMode.None)
        {
            return;
        }

        if (_timelineInteractionMode == TimelineInteractionMode.Pan)
        {
            UpdateTimelinePan(e.GetCurrentPoint(_clipBarView.TimelineViewport).Position);
        }
        else
        {
            UpdateTimelineInteraction(e.GetCurrentPoint(_clipBarView.TimelineContentCanvas).Position, finalize: true);
        }

        EndTimelineInteraction();
        e.Handled = true;
    }

    private void TimelineInteraction_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        EndTimelineInteraction();
    }

    private static void TimelineScrubSurface_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
    }

    private bool CanInteractWithTimeline(UIElement? owner, PointerRoutedEventArgs e, bool requireLeftButtonForMouse)
    {
        if (owner == null || !_isClipModeActive || _isExportingClip)
        {
            return false;
        }

        var duration = GetBestKnownVideoDuration(_libraryViewModel.SelectedMedia);
        if (duration <= TimeSpan.Zero)
        {
            return false;
        }

        var point = e.GetCurrentPoint(owner);
        if (requireLeftButtonForMouse
            && e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse
            && !point.Properties.IsLeftButtonPressed)
        {
            return false;
        }

        return true;
    }

    private void BeginTimelinePan(UIElement? owner, PointerRoutedEventArgs e)
    {
        EndTimelineInteraction();
        _timelinePanStartPoint = e.GetCurrentPoint(_clipBarView.TimelineViewport).Position;
        _timelinePanStartHorizontalOffset = _clipBarView.TimelineScrollViewer.HorizontalOffset;
        _timelineInteractionMode = TimelineInteractionMode.Pan;
        _timelineCaptureOwner = owner;
        _timelineCaptureOwner?.CapturePointer(e.Pointer);
        _showControls();
    }

    private void BeginTimelineInteraction(TimelineInteractionMode mode, UIElement? owner, PointerRoutedEventArgs e)
    {
        EndTimelineInteraction();
        _timelineInteractionMode = mode;
        _timelineCaptureOwner = owner;
        _timelineCaptureOwner?.CapturePointer(e.Pointer);
        _showControls();
    }

    private void EndTimelineInteraction()
    {
        if (_timelineCaptureOwner != null)
        {
            _timelineCaptureOwner.ReleasePointerCaptures();
            _timelineCaptureOwner = null;
        }

        _timelineInteractionMode = TimelineInteractionMode.None;
        _activeSegmentIndex = -1;
        _timelineInteractionSwitchedFromPlan = false;
    }

    private void UpdateTimelinePan(Point point)
    {
        var viewportWidth = _clipBarView.TimelineViewport.ActualWidth;
        var contentWidth = ResolveTimelineContentWidth();
        if (viewportWidth <= 0 || contentWidth <= viewportWidth)
        {
            return;
        }

        var delta = point.X - _timelinePanStartPoint.X;
        var maxOffset = Math.Max(0, contentWidth - viewportWidth);
        var nextOffset = Math.Clamp(_timelinePanStartHorizontalOffset - delta, 0, maxOffset);
        _clipBarView.TimelineScrollViewer.ChangeView(nextOffset, null, null, true);
    }

    private void UpdateTimelineInteraction(Point point, bool finalize)
    {
        var duration = GetBestKnownVideoDuration(_libraryViewModel.SelectedMedia);
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        EnsureClipSegments(duration);
        var value = PositionToTimelineTime(point.X, duration);
        switch (_timelineInteractionMode)
        {
            case TimelineInteractionMode.Seek:
                SeekPlayback(value);
                break;

            case TimelineInteractionMode.StartHandle:
            {
                var updatedSegments = GetEditableSegments(duration);
                if (_activeSegmentIndex < 0 || _activeSegmentIndex >= updatedSegments.Count)
                {
                    break;
                }

                var gap = GetMinimumSelectionGap(duration);
                var current = updatedSegments[_activeSegmentIndex];
                var minStart = _activeSegmentIndex == 0
                    ? TimeSpan.Zero
                    : updatedSegments[_activeSegmentIndex - 1].End;
                var maxStart = current.End - gap;
                current = new VideoClipSegment
                {
                    Start = ClampToRange(value, minStart, maxStart),
                    End = current.End
                };
                updatedSegments[_activeSegmentIndex] = current;
                SetEditableSegments(updatedSegments, duration);
                _selectedSegmentIndex = _activeSegmentIndex;

                SeekPlayback(current.Start);
                _clipStatusMessage = finalize
                    ? $"片段 {_activeSegmentIndex + 1} 起点已更新为 {FormatTime(current.Start)}。"
                    : $"片段 {_activeSegmentIndex + 1} 起点：{FormatTime(current.Start)}";
                break;
            }

            case TimelineInteractionMode.EndHandle:
            {
                var updatedSegments = GetEditableSegments(duration);
                if (_activeSegmentIndex < 0 || _activeSegmentIndex >= updatedSegments.Count)
                {
                    break;
                }

                var gap = GetMinimumSelectionGap(duration);
                var current = updatedSegments[_activeSegmentIndex];
                var minEnd = current.Start + gap;
                var maxEnd = _activeSegmentIndex == updatedSegments.Count - 1
                    ? duration
                    : updatedSegments[_activeSegmentIndex + 1].Start;
                current = new VideoClipSegment
                {
                    Start = current.Start,
                    End = ClampToRange(value, minEnd, maxEnd)
                };
                updatedSegments[_activeSegmentIndex] = current;
                SetEditableSegments(updatedSegments, duration);
                _selectedSegmentIndex = _activeSegmentIndex;

                SeekPlayback(current.End);
                _clipStatusMessage = finalize
                    ? $"片段 {_activeSegmentIndex + 1} 终点已更新为 {FormatTime(current.End)}。"
                    : $"片段 {_activeSegmentIndex + 1} 终点：{FormatTime(current.End)}";
                break;
            }
        }

        UpdateUi();
    }

    private void SeekPlayback(TimeSpan value)
    {
        _seekPlaybackPosition(value);
        _showControls();
    }

    private bool PrepareSingleSegmentEditing(TimeSpan duration)
    {
        if (_clipSegments.Count == 0)
        {
            return false;
        }

        var normalized = NormalizeSegments(_clipSegments, duration);
        if (normalized.Count > 0)
        {
            _clipStart = normalized[0].Start;
            _clipEnd = normalized[^1].End;
        }

        _clipSegments.Clear();
        return true;
    }

    private void ResetClipRangeToFullDuration()
    {
        var duration = _getCurrentVideoDuration();
        _clipSegments.Clear();
        if (duration > TimeSpan.Zero)
        {
            _clipSegments.Add(new VideoClipSegment
            {
                Start = TimeSpan.Zero,
                End = duration
            });
        }

        _selectedSegmentIndex = _clipSegments.Count > 0 ? 0 : -1;
        SyncClipBoundsFromSegments(duration);
    }

    private void InitializeClipRange(TimeSpan duration)
    {
        EnsureClipSegments(duration);
    }

    private void SetTimelineZoom(double nextZoomFactor)
    {
        var duration = GetBestKnownVideoDuration(_libraryViewModel.SelectedMedia);
        var currentPosition = duration > TimeSpan.Zero
            ? ClampToDuration(_getCurrentPlaybackPosition(), duration)
            : TimeSpan.Zero;

        _timelineZoomFactor = Math.Clamp(nextZoomFactor, TimelineZoomMin, TimelineZoomMax);
        _timelineThumbnailRenderKey = null;
        UpdateUi();
        ScrollTimelineToTime(currentPosition, centerInViewport: true);
    }

    private void ScrollTimelineToTime(TimeSpan value, bool centerInViewport)
    {
        var duration = GetBestKnownVideoDuration(_libraryViewModel.SelectedMedia);
        var viewportWidth = _clipBarView.TimelineViewport.ActualWidth;
        var contentWidth = ResolveTimelineContentWidth();
        if (duration <= TimeSpan.Zero || viewportWidth <= 0 || contentWidth <= viewportWidth)
        {
            _clipBarView.TimelineScrollViewer.ChangeView(0, null, null, true);
            return;
        }

        var targetX = TimeToTimelineX(value, duration, contentWidth);
        var offset = centerInViewport
            ? targetX - (viewportWidth / 2)
            : targetX - Math.Min(viewportWidth * 0.2, 64);
        var maxOffset = Math.Max(0, contentWidth - viewportWidth);
        _clipBarView.TimelineScrollViewer.ChangeView(Math.Clamp(offset, 0, maxOffset), null, null, true);
    }

    private void UpdateUi()
    {
        var media = _libraryViewModel.SelectedMedia;
        var isVideo = media?.Type == MediaType.Video;
        var showClipBar = isVideo && _isClipModeActive;

        _setTransportSuppressed(showClipBar);
        SetButtonGlyph(_clipModeToggleButton, _isClipModeActive ? "\uE711" : "\uE7C8");
        ToolTipService.SetToolTip(_clipModeToggleButton, _isClipModeActive ? "退出剪辑" : "进入剪辑");
        _clipModeToggleButton.IsEnabled = isVideo && !_isExportingClip;

        _viewModel.Visibility = showClipBar ? Visibility.Visible : Visibility.Collapsed;
        if (!showClipBar)
        {
            _viewModel.ProgressVisibility = Visibility.Collapsed;
            _viewModel.StatusText = _clipStatusMessage;
            ResetTimelineVisuals();
            return;
        }

        var duration = _getCurrentVideoDuration();
        EnsureClipSegments(duration);

        var configuredSegments = GetConfiguredSegments();
        var clipStart = configuredSegments.Count > 0 ? configuredSegments[0].Start : TimeSpan.Zero;
        var clipEnd = configuredSegments.Count > 0 ? configuredSegments[^1].End : duration;
        var clipLength = clipEnd > clipStart ? clipEnd - clipStart : TimeSpan.Zero;
        var currentPosition = ClampToDuration(_getCurrentPlaybackPosition(), duration);
        var summaryDuration = CalculateEffectiveOutputDuration(configuredSegments, duration, _clipMode);

        _viewModel.ClipStartText = $"起点：{FormatTime(clipStart)}";
        _viewModel.ClipEndText = clipEnd > TimeSpan.Zero ? $"终点：{FormatTime(clipEnd)}" : "终点：--";
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
        _exitClipModeButton.IsEnabled = !_isExportingClip;
        _clipPlayPauseButton.IsEnabled = !_isExportingClip && duration > TimeSpan.Zero;
        _splitClipButton.IsEnabled = !_isExportingClip && CanSplitAtCurrentPosition(duration);
        _setClipStartButton.IsEnabled = false;
        _setClipEndButton.IsEnabled = false;
        _clipPlanButton.IsEnabled = false;
        _clearClipButton.IsEnabled = !_isExportingClip;
        _exportClipButton.IsEnabled = !_isExportingClip && _clipService.IsAvailable && configuredSegments.Count > 0;
        _timelineZoomOutButton.IsEnabled = false;
        _timelineZoomInButton.IsEnabled = false;
        _timelineZoomResetButton.IsEnabled = false;

        SetButtonGlyph(_clipPlayPauseButton, _isPlaybackPlaying() ? "\uE769" : "\uE768");
        ToolTipService.SetToolTip(_clipPlayPauseButton, _isPlaybackPlaying() ? "暂停 (Space)" : "播放 (Space)");
        ToolTipService.SetToolTip(_exportClipButton, _isExportingClip ? "导出中..." : "导出剪辑 (E)");
        _clipBarView.TimelineZoomText.Text = $"缩放：{_timelineZoomFactor * 100:0}%";

        UpdateTimelineLabels(duration, currentPosition);
        UpdateTimelineVisuals(duration, currentPosition, configuredSegments);
        EnsureTimelineThumbnails(media, duration);
    }

    private void UpdateTimelineLabels(TimeSpan duration, TimeSpan currentPosition)
    {
        _clipBarView.TimelineCursorText.Text = $"游标：{FormatTime(currentPosition)} / {(duration > TimeSpan.Zero ? FormatTime(duration) : "--")}";
        _clipBarView.TimelineStartLabel.Text = "0:00";
        _clipBarView.TimelineMidLabel.Text = duration > TimeSpan.Zero
            ? FormatTime(TimeSpan.FromSeconds(duration.TotalSeconds / 2))
            : "--";
        _clipBarView.TimelineEndLabel.Text = duration > TimeSpan.Zero ? FormatTime(duration) : "--";
    }

    private void UpdateTimelineVisuals(TimeSpan duration, TimeSpan currentPosition, IReadOnlyList<VideoClipSegment> configuredSegments)
    {
        var viewportWidth = _clipBarView.TimelineViewport.ActualWidth;
        var canvasHeight = _clipBarView.TimelineContentCanvas.Height;
        if (viewportWidth <= 0 || canvasHeight <= 0)
        {
            return;
        }

        var contentWidth = Math.Max(viewportWidth, viewportWidth * _timelineZoomFactor);
        _clipBarView.TimelineContentCanvas.Width = contentWidth;
        _clipBarView.TimelineContentCanvas.Height = canvasHeight;
        _clipBarView.TimelineThumbnailCanvas.Width = contentWidth;
        _clipBarView.TimelineThumbnailCanvas.Height = canvasHeight;
        _clipBarView.TimelineSegmentsCanvas.Width = contentWidth;
        _clipBarView.TimelineSegmentsCanvas.Height = canvasHeight;
        _clipBarView.TimelineScrubSurface.Width = contentWidth;
        _clipBarView.TimelineScrubSurface.Height = canvasHeight;

        var trackLeft = GetTimelineTrackLeft();
        var trackWidth = GetTimelineTrackWidth(contentWidth);
        var playheadHeight = Math.Max(TimelineHandleHeight + 8, canvasHeight - 8);

        Canvas.SetLeft(_clipBarView.TimelineScrubSurface, 0);
        Canvas.SetTop(_clipBarView.TimelineScrubSurface, 0);

        _clipBarView.TimelineTrackBackground.Width = trackWidth;
        Canvas.SetLeft(_clipBarView.TimelineTrackBackground, trackLeft);
        Canvas.SetTop(_clipBarView.TimelineTrackBackground, TimelineTrackTop);

        RenderTimelineThumbnails(contentWidth);

        _clipBarView.TimelineSegmentsCanvas.Children.Clear();
        var canInteract = !_isExportingClip && duration > TimeSpan.Zero;
        for (var i = 0; i < configuredSegments.Count; i++)
        {
            var segment = configuredSegments[i];
            var startX = TimeToTimelineX(segment.Start, duration, contentWidth);
            var endX = TimeToTimelineX(segment.End, duration, contentWidth);
            var width = Math.Max(2, endX - startX);
            var isSelected = i == _selectedSegmentIndex;
            var segmentVisual = new Border
            {
                Width = width,
                Height = TimelineThumbnailHeight + 8,
                BorderThickness = new Thickness(isSelected ? 3 : 2),
                BorderBrush = isSelected
                    ? ResolveBrush("AccentStrongBrush") ?? ResolveBrush("AccentBrush") ?? (_clipMode == VideoClipMode.Keep ? _keepSegmentBrush : _deleteSegmentBrush)
                    : (_clipMode == VideoClipMode.Keep ? _keepSegmentBrush : _deleteSegmentBrush),
                Background = isSelected
                    ? ResolveBrush("AccentBrush") ?? ResolveBrush("LibrarySelectionBrush")
                    : ResolveBrush("LibrarySelectionBrush"),
                CornerRadius = new CornerRadius(10),
                Opacity = isSelected ? 0.9 : (_clipMode == VideoClipMode.Keep ? 0.82 : 0.68),
                IsHitTestVisible = canInteract,
                Tag = new SegmentBodyTag(i)
            };
            segmentVisual.PointerPressed += SegmentBody_PointerPressed;
            segmentVisual.PointerMoved += TimelineInteraction_PointerMoved;
            segmentVisual.PointerReleased += TimelineInteraction_PointerReleased;
            segmentVisual.PointerCaptureLost += TimelineInteraction_PointerCaptureLost;
            Canvas.SetLeft(segmentVisual, startX);
            Canvas.SetTop(segmentVisual, TimelineThumbnailTop - 4);
            _clipBarView.TimelineSegmentsCanvas.Children.Add(segmentVisual);

            var trackVisual = new Border
            {
                Width = width,
                Height = isSelected ? TimelineTrackHeight + 2 : TimelineTrackHeight,
                CornerRadius = new CornerRadius(TimelineTrackHeight / 2),
                Background = isSelected
                    ? ResolveBrush("AccentStrongBrush") ?? ResolveBrush("AccentBrush") ?? (_clipMode == VideoClipMode.Keep ? _keepSegmentBrush : _deleteSegmentBrush)
                    : (_clipMode == VideoClipMode.Keep ? _keepSegmentBrush : _deleteSegmentBrush),
                Opacity = isSelected ? 0.95 : (_clipMode == VideoClipMode.Keep ? 0.82 : 0.62),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(trackVisual, startX);
            Canvas.SetTop(trackVisual, isSelected ? TimelineTrackTop - 1 : TimelineTrackTop);
            _clipBarView.TimelineSegmentsCanvas.Children.Add(trackVisual);

            AddSegmentHandleVisual(i, isStart: true, startX, TimelineThumbnailTop - 2, canInteract);
            AddSegmentHandleVisual(i, isStart: false, endX, TimelineThumbnailTop - 2, canInteract);
        }

        var playheadX = TimeToTimelineX(currentPosition, duration, contentWidth);
        _clipBarView.TimelinePlayhead.Height = playheadHeight;
        Canvas.SetLeft(_clipBarView.TimelinePlayhead, playheadX - (TimelinePlayheadWidth / 2));
        Canvas.SetTop(_clipBarView.TimelinePlayhead, TimelinePlayheadTop);

        _clipBarView.TimelineScrubSurface.IsHitTestVisible = canInteract;
        _clipBarView.TimelineStartHandle.Visibility = Visibility.Collapsed;
        _clipBarView.TimelineEndHandle.Visibility = Visibility.Collapsed;
        _clipBarView.TimelineStartHandle.IsHitTestVisible = false;
        _clipBarView.TimelineEndHandle.IsHitTestVisible = false;
    }

    private void AddSegmentHandleVisual(int segmentIndex, bool isStart, double centerX, double top, bool canInteract)
    {
        var handle = new Border
        {
            Width = 12,
            Height = TimelineThumbnailHeight + 4,
            Background = isStart
                ? ResolveBrush("AccentBrush")
                : ResolveBrush("AccentStrongBrush") ?? ResolveBrush("AccentBrush"),
            BorderBrush = ResolveBrush("AccentForegroundBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Opacity = canInteract ? 0.95 : 0.45,
            IsHitTestVisible = canInteract,
            Tag = new SegmentHandleTag(segmentIndex, isStart)
        };

        handle.PointerPressed += SegmentHandle_PointerPressed;
        handle.PointerMoved += TimelineInteraction_PointerMoved;
        handle.PointerReleased += TimelineInteraction_PointerReleased;
        handle.PointerCaptureLost += TimelineInteraction_PointerCaptureLost;

        Canvas.SetLeft(handle, centerX - (handle.Width / 2));
        Canvas.SetTop(handle, top);
        _clipBarView.TimelineSegmentsCanvas.Children.Add(handle);
    }

    private void RenderTimelineThumbnails(double contentWidth)
    {
        var renderKey = $"{_timelineThumbnailRequestKey}|{_timelineThumbnails.Count}|{Math.Round(contentWidth)}";
        if (string.Equals(_timelineThumbnailRenderKey, renderKey, StringComparison.Ordinal)
            && _clipBarView.TimelineThumbnailCanvas.Children.Count > 0)
        {
            return;
        }

        _timelineThumbnailRenderKey = renderKey;
        _clipBarView.TimelineThumbnailCanvas.Children.Clear();
        if (_timelineThumbnails.Count == 0 || contentWidth <= 0)
        {
            return;
        }

        var thumbnailWidth = Math.Max(1, contentWidth / _timelineThumbnails.Count);
        for (var i = 0; i < _timelineThumbnails.Count; i++)
        {
            var source = _timelineThumbnails[i];
            if (source == null)
            {
                continue;
            }

            var image = new Image
            {
                Width = thumbnailWidth + 1,
                Height = TimelineThumbnailHeight,
                Stretch = Stretch.UniformToFill,
                IsHitTestVisible = false,
                Source = source,
                Opacity = 0.88
            };

            Canvas.SetLeft(image, i * thumbnailWidth);
            Canvas.SetTop(image, TimelineThumbnailTop);
            _clipBarView.TimelineThumbnailCanvas.Children.Add(image);
        }
    }

    private void ResetTimelineVisuals()
    {
        _timelineThumbnailRenderKey = null;
        _clipBarView.TimelineCursorText.Text = "游标：0:00 / 0:00";
        _clipBarView.TimelineStartLabel.Text = "0:00";
        _clipBarView.TimelineMidLabel.Text = "0:00";
        _clipBarView.TimelineEndLabel.Text = "0:00";
        _clipBarView.TimelineZoomText.Text = $"缩放：{_timelineZoomFactor * 100:0}%";
        _clipBarView.TimelineThumbnailCanvas.Children.Clear();
        _clipBarView.TimelineSegmentsCanvas.Children.Clear();
        _clipBarView.TimelinePlayhead.Height = 0;
        _clipBarView.TimelineStartHandle.Visibility = Visibility.Collapsed;
        _clipBarView.TimelineEndHandle.Visibility = Visibility.Collapsed;
    }

    private void EnsureTimelineThumbnails(MediaItemViewModel? media, TimeSpan duration)
    {
        if (!_isClipModeActive || media?.Type != MediaType.Video || duration <= TimeSpan.Zero)
        {
            ClearTimelineThumbnailState(clearCanvas: false);
            return;
        }

        var viewportWidth = _clipBarView.TimelineViewport.ActualWidth;
        if (viewportWidth <= 0)
        {
            return;
        }

        var contentWidth = Math.Max(viewportWidth, viewportWidth * _timelineZoomFactor);
        var frameCount = Math.Clamp((int)Math.Ceiling(contentWidth / TimelineThumbnailTileWidth), TimelineThumbnailMinCount, TimelineThumbnailMaxCount);
        var requestKey = $"{media.Id}|{media.Media.ModifiedAt}|{frameCount}|{(int)Math.Round(_timelineZoomFactor * 100)}|{(int)Math.Round(viewportWidth)}";
        if (string.Equals(_timelineThumbnailRequestKey, requestKey, StringComparison.Ordinal))
        {
            return;
        }

        _timelineThumbnailRequestKey = requestKey;
        _timelineThumbnailRenderKey = null;
        _timelineThumbnailLoadCts?.Cancel();
        _timelineThumbnailLoadCts?.Dispose();
        _timelineThumbnailLoadCts = new CancellationTokenSource();
        var cancellationToken = _timelineThumbnailLoadCts.Token;
        var fallbackSource = media.Thumbnail;

        _ = LoadTimelineThumbnailsAsync(new TimelineThumbnailStripRequest
        {
            MediaId = media.Id,
            MediaPath = media.FileSystemPath,
            CacheToken = media.Media.ModifiedAt.ToString(),
            Duration = duration,
            FrameCount = frameCount,
            FrameWidth = (int)Math.Ceiling(TimelineThumbnailTileWidth),
            FrameHeight = (int)Math.Ceiling(TimelineThumbnailHeight)
        }, requestKey, fallbackSource, cancellationToken);
    }

    private async Task LoadTimelineThumbnailsAsync(
        TimelineThumbnailStripRequest request,
        string requestKey,
        ImageSource? fallbackSource,
        CancellationToken cancellationToken)
    {
        try
        {
            var frames = await _timelineThumbnailStripService.GetStripAsync(request, fallbackSource, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _clipBarView.DispatcherQueue.TryEnqueue(() =>
            {
                if (cancellationToken.IsCancellationRequested
                    || !string.Equals(_timelineThumbnailRequestKey, requestKey, StringComparison.Ordinal))
                {
                    return;
                }

                _timelineThumbnailRenderKey = null;
                _timelineThumbnails.Clear();
                _timelineThumbnails.AddRange(frames);
                RenderTimelineThumbnails(ResolveTimelineContentWidth());
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            _clipBarView.DispatcherQueue.TryEnqueue(() =>
            {
                if (!string.Equals(_timelineThumbnailRequestKey, requestKey, StringComparison.Ordinal))
                {
                    return;
                }

                _timelineThumbnailRenderKey = null;
                _timelineThumbnails.Clear();
                if (fallbackSource != null)
                {
                    for (var i = 0; i < request.FrameCount; i++)
                    {
                        _timelineThumbnails.Add(fallbackSource);
                    }
                }

                RenderTimelineThumbnails(ResolveTimelineContentWidth());
            });
        }
    }

    private void ClearTimelineThumbnailState(bool clearCanvas)
    {
        _timelineThumbnailLoadCts?.Cancel();
        _timelineThumbnailLoadCts?.Dispose();
        _timelineThumbnailLoadCts = null;
        _timelineThumbnailRequestKey = null;
        _timelineThumbnailRenderKey = null;
        _timelineThumbnails.Clear();
        if (clearCanvas)
        {
            _clipBarView.TimelineThumbnailCanvas.Children.Clear();
        }
    }

    private IReadOnlyList<VideoClipSegment> GetConfiguredSegments()
    {
        var duration = GetBestKnownVideoDuration(_libraryViewModel.SelectedMedia);
        if (_clipSegments.Count > 0)
        {
            return NormalizeSegments(_clipSegments, duration);
        }

        if (duration > TimeSpan.Zero)
        {
            return new[]
            {
                new VideoClipSegment
                {
                    Start = TimeSpan.Zero,
                    End = duration
                }
            };
        }

        return Array.Empty<VideoClipSegment>();
    }

    private List<VideoClipSegment> GetEditableSegments(TimeSpan duration)
    {
        EnsureClipSegments(duration);
        return NormalizeSegments(_clipSegments, duration);
    }

    private void EnsureClipSegments(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            _clipStart = null;
            _clipEnd = null;
            return;
        }

        if (_clipSegments.Count == 0)
        {
            _clipSegments.Add(new VideoClipSegment
            {
                Start = TimeSpan.Zero,
                End = duration
            });
        }

        SetEditableSegments(GetEditableSegmentsInternal(duration), duration);
    }

    private List<VideoClipSegment> GetEditableSegmentsInternal(TimeSpan duration)
    {
        return NormalizeSegments(_clipSegments, duration);
    }

    private void SetEditableSegments(IEnumerable<VideoClipSegment> segments, TimeSpan duration)
    {
        var normalized = NormalizeSegments(segments, duration);
        _clipSegments.Clear();
        foreach (var segment in normalized)
        {
            _clipSegments.Add(segment);
        }

        NormalizeSegmentSelection(_clipSegments.Count);
        SyncClipBoundsFromSegments(duration);
    }

    private void NormalizeSegmentSelection(int segmentCount)
    {
        if (segmentCount <= 0)
        {
            _selectedSegmentIndex = -1;
            return;
        }

        if (_selectedSegmentIndex >= segmentCount)
        {
            _selectedSegmentIndex = segmentCount - 1;
        }
    }

    private void SyncClipBoundsFromSegments(TimeSpan duration)
    {
        var normalized = NormalizeSegments(_clipSegments, duration);
        if (normalized.Count == 0)
        {
            _clipStart = null;
            _clipEnd = null;
            return;
        }

        _clipStart = normalized[0].Start;
        _clipEnd = normalized[^1].End;
    }

    private bool CanSplitAtCurrentPosition(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return false;
        }

        var position = ClampToDuration(_getCurrentPlaybackPosition(), duration);
        var gap = GetMinimumSelectionGap(duration);
        return GetConfiguredSegments().Any(segment =>
            position > segment.Start + gap
            && position < segment.End - gap);
    }

    private static int FindSegmentIndexContaining(TimeSpan position, IReadOnlyList<VideoClipSegment> segments)
    {
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (position >= segment.Start && position <= segment.End)
            {
                return i;
            }
        }

        return -1;
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
            if (current.Start < previous.End)
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

    private double PositionToTimelineRatio(double x)
    {
        var width = ResolveTimelineContentWidth();
        var trackLeft = GetTimelineTrackLeft();
        var trackWidth = GetTimelineTrackWidth(width);
        if (trackWidth <= 0)
        {
            return 0;
        }

        return Math.Clamp((x - trackLeft) / trackWidth, 0, 1);
    }

    private TimeSpan PositionToTimelineTime(double x, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds(duration.TotalSeconds * PositionToTimelineRatio(x));
    }

    private double TimeToTimelineX(TimeSpan value, TimeSpan duration, double canvasWidth)
    {
        var trackLeft = GetTimelineTrackLeft();
        var trackWidth = GetTimelineTrackWidth(canvasWidth);
        if (duration <= TimeSpan.Zero || trackWidth <= 0)
        {
            return trackLeft;
        }

        var ratio = Math.Clamp(value.TotalSeconds / duration.TotalSeconds, 0, 1);
        return trackLeft + (trackWidth * ratio);
    }

    private double ResolveTimelineContentWidth()
    {
        var width = _clipBarView.TimelineContentCanvas.Width;
        if (double.IsNaN(width) || width <= 0)
        {
            width = _clipBarView.TimelineContentCanvas.ActualWidth;
        }

        return width;
    }

    private static TimeSpan GetMinimumSelectionGap(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return duration < MinTimelineSelectionDuration ? duration : MinTimelineSelectionDuration;
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

    private static TimeSpan ClampToRange(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
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

    private static Brush? ResolveBrush(string resourceKey)
    {
        return Application.Current.Resources.TryGetValue(resourceKey, out var resource)
            ? resource as Brush
            : null;
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right)
    {
        return left >= right ? left : right;
    }

    private static double GetTimelineTrackLeft()
    {
        return TimelineHandleWidth / 2;
    }

    private static double GetTimelineTrackWidth(double canvasWidth)
    {
        return Math.Max(1, canvasWidth - TimelineHandleWidth);
    }
}
