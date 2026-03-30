using System.Collections.ObjectModel;
using System.Text.Json;
using JvJvMediaManager.Models;
using JvJvMediaManager.Services;
using JvJvMediaManager.Services.MainPage;
using JvJvMediaManager.Utilities;
using JvJvMediaManager.ViewModels;
using JvJvMediaManager.ViewModels.MainPage;
using JvJvMediaManager.Views.MainPageParts;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace JvJvMediaManager.Controllers.MainPage;

public sealed class ClipEditorController : IClipTimelineEditor
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
    private const double TimelineDragActivationDistance = 4;
    private const double TimelineThumbnailTileWidth = 96;
    private const int TimelineThumbnailMinCount = 6;
    private const int TimelineThumbnailMaxCount = 48;
    private static readonly TimeSpan MinTimelineSelectionDuration = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MinTimelineSplitBoundaryGap = TimeSpan.FromMilliseconds(33);

    private enum TimelineInteractionMode
    {
        None,
        Seek,
        StartHandle,
        EndHandle,
        MoveSegment,
        Pan
    }

    private readonly record struct SegmentBodyTag(int SegmentIndex);

    private readonly IClipEditorHost _host;
    private readonly ClipEditorViewModel _viewModel;
    private readonly VideoClipService _clipService = new();
    private readonly TimelineThumbnailStripService _timelineThumbnailStripService = new();
    private readonly IClipTimelineWebBridge _webBridge = new ClipTimelineWebBridge();
    private readonly ClipEditorBarView _clipBarView;
    private readonly Button _clipModeToggleButton;
    private readonly Button _clipModeActionButton;
    private readonly Button _exitClipModeButton;
    private readonly Button _clipPlayPauseButton;
    private readonly Button _splitClipButton;
    private readonly Button _clearClipButton;
    private readonly Button _exportClipButton;
    private readonly Func<TimeSpan> _getCurrentPlaybackPosition;
    private readonly Func<TimeSpan> _getCurrentVideoDuration;
    private readonly Action<TimeSpan> _seekPlaybackPosition;
    private readonly Action _togglePlayPause;
    private readonly Func<bool> _isPlaybackPlaying;
    private readonly Action<string?, string?, double?, double?, Func<TimeSpan, TimeSpan>?> _setPlaybackDisplayOverride;
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
    private bool _isStartHandlePointerOver;
    private bool _isEndHandlePointerOver;
    private bool _isPreviewPlaybackSyncInProgress;
    private TimeSpan _segmentDragPointerOffset;
    private bool _timelineInteractionRequiresDragActivation;
    private bool _timelineInteractionHasActivatedDrag;
    private Point _timelineInteractionStartPoint;
    private double _timelineZoomFactor = TimelineZoomMin;
    private Point _timelinePanStartPoint;
    private double _timelinePanStartHorizontalOffset;
    private CancellationTokenSource? _timelineThumbnailLoadCts;
    private string? _timelineThumbnailRequestKey;
    private string? _timelineThumbnailRenderKey;
    private readonly List<ImageSource?> _timelineThumbnails = new();

    public ClipEditorController(
        IClipEditorHost host,
        ClipEditorViewModel viewModel,
        Button clipModeToggleButton,
        ClipEditorBarView clipBarView)
    {
        _host = host;
        _viewModel = viewModel;
        _clipModeToggleButton = clipModeToggleButton;
        _clipBarView = clipBarView;
        _clipModeActionButton = clipBarView.ClipModeActionButton;
        _exitClipModeButton = clipBarView.ExitClipModeButton;
        _clipPlayPauseButton = clipBarView.ClipPlayPauseButton;
        _splitClipButton = clipBarView.SplitClipButton;
        _clearClipButton = clipBarView.ClearClipButton;
        _exportClipButton = clipBarView.ExportClipButton;
        _getCurrentPlaybackPosition = () => _host.CurrentPlaybackPosition;
        _getCurrentVideoDuration = () => _host.CurrentVideoDuration;
        _seekPlaybackPosition = _host.SeekPlaybackPosition;
        _togglePlayPause = _host.TogglePlayPause;
        _isPlaybackPlaying = () => _host.IsPlaybackPlaying;
        _setPlaybackDisplayOverride = _host.SetPlaybackDisplayOverride;
        _setTransportSuppressed = _host.SetTransportSuppressed;
        _registerOutputPaths = _host.RegisterOutputPaths;
        _showControls = _host.ShowControls;
        _keepSegmentBrush = ResolveBrush("AccentBrush");
        _deleteSegmentBrush = ResolveBrush("LibrarySelectionStrongBrush") ?? _keepSegmentBrush;

        _webBridge.CommandReceived += WebBridge_CommandReceived;
        _webBridge.Attach(_clipBarView.TimelineWebView);
        AttachTimelineEvents();
    }

    public bool IsClipModeActive => _isClipModeActive;

    public void Dispose()
    {
        DetachTimelineEvents();
        _webBridge.CommandReceived -= WebBridge_CommandReceived;
        _webBridge.Dispose();
        EndTimelineInteraction();
        ClearTimelineThumbnailState(clearCanvas: true);
        _setPlaybackDisplayOverride(null, null, null, null, null);
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
        if (_host.SelectedMedia?.Type != MediaType.Video || _isExportingClip)
        {
            return;
        }

        if (_isClipModeActive)
        {
            EndTimelineInteraction();
        }

        _isClipModeActive = !_isClipModeActive;
        if (_isClipModeActive)
        {
            PausePlaybackForClipInteraction("Enter clip mode");
        }

        LogClipTrace($"ToggleClipMode active={_isClipModeActive} media={_host.SelectedMedia?.FileName ?? "<none>"} segments={DescribeSegments(_clipSegments)}");
        UpdateUi();
        _showControls();
    }

    public void HandlePreviewSurfaceInteraction()
    {
        if (!_isClipModeActive)
        {
            return;
        }

        PausePlaybackForClipInteraction("Preview surface interaction");
        UpdateUi();
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
        var duration = GetBestKnownVideoDuration(_host.SelectedMedia);
        if (duration <= TimeSpan.Zero || _isExportingClip)
        {
            return;
        }

        if (!TryResolveSplitTarget(duration, out var segments, out var targetIndex, out var position, out var failureReason))
        {
            _clipStatusMessage = failureReason;
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
        LogClipTrace($"SplitSegmentAtCurrentPosition success position={position.TotalSeconds:F3}s targetIndex={targetIndex} segments={DescribeSegments(segments)}");
        UpdateUi();
        _showControls();
    }

    private void ToggleClipOutputMode()
    {
        if (!_isClipModeActive || _isExportingClip)
        {
            return;
        }

        _clipMode = _clipMode == VideoClipMode.Keep
            ? VideoClipMode.Delete
            : VideoClipMode.Keep;
        _clipStatusMessage = _clipMode == VideoClipMode.Keep
            ? "当前为保留片段模式，亮色片段会输出。"
            : "当前为删除片段模式，淡色预览区会被删除。";
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

        var duration = GetBestKnownVideoDuration(_host.SelectedMedia);
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
        var media = _host.SelectedMedia;
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

            await _host.AddOutputFilesAsync(new[] { result.OutputPath });
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

    private void AttachTimelineEvents()
    {
        _clipModeActionButton.Click += ClipModeActionButton_Click;
        _exitClipModeButton.Click += ExitClipModeButton_Click;
        _clipPlayPauseButton.Click += ClipPlayPauseButton_Click;
        _splitClipButton.Click += SplitClipButton_Click;
        _clipBarView.TimelineViewport.SizeChanged += TimelineViewport_SizeChanged;
        _clipBarView.TimelineScrollViewer.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(TimelineViewport_PointerWheelChanged), true);
        _clipBarView.TimelineContentCanvas.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(TimelineContentCanvas_PointerPressed), true);
        _clipBarView.TimelineContentCanvas.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(TimelineInteraction_PointerMoved), true);
        _clipBarView.TimelineContentCanvas.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(TimelineInteraction_PointerReleased), true);
        _clipBarView.TimelineContentCanvas.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(TimelineInteraction_PointerCaptureLost), true);
        _clipBarView.TimelineContentCanvas.AddHandler(UIElement.RightTappedEvent, new RightTappedEventHandler(TimelineContentCanvas_RightTapped), true);
        _clipBarView.TimelineStartHandleThumb.DragStarted += TimelineStartHandleThumb_DragStarted;
        _clipBarView.TimelineStartHandleThumb.DragDelta += TimelineStartHandleThumb_DragDelta;
        _clipBarView.TimelineStartHandleThumb.DragCompleted += TimelineStartHandleThumb_DragCompleted;
        _clipBarView.TimelineStartHandleThumb.PointerEntered += TimelineStartHandle_PointerEntered;
        _clipBarView.TimelineStartHandleThumb.PointerExited += TimelineStartHandle_PointerExited;
        _clipBarView.TimelineEndHandleThumb.DragStarted += TimelineEndHandleThumb_DragStarted;
        _clipBarView.TimelineEndHandleThumb.DragDelta += TimelineEndHandleThumb_DragDelta;
        _clipBarView.TimelineEndHandleThumb.DragCompleted += TimelineEndHandleThumb_DragCompleted;
        _clipBarView.TimelineEndHandleThumb.PointerEntered += TimelineEndHandle_PointerEntered;
        _clipBarView.TimelineEndHandleThumb.PointerExited += TimelineEndHandle_PointerExited;
    }

    private void DetachTimelineEvents()
    {
        _clipModeActionButton.Click -= ClipModeActionButton_Click;
        _exitClipModeButton.Click -= ExitClipModeButton_Click;
        _clipPlayPauseButton.Click -= ClipPlayPauseButton_Click;
        _splitClipButton.Click -= SplitClipButton_Click;
        _clipBarView.TimelineViewport.SizeChanged -= TimelineViewport_SizeChanged;
        _clipBarView.TimelineScrollViewer.RemoveHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(TimelineViewport_PointerWheelChanged));
        _clipBarView.TimelineContentCanvas.RemoveHandler(UIElement.PointerPressedEvent, new PointerEventHandler(TimelineContentCanvas_PointerPressed));
        _clipBarView.TimelineContentCanvas.RemoveHandler(UIElement.PointerMovedEvent, new PointerEventHandler(TimelineInteraction_PointerMoved));
        _clipBarView.TimelineContentCanvas.RemoveHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(TimelineInteraction_PointerReleased));
        _clipBarView.TimelineContentCanvas.RemoveHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(TimelineInteraction_PointerCaptureLost));
        _clipBarView.TimelineContentCanvas.RemoveHandler(UIElement.RightTappedEvent, new RightTappedEventHandler(TimelineContentCanvas_RightTapped));
        _clipBarView.TimelineStartHandleThumb.DragStarted -= TimelineStartHandleThumb_DragStarted;
        _clipBarView.TimelineStartHandleThumb.DragDelta -= TimelineStartHandleThumb_DragDelta;
        _clipBarView.TimelineStartHandleThumb.DragCompleted -= TimelineStartHandleThumb_DragCompleted;
        _clipBarView.TimelineStartHandleThumb.PointerEntered -= TimelineStartHandle_PointerEntered;
        _clipBarView.TimelineStartHandleThumb.PointerExited -= TimelineStartHandle_PointerExited;
        _clipBarView.TimelineEndHandleThumb.DragStarted -= TimelineEndHandleThumb_DragStarted;
        _clipBarView.TimelineEndHandleThumb.DragDelta -= TimelineEndHandleThumb_DragDelta;
        _clipBarView.TimelineEndHandleThumb.DragCompleted -= TimelineEndHandleThumb_DragCompleted;
        _clipBarView.TimelineEndHandleThumb.PointerEntered -= TimelineEndHandle_PointerEntered;
        _clipBarView.TimelineEndHandleThumb.PointerExited -= TimelineEndHandle_PointerExited;
    }

    private void ExitClipModeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleClipMode();
    }

    private void ClipModeActionButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleClipOutputMode();
    }

    private void ClipPlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isPlaybackPlaying())
        {
            MovePlaybackToPreviewStartIfNeeded();
        }

        _togglePlayPause();
        UpdateUi();
        _showControls();
    }

    private void SplitClipButton_Click(object sender, RoutedEventArgs e)
    {
        SplitSegmentAtCurrentPosition();
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

    private void TimelineContentCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        if (!_isClipModeActive || _isExportingClip)
        {
            return;
        }

        var owner = _clipBarView.TimelineContentCanvas;
        var originalSource = e.OriginalSource as DependencyObject;
        if (IsWithinTimelineHandleThumb(originalSource))
        {
            return;
        }

        var point = e.GetCurrentPoint(owner);
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && point.Properties.IsRightButtonPressed)
        {
            BeginTimelinePan(owner, e);
            e.Handled = true;
            return;
        }

        if (TryGetTimelineTag<SegmentBodyTag>(e.OriginalSource as DependencyObject, out _, out var bodyTag))
        {
            if (!CanInteractWithTimeline(owner, e, requireLeftButtonForMouse: true))
            {
                return;
            }

            BeginSegmentBodyInteraction(bodyTag.SegmentIndex, owner, e);
            e.Handled = true;
            return;
        }

        if (!CanInteractWithTimeline(owner, e, requireLeftButtonForMouse: true))
        {
            return;
        }

        _selectedSegmentIndex = -1;
        BeginTimelineInteraction(TimelineInteractionMode.Seek, owner, e);
        UpdateTimelineInteraction(e.GetCurrentPoint(owner).Position, finalize: false);
        e.Handled = true;
    }

    private void SegmentBody_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border segmentBody
            || segmentBody.Tag is not SegmentBodyTag tag
            || !CanInteractWithTimeline(segmentBody, e, requireLeftButtonForMouse: true))
        {
            return;
        }

        BeginSegmentBodyInteraction(tag.SegmentIndex, _clipBarView.TimelineContentCanvas, e);
        e.Handled = true;
    }

    private void BeginSegmentBodyInteraction(int segmentIndex, UIElement owner, PointerRoutedEventArgs e)
    {
        var duration = GetBestKnownVideoDuration(_host.SelectedMedia);
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        var segments = GetEditableSegments(duration);
        if (segmentIndex < 0 || segmentIndex >= segments.Count)
        {
            return;
        }

        var point = e.GetCurrentPoint(_clipBarView.TimelineContentCanvas).Position;
        var pointerTime = PositionToTimelineTime(point.X, duration);
        var segment = segments[segmentIndex];
        var gap = GetMinimumSelectionGap(duration);
        var leftSlack = segmentIndex == 0
            ? segment.Start
            : segment.Start - segments[segmentIndex - 1].End;
        var rightSlack = segmentIndex == segments.Count - 1
            ? duration - segment.End
            : segments[segmentIndex + 1].Start - segment.End;
        var canMoveSegment = leftSlack > gap && rightSlack > gap;
        var innerMoveZone = TimeSpan.FromTicks(Math.Max(gap.Ticks, segment.Duration.Ticks / 4));
        var distanceToStart = pointerTime > segment.Start ? pointerTime - segment.Start : TimeSpan.Zero;
        var distanceToEnd = segment.End > pointerTime ? segment.End - pointerTime : TimeSpan.Zero;

        var mode = canMoveSegment
            && distanceToStart >= innerMoveZone
            && distanceToEnd >= innerMoveZone
                ? TimelineInteractionMode.MoveSegment
                : (distanceToStart <= distanceToEnd
                    ? TimelineInteractionMode.StartHandle
                    : TimelineInteractionMode.EndHandle);

        BeginTimelineInteraction(mode, owner, e);
        _activeSegmentIndex = segmentIndex;
        _selectedSegmentIndex = segmentIndex;
        _segmentDragPointerOffset = ClampToRange(pointerTime - segment.Start, TimeSpan.Zero, segment.Duration);
        _timelineInteractionRequiresDragActivation = true;
        _timelineInteractionHasActivatedDrag = false;
        _timelineInteractionStartPoint = point;
        _clipStatusMessage = mode == TimelineInteractionMode.MoveSegment
            ? $"已选中片段 {segmentIndex + 1}。拖动片段整体移动，拖动左右边界微调，按 Delete 删除。"
            : $"已选中片段 {segmentIndex + 1}。当前拖动会调整{(mode == TimelineInteractionMode.StartHandle ? "左" : "右")}边界。";
        LogClipTrace($"SegmentBody Begin mode={mode} activeIndex={segmentIndex} pointer={pointerTime.TotalSeconds:F3}s offset={_segmentDragPointerOffset.TotalSeconds:F3}s leftSlack={leftSlack.TotalSeconds:F3}s rightSlack={rightSlack.TotalSeconds:F3}s segment={segment.Start.TotalSeconds:F3}-{segment.End.TotalSeconds:F3}s allSegments={DescribeSegments(segments)}");
        UpdateUi();
    }

    private void TimelineStartHandleThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (!_isClipModeActive || _isExportingClip)
        {
            return;
        }

        var duration = GetBestKnownVideoDuration(_host.SelectedMedia);
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        var segments = GetEditableSegments(duration);
        _activeSegmentIndex = ResolveHandleSegmentIndex(isStartHandle: true, segments.Count);
        _selectedSegmentIndex = _activeSegmentIndex;
        _timelineInteractionMode = TimelineInteractionMode.StartHandle;
        _timelineInteractionRequiresDragActivation = false;
        _timelineInteractionHasActivatedDrag = false;
        PausePlaybackForClipInteraction("Start handle drag started");
        LogClipTrace($"StartHandle DragStarted activeIndex={_activeSegmentIndex} duration={duration.TotalSeconds:F3}s segments={DescribeSegments(segments)}");
        _showControls();
    }

    private void TimelineStartHandleThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_timelineInteractionMode != TimelineInteractionMode.StartHandle)
        {
            return;
        }

        var boundaryX = Canvas.GetLeft(_clipBarView.TimelineStartHandleThumb) + (_clipBarView.TimelineStartHandleThumb.Width / 2) + e.HorizontalChange;
        LogClipTrace($"StartHandle DragDelta activeIndex={_activeSegmentIndex} horizontalChange={e.HorizontalChange:F2} boundaryX={boundaryX:F2}");
        UpdateTimelineInteraction(new Point(boundaryX, 0), finalize: false);
    }

    private void TimelineStartHandleThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_timelineInteractionMode != TimelineInteractionMode.StartHandle)
        {
            return;
        }

        var boundaryX = Canvas.GetLeft(_clipBarView.TimelineStartHandleThumb) + (_clipBarView.TimelineStartHandleThumb.Width / 2);
        LogClipTrace($"StartHandle DragCompleted activeIndex={_activeSegmentIndex} boundaryX={boundaryX:F2}");
        UpdateTimelineInteraction(new Point(boundaryX, 0), finalize: true);
        EndTimelineInteraction();
    }

    private void TimelineEndHandleThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (!_isClipModeActive || _isExportingClip)
        {
            return;
        }

        var duration = GetBestKnownVideoDuration(_host.SelectedMedia);
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        var segments = GetEditableSegments(duration);
        _activeSegmentIndex = ResolveHandleSegmentIndex(isStartHandle: false, segments.Count);
        _selectedSegmentIndex = _activeSegmentIndex;
        _timelineInteractionMode = TimelineInteractionMode.EndHandle;
        _timelineInteractionRequiresDragActivation = false;
        _timelineInteractionHasActivatedDrag = false;
        PausePlaybackForClipInteraction("End handle drag started");
        LogClipTrace($"EndHandle DragStarted activeIndex={_activeSegmentIndex} duration={duration.TotalSeconds:F3}s segments={DescribeSegments(segments)}");
        _showControls();
    }

    private void TimelineEndHandleThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_timelineInteractionMode != TimelineInteractionMode.EndHandle)
        {
            return;
        }

        var boundaryX = Canvas.GetLeft(_clipBarView.TimelineEndHandleThumb) + (_clipBarView.TimelineEndHandleThumb.Width / 2) + e.HorizontalChange;
        LogClipTrace($"EndHandle DragDelta activeIndex={_activeSegmentIndex} horizontalChange={e.HorizontalChange:F2} boundaryX={boundaryX:F2}");
        UpdateTimelineInteraction(new Point(boundaryX, 0), finalize: false);
    }

    private void TimelineEndHandleThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_timelineInteractionMode != TimelineInteractionMode.EndHandle)
        {
            return;
        }

        var boundaryX = Canvas.GetLeft(_clipBarView.TimelineEndHandleThumb) + (_clipBarView.TimelineEndHandleThumb.Width / 2);
        LogClipTrace($"EndHandle DragCompleted activeIndex={_activeSegmentIndex} boundaryX={boundaryX:F2}");
        UpdateTimelineInteraction(new Point(boundaryX, 0), finalize: true);
        EndTimelineInteraction();
    }

    private void TimelineStartHandle_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isStartHandlePointerOver = true;
        ApplyTimelineHandleVisualState(_clipBarView.TimelineStartHandle, isStart: true, isPointerOver: true);
    }

    private void TimelineStartHandle_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isStartHandlePointerOver = false;
        ApplyTimelineHandleVisualState(_clipBarView.TimelineStartHandle, isStart: true, isPointerOver: false);
    }

    private void TimelineEndHandle_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isEndHandlePointerOver = true;
        ApplyTimelineHandleVisualState(_clipBarView.TimelineEndHandle, isStart: false, isPointerOver: true);
    }

    private void TimelineEndHandle_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isEndHandlePointerOver = false;
        ApplyTimelineHandleVisualState(_clipBarView.TimelineEndHandle, isStart: false, isPointerOver: false);
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
            var position = e.GetCurrentPoint(_clipBarView.TimelineContentCanvas).Position;
            if (_timelineInteractionRequiresDragActivation && !_timelineInteractionHasActivatedDrag)
            {
                var deltaX = position.X - _timelineInteractionStartPoint.X;
                var deltaY = position.Y - _timelineInteractionStartPoint.Y;
                var distance = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
                if (distance < TimelineDragActivationDistance)
                {
                    e.Handled = true;
                    return;
                }

                _timelineInteractionHasActivatedDrag = true;
                _timelineInteractionRequiresDragActivation = false;
                LogClipTrace($"TimelineInteraction drag activated mode={_timelineInteractionMode} distance={distance:F2}px");
            }

            UpdateTimelineInteraction(position, finalize: false);
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
        else if (_timelineInteractionRequiresDragActivation && !_timelineInteractionHasActivatedDrag)
        {
            LogClipTrace($"TimelineInteraction click-only selection mode={_timelineInteractionMode} activeIndex={_activeSegmentIndex}");
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

    private static void TimelineContentCanvas_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
    }

    private static bool TryGetTimelineTag<TTag>(DependencyObject? source, out FrameworkElement? owner, out TTag tag)
    {
        while (source != null)
        {
            if (source is FrameworkElement element && element.Tag is TTag matchedTag)
            {
                owner = element;
                tag = matchedTag;
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        owner = null;
        tag = default!;
        return false;
    }

    private void ApplyTimelineHandleVisualState(Border handle, bool isStart, bool isPointerOver)
    {
        handle.Background = isPointerOver
            ? ResolveBrush("AccentStrongBrush") ?? ResolveBrush("AccentBrush")
            : (isStart
                ? ResolveBrush("AccentBrush")
                : ResolveBrush("AccentStrongBrush") ?? ResolveBrush("AccentBrush"));
        handle.BorderBrush = isPointerOver
            ? ResolveBrush("TextBrush") ?? ResolveBrush("AccentForegroundBrush")
            : ResolveBrush("AccentForegroundBrush");
        handle.BorderThickness = isPointerOver ? new Thickness(1.5) : new Thickness(1);
        handle.Opacity = isPointerOver ? 1 : (handle.IsHitTestVisible ? 0.92 : 0.45);
    }

    private int ResolveHandleSegmentIndex(bool isStartHandle, int segmentCount)
    {
        if (segmentCount <= 0)
        {
            return -1;
        }

        if (_selectedSegmentIndex >= 0 && _selectedSegmentIndex < segmentCount)
        {
            return _selectedSegmentIndex;
        }

        return isStartHandle ? 0 : segmentCount - 1;
    }

    private bool IsWithinTimelineHandleThumb(DependencyObject? source)
    {
        return IsDescendantOf(source, _clipBarView.TimelineStartHandleThumb)
            || IsDescendantOf(source, _clipBarView.TimelineEndHandleThumb);
    }

    private static bool IsDescendantOf(DependencyObject? source, DependencyObject? target)
    {
        while (source != null)
        {
            if (ReferenceEquals(source, target))
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private bool CanInteractWithTimeline(UIElement? owner, PointerRoutedEventArgs e, bool requireLeftButtonForMouse)
    {
        if (owner == null || !_isClipModeActive || _isExportingClip)
        {
            return false;
        }

        var duration = GetBestKnownVideoDuration(_host.SelectedMedia);
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
        PausePlaybackForClipInteraction($"Begin timeline interaction mode={mode}");
        _timelineInteractionMode = mode;
        _timelineInteractionRequiresDragActivation = false;
        _timelineInteractionHasActivatedDrag = false;
        _timelineInteractionStartPoint = e.GetCurrentPoint(_clipBarView.TimelineContentCanvas).Position;
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
        _segmentDragPointerOffset = TimeSpan.Zero;
        _timelineInteractionRequiresDragActivation = false;
        _timelineInteractionHasActivatedDrag = false;
        _timelineInteractionStartPoint = default;
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
        var duration = GetBestKnownVideoDuration(_host.SelectedMedia);
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        EnsureClipSegments(duration);
        var value = PositionToTimelineTime(point.X, duration);
        LogClipTrace($"UpdateTimelineInteraction mode={_timelineInteractionMode} finalize={finalize} pointX={point.X:F2} mapped={value.TotalSeconds:F3}s activeIndex={_activeSegmentIndex} selectedIndex={_selectedSegmentIndex}");
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
                LogClipTrace($"StartHandle Applied activeIndex={_activeSegmentIndex} start={current.Start.TotalSeconds:F3}s end={current.End.TotalSeconds:F3}s segments={DescribeSegments(updatedSegments)}");

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
                LogClipTrace($"EndHandle Applied activeIndex={_activeSegmentIndex} start={current.Start.TotalSeconds:F3}s end={current.End.TotalSeconds:F3}s segments={DescribeSegments(updatedSegments)}");

                SeekPlayback(current.End);
                _clipStatusMessage = finalize
                    ? $"片段 {_activeSegmentIndex + 1} 终点已更新为 {FormatTime(current.End)}。"
                    : $"片段 {_activeSegmentIndex + 1} 终点：{FormatTime(current.End)}";
                break;
            }

            case TimelineInteractionMode.MoveSegment:
            {
                var updatedSegments = GetEditableSegments(duration);
                if (_activeSegmentIndex < 0 || _activeSegmentIndex >= updatedSegments.Count)
                {
                    break;
                }

                var current = updatedSegments[_activeSegmentIndex];
                var segmentDuration = current.Duration;
                var minStart = _activeSegmentIndex == 0
                    ? TimeSpan.Zero
                    : updatedSegments[_activeSegmentIndex - 1].End;
                var maxStart = _activeSegmentIndex == updatedSegments.Count - 1
                    ? Max(TimeSpan.Zero, duration - segmentDuration)
                    : updatedSegments[_activeSegmentIndex + 1].Start - segmentDuration;
                var nextStart = ClampToRange(value - _segmentDragPointerOffset, minStart, maxStart);
                current = new VideoClipSegment
                {
                    Start = nextStart,
                    End = nextStart + segmentDuration
                };
                updatedSegments[_activeSegmentIndex] = current;

                SetEditableSegments(updatedSegments, duration);
                _selectedSegmentIndex = _activeSegmentIndex;
                LogClipTrace($"MoveSegment Applied activeIndex={_activeSegmentIndex} start={current.Start.TotalSeconds:F3}s end={current.End.TotalSeconds:F3}s offset={_segmentDragPointerOffset.TotalSeconds:F3}s segments={DescribeSegments(updatedSegments)}");

                SeekPlayback(ClampToRange(current.Start + _segmentDragPointerOffset, current.Start, current.End));
                _clipStatusMessage = finalize
                    ? $"片段 {_activeSegmentIndex + 1} 已移动到 {FormatTime(current.Start)} - {FormatTime(current.End)}。"
                    : $"片段 {_activeSegmentIndex + 1}：{FormatTime(current.Start)} - {FormatTime(current.End)}";
                break;
            }
        }

        UpdateUi();
    }

    private void SeekPlayback(TimeSpan value)
    {
        var duration = GetBestKnownVideoDuration(_host.SelectedMedia);
        var previewSegments = GetPreviewSegments(duration);
        var target = ResolvePreviewSeekTarget(value, previewSegments);
        LogClipTrace($"SeekPlayback requested={value.TotalSeconds:F3}s target={target.TotalSeconds:F3}s previewSegments={DescribeSegments(previewSegments)}");
        _seekPlaybackPosition(target);
        _showControls();
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

    private void SetTimelineZoom(double nextZoomFactor)
    {
        var duration = GetBestKnownVideoDuration(_host.SelectedMedia);
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
        var duration = GetBestKnownVideoDuration(_host.SelectedMedia);
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
        var media = _host.SelectedMedia;
        var isVideo = media?.Type == MediaType.Video;
        var showClipBar = isVideo && _isClipModeActive;

        _setTransportSuppressed(showClipBar);
        SetButtonGlyph(_clipModeToggleButton, _isClipModeActive ? "\uE711" : "\uE7C8");
        ToolTipService.SetToolTip(_clipModeToggleButton, _isClipModeActive ? "退出剪辑" : "进入剪辑");
        _clipModeToggleButton.IsEnabled = isVideo && !_isExportingClip;

        _viewModel.Visibility = showClipBar ? Visibility.Visible : Visibility.Collapsed;
        if (!showClipBar)
        {
            _setPlaybackDisplayOverride(null, null, null, null, null);
            _viewModel.ProgressVisibility = Visibility.Collapsed;
            _viewModel.StatusText = _clipStatusMessage;
            _clipBarView.TimelineWebLoadingOverlay.Visibility = Visibility.Visible;
            _clipBarView.TimelineWebStatusText.Text = "等待进入剪辑模式...";
            ResetTimelineVisuals();
            return;
        }

        var duration = _getCurrentVideoDuration();
        EnsureClipSegments(duration);
        SyncPreviewPlaybackIfNeeded(duration);

        var configuredSegments = GetConfiguredSegments();
        var clipStart = configuredSegments.Count > 0 ? configuredSegments[0].Start : TimeSpan.Zero;
        var clipEnd = configuredSegments.Count > 0 ? configuredSegments[^1].End : duration;
        var clipLength = clipEnd > clipStart ? clipEnd - clipStart : TimeSpan.Zero;
        var currentPosition = ClampToDuration(_getCurrentPlaybackPosition(), duration);
        var summaryDuration = CalculateEffectiveOutputDuration(configuredSegments, duration, _clipMode);
        ApplyPreviewTimeDisplayOverride(duration, currentPosition);

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
        _viewModel.TimelineGuideText = _clipMode == VideoClipMode.Keep
            ? "亮色片段会输出；拖边界会吸附到播放头和相邻片段，Ctrl+滚轮以鼠标为中心缩放，右键拖动画布平移，方向键逐帧，Shift+方向键按秒移动"
            : "亮色片段会保留并输出，淡色片段会删除；拖边界会吸附到播放头和相邻片段，Ctrl+滚轮以鼠标为中心缩放，右键拖动画布平移，方向键逐帧，Shift+方向键按秒移动";
        _viewModel.ProgressVisibility = _isExportingClip ? Visibility.Visible : Visibility.Collapsed;

        if (!_isExportingClip && string.IsNullOrWhiteSpace(_clipStatusMessage))
        {
            _viewModel.ProgressValue = 0;
        }

        _viewModel.StatusText = _clipStatusMessage;
        _exitClipModeButton.IsEnabled = !_isExportingClip;
        _clipPlayPauseButton.IsEnabled = !_isExportingClip && duration > TimeSpan.Zero;
        _splitClipButton.IsEnabled = !_isExportingClip && CanSplitAtCurrentPosition(duration);
        _clearClipButton.IsEnabled = !_isExportingClip;
        _exportClipButton.IsEnabled = !_isExportingClip && _clipService.IsAvailable && configuredSegments.Count > 0;
        _clipModeActionButton.IsEnabled = !_isExportingClip && configuredSegments.Count > 0;
        _clipBarView.ClipModeActionText.Text = _clipMode == VideoClipMode.Keep ? "保留模式" : "删除模式";
        ToolTipService.SetToolTip(_clipModeActionButton, _clipMode == VideoClipMode.Keep ? "切换到删除模式" : "切换到保留模式");

        SetButtonGlyph(_clipPlayPauseButton, _isPlaybackPlaying() ? "\uE769" : "\uE768");
        ToolTipService.SetToolTip(_clipPlayPauseButton, _isPlaybackPlaying() ? "暂停" : "播放");
        ToolTipService.SetToolTip(_exportClipButton, _isExportingClip ? "导出中..." : "导出剪辑 (E)");
        _clipBarView.TimelineZoomText.Text = $"缩放：{_timelineZoomFactor * 100:0}%";

        UpdateTimelineLabels(duration, currentPosition);
        UpdateTimelineVisuals(duration, currentPosition, configuredSegments);
        EnsureTimelineThumbnails(media, duration);
        UpdateWebTimelineSurfaceState(media, duration, currentPosition, configuredSegments);
    }

    private void UpdateWebTimelineSurfaceState(
        MediaItemViewModel? media,
        TimeSpan duration,
        TimeSpan currentPosition,
        IReadOnlyList<VideoClipSegment> configuredSegments)
    {
        if (!_webBridge.IsReady)
        {
            _clipBarView.TimelineWebLoadingOverlay.Visibility = Visibility.Visible;
            _clipBarView.TimelineWebStatusText.Text = "正在连接时间线编辑器...";
        }
        else
        {
            _clipBarView.TimelineWebLoadingOverlay.Visibility = Visibility.Collapsed;
        }

        if (media?.Type != MediaType.Video || duration <= TimeSpan.Zero)
        {
            return;
        }

        _ = PublishWebTimelineStateAsync(media, duration, currentPosition, configuredSegments);
    }

    private Task PublishWebTimelineStateAsync(
        MediaItemViewModel media,
        TimeSpan duration,
        TimeSpan currentPosition,
        IReadOnlyList<VideoClipSegment> configuredSegments)
    {
        var previewSegments = GetPreviewSegments(duration);
        var webState = new ClipTimelineWebState(
            media.Id,
            media.FileName,
            _clipMode == VideoClipMode.Keep ? "keep" : "delete",
            _isPlaybackPlaying(),
            _isExportingClip,
            duration.TotalSeconds,
            currentPosition.TotalSeconds,
            _timelineZoomFactor,
            _selectedSegmentIndex,
            _clipStatusMessage,
            configuredSegments.Select((segment, index) => new ClipTimelineWebSegment(
                index,
                segment.Start.TotalSeconds,
                segment.End.TotalSeconds,
                index == _selectedSegmentIndex,
                false)).ToList(),
            previewSegments.Select((segment, index) => new ClipTimelineWebSegment(
                index,
                segment.Start.TotalSeconds,
                segment.End.TotalSeconds,
                false,
                true)).ToList());
        return _webBridge.PublishStateAsync(webState);
    }

    private void WebBridge_CommandReceived(object? sender, ClipTimelineWebCommand command)
    {
        switch (command.Type)
        {
            case "ready":
                UpdateUi();
                _webBridge.FocusPlayhead();
                break;

            case "requestSeek":
                if (TryReadPayloadSeconds(command.Payload, "positionSeconds", out var seekSeconds))
                {
                    SeekPlayback(TimeSpan.FromSeconds(seekSeconds));
                    UpdateUi();
                }
                break;

            case "selectSegment":
                if (TryReadPayloadInt(command.Payload, "segmentIndex", out var selectSegmentIndex))
                {
                    SelectSegment(selectSegmentIndex);
                }
                break;

            case "trimSegment":
                if (TryReadPayloadInt(command.Payload, "segmentIndex", out var trimSegmentIndex)
                    && TryReadPayloadSeconds(command.Payload, "positionSeconds", out var trimPositionSeconds)
                    && TryReadPayloadString(command.Payload, "edge", out var edge))
                {
                    ApplyTrimSegmentFromWeb(trimSegmentIndex, string.Equals(edge, "start", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(trimPositionSeconds));
                }
                break;

            case "moveSegment":
                if (TryReadPayloadInt(command.Payload, "segmentIndex", out var moveSegmentIndex)
                    && TryReadPayloadSeconds(command.Payload, "startSeconds", out var moveStartSeconds))
                {
                    ApplyMoveSegmentFromWeb(moveSegmentIndex, TimeSpan.FromSeconds(moveStartSeconds));
                }
                break;

            case "splitAt":
                if (TryReadPayloadSeconds(command.Payload, "positionSeconds", out var splitPositionSeconds))
                {
                    SplitSegmentAtPosition(TimeSpan.FromSeconds(splitPositionSeconds));
                }
                else
                {
                    SplitSegmentAtCurrentPosition();
                }
                break;

            case "deleteSegment":
                if (TryReadPayloadInt(command.Payload, "segmentIndex", out var deleteSegmentIndex))
                {
                    DeleteSegmentByIndex(deleteSegmentIndex);
                }
                else
                {
                    DeleteSelectedSegment();
                }
                break;

            case "setZoom":
                if (TryReadPayloadDouble(command.Payload, "zoomFactor", out var zoomFactor))
                {
                    SetTimelineZoom(zoomFactor);
                    _webBridge.FocusPlayhead();
                }
                break;

            case "requestExport":
                _ = ExportCurrentClipAsync();
                break;

            case "requestPlayPause":
                TogglePlaybackFromTimeline();
                break;
        }
    }

    private void SelectSegment(int segmentIndex)
    {
        var duration = GetBestKnownVideoDuration(_host.SelectedMedia);
        var segments = GetEditableSegments(duration);
        if (segmentIndex < 0 || segmentIndex >= segments.Count)
        {
            return;
        }

        _selectedSegmentIndex = segmentIndex;
        var segment = segments[segmentIndex];
        _clipStatusMessage = $"已选中片段 {segmentIndex + 1}：{FormatTime(segment.Start)} - {FormatTime(segment.End)}";
        UpdateUi();
    }

    private void ApplyTrimSegmentFromWeb(int segmentIndex, bool isStart, TimeSpan position)
    {
        var duration = GetBestKnownVideoDuration(_host.SelectedMedia);
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        var updatedSegments = GetEditableSegments(duration);
        if (segmentIndex < 0 || segmentIndex >= updatedSegments.Count)
        {
            return;
        }

        var gap = GetMinimumSelectionGap(duration);
        var current = updatedSegments[segmentIndex];
        if (isStart)
        {
            var minStart = segmentIndex == 0
                ? TimeSpan.Zero
                : updatedSegments[segmentIndex - 1].End;
            var maxStart = current.End - gap;
            current = new VideoClipSegment
            {
                Start = ClampToRange(position, minStart, maxStart),
                End = current.End
            };
            _clipStatusMessage = $"片段 {segmentIndex + 1} 起点：{FormatTime(current.Start)}";
            SeekPlayback(current.Start);
        }
        else
        {
            var minEnd = current.Start + gap;
            var maxEnd = segmentIndex == updatedSegments.Count - 1
                ? duration
                : updatedSegments[segmentIndex + 1].Start;
            current = new VideoClipSegment
            {
                Start = current.Start,
                End = ClampToRange(position, minEnd, maxEnd)
            };
            _clipStatusMessage = $"片段 {segmentIndex + 1} 终点：{FormatTime(current.End)}";
            SeekPlayback(current.End);
        }

        updatedSegments[segmentIndex] = current;
        SetEditableSegments(updatedSegments, duration);
        _selectedSegmentIndex = segmentIndex;
        LogClipTrace($"ApplyTrimSegmentFromWeb index={segmentIndex} edge={(isStart ? "start" : "end")} start={current.Start.TotalSeconds:F3}s end={current.End.TotalSeconds:F3}s");
        UpdateUi();
    }

    private void ApplyMoveSegmentFromWeb(int segmentIndex, TimeSpan start)
    {
        var duration = GetBestKnownVideoDuration(_host.SelectedMedia);
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        var updatedSegments = GetEditableSegments(duration);
        if (segmentIndex < 0 || segmentIndex >= updatedSegments.Count)
        {
            return;
        }

        var current = updatedSegments[segmentIndex];
        var segmentDuration = current.Duration;
        var minStart = segmentIndex == 0
            ? TimeSpan.Zero
            : updatedSegments[segmentIndex - 1].End;
        var maxStart = segmentIndex == updatedSegments.Count - 1
            ? Max(TimeSpan.Zero, duration - segmentDuration)
            : updatedSegments[segmentIndex + 1].Start - segmentDuration;
        var nextStart = ClampToRange(start, minStart, maxStart);
        current = new VideoClipSegment
        {
            Start = nextStart,
            End = nextStart + segmentDuration
        };
        updatedSegments[segmentIndex] = current;
        SetEditableSegments(updatedSegments, duration);
        _selectedSegmentIndex = segmentIndex;
        _clipStatusMessage = $"片段 {segmentIndex + 1}：{FormatTime(current.Start)} - {FormatTime(current.End)}";
        SeekPlayback(current.Start);
        LogClipTrace($"ApplyMoveSegmentFromWeb index={segmentIndex} start={current.Start.TotalSeconds:F3}s end={current.End.TotalSeconds:F3}s");
        UpdateUi();
    }

    private void SplitSegmentAtPosition(TimeSpan position)
    {
        var duration = GetBestKnownVideoDuration(_host.SelectedMedia);
        if (duration <= TimeSpan.Zero || _isExportingClip)
        {
            return;
        }

        if (!TryResolveSplitTarget(duration, position, out var segments, out var targetIndex, out var splitPosition, out var failureReason))
        {
            _clipStatusMessage = failureReason;
            UpdateUi();
            return;
        }

        var segment = segments[targetIndex];
        segments[targetIndex] = new VideoClipSegment
        {
            Start = segment.Start,
            End = splitPosition
        };
        segments.Insert(targetIndex + 1, new VideoClipSegment
        {
            Start = splitPosition,
            End = segment.End
        });

        SetEditableSegments(segments, duration);
        _selectedSegmentIndex = Math.Clamp(targetIndex + 1, 0, segments.Count - 1);
        _clipStatusMessage = $"已在 {FormatTime(splitPosition)} 切开当前片段，共 {segments.Count} 段。";
        SeekPlayback(splitPosition);
        UpdateUi();
        _showControls();
    }

    private void DeleteSegmentByIndex(int segmentIndex)
    {
        if (segmentIndex >= 0)
        {
            _selectedSegmentIndex = segmentIndex;
        }

        DeleteSelectedSegment();
    }

    private void TogglePlaybackFromTimeline()
    {
        if (!_isPlaybackPlaying())
        {
            MovePlaybackToPreviewStartIfNeeded();
        }

        _togglePlayPause();
        UpdateUi();
        _showControls();
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
        var previewSegments = _clipMode == VideoClipMode.Delete
            ? GetPreviewSegments(duration)
            : configuredSegments;

        for (var i = 0; i < previewSegments.Count; i++)
        {
            var segment = previewSegments[i];
            var startX = TimeToTimelineX(segment.Start, duration, contentWidth);
            var endX = TimeToTimelineX(segment.End, duration, contentWidth);
            var width = Math.Max(2, endX - startX);
            var trackVisual = new Border
            {
                Width = width,
                Height = TimelineTrackHeight,
                CornerRadius = new CornerRadius(TimelineTrackHeight / 2),
                Background = _keepSegmentBrush,
                Opacity = _clipMode == VideoClipMode.Delete ? 0.92 : 0.82,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(trackVisual, startX);
            Canvas.SetTop(trackVisual, TimelineTrackTop);
            Canvas.SetZIndex(trackVisual, 1);
            _clipBarView.TimelineSegmentsCanvas.Children.Add(trackVisual);
        }

        for (var i = 0; i < configuredSegments.Count; i++)
        {
            var segment = configuredSegments[i];
            var startX = TimeToTimelineX(segment.Start, duration, contentWidth);
            var endX = TimeToTimelineX(segment.End, duration, contentWidth);
            var width = Math.Max(2, endX - startX);
            var isSelected = i == _selectedSegmentIndex;
            var isDeleteMode = _clipMode == VideoClipMode.Delete;
            var segmentVisual = new Border
            {
                Width = width,
                Height = TimelineThumbnailHeight + 8,
                BorderThickness = new Thickness(isSelected ? 2 : (isDeleteMode ? 1 : 0)),
                BorderBrush = isSelected
                    ? ResolveBrush("AccentStrongBrush") ?? ResolveBrush("AccentBrush") ?? (isDeleteMode ? _deleteSegmentBrush : _keepSegmentBrush)
                    : isDeleteMode
                        ? _deleteSegmentBrush ?? ResolveBrush("SurfaceStrokeBrush") ?? new SolidColorBrush(Colors.Transparent)
                        : new SolidColorBrush(Colors.Transparent),
                Background = isDeleteMode
                    ? _deleteSegmentBrush ?? new SolidColorBrush(Colors.Transparent)
                    : new SolidColorBrush(Colors.Transparent),
                CornerRadius = new CornerRadius(10),
                Opacity = isDeleteMode ? (isSelected ? 0.28 : 0.16) : 1,
                IsHitTestVisible = canInteract,
                Tag = new SegmentBodyTag(i)
            };
            segmentVisual.PointerPressed += SegmentBody_PointerPressed;
            Canvas.SetLeft(segmentVisual, startX);
            Canvas.SetTop(segmentVisual, TimelineThumbnailTop - 4);
            Canvas.SetZIndex(segmentVisual, isDeleteMode ? 2 : 0);
            _clipBarView.TimelineSegmentsCanvas.Children.Add(segmentVisual);

            var trackVisual = new Border
            {
                Width = width,
                Height = isSelected ? TimelineTrackHeight + 2 : TimelineTrackHeight,
                CornerRadius = new CornerRadius(TimelineTrackHeight / 2),
                Background = isSelected
                    ? ResolveBrush("AccentStrongBrush") ?? ResolveBrush("AccentBrush") ?? (isDeleteMode ? _deleteSegmentBrush : _keepSegmentBrush)
                    : (isDeleteMode ? _deleteSegmentBrush : _keepSegmentBrush),
                Opacity = isDeleteMode
                    ? (isSelected ? 0.72 : 0.38)
                    : (isSelected ? 0.95 : 0.82),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(trackVisual, startX);
            Canvas.SetTop(trackVisual, isSelected ? TimelineTrackTop - 1 : TimelineTrackTop);
            Canvas.SetZIndex(trackVisual, isDeleteMode ? 3 : 1);
            _clipBarView.TimelineSegmentsCanvas.Children.Add(trackVisual);

        }

        var playheadX = TimeToTimelineX(currentPosition, duration, contentWidth);
        _clipBarView.TimelinePlayhead.Height = playheadHeight;
        Canvas.SetLeft(_clipBarView.TimelinePlayhead, playheadX - (TimelinePlayheadWidth / 2));
        Canvas.SetTop(_clipBarView.TimelinePlayhead, TimelinePlayheadTop);

        _clipBarView.TimelineScrubSurface.IsHitTestVisible = canInteract;
        var startHandleSegmentIndex = ResolveHandleSegmentIndex(isStartHandle: true, configuredSegments.Count);
        var endHandleSegmentIndex = ResolveHandleSegmentIndex(isStartHandle: false, configuredSegments.Count);
        if (startHandleSegmentIndex >= 0
            && startHandleSegmentIndex < configuredSegments.Count
            && endHandleSegmentIndex >= 0
            && endHandleSegmentIndex < configuredSegments.Count)
        {
            var startSegment = configuredSegments[startHandleSegmentIndex];
            var endSegment = configuredSegments[endHandleSegmentIndex];
            var handleTop = TimelineThumbnailTop - 1;
            var startBoundaryX = TimeToTimelineX(startSegment.Start, duration, contentWidth);
            var endBoundaryX = TimeToTimelineX(endSegment.End, duration, contentWidth);

            _clipBarView.TimelineStartHandle.Visibility = Visibility.Visible;
            _clipBarView.TimelineStartHandle.IsHitTestVisible = false;
            _clipBarView.TimelineStartHandleThumb.Visibility = Visibility.Visible;
            _clipBarView.TimelineStartHandleThumb.IsHitTestVisible = canInteract;
            ApplyTimelineHandleVisualState(_clipBarView.TimelineStartHandle, isStart: true, isPointerOver: _isStartHandlePointerOver);
            Canvas.SetLeft(_clipBarView.TimelineStartHandle, startBoundaryX);
            Canvas.SetTop(_clipBarView.TimelineStartHandle, handleTop);
            Canvas.SetLeft(_clipBarView.TimelineStartHandleThumb, startBoundaryX - (_clipBarView.TimelineStartHandleThumb.Width / 2));
            Canvas.SetTop(_clipBarView.TimelineStartHandleThumb, handleTop);
            Canvas.SetZIndex(_clipBarView.TimelineStartHandle, 3);
            Canvas.SetZIndex(_clipBarView.TimelineStartHandleThumb, 4);

            _clipBarView.TimelineEndHandle.Visibility = Visibility.Visible;
            _clipBarView.TimelineEndHandle.IsHitTestVisible = false;
            _clipBarView.TimelineEndHandleThumb.Visibility = Visibility.Visible;
            _clipBarView.TimelineEndHandleThumb.IsHitTestVisible = canInteract;
            ApplyTimelineHandleVisualState(_clipBarView.TimelineEndHandle, isStart: false, isPointerOver: _isEndHandlePointerOver);
            Canvas.SetLeft(_clipBarView.TimelineEndHandle, endBoundaryX - _clipBarView.TimelineEndHandle.Width);
            Canvas.SetTop(_clipBarView.TimelineEndHandle, handleTop);
            Canvas.SetLeft(_clipBarView.TimelineEndHandleThumb, endBoundaryX - (_clipBarView.TimelineEndHandleThumb.Width / 2));
            Canvas.SetTop(_clipBarView.TimelineEndHandleThumb, handleTop);
            Canvas.SetZIndex(_clipBarView.TimelineEndHandle, 3);
            Canvas.SetZIndex(_clipBarView.TimelineEndHandleThumb, 4);
        }
        else
        {
            _clipBarView.TimelineStartHandle.Visibility = Visibility.Collapsed;
            _clipBarView.TimelineEndHandle.Visibility = Visibility.Collapsed;
            _clipBarView.TimelineStartHandleThumb.Visibility = Visibility.Collapsed;
            _clipBarView.TimelineEndHandleThumb.Visibility = Visibility.Collapsed;
            _clipBarView.TimelineStartHandle.IsHitTestVisible = false;
            _clipBarView.TimelineEndHandle.IsHitTestVisible = false;
            _clipBarView.TimelineStartHandleThumb.IsHitTestVisible = false;
            _clipBarView.TimelineEndHandleThumb.IsHitTestVisible = false;
        }
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

        var trackLeft = GetTimelineTrackLeft();
        var trackWidth = GetTimelineTrackWidth(contentWidth);
        var thumbnailWidth = Math.Max(1, trackWidth / _timelineThumbnails.Count);
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

            Canvas.SetLeft(image, trackLeft + (i * thumbnailWidth));
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
        var duration = GetBestKnownVideoDuration(_host.SelectedMedia);
        if (_clipSegments.Count > 0)
        {
            return NormalizeSegments(_clipSegments, duration);
        }

        if (duration > TimeSpan.Zero && (_clipStart.HasValue || _clipEnd.HasValue))
        {
            var start = ClampToDuration(_clipStart ?? TimeSpan.Zero, duration);
            var end = ClampToDuration(_clipEnd ?? duration, duration);
            if (end > start)
            {
                return new[]
                {
                    new VideoClipSegment
                    {
                        Start = start,
                        End = end
                    }
                };
            }
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

    private void SyncPreviewPlaybackIfNeeded()
    {
        SyncPreviewPlaybackIfNeeded(_getCurrentVideoDuration());
    }

    private void SyncPreviewPlaybackIfNeeded(TimeSpan duration)
    {
        if (_isPreviewPlaybackSyncInProgress
            || !_isClipModeActive
            || _isExportingClip
            || _host.SelectedMedia?.Type != MediaType.Video
            || duration <= TimeSpan.Zero)
        {
            return;
        }

        var previewSegments = GetPreviewSegments(duration);
        if (previewSegments.Count == 0)
        {
            if (_isPlaybackPlaying())
            {
                LogClipTrace("SyncPreviewPlaybackIfNeeded no preview segments while playing; rewinding to zero and pausing");
                _isPreviewPlaybackSyncInProgress = true;
                try
                {
                    _seekPlaybackPosition(TimeSpan.Zero);
                    _togglePlayPause();
                }
                finally
                {
                    _isPreviewPlaybackSyncInProgress = false;
                }
            }

            return;
        }

        var currentPosition = ClampToDuration(_getCurrentPlaybackPosition(), duration);
        var activeSegmentIndex = FindSegmentIndexContaining(currentPosition, previewSegments);
        if (activeSegmentIndex >= 0)
        {
            if (!_isPlaybackPlaying())
            {
                return;
            }

            var activeSegment = previewSegments[activeSegmentIndex];
            if (currentPosition < activeSegment.End)
            {
                return;
            }

            if (activeSegmentIndex < previewSegments.Count - 1)
            {
                LogClipTrace($"SyncPreviewPlaybackIfNeeded jumping to next preview segment from {currentPosition.TotalSeconds:F3}s to {previewSegments[activeSegmentIndex + 1].Start.TotalSeconds:F3}s");
                SeekPreviewPlaybackSilently(previewSegments[activeSegmentIndex + 1].Start);
            }
            else
            {
                LogClipTrace($"SyncPreviewPlaybackIfNeeded pausing at final preview segment end {previewSegments[^1].End.TotalSeconds:F3}s from current {currentPosition.TotalSeconds:F3}s");
                PausePreviewPlaybackAt(previewSegments[^1].End);
            }

            return;
        }

        if (!_isPlaybackPlaying())
        {
            return;
        }

        var nextSegment = previewSegments.FirstOrDefault(segment => segment.Start > currentPosition);
        if (nextSegment != null)
        {
            LogClipTrace($"SyncPreviewPlaybackIfNeeded outside preview; seeking forward from {currentPosition.TotalSeconds:F3}s to next segment {nextSegment.Start.TotalSeconds:F3}s");
            SeekPreviewPlaybackSilently(nextSegment.Start);
            return;
        }

        if (currentPosition < previewSegments[0].Start)
        {
            LogClipTrace($"SyncPreviewPlaybackIfNeeded before first preview; seeking from {currentPosition.TotalSeconds:F3}s to {previewSegments[0].Start.TotalSeconds:F3}s");
            SeekPreviewPlaybackSilently(previewSegments[0].Start);
            return;
        }

        LogClipTrace($"SyncPreviewPlaybackIfNeeded after preview end; pausing at {previewSegments[^1].End.TotalSeconds:F3}s from {currentPosition.TotalSeconds:F3}s");
        PausePreviewPlaybackAt(previewSegments[^1].End);
    }

    private void MovePlaybackToPreviewStartIfNeeded()
    {
        var duration = _getCurrentVideoDuration();
        if (!_isClipModeActive || duration <= TimeSpan.Zero)
        {
            return;
        }

        var previewSegments = GetPreviewSegments(duration);
        if (previewSegments.Count == 0)
        {
            return;
        }

        var currentPosition = ClampToDuration(_getCurrentPlaybackPosition(), duration);
        if (FindSegmentIndexContaining(currentPosition, previewSegments) >= 0)
        {
            return;
        }

        var nextSegment = previewSegments.FirstOrDefault(segment => segment.Start > currentPosition);
        var target = nextSegment?.Start ?? previewSegments[0].Start;
        SeekPreviewPlaybackSilently(target);
    }

    private IReadOnlyList<VideoClipSegment> GetPreviewSegments(TimeSpan duration)
    {
        var configuredSegments = GetConfiguredSegments();
        if (_clipMode == VideoClipMode.Keep)
        {
            return configuredSegments;
        }

        if (configuredSegments.Count == 0)
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

        return InvertSegments(configuredSegments, duration);
    }

    private void ApplyPreviewTimeDisplayOverride(TimeSpan duration, TimeSpan currentPosition)
    {
        var previewSegments = GetPreviewSegments(duration);
        var previewDuration = previewSegments.Aggregate(TimeSpan.Zero, (current, segment) => current + segment.Duration);
        var previewPosition = MapToPreviewTimelinePosition(currentPosition, previewSegments);
        _setPlaybackDisplayOverride(
            FormatTime(previewPosition),
            FormatTime(previewDuration),
            previewPosition.TotalSeconds,
            previewDuration.TotalSeconds,
            displayPosition => MapFromPreviewTimelinePosition(displayPosition, previewSegments));
    }

    private static TimeSpan MapToPreviewTimelinePosition(TimeSpan absolutePosition, IReadOnlyList<VideoClipSegment> previewSegments)
    {
        if (previewSegments.Count == 0)
        {
            return TimeSpan.Zero;
        }

        var elapsed = TimeSpan.Zero;
        foreach (var segment in previewSegments)
        {
            if (absolutePosition <= segment.Start)
            {
                return elapsed;
            }

            if (absolutePosition < segment.End)
            {
                return elapsed + (absolutePosition - segment.Start);
            }

            elapsed += segment.Duration;
        }

        return elapsed;
    }

    private static TimeSpan MapFromPreviewTimelinePosition(TimeSpan previewPosition, IReadOnlyList<VideoClipSegment> previewSegments)
    {
        if (previewSegments.Count == 0)
        {
            return TimeSpan.Zero;
        }

        if (previewPosition <= TimeSpan.Zero)
        {
            return previewSegments[0].Start;
        }

        var elapsed = TimeSpan.Zero;
        foreach (var segment in previewSegments)
        {
            var nextElapsed = elapsed + segment.Duration;
            if (previewPosition <= nextElapsed)
            {
                var offset = previewPosition - elapsed;
                return segment.Start + ClampToRange(offset, TimeSpan.Zero, segment.Duration);
            }

            elapsed = nextElapsed;
        }

        return previewSegments[^1].End;
    }

    private void SeekPreviewPlaybackSilently(TimeSpan position)
    {
        _isPreviewPlaybackSyncInProgress = true;
        try
        {
            LogClipTrace($"SeekPreviewPlaybackSilently position={position.TotalSeconds:F3}s");
            _seekPlaybackPosition(position);
        }
        finally
        {
            _isPreviewPlaybackSyncInProgress = false;
        }
    }

    private void PausePreviewPlaybackAt(TimeSpan position)
    {
        _isPreviewPlaybackSyncInProgress = true;
        try
        {
            LogClipTrace($"PausePreviewPlaybackAt position={position.TotalSeconds:F3}s isPlaying={_isPlaybackPlaying()}");
            _seekPlaybackPosition(position);
            if (_isPlaybackPlaying())
            {
                _togglePlayPause();
            }
        }
        finally
        {
            _isPreviewPlaybackSyncInProgress = false;
        }
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
        LogClipTrace($"SetEditableSegments duration={duration.TotalSeconds:F3}s incoming={DescribeSegments(segments)} normalized={DescribeSegments(normalized)}");
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

    private bool TryResolveSplitTarget(
        TimeSpan duration,
        out List<VideoClipSegment> segments,
        out int targetIndex,
        out TimeSpan splitPosition,
        out string failureReason)
    {
        return TryResolveSplitTarget(
            duration,
            ClampToDuration(_getCurrentPlaybackPosition(), duration),
            out segments,
            out targetIndex,
            out splitPosition,
            out failureReason);
    }

    private bool TryResolveSplitTarget(
        TimeSpan duration,
        TimeSpan requestedPosition,
        out List<VideoClipSegment> segments,
        out int targetIndex,
        out TimeSpan splitPosition,
        out string failureReason)
    {
        segments = new List<VideoClipSegment>();
        targetIndex = -1;
        splitPosition = TimeSpan.Zero;
        failureReason = "当前没有可切开的片段。";

        if (duration <= TimeSpan.Zero)
        {
            failureReason = "视频时长尚未就绪，请稍后再试。";
            return false;
        }

        segments = GetEditableSegments(duration);
        if (segments.Count == 0)
        {
            return false;
        }

        var position = ClampToDuration(requestedPosition, duration);
        targetIndex = FindSegmentIndexContaining(position, segments);
        if (targetIndex < 0)
        {
            failureReason = "游标需要落在某个片段内部，才能切开。";
            LogClipTrace($"TryResolveSplitTarget miss position={position.TotalSeconds:F3}s segments={DescribeSegments(segments)}");
            return false;
        }

        var segment = segments[targetIndex];
        var splitGap = GetMinimumSplitBoundaryGap(duration, segment.Duration);
        var minSplitPosition = segment.Start + splitGap;
        var maxSplitPosition = segment.End - splitGap;
        if (maxSplitPosition <= minSplitPosition)
        {
            failureReason = "当前片段太短，不能再切开了。";
            LogClipTrace($"TryResolveSplitTarget segment too short index={targetIndex} duration={segment.Duration.TotalSeconds:F3}s splitGap={splitGap.TotalSeconds:F3}s segment={segment.Start.TotalSeconds:F3}-{segment.End.TotalSeconds:F3}");
            return false;
        }

        if (position <= minSplitPosition || position >= maxSplitPosition)
        {
            failureReason = "游标需要落在片段内部，且离边界稍微远一点，才能切开。";
            LogClipTrace($"TryResolveSplitTarget near boundary index={targetIndex} position={position.TotalSeconds:F3}s allowed={minSplitPosition.TotalSeconds:F3}-{maxSplitPosition.TotalSeconds:F3}s segment={segment.Start.TotalSeconds:F3}-{segment.End.TotalSeconds:F3}");
            return false;
        }

        splitPosition = position;
        return true;
    }

    private bool CanSplitAtCurrentPosition(TimeSpan duration)
    {
        return TryResolveSplitTarget(duration, out _, out _, out _, out _);
    }

    private static bool TryReadPayloadSeconds(JsonElement payload, string propertyName, out double value)
    {
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(propertyName, out var property)
            && property.TryGetDouble(out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryReadPayloadDouble(JsonElement payload, string propertyName, out double value)
    {
        return TryReadPayloadSeconds(payload, propertyName, out value);
    }

    private static bool TryReadPayloadInt(JsonElement payload, string propertyName, out int value)
    {
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(propertyName, out var property)
            && property.TryGetInt32(out value))
        {
            return true;
        }

        value = -1;
        return false;
    }

    private static bool TryReadPayloadString(JsonElement payload, string propertyName, out string value)
    {
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(propertyName, out var property))
        {
            value = property.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
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

    private static TimeSpan ResolvePreviewSeekTarget(TimeSpan requested, IReadOnlyList<VideoClipSegment> previewSegments)
    {
        if (previewSegments.Count == 0)
        {
            return requested;
        }

        if (requested <= previewSegments[0].Start)
        {
            return previewSegments[0].Start;
        }

        for (var i = 0; i < previewSegments.Count; i++)
        {
            var segment = previewSegments[i];
            if (requested >= segment.Start && requested <= segment.End)
            {
                return requested;
            }

            if (i >= previewSegments.Count - 1)
            {
                continue;
            }

            var nextSegment = previewSegments[i + 1];
            if (requested > segment.End && requested < nextSegment.Start)
            {
                var distanceToCurrentEnd = requested - segment.End;
                var distanceToNextStart = nextSegment.Start - requested;
                return distanceToCurrentEnd <= distanceToNextStart
                    ? segment.End
                    : nextSegment.Start;
            }
        }

        return previewSegments[^1].End;
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

    private static List<VideoClipSegment> InvertSegments(IReadOnlyList<VideoClipSegment> segments, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return new List<VideoClipSegment>();
        }

        var keptSegments = new List<VideoClipSegment>();
        var current = TimeSpan.Zero;

        foreach (var segment in NormalizeSegments(segments, duration))
        {
            if (segment.Start > current)
            {
                keptSegments.Add(new VideoClipSegment
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
            keptSegments.Add(new VideoClipSegment
            {
                Start = current,
                End = duration
            });
        }

        return keptSegments
            .Where(segment => segment.End > segment.Start)
            .ToList();
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

    private static TimeSpan GetMinimumSplitBoundaryGap(TimeSpan duration, TimeSpan segmentDuration)
    {
        if (duration <= TimeSpan.Zero || segmentDuration <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var selectionGap = GetMinimumSelectionGap(duration);
        var boundaryGap = selectionGap <= MinTimelineSplitBoundaryGap
            ? selectionGap
            : MinTimelineSplitBoundaryGap;
        var maxAllowedGap = TimeSpan.FromTicks(Math.Max(0, (segmentDuration.Ticks / 2) - 1));
        return boundaryGap <= maxAllowedGap ? boundaryGap : maxAllowedGap;
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

    private void LogClipTrace(string message)
    {
        var mediaId = _host.SelectedMedia?.Id ?? "<none>";
        AppTraceLogger.Log("ClipEditor", $"mediaId={mediaId} {message}");
    }

    private void PausePlaybackForClipInteraction(string reason)
    {
        if (!_isPlaybackPlaying())
        {
            return;
        }

        LogClipTrace($"PausePlaybackForClipInteraction reason={reason}");
        _togglePlayPause();
    }

    private static string DescribeSegments(IEnumerable<VideoClipSegment> segments)
    {
        return string.Join(", ", segments.Select(segment => $"[{segment.Start.TotalSeconds:F3}-{segment.End.TotalSeconds:F3}]"));
    }
}
