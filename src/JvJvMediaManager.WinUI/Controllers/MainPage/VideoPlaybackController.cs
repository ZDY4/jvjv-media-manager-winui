using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using JvJvMediaManager.Models;
using JvJvMediaManager.Services;
using JvJvMediaManager.Utilities;
using JvJvMediaManager.ViewModels;
using JvJvMediaManager.ViewModels.MainPage;
using JvJvMediaManager.Views.MainPageParts;

namespace JvJvMediaManager.Controllers.MainPage;

public sealed class VideoPlaybackController
{
    private const double TransportPointerZoneHeight = 112;

    private enum PlaybackMode
    {
        ListLoop,
        SingleLoop,
        Shuffle
    }

    private readonly SettingsService _settings;
    private readonly LibraryShellViewModel _libraryViewModel;
    private readonly VideoPlaybackViewModel _viewModel;
    private readonly TransportControlBarView _transportBarView;
    private readonly PlayerNavigationOverlayView _navigationOverlayView;
    private readonly VideoViewportView _videoViewportView;
    private readonly MediaPlayerElement _videoPlayer;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Func<AppWindow?> _getAppWindow;
    private readonly Func<int, Task> _navigateRelativeAsync;
    private readonly Func<bool> _canAutoHideControls;
    private readonly Action _focusHost;
    private readonly Action _refreshNavigationHotspots;
    private readonly Action<TimeSpan> _handleMediaOpened;
    private readonly Action _notifyPlaybackProgressChanged;
    private readonly PlaybackResourceCoordinator _playbackResources;
    private readonly IPlaybackActivityLease _playbackLease;
    private static int _playbackOwnerSequence;
    private readonly Random _random = new();
    private readonly DispatcherTimer _playbackTimer = new();
    private readonly DispatcherTimer _controlsHideTimer = new();
    private readonly DispatcherTimer _uiHeartbeatTimer = new();
    private readonly MenuFlyout _playbackModeFlyout;
    private readonly RadioMenuFlyoutItem _listLoopModeItem;
    private readonly RadioMenuFlyoutItem _singleLoopModeItem;
    private readonly RadioMenuFlyoutItem _shuffleModeItem;
    private readonly ToggleMenuFlyoutItem _shuffleVideoOnlyMenuItem;
    private readonly ToggleMenuFlyoutItem _autoAdvanceMenuItem;
    private readonly PlaybackWatchdogController _watchdog;
    private readonly ShufflePlaybackController _shuffle;
    private readonly ProgressPreviewController _progressPreview;
    private readonly PointerEventHandler _progressSliderPointerPressedHandler;
    private readonly PointerEventHandler _progressSliderPointerReleasedHandler;

    private MediaPlayer? _player;
    private bool _isSeeking;
    private bool _areControlsVisible = true;
    private bool _isTransportSuppressed;
    private bool _isPointerOverProgressInteraction;
    private bool _isPointerInTransportZone;
    private bool _progressSliderHandlersAttached;
    private double _lastNonZeroVolume = 0.8;
    private bool _autoAdvanceEnabled;
    private bool _shuffleVideoOnly;
    private PlaybackMode _playbackMode = PlaybackMode.ListLoop;
    private TimeSpan _lastPlaybackPosition;
    private TimeSpan _lastPlaybackDuration;
    private long _lastUiHeartbeatTimestamp;
    private long _lastSeekTimestamp;
    private TimeSpan _lastSeekTarget;
    private int _pendingSeekVersion;
    private int _completedSeekVersion;
    private string? _currentTimeTextOverride;
    private string? _totalTimeTextOverride;
    private double? _progressValueOverrideSeconds;
    private double? _progressMaximumOverrideSeconds;
    private Func<TimeSpan, TimeSpan>? _displayToPlaybackPositionOverride;
    private const double VolumeWheelStep = 0.05;
    private static readonly HashSet<string> CommonWindowsPlaybackExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".m4v",
        ".mov",
        ".wmv",
        ".avi",
        ".mkv",
        ".webm"
    };
    private int _mediaSourceVersion;
    private string? _pendingInternalShuffleSelectionId;
    private readonly PlaybackSessionTelemetry _telemetry;
    private int _failedMediaSourceVersion;
    private int _handledMediaFailedSourceVersion;
    private int _pendingPlayerDisposalCount;
    private int _isDisposed;

    public VideoPlaybackController(
        SettingsService settings,
        LibraryShellViewModel libraryViewModel,
        VideoPlaybackViewModel viewModel,
        TransportControlBarView transportBarView,
        PlayerNavigationOverlayView navigationOverlayView,
        VideoViewportView videoViewportView,
        MediaPlayerElement videoPlayer,
        DispatcherQueue dispatcherQueue,
        Func<AppWindow?> getAppWindow,
        Func<int, Task> navigateRelativeAsync,
        Func<bool> canAutoHideControls,
        Action focusHost,
        Action refreshNavigationHotspots,
        Action<TimeSpan> handleMediaOpened,
        Action notifyPlaybackProgressChanged,
        PlaybackResourceCoordinator playbackResources)
    {
        _settings = settings;
        _libraryViewModel = libraryViewModel;
        _viewModel = viewModel;
        _transportBarView = transportBarView;
        _navigationOverlayView = navigationOverlayView;
        _videoViewportView = videoViewportView;
        _videoPlayer = videoPlayer;
        _dispatcherQueue = dispatcherQueue;
        _getAppWindow = getAppWindow;
        _navigateRelativeAsync = navigateRelativeAsync;
        _canAutoHideControls = canAutoHideControls;
        _focusHost = focusHost;
        _refreshNavigationHotspots = refreshNavigationHotspots;
        _handleMediaOpened = handleMediaOpened;
        _notifyPlaybackProgressChanged = notifyPlaybackProgressChanged;
        _playbackResources = playbackResources;
        _playbackLease = playbackResources.Acquire($"video-controller-{Interlocked.Increment(ref _playbackOwnerSequence)}");
        _playbackMode = ParsePlaybackMode(_settings.PlaybackMode);
        _autoAdvanceEnabled = _settings.PlaybackAutoAdvanceEnabled;
        _shuffleVideoOnly = _settings.ShuffleVideoOnly;
        _playbackModeFlyout = new MenuFlyout();
        _listLoopModeItem = CreatePlaybackModeItem("列表循环", PlaybackMode.ListLoop);
        _singleLoopModeItem = CreatePlaybackModeItem("单曲循环", PlaybackMode.SingleLoop);
        _shuffleModeItem = CreatePlaybackModeItem("随机播放", PlaybackMode.Shuffle);
        _shuffleVideoOnlyMenuItem = new ToggleMenuFlyoutItem
        {
            Text = "随机播放只播放视频",
            IsChecked = _shuffleVideoOnly
        };
        _autoAdvanceMenuItem = new ToggleMenuFlyoutItem
        {
            Text = "是否自动切换下一个媒体",
            IsChecked = _autoAdvanceEnabled
        };
        _watchdog = new PlaybackWatchdogController(
            IsCurrentMediaSourceActive,
            IsDisposed,
            VerifyPlaybackStartedOnUiThread,
            TryRevealVideoSurfaceOnUiThreadAsync,
            LogPlaybackTrace);
        _shuffle = new ShufflePlaybackController(() => _shuffleVideoOnly, LogPlaybackTrace);
        _telemetry = new PlaybackSessionTelemetry(() => _libraryViewModel.SelectedMedia);
        _progressPreview = new ProgressPreviewController(
            _libraryViewModel,
            _transportBarView,
            IsDisposed,
            () => _isTransportSuppressed || _player == null,
            GetCurrentVideoDuration,
            displayTime => _displayToPlaybackPositionOverride?.Invoke(displayTime) ?? displayTime,
            FormatTime,
            EnqueueSafeAsync);
        _progressSliderPointerPressedHandler = ProgressSlider_PointerPressed;
        _progressSliderPointerReleasedHandler = ProgressSlider_PointerReleased;

        _transportBarView.ProgressSlider.Loaded += ProgressSlider_Loaded;
        _transportBarView.ProgressSlider.KeyDown += ProgressSlider_KeyDown;
        _transportBarView.ProgressSlider.ValueChanged += ProgressSlider_ValueChanged;
        _transportBarView.ProgressSlider.PointerEntered += ProgressSlider_PointerEntered;
        _transportBarView.ProgressSlider.PointerMoved += ProgressSlider_PointerMoved;
        _transportBarView.ProgressSlider.PointerExited += ProgressSlider_PointerExited;
        _transportBarView.ProgressSlider.PointerCanceled += ProgressSlider_PointerCanceled;
        _transportBarView.PlayPauseButton.Click += PlayPauseButton_Click;
        _transportBarView.VolumeButton.Click += VolumeButton_Click;
        _transportBarView.VolumeFlyoutPopup.Opened += VolumeFlyoutPopup_Opened;
        _transportBarView.VolumeFlyoutPopup.Closed += VolumeFlyoutPopup_Closed;
        _transportBarView.VolumeButton.PointerWheelChanged += VolumeInteraction_PointerWheelChanged;
        _transportBarView.VolumeFlyoutContent.PointerWheelChanged += VolumeInteraction_PointerWheelChanged;
        _transportBarView.VolumeSlider.PointerWheelChanged += VolumeInteraction_PointerWheelChanged;
        _transportBarView.VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;
        _navigationOverlayView.PlaybackModeButton.Click += PlaybackModeButton_Click;
        _navigationOverlayView.FullScreenButton.Click += FullScreenButton_Click;
        _transportBarView.ControlBar.PointerMoved += ControlBar_PointerMoved;
        _transportBarView.ControlBar.PointerPressed += ControlBar_PointerPressed;

        _playbackTimer.Interval = TimeSpan.FromMilliseconds(250);
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _controlsHideTimer.Interval = TimeSpan.FromSeconds(2.5);
        _controlsHideTimer.Tick += ControlsHideTimer_Tick;
        _uiHeartbeatTimer.Interval = TimeSpan.FromMilliseconds(500);
        _uiHeartbeatTimer.Tick += UiHeartbeatTimer_Tick;
        _lastUiHeartbeatTimestamp = Stopwatch.GetTimestamp();
        _uiHeartbeatTimer.Start();
        _shuffleVideoOnlyMenuItem.Click += ShuffleVideoOnlyMenuItem_Click;
        _autoAdvanceMenuItem.Click += AutoAdvanceMenuItem_Click;

        _playbackModeFlyout.Items.Add(_listLoopModeItem);
        _playbackModeFlyout.Items.Add(_singleLoopModeItem);
        _playbackModeFlyout.Items.Add(_shuffleModeItem);
        _playbackModeFlyout.Items.Add(new MenuFlyoutSeparator());
        _playbackModeFlyout.Items.Add(_shuffleVideoOnlyMenuItem);
        _playbackModeFlyout.Items.Add(_autoAdvanceMenuItem);
        LogPlaybackTrace($"Playback mode restored. Mode={_playbackMode}, ShuffleVideoOnly={_shuffleVideoOnly}.");
        UpdatePlaybackModeUi();
        UpdateFullScreenButtonUi();
        UpdateVolumeButtonUi();
        SetPassiveState(showImageNavigation: false);
    }

    public bool AreControlsVisible => _areControlsVisible;

    public bool IsSeeking => _isSeeking;

    public bool IsPlaying => _player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

    public bool IsShuffleMode => _playbackMode == PlaybackMode.Shuffle;

    public void NotifySelectedMediaChanged(MediaItemViewModel? selectedMedia)
    {
        if (IsDisposed())
        {
            return;
        }

        var selectedId = selectedMedia?.Id;
        if (!string.IsNullOrWhiteSpace(_pendingInternalShuffleSelectionId)
            && string.Equals(_pendingInternalShuffleSelectionId, selectedId, StringComparison.Ordinal))
        {
            _pendingInternalShuffleSelectionId = null;
            return;
        }

        _pendingInternalShuffleSelectionId = null;
        ClearShuffleHistory("external-selection");
    }

    private static PlaybackActivityState MapPlaybackState(MediaPlaybackState state)
    {
        return state switch
        {
            MediaPlaybackState.Opening => PlaybackActivityState.Opening,
            MediaPlaybackState.Buffering => PlaybackActivityState.Buffering,
            MediaPlaybackState.Playing => PlaybackActivityState.Playing,
            _ => PlaybackActivityState.Inactive
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
        {
            return;
        }

        Interlocked.Increment(ref _mediaSourceVersion);
        LogPlaybackTrace("Dispose start.");
        _playbackLease.SetState(PlaybackActivityState.Inactive);
        _transportBarView.ProgressSlider.Loaded -= ProgressSlider_Loaded;
        _transportBarView.ProgressSlider.KeyDown -= ProgressSlider_KeyDown;
        _transportBarView.ProgressSlider.ValueChanged -= ProgressSlider_ValueChanged;
        _transportBarView.ProgressSlider.PointerEntered -= ProgressSlider_PointerEntered;
        _transportBarView.ProgressSlider.PointerMoved -= ProgressSlider_PointerMoved;
        _transportBarView.ProgressSlider.PointerExited -= ProgressSlider_PointerExited;
        _transportBarView.ProgressSlider.PointerCanceled -= ProgressSlider_PointerCanceled;
        _transportBarView.PlayPauseButton.Click -= PlayPauseButton_Click;
        _transportBarView.VolumeButton.Click -= VolumeButton_Click;
        _transportBarView.VolumeFlyoutPopup.Opened -= VolumeFlyoutPopup_Opened;
        _transportBarView.VolumeFlyoutPopup.Closed -= VolumeFlyoutPopup_Closed;
        _transportBarView.VolumeButton.PointerWheelChanged -= VolumeInteraction_PointerWheelChanged;
        _transportBarView.VolumeFlyoutContent.PointerWheelChanged -= VolumeInteraction_PointerWheelChanged;
        _transportBarView.VolumeSlider.PointerWheelChanged -= VolumeInteraction_PointerWheelChanged;
        _transportBarView.VolumeSlider.ValueChanged -= VolumeSlider_ValueChanged;
        _navigationOverlayView.PlaybackModeButton.Click -= PlaybackModeButton_Click;
        _navigationOverlayView.FullScreenButton.Click -= FullScreenButton_Click;
        _transportBarView.ControlBar.PointerMoved -= ControlBar_PointerMoved;
        _transportBarView.ControlBar.PointerPressed -= ControlBar_PointerPressed;
        if (_progressSliderHandlersAttached)
        {
            _transportBarView.ProgressSlider.RemoveHandler(UIElement.PointerPressedEvent, _progressSliderPointerPressedHandler);
            _transportBarView.ProgressSlider.RemoveHandler(UIElement.PointerReleasedEvent, _progressSliderPointerReleasedHandler);
            _transportBarView.ProgressSlider.PointerCaptureLost -= ProgressSlider_PointerCaptureLost;
            _progressSliderHandlersAttached = false;
        }

        _listLoopModeItem.Click -= PlaybackModeMenuItem_Click;
        _singleLoopModeItem.Click -= PlaybackModeMenuItem_Click;
        _shuffleModeItem.Click -= PlaybackModeMenuItem_Click;
        _shuffleVideoOnlyMenuItem.Click -= ShuffleVideoOnlyMenuItem_Click;
        _autoAdvanceMenuItem.Click -= AutoAdvanceMenuItem_Click;
        _playbackTimer.Stop();
        _controlsHideTimer.Stop();
        _uiHeartbeatTimer.Stop();
        _uiHeartbeatTimer.Tick -= UiHeartbeatTimer_Tick;
        _progressPreview.Dispose();

        var player = Interlocked.Exchange(ref _player, null);
        if (player != null)
        {
            DetachMediaPlayer(player);
            player.Dispose();
        }

        _playbackLease.Dispose();
        LogPlaybackTrace("Dispose completed.");
    }

    public void ShowVideo(MediaItemViewModel media, long selectionChangedTimestamp = 0, int switchTraceId = 0)
    {
        if (IsDisposed())
        {
            return;
        }

        var mediaSourceVersion = Interlocked.Increment(ref _mediaSourceVersion);
        _playbackLease.SetState(PlaybackActivityState.Opening);
        var player = ReplaceMediaPlayerForNewSource();
        var showVideoStartTimestamp = Stopwatch.GetTimestamp();
        _telemetry.BeginSwitch(showVideoStartTimestamp, mediaSourceVersion, selectionChangedTimestamp, switchTraceId);
        Interlocked.Exchange(ref _failedMediaSourceVersion, 0);
        Interlocked.Exchange(ref _handledMediaFailedSourceVersion, 0);
        LogPlaybackTrace($"ShowVideo file={media.FileName} path={media.FileSystemPath} sourceVersion={mediaSourceVersion} {DescribeMediaPlaybackDiagnostics(media)}");
        _telemetry.LogSwitchTrace(
            mediaSourceVersion,
            "ShowVideoStart",
            $"SelectedToShowVideoMs={PlaybackSessionTelemetry.FormatElapsedMilliseconds(selectionChangedTimestamp)}, PendingPlayerDisposal={Volatile.Read(ref _pendingPlayerDisposalCount)}, ReusePlayer={FeatureFlags.ReusePlayerEnabled}.");
        _progressPreview.ResetForMedia(media.Id);
        ShowVideoLoadingPreview(media, mediaSourceVersion);
        _videoPlayer.Visibility = Visibility.Visible;
        _videoPlayer.Opacity = 0;
        _videoPlayer.InvalidateMeasure();
        _videoViewportView.InvalidateMeasure();
        LogPlaybackTrace($"Assigning MediaPlayer source. SourceVersion={mediaSourceVersion}, UriScheme='{new Uri(media.FileSystemPath).Scheme}', PathExists={File.Exists(media.FileSystemPath)}.");
        var sourceAssignStartTimestamp = Stopwatch.GetTimestamp();
        player.Source = MediaSource.CreateFromUri(new Uri(media.FileSystemPath));
        _telemetry.MarkSourceAssigned();
        _telemetry.LogSwitchTrace(
            mediaSourceVersion,
            "SourceAssigned",
            $"SelectedToSourceAssignedMs={PlaybackSessionTelemetry.FormatElapsedMilliseconds(selectionChangedTimestamp)}, ShowVideoToSourceAssignedMs={Stopwatch.GetElapsedTime(showVideoStartTimestamp).TotalMilliseconds:0.###}, SourceAssignSyncMs={Stopwatch.GetElapsedTime(sourceAssignStartTimestamp).TotalMilliseconds:0.###}.");
        RequestPlaybackStart(mediaSourceVersion, "ShowVideo");
        SetVideoState();
        ShowMediaSwitchControls();
        _focusHost();
    }

    public void PauseAndClearSource()
    {
        Interlocked.Increment(ref _mediaSourceVersion);
        LogPlaybackTrace("PauseAndClearSource invoked.");
        _playbackLease.SetState(PlaybackActivityState.Inactive);
        if (IsDisposed())
        {
            return;
        }

        if (_player == null)
        {
            return;
        }

        _player.Pause();
        _player.Source = null;
    }

    public void Clear()
    {
        if (IsDisposed())
        {
            return;
        }

        LogPlaybackTrace("Clear invoked.");
        PauseAndClearSource();
        CloseVolumeFlyout();
        _progressPreview.Clear();
        _videoPlayer.Visibility = Visibility.Collapsed;
        _videoPlayer.Opacity = 1;
        HideVideoLoadingPreview();
        _lastPlaybackPosition = TimeSpan.Zero;
        _lastPlaybackDuration = TimeSpan.Zero;
        _currentTimeTextOverride = null;
        _totalTimeTextOverride = null;
        _progressValueOverrideSeconds = null;
        _progressMaximumOverrideSeconds = null;
        _displayToPlaybackPositionOverride = null;
        UpdateTimeDisplay();
        SetPassiveState(showImageNavigation: false);
        _notifyPlaybackProgressChanged();
    }

    public void ShowImageState()
    {
        if (IsDisposed())
        {
            return;
        }

        LogPlaybackTrace("ShowImageState invoked.");
        PauseAndClearSource();
        CloseVolumeFlyout();
        _progressPreview.Clear();
        _videoPlayer.Visibility = Visibility.Collapsed;
        _videoPlayer.Opacity = 1;
        HideVideoLoadingPreview();
        SetPassiveState(showImageNavigation: true);
    }

    public void ShowControls()
    {
        if (IsDisposed())
        {
            return;
        }

        if (_isTransportSuppressed)
        {
            _areControlsVisible = true;
            _refreshNavigationHotspots();
            _controlsHideTimer.Stop();
            return;
        }

        if (_viewModel.ControlBarVisibility != Visibility.Visible)
        {
            return;
        }

        _viewModel.ControlBarOpacity = 1;
        _transportBarView.ControlBar.IsHitTestVisible = true;
        _areControlsVisible = true;
        SetProgressInteractionExpanded(_isPointerInTransportZone || _isPointerOverProgressInteraction);
        _refreshNavigationHotspots();
        if (_isPointerInTransportZone)
        {
            _controlsHideTimer.Stop();
        }
        else
        {
            RestartControlsHideTimer();
        }
    }

    public void HandlePlayerPointerMoved(double pointerY, double playerHeight)
    {
        if (IsDisposed() || playerHeight <= 0)
        {
            return;
        }

        var isInTransportZone = pointerY >= Math.Max(0, playerHeight - TransportPointerZoneHeight);
        if (_isPointerInTransportZone == isInTransportZone)
        {
            if (isInTransportZone)
            {
                _controlsHideTimer.Stop();
            }
            else if (_areControlsVisible && !_isSeeking && !_viewModel.IsVolumeFlyoutOpen)
            {
                ShowCompactProgressOnly();
            }

            return;
        }

        _isPointerInTransportZone = isInTransportZone;
        if (isInTransportZone)
        {
            ShowControls();
            SetProgressInteractionExpanded(true);
            _controlsHideTimer.Stop();
            return;
        }

        if (!_isSeeking && !_viewModel.IsVolumeFlyoutOpen)
        {
            ShowCompactProgressOnly();
        }
    }

    public void ShowMediaSwitchControls()
    {
        if (IsDisposed())
        {
            return;
        }

        if (_isPointerInTransportZone || _isPointerOverProgressInteraction)
        {
            ShowControls();
            SetProgressInteractionExpanded(true);
            return;
        }

        ShowCompactProgressOnly();
    }

    public void HandlePointerExited()
    {
        if (IsDisposed())
        {
            return;
        }

        _isPointerInTransportZone = false;
        if (!_isSeeking && !_viewModel.IsVolumeFlyoutOpen)
        {
            ShowCompactProgressOnly();
        }
    }

    public void HideControlsImmediately()
    {
        if (IsDisposed())
        {
            return;
        }

        if (_isTransportSuppressed)
        {
            _controlsHideTimer.Stop();
            _areControlsVisible = true;
            _refreshNavigationHotspots();
            return;
        }

        if (_viewModel.ControlBarVisibility != Visibility.Visible)
        {
            return;
        }

        CloseVolumeFlyout();
        _viewModel.ControlBarOpacity = 0;
        _transportBarView.ControlBar.IsHitTestVisible = false;
        _areControlsVisible = false;
        _progressPreview.Hide();
        _progressPreview.CancelFrameRequest();
        SetProgressInteractionExpanded(false);
        _refreshNavigationHotspots();
        _controlsHideTimer.Stop();
    }

    public void TogglePlayPause()
    {
        if (IsDisposed())
        {
            return;
        }

        var player = _player;
        if (player == null)
        {
            return;
        }

        if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            player.Pause();
            _playbackLease.SetState(PlaybackActivityState.Inactive);
        }
        else
        {
            player.Play();
            _playbackLease.SetState(PlaybackActivityState.Playing);
        }

        ShowControls();
    }

    public void SetTransportSuppressed(bool suppressed)
    {
        if (IsDisposed())
        {
            return;
        }

        if (_isTransportSuppressed == suppressed)
        {
            return;
        }

        _isTransportSuppressed = suppressed;
        CloseVolumeFlyout();
        if (suppressed)
        {
            _progressPreview.Hide();
            _progressPreview.CancelFrameRequest();
        }

        if (suppressed)
        {
            _viewModel.ControlBarVisibility = Visibility.Collapsed;
            _viewModel.ControlBarOpacity = 0;
            _transportBarView.ControlBar.IsHitTestVisible = false;
            _areControlsVisible = true;
            _controlsHideTimer.Stop();
            _refreshNavigationHotspots();
            return;
        }

        if (_videoPlayer.Visibility == Visibility.Visible)
        {
            SetVideoState();
            return;
        }

        SetPassiveState(showImageNavigation: false);
    }

    public void SeekRelative(double seconds)
    {
        if (IsDisposed())
        {
            return;
        }

        var player = _player;
        if (player == null)
        {
            return;
        }

        var next = player.PlaybackSession.Position + TimeSpan.FromSeconds(seconds);
        SeekTo(next);
    }

    public TimeSpan GetCurrentPlaybackPosition()
    {
        return _player?.PlaybackSession.Position ?? TimeSpan.Zero;
    }

    public TimeSpan GetCurrentVideoDuration()
    {
        return _player?.PlaybackSession.NaturalDuration ?? TimeSpan.Zero;
    }

    public void SeekTo(TimeSpan position)
    {
        if (IsDisposed())
        {
            return;
        }

        var player = _player;
        if (player == null)
        {
            LogPlaybackTrace($"SeekTo ignored because player is null requested={position.TotalSeconds:F3}s");
            return;
        }

        var requested = position;
        var duration = player.PlaybackSession.NaturalDuration;
        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }
        else if (duration > TimeSpan.Zero && position > duration)
        {
            position = duration;
        }

        player.PlaybackSession.Position = position;
        _lastSeekTimestamp = Stopwatch.GetTimestamp();
        _lastSeekTarget = position;
        var seekVersion = Interlocked.Increment(ref _pendingSeekVersion);
        LogPlaybackTraceSampled("seek-to", $"SeekTo requested={requested.TotalSeconds:F3}s applied={position.TotalSeconds:F3}s duration={duration.TotalSeconds:F3}s", TimeSpan.FromSeconds(2));
        AppTraceLogger.LogSampled(
            "VideoPlayback",
            "seek-request",
            $"mediaId={_libraryViewModel.SelectedMedia?.Id ?? "<null>"} Seek requested. SeekVersion={seekVersion}, Requested={requested.TotalSeconds:F3}s, Applied={position.TotalSeconds:F3}s, Duration={duration.TotalSeconds:F3}s, PlaybackState={player.PlaybackSession.PlaybackState}.",
            TimeSpan.FromMilliseconds(500));
        _lastPlaybackPosition = position;
        _lastPlaybackDuration = duration;
        UpdateTimeDisplay();
        _notifyPlaybackProgressChanged();
    }

    public void SetPlaybackDisplayOverride(
        string? currentTimeText,
        string? totalTimeText,
        double? progressValueSeconds,
        double? progressMaximumSeconds,
        Func<TimeSpan, TimeSpan>? displayToPlaybackPosition)
    {
        if (IsDisposed())
        {
            return;
        }

        _currentTimeTextOverride = string.IsNullOrWhiteSpace(currentTimeText) ? null : currentTimeText;
        _totalTimeTextOverride = string.IsNullOrWhiteSpace(totalTimeText) ? null : totalTimeText;
        _progressValueOverrideSeconds = progressValueSeconds;
        _progressMaximumOverrideSeconds = progressMaximumSeconds;
        _displayToPlaybackPositionOverride = displayToPlaybackPosition;
        AppTraceLogger.LogSampled(
            "VideoPlayback",
            "display-override",
            $"mediaId={_libraryViewModel.SelectedMedia?.Id ?? "<none>"} SetPlaybackDisplayOverride current={_currentTimeTextOverride ?? "<default>"} total={_totalTimeTextOverride ?? "<default>"} progressValue={_progressValueOverrideSeconds?.ToString("F3") ?? "<default>"} progressMax={_progressMaximumOverrideSeconds?.ToString("F3") ?? "<default>"} mapper={(_displayToPlaybackPositionOverride != null)}",
            TimeSpan.FromSeconds(5));
        UpdateTimeDisplay();
    }

    private void SetVideoState()
    {
        if (IsDisposed())
        {
            return;
        }

        if (_isTransportSuppressed)
        {
            _viewModel.ControlBarVisibility = Visibility.Collapsed;
            _viewModel.ControlBarOpacity = 0;
            _transportBarView.ControlBar.IsHitTestVisible = false;
            SetProgressInteractionExpanded(false);
            _areControlsVisible = true;
            _refreshNavigationHotspots();
            _controlsHideTimer.Stop();
            return;
        }

        _viewModel.ControlBarVisibility = Visibility.Visible;
        _viewModel.ControlBarOpacity = 1;
        _transportBarView.ControlBar.IsHitTestVisible = true;
        SetProgressInteractionExpanded(false);
        _areControlsVisible = true;
        _refreshNavigationHotspots();
        _isPointerInTransportZone = false;
        if (!_isSeeking && !_viewModel.IsVolumeFlyoutOpen)
        {
            ShowCompactProgressOnly();
        }
    }

    private void SetPassiveState(bool showImageNavigation)
    {
        if (IsDisposed())
        {
            return;
        }

        CloseVolumeFlyout();
        _progressPreview.Hide();
        _progressPreview.CancelFrameRequest();
        _viewModel.ControlBarVisibility = Visibility.Collapsed;
        _viewModel.ControlBarOpacity = 0;
        _transportBarView.ControlBar.IsHitTestVisible = false;
        SetProgressInteractionExpanded(false);
        _areControlsVisible = showImageNavigation;
        _refreshNavigationHotspots();
        _controlsHideTimer.Stop();
    }

    private bool ShouldAutoHideControls()
    {
        return !_isTransportSuppressed
            && _viewModel.ControlBarVisibility == Visibility.Visible
            && !_isPointerInTransportZone
            && !_isSeeking
            && !_viewModel.IsVolumeFlyoutOpen
            && _canAutoHideControls();
    }

    private void HideControls()
    {
        if (!ShouldAutoHideControls())
        {
            return;
        }

        HideControlsImmediately();
    }

    private void RestartControlsHideTimer()
    {
        _controlsHideTimer.Stop();
        if (!ShouldAutoHideControls())
        {
            return;
        }

        _controlsHideTimer.Start();
    }

    private MediaPlayer EnsureMediaPlayer()
    {
        ObjectDisposedException.ThrowIf(IsDisposed(), this);

        if (_player != null)
        {
            return _player;
        }

        _player = CreateMediaPlayer();
        _videoPlayer.SetMediaPlayer(_player);
        _playbackTimer.Start();
        return _player;
    }

    private MediaPlayer ReplaceMediaPlayerForNewSource()
    {
        ObjectDisposedException.ThrowIf(IsDisposed(), this);

        // WP-07 Step B: controlled player reuse behind a feature flag. Reuses the
        // same MediaPlayer and only swaps the source, avoiding native decoder /
        // GPU resource churn. Off by default; the replace-per-source strategy
        // below remains the verified default.
        if (FeatureFlags.ReusePlayerEnabled && _player != null)
        {
            var reused = _player;
            try
            {
                reused.Pause();
                reused.Source = null;
            }
            catch (Exception ex)
            {
                AppTraceLogger.LogException("VideoPlayback", "Failed to reset reused MediaPlayer source.", ex);
            }

            AppTraceLogger.LogSampled(
                "VideoPlayback",
                "player-reused",
                "Reused existing MediaPlayer for new source (ReusePlayer strategy).",
                TimeSpan.FromSeconds(2));
            return reused;
        }

        var oldPlayer = _player;
        if (oldPlayer != null)
        {
            try
            {
                // Synchronously stop the outgoing player before scheduling its
                // disposal so its decoder/session releases promptly and stale
                // events stop arriving.
                oldPlayer.Pause();
                oldPlayer.Source = null;
            }
            catch (Exception ex)
            {
                AppTraceLogger.LogException("VideoPlayback", "Failed to pause old MediaPlayer while replacing source.", ex);
            }

            DetachMediaPlayer(oldPlayer);
        }

        var player = CreateMediaPlayer();
        var exchangedPlayer = Interlocked.Exchange(ref _player, player);
        if (exchangedPlayer != null && !ReferenceEquals(exchangedPlayer, oldPlayer))
        {
            oldPlayer = exchangedPlayer;
            DetachMediaPlayer(oldPlayer);
        }

        _videoPlayer.SetMediaPlayer(player);
        _playbackTimer.Start();

        if (oldPlayer != null)
        {
            var pending = Interlocked.Increment(ref _pendingPlayerDisposalCount);
            if (pending > 1)
            {
                AppTraceLogger.LogSampled(
                    "VideoPlayback",
                    "pending-player-disposal-backlog",
                    $"Pending MediaPlayer disposal backlog detected. PendingDisposalCount={pending}.",
                    TimeSpan.FromSeconds(2));
            }

            var playerToDispose = oldPlayer;
            _dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (ReferenceEquals(playerToDispose, Volatile.Read(ref _player)))
                {
                    Interlocked.Decrement(ref _pendingPlayerDisposalCount);
                    return;
                }

                try
                {
                    playerToDispose.Dispose();
                }
                catch (Exception ex)
                {
                    AppTraceLogger.LogException("VideoPlayback", "Failed to dispose old MediaPlayer after source replacement.", ex);
                }
                finally
                {
                    Interlocked.Decrement(ref _pendingPlayerDisposalCount);
                }
            });
        }

        return player;
    }

    private MediaPlayer CreateMediaPlayer()
    {
        var player = new MediaPlayer();
        player.MediaOpened += Player_MediaOpened;
        player.MediaFailed += Player_MediaFailed;
        player.MediaEnded += Player_MediaEnded;
        player.PlaybackSession.PlaybackStateChanged += PlaybackSession_PlaybackStateChanged;
        player.Volume = _transportBarView.VolumeSlider.Value;
        return player;
    }

    private void DetachMediaPlayer(MediaPlayer player)
    {
        player.MediaOpened -= Player_MediaOpened;
        player.MediaFailed -= Player_MediaFailed;
        player.MediaEnded -= Player_MediaEnded;
        player.PlaybackSession.PlaybackStateChanged -= PlaybackSession_PlaybackStateChanged;
    }

    private void UpdatePlayPauseState()
    {
        if (_player == null)
        {
            return;
        }

        var isPlaying = _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
        SetButtonGlyph(_transportBarView.PlayPauseButton, isPlaying ? "\uE769" : "\uE768");

        ToolTipService.SetToolTip(_transportBarView.PlayPauseButton, isPlaying ? "暂停" : "播放");
    }

    private void UpdatePlaybackModeUi()
    {
        var (glyph, description) = _playbackMode switch
        {
            PlaybackMode.ListLoop => ("\uE8EE", "播放模式：列表循环"),
            PlaybackMode.SingleLoop => ("\uE8ED", "播放模式：单曲循环"),
            _ => ("\uE8B1", _shuffleVideoOnly ? "播放模式：随机播放（仅视频）" : "播放模式：随机播放")
        };

        SetButtonGlyph(_navigationOverlayView.PlaybackModeButton, glyph);
        ToolTipService.SetToolTip(_navigationOverlayView.PlaybackModeButton, description);
        AutomationProperties.SetName(_navigationOverlayView.PlaybackModeButton, description);
        AutomationProperties.SetHelpText(_navigationOverlayView.PlaybackModeButton, description);
        _listLoopModeItem.IsChecked = _playbackMode == PlaybackMode.ListLoop;
        _singleLoopModeItem.IsChecked = _playbackMode == PlaybackMode.SingleLoop;
        _shuffleModeItem.IsChecked = _playbackMode == PlaybackMode.Shuffle;
        _shuffleVideoOnlyMenuItem.IsChecked = _shuffleVideoOnly;
        _autoAdvanceMenuItem.IsChecked = _autoAdvanceEnabled;
    }

    private void UpdateVolumeButtonUi()
    {
        var volume = _transportBarView.VolumeSlider.Value;
        var isMuted = volume <= 0.001;
        if (_transportBarView.VolumeButton.Content is FontIcon icon)
        {
            icon.Glyph = isMuted ? "\uE74F" : "\uE767";
        }

        var percentage = (int)Math.Round(volume * 100);
        ToolTipService.SetToolTip(_transportBarView.VolumeButton, $"音量：{percentage}%（点击调节）");
    }

    private void UpdateFullScreenButtonUi()
    {
        var description = _viewModel.IsFullScreen ? "退出全屏（当前全屏）" : "全屏（当前窗口）";
        SetButtonGlyph(_navigationOverlayView.FullScreenButton, _viewModel.IsFullScreen ? "\uE73F" : "\uE740");
        ToolTipService.SetToolTip(_navigationOverlayView.FullScreenButton, description);
        AutomationProperties.SetName(_navigationOverlayView.FullScreenButton, description);
        AutomationProperties.SetHelpText(_navigationOverlayView.FullScreenButton, description);
    }

    private void ProgressSlider_Loaded(object sender, RoutedEventArgs e)
    {
        if (IsDisposed())
        {
            return;
        }

        EnsureProgressSliderHandlers();
    }

    private void EnsureProgressSliderHandlers()
    {
        if (IsDisposed() || _progressSliderHandlersAttached)
        {
            return;
        }

        _transportBarView.ProgressSlider.AddHandler(UIElement.PointerPressedEvent, _progressSliderPointerPressedHandler, true);
        _transportBarView.ProgressSlider.AddHandler(UIElement.PointerReleasedEvent, _progressSliderPointerReleasedHandler, true);
        _transportBarView.ProgressSlider.PointerCaptureLost += ProgressSlider_PointerCaptureLost;
        _progressSliderHandlersAttached = true;
    }

    private void ProgressSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (IsDisposed())
        {
            return;
        }

        _isSeeking = true;
        ShowControls();
    }

    private void ProgressSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (IsDisposed())
        {
            return;
        }

        CompleteSliderSeek();
    }

    private void ProgressSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (IsDisposed())
        {
            return;
        }

        if (_isSeeking)
        {
            CompleteSliderSeek();
        }
    }

    private void CompleteSliderSeek()
    {
        if (IsDisposed() || !_isSeeking)
        {
            return;
        }

        SeekToSlider();
        _isSeeking = false;
        UpdatePlayPauseState();
        _focusHost();
        ShowControls();
    }

    private async void ProgressSlider_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        try
        {
            if (IsDisposed())
            {
                return;
            }

            if (_libraryViewModel.SelectedMedia == null)
            {
                return;
            }

            if (e.Key == Windows.System.VirtualKey.Left)
            {
                await _navigateRelativeAsync(-1);
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Right)
            {
                await _navigateRelativeAsync(1);
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Up && _libraryViewModel.SelectedMedia.Type == MediaType.Video)
            {
                SeekRelative(-5);
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Down && _libraryViewModel.SelectedMedia.Type == MediaType.Video)
            {
                SeekRelative(5);
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.PageUp)
            {
                await _navigateRelativeAsync(-1);
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.PageDown)
            {
                await _navigateRelativeAsync(1);
                e.Handled = true;
            }

            if (!e.Handled)
            {
                return;
            }

            _focusHost();
            ShowControls();
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("VideoPlayback", $"ProgressSlider_KeyDown failed. Key={e.Key}.", ex);
        }
    }

    private void SeekToSlider()
    {
        var displayTarget = TimeSpan.FromSeconds(_transportBarView.ProgressSlider.Value);
        var target = _displayToPlaybackPositionOverride?.Invoke(displayTarget) ?? displayTarget;
        SeekTo(target);
    }

    private void ProgressSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (IsDisposed() || _player == null || !_isSeeking)
        {
            return;
        }

        UpdateSliderSeekPreview();
    }

    private void UpdateSliderSeekPreview()
    {
        var displayTarget = TimeSpan.FromSeconds(_transportBarView.ProgressSlider.Value);
        _viewModel.CurrentTimeText = FormatTime(displayTarget);
    }

    private void ProgressSlider_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverProgressInteraction = true;
        ShowControls();
        SetProgressInteractionExpanded(true);
        _progressPreview.UpdateFromPointer(e);
    }

    private void ProgressSlider_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        _progressPreview.UpdateFromPointer(e);
    }

    private void ProgressSlider_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverProgressInteraction = false;
        _progressPreview.Hide();
        _progressPreview.CancelFrameRequest();
    }

    private void ProgressSlider_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverProgressInteraction = false;
        _progressPreview.Hide();
        _progressPreview.CancelFrameRequest();
    }

    private void SetProgressInteractionExpanded(bool expanded)
    {
        var canShowProgress = _viewModel.ControlBarVisibility == Visibility.Visible
            && !_isTransportSuppressed
            && _libraryViewModel.SelectedMedia?.Type == MediaType.Video;
        var compactVisibility = canShowProgress && !expanded
            ? Visibility.Visible
            : Visibility.Collapsed;
        var sliderVisibility = canShowProgress && expanded ? Visibility.Visible : Visibility.Collapsed;
        var fullVisibility = canShowProgress && expanded ? Visibility.Visible : Visibility.Collapsed;
        _transportBarView.CompactProgressHost.Visibility = compactVisibility;
        _transportBarView.CurrentTimeText.Visibility = fullVisibility;
        _transportBarView.ProgressSlider.Visibility = sliderVisibility;
        _transportBarView.ProgressSlider.Opacity = expanded ? 1 : 0.15;
        _transportBarView.TotalTimeText.Visibility = Visibility.Collapsed;
        if (expanded)
        {
            _transportBarView.ProgressSlider.UpdateLayout();
        }
    }

    private void KeepControlsVisible()
    {
        if (_isTransportSuppressed)
        {
            _areControlsVisible = true;
            _refreshNavigationHotspots();
            _controlsHideTimer.Stop();
            return;
        }

        if (_viewModel.ControlBarVisibility != Visibility.Visible)
        {
            return;
        }

        _viewModel.ControlBarOpacity = 1;
        _transportBarView.ControlBar.IsHitTestVisible = true;
        _areControlsVisible = true;
        _refreshNavigationHotspots();
        RestartControlsHideTimer();
    }

    private void ShowCompactProgressOnly()
    {
        if (_isTransportSuppressed)
        {
            _areControlsVisible = true;
            _refreshNavigationHotspots();
            _controlsHideTimer.Stop();
            return;
        }

        if (_viewModel.ControlBarVisibility != Visibility.Visible)
        {
            return;
        }

        CloseVolumeFlyout();
        _viewModel.ControlBarOpacity = 0;
        _transportBarView.ControlBar.IsHitTestVisible = false;
        _areControlsVisible = false;
        SetProgressInteractionExpanded(false);
        _progressPreview.Hide();
        _progressPreview.CancelFrameRequest();
        _refreshNavigationHotspots();
        _controlsHideTimer.Stop();
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (IsDisposed())
        {
            return;
        }

        if (_transportBarView.VolumeSlider.Value > 0.001)
        {
            _lastNonZeroVolume = _transportBarView.VolumeSlider.Value;
        }

        if (_player != null)
        {
            _player.Volume = _transportBarView.VolumeSlider.Value;
        }

        UpdateVolumeButtonUi();
    }

    private void VolumeButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsDisposed())
        {
            return;
        }

        if (_viewModel.IsVolumeFlyoutOpen)
        {
            CloseVolumeFlyout();
        }
        else
        {
            ShowVolumeFlyout();
        }

        ShowControls();
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        TogglePlayPause();
    }

    private void PlaybackModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsDisposed())
        {
            return;
        }

        CloseVolumeFlyout();
        _playbackModeFlyout.ShowAt(_navigationOverlayView.PlaybackModeButton);
        ShowControls();
    }

    private RadioMenuFlyoutItem CreatePlaybackModeItem(string text, PlaybackMode mode)
    {
        var item = new RadioMenuFlyoutItem
        {
            Text = text,
            GroupName = "PlaybackMode",
            Tag = mode
        };
        item.Click += PlaybackModeMenuItem_Click;
        return item;
    }

    private void PlaybackModeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (IsDisposed())
        {
            return;
        }

        if (sender is not RadioMenuFlyoutItem item || item.Tag is not PlaybackMode mode || _playbackMode == mode)
        {
            return;
        }

        _playbackMode = mode;
        _settings.SetPlaybackMode(_playbackMode.ToString());
        ClearShuffleHistory("playback-mode-changed");
        LogPlaybackTrace($"Playback mode changed. Mode={_playbackMode}.");
        UpdatePlaybackModeUi();
        ShowControls();
    }

    private static PlaybackMode ParsePlaybackMode(string value)
    {
        return Enum.TryParse<PlaybackMode>(value, ignoreCase: true, out var mode)
            ? mode
            : PlaybackMode.ListLoop;
    }

    private void AutoAdvanceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (IsDisposed())
        {
            return;
        }

        _autoAdvanceEnabled = _autoAdvanceMenuItem.IsChecked;
        _settings.SetPlaybackAutoAdvanceEnabled(_autoAdvanceEnabled);
        LogPlaybackTrace($"Auto advance changed. Enabled={_autoAdvanceEnabled}.");
        UpdatePlaybackModeUi();
        ShowControls();
    }

    private void ShuffleVideoOnlyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (IsDisposed())
        {
            return;
        }

        _shuffleVideoOnly = _shuffleVideoOnlyMenuItem.IsChecked;
        _settings.SetShuffleVideoOnly(_shuffleVideoOnly);
        ClearShuffleHistory("shuffle-video-only-changed");
        LogPlaybackTrace($"Shuffle video-only changed. Enabled={_shuffleVideoOnly}.");
        UpdatePlaybackModeUi();
        ShowControls();
    }

    private void FullScreenButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsDisposed())
        {
            return;
        }

        CloseVolumeFlyout();
        var appWindow = _getAppWindow();
        if (appWindow == null)
        {
            return;
        }

        if (_viewModel.IsFullScreen)
        {
            appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            _viewModel.IsFullScreen = false;
        }
        else
        {
            appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            _viewModel.IsFullScreen = true;
        }

        UpdateFullScreenButtonUi();
        ShowControls();
    }

    private void ControlBar_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (IsDisposed())
        {
            return;
        }

        KeepControlsVisible();
    }

    private void ControlBar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (IsDisposed())
        {
            return;
        }

        if (_viewModel.IsVolumeFlyoutOpen && !IsVolumeInteractionSource(e.OriginalSource as DependencyObject))
        {
            CloseVolumeFlyout();
        }

        ShowControls();
    }

    private void VolumeInteraction_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (IsDisposed())
        {
            return;
        }

        if (!_viewModel.IsVolumeFlyoutOpen)
        {
            return;
        }

        var source = sender as UIElement ?? _transportBarView.VolumeFlyoutContent;
        var delta = e.GetCurrentPoint(source).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        var nextValue = _transportBarView.VolumeSlider.Value + (delta > 0 ? VolumeWheelStep : -VolumeWheelStep);
        _transportBarView.VolumeSlider.Value = Math.Clamp(nextValue, 0, 1);
        ShowControls();
        e.Handled = true;
    }

    private void ControlsHideTimer_Tick(object? sender, object e)
    {
        if (IsDisposed())
        {
            return;
        }

        _controlsHideTimer.Stop();
        HideControls();
    }

    private void UiHeartbeatTimer_Tick(object? sender, object e)
    {
        if (IsDisposed())
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var previous = _lastUiHeartbeatTimestamp;
        _lastUiHeartbeatTimestamp = now;
        if (previous == 0)
        {
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(previous, now);
        if (elapsed <= TimeSpan.FromMilliseconds(900))
        {
            return;
        }

        AppTraceLogger.LogSampled(
            "UiPerf",
            "dispatcher-heartbeat-lag",
            $"Dispatcher heartbeat lag detected. ElapsedMs={elapsed.TotalMilliseconds:0}, SelectedMediaId='{_libraryViewModel.SelectedMedia?.Id ?? "<null>"}', PlayerState={_player?.PlaybackSession.PlaybackState.ToString() ?? "<null>"}, PlaybackActive={_playbackResources.IsPlaybackActive}.",
            TimeSpan.FromSeconds(2));
    }

    private void PlaybackTimer_Tick(object? sender, object e)
    {
        if (IsDisposed())
        {
            return;
        }

        var player = _player;
        if (player == null)
        {
            return;
        }

        var duration = player.PlaybackSession.NaturalDuration;
        if (duration.TotalSeconds <= 0)
        {
            return;
        }

        _lastPlaybackPosition = player.PlaybackSession.Position;
        _lastPlaybackDuration = duration;
        TryLogSeekRecovered(player.PlaybackSession.Position, duration, player.PlaybackSession.PlaybackState);

        UpdateTimeDisplay();
        _notifyPlaybackProgressChanged();
    }

    private void TryLogSeekRecovered(TimeSpan position, TimeSpan duration, MediaPlaybackState playbackState)
    {
        var seekVersion = Volatile.Read(ref _pendingSeekVersion);
        if (seekVersion == 0
            || seekVersion == Volatile.Read(ref _completedSeekVersion)
            || _lastSeekTimestamp == 0)
        {
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(_lastSeekTimestamp);
        var distanceFromTarget = Math.Abs((position - _lastSeekTarget).TotalSeconds);
        var recovered = playbackState == MediaPlaybackState.Playing
            && (distanceFromTarget <= 1.0 || position >= _lastSeekTarget);
        if (!recovered && elapsed < TimeSpan.FromSeconds(2))
        {
            return;
        }

        Interlocked.Exchange(ref _completedSeekVersion, seekVersion);
        AppTraceLogger.LogSampled(
            "VideoPlayback",
            "seek-recovered",
            $"mediaId={_libraryViewModel.SelectedMedia?.Id ?? "<null>"} Seek recovered. SeekVersion={seekVersion}, ElapsedMs={elapsed.TotalMilliseconds:0}, Target={_lastSeekTarget.TotalSeconds:F3}s, Position={position.TotalSeconds:F3}s, Duration={duration.TotalSeconds:F3}s, State={playbackState}, Recovered={recovered}.",
            TimeSpan.FromMilliseconds(500));
    }

    private void Player_MediaOpened(MediaPlayer sender, object args)
    {
        if (IsDisposed() || !ReferenceEquals(sender, _player))
        {
            return;
        }

        var duration = sender.PlaybackSession.NaturalDuration;
        var mediaSourceVersion = Volatile.Read(ref _mediaSourceVersion);
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (IsDisposed() || !ReferenceEquals(sender, _player) || !IsCurrentMediaSourceActive(mediaSourceVersion))
            {
                return;
            }

            LogPlaybackTrace(
                $"MediaOpened duration={duration.TotalSeconds:F3}s sourceVersion={mediaSourceVersion} OpenElapsedMs={_telemetry.GetSourceLoadElapsedMilliseconds(mediaSourceVersion):0} NaturalSize={sender.PlaybackSession.NaturalVideoWidth}x{sender.PlaybackSession.NaturalVideoHeight} SelectedId='{_libraryViewModel.SelectedMedia?.Id ?? "<null>"}'.");
            _telemetry.LogSwitchTrace(
                mediaSourceVersion,
                "MediaOpened",
                $"SelectedToMediaOpenedMs={PlaybackSessionTelemetry.FormatElapsedMilliseconds(_telemetry.SwitchSelectionChangedTimestamp)}, SourceAssignedToMediaOpenedMs={PlaybackSessionTelemetry.FormatElapsedMilliseconds(_telemetry.SourceAssignedTimestamp)}, OpenElapsedMs={_telemetry.GetSourceLoadElapsedMilliseconds(mediaSourceVersion):0.###}, NaturalSize={sender.PlaybackSession.NaturalVideoWidth}x{sender.PlaybackSession.NaturalVideoHeight}.");
            _lastPlaybackPosition = TimeSpan.Zero;
            _lastPlaybackDuration = duration;
            UpdateTimeDisplay();
            _handleMediaOpened(duration);
            _notifyPlaybackProgressChanged();
            RequestPlaybackStart(mediaSourceVersion, "MediaOpened");
                _watchdog.ScheduleVideoSurfaceReveal(mediaSourceVersion, "MediaOpened");
        });
    }

    private void Player_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        if (IsDisposed() || !ReferenceEquals(sender, _player))
        {
            return;
        }

        var mediaSourceVersion = Volatile.Read(ref _mediaSourceVersion);
        if (Interlocked.CompareExchange(ref _handledMediaFailedSourceVersion, mediaSourceVersion, 0) != 0)
        {
            LogPlaybackTrace($"Duplicate MediaFailed ignored. SourceVersion={mediaSourceVersion}, Error={args.Error}, ExtendedError=0x{args.ExtendedErrorCode.HResult:X8}.");
            return;
        }

        _dispatcherQueue.TryEnqueue(() =>
        {
            if (IsDisposed() || !ReferenceEquals(sender, _player) || mediaSourceVersion != Volatile.Read(ref _mediaSourceVersion))
            {
                return;
            }

            Interlocked.Exchange(ref _failedMediaSourceVersion, mediaSourceVersion);
            var selected = _libraryViewModel.SelectedMedia;
            LogPlaybackTrace(
                $"MediaFailed error={args.Error} extendedError=0x{args.ExtendedErrorCode.HResult:X8} message={args.ErrorMessage} file='{selected?.FileName ?? "<none>"}' path='{selected?.FileSystemPath ?? "<none>"}' SourceVersion={mediaSourceVersion} OpenElapsedMs={_telemetry.GetSourceLoadElapsedMilliseconds(mediaSourceVersion):0} {DescribeMediaPlaybackDiagnostics(selected)} CompatibilityHint='{GetPlaybackCompatibilityHint(selected, args)}'.");
            _playbackLease.SetState(PlaybackActivityState.Inactive);
            TryStopFailedPlaybackSource(sender, mediaSourceVersion);
            _videoPlayer.Visibility = Visibility.Visible;
            _videoPlayer.Opacity = 1;
            HideVideoLoadingPreview();
        });
    }

    private void Player_MediaEnded(MediaPlayer sender, object args)
    {
        if (IsDisposed() || !ReferenceEquals(sender, _player))
        {
            return;
        }

        var mediaSourceVersion = Volatile.Read(ref _mediaSourceVersion);
        EnqueueSafeAsync(async () =>
        {
            if (IsDisposed() || !ReferenceEquals(sender, _player) || mediaSourceVersion != Volatile.Read(ref _mediaSourceVersion))
            {
                return;
            }

            if (_libraryViewModel.SelectedMedia == null)
            {
                return;
            }

            if (!_autoAdvanceEnabled || _playbackMode == PlaybackMode.SingleLoop)
            {
                LogPlaybackTrace($"MediaEnded restarting current media. AutoAdvance={_autoAdvanceEnabled}, Mode={_playbackMode}.");
                sender.PlaybackSession.Position = TimeSpan.Zero;
                sender.Play();
                return;
            }

            if (_playbackMode == PlaybackMode.Shuffle)
            {
                if (!await TryNavigateShuffleAsync(1))
                {
                    if (IsDisposed() || !ReferenceEquals(sender, _player) || mediaSourceVersion != Volatile.Read(ref _mediaSourceVersion))
                    {
                        return;
                    }

                    LogPlaybackTrace("MediaEnded shuffle fallback restarting current media because no random candidate was available.");
                    sender.PlaybackSession.Position = TimeSpan.Zero;
                    sender.Play();
                }

                return;
            }

            LogPlaybackTrace("MediaEnded navigating to next media.");
            await _navigateRelativeAsync(1);
        }, $"MediaEnded handling failed. SourceVersion={mediaSourceVersion}.");
    }

    private void PlaybackSession_PlaybackStateChanged(MediaPlaybackSession sender, object args)
    {
        if (IsDisposed())
        {
            return;
        }

        var mediaSourceVersion = Volatile.Read(ref _mediaSourceVersion);
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (IsDisposed() || !IsCurrentMediaSourceActive(mediaSourceVersion))
            {
                return;
            }

            var player = _player;
            if (player == null || !ReferenceEquals(sender, player.PlaybackSession))
            {
                return;
            }

            LogPlaybackTrace($"PlaybackStateChanged state={sender.PlaybackState} position={sender.Position.TotalSeconds:F3}s duration={sender.NaturalDuration.TotalSeconds:F3}s");
            _playbackLease.SetState(MapPlaybackState(sender.PlaybackState));
            TryLogSeekRecovered(sender.Position, sender.NaturalDuration, sender.PlaybackState);
            if (sender.PlaybackState == MediaPlaybackState.Playing && _telemetry.TryMarkFirstPlaying())
            {
                var sourceVersion = mediaSourceVersion;
                LogPlaybackTrace(
                    $"Playback first playing. SourceVersion={sourceVersion}, FirstPlayingElapsedMs={_telemetry.GetSourceLoadElapsedMilliseconds(sourceVersion):0}, Position={sender.Position.TotalSeconds:F3}s, Duration={sender.NaturalDuration.TotalSeconds:F3}s, NaturalSize={sender.NaturalVideoWidth}x{sender.NaturalVideoHeight}.");
                _telemetry.LogSwitchTrace(
                    sourceVersion,
                    "FirstPlaying",
                    $"SelectedToFirstPlayingMs={PlaybackSessionTelemetry.FormatElapsedMilliseconds(_telemetry.SwitchSelectionChangedTimestamp)}, SourceAssignedToFirstPlayingMs={PlaybackSessionTelemetry.FormatElapsedMilliseconds(_telemetry.SourceAssignedTimestamp)}, FirstPlayingElapsedMs={_telemetry.GetSourceLoadElapsedMilliseconds(sourceVersion):0.###}, Position={sender.Position.TotalSeconds:F3}s, NaturalSize={sender.NaturalVideoWidth}x{sender.NaturalVideoHeight}.");
                _watchdog.ScheduleVideoSurfaceReveal(sourceVersion, "PlaybackStateChanged");
            }

            UpdatePlayPauseState();
        });
    }

    public async Task<bool> TryNavigateShuffleAsync(int offset)
    {
        if (IsDisposed() || _playbackMode != PlaybackMode.Shuffle)
        {
            return false;
        }

        if (offset < 0)
        {
            return await TryNavigateShuffleHistoryAsync(navigatingBack: true);
        }

        if (offset > 0 && await TryNavigateShuffleHistoryAsync(navigatingBack: false))
        {
            return true;
        }

        return offset > 0 && await TrySelectRandomMediaCoreAsync();
    }

    public Task<bool> TrySelectRandomMediaAsync()
    {
        return TrySelectRandomMediaCoreAsync();
    }

    private async Task<bool> TrySelectRandomMediaCoreAsync()
    {
        var current = _libraryViewModel.SelectedMedia;
        if (current == null)
        {
            LogPlaybackTraceSampled(
                "shuffle-random-unavailable",
                "Shuffle random skipped because current media is null.",
                TimeSpan.FromSeconds(2));
            return false;
        }

        var excludedIds = _shuffle.BuildRandomExcludedIds(current, includeHistory: true);
        var selected = await _libraryViewModel.GetRandomMediaAsync(_shuffleVideoOnly, excludedIds);
        if (selected == null && excludedIds.Count > 1)
        {
            excludedIds = _shuffle.BuildRandomExcludedIds(current, includeHistory: false);
            selected = await _libraryViewModel.GetRandomMediaAsync(_shuffleVideoOnly, excludedIds);
        }

        if (selected == null || string.Equals(selected.Id, current.Id, StringComparison.Ordinal))
        {
            LogPlaybackTraceSampled(
                "shuffle-random-empty",
                $"Shuffle random skipped because candidates are empty. CurrentId='{current.Id}', ExcludedCount={excludedIds.Count}, ShuffleVideoOnly={_shuffleVideoOnly}.",
                TimeSpan.FromSeconds(2));
            return false;
        }

        _shuffle.RecordRandomSelection(current);
        SetInternalShuffleSelection(selected);
        LogPlaybackTrace(
            $"Shuffle random selected. PreviousId='{current.Id}', SelectedId='{selected.Id}', ExcludedCount={excludedIds.Count}, BackCount={_shuffle.BackCount}, ForwardCount={_shuffle.ForwardCount}, ShuffleVideoOnly={_shuffleVideoOnly}.");
        return true;
    }

    private async Task<bool> TryNavigateShuffleHistoryAsync(bool navigatingBack)
    {
        var direction = navigatingBack ? "back" : "forward";
        var current = _libraryViewModel.SelectedMedia;
        if (current == null)
        {
            LogPlaybackTrace($"Shuffle history {direction} skipped because current media is null.");
            return false;
        }

        while ((navigatingBack ? _shuffle.BackCount : _shuffle.ForwardCount) > 0)
        {
            var hasTarget = navigatingBack
                ? _shuffle.TryPopBack(out var targetId)
                : _shuffle.TryPopForward(out targetId);
            if (!hasTarget)
            {
                break;
            }

            var target = await ResolveCurrentMediaAsync(targetId);
            if (target == null)
            {
                LogPlaybackTrace($"Shuffle history {direction} skipped stale item. TargetId='{targetId}'.");
                continue;
            }

            if (!_shuffle.IsHistoryEligible(target))
            {
                LogPlaybackTrace($"Shuffle history {direction} skipped ineligible item. TargetId='{targetId}', Type={target.Type}, ShuffleVideoOnly={_shuffleVideoOnly}.");
                continue;
            }

            if (string.Equals(target.Id, current.Id, StringComparison.Ordinal))
            {
                LogPlaybackTrace($"Shuffle history {direction} skipped current item. TargetId='{targetId}'.");
                continue;
            }

            _shuffle.RecordHistorySelection(current, navigatingBack);
            SetInternalShuffleSelection(target);
            LogPlaybackTrace(
                $"Shuffle history {direction} selected. PreviousId='{current.Id}', SelectedId='{target.Id}', BackCount={_shuffle.BackCount}, ForwardCount={_shuffle.ForwardCount}, ShuffleVideoOnly={_shuffleVideoOnly}.");
            return true;
        }

        LogPlaybackTraceSampled(
            $"shuffle-history-{direction}-empty",
            $"Shuffle history {direction} unavailable. CurrentId='{current.Id}', BackCount={_shuffle.BackCount}, ForwardCount={_shuffle.ForwardCount}.",
            TimeSpan.FromSeconds(2));
        return false;
    }

    private Task<MediaItemViewModel?> ResolveCurrentMediaAsync(string mediaId)
    {
        return _libraryViewModel.GetMediaByIdAsync(mediaId);
    }

    private bool IsShuffleCandidate(MediaItemViewModel item, MediaItemViewModel current)
    {
        return !string.Equals(item.Id, current.Id, StringComparison.Ordinal)
            && _shuffle.IsHistoryEligible(item);
    }

    private static string DescribeMediaPlaybackDiagnostics(MediaItemViewModel? media)
    {
        if (media == null)
        {
            return "MediaDiagnostics=<none>";
        }

        var path = media.FileSystemPath;
        var extension = Path.GetExtension(path);
        var exists = File.Exists(path);
        var actualSize = TryGetFileSize(path);
        var indexedSize = media.Media.Size;
        var sizeMismatch = exists && actualSize >= 0 && indexedSize > 0 && actualSize != indexedSize;
        var commonExtension = CommonWindowsPlaybackExtensions.Contains(extension);

        return $"MediaDiagnostics=(MediaId='{media.Id}', Extension='{extension}', CommonWindowsExtension={commonExtension}, PathExists={exists}, IndexedSize={indexedSize}, ActualSize={actualSize}, SizeMismatch={sizeMismatch}, IndexedDuration={media.Media.Duration?.ToString("F3") ?? "<null>"}, IndexedSizePx={media.Media.Width?.ToString() ?? "<null>"}x{media.Media.Height?.ToString() ?? "<null>"})";
    }

    private static string GetPlaybackCompatibilityHint(MediaItemViewModel? media, MediaPlayerFailedEventArgs args)
    {
        if (media == null)
        {
            return "No selected media when MediaFailed fired.";
        }

        var path = media.FileSystemPath;
        var extension = Path.GetExtension(path);
        if (!File.Exists(path))
        {
            return "File is missing on disk.";
        }

        if (!CommonWindowsPlaybackExtensions.Contains(extension))
        {
            return $"Extension '{extension}' is not in the common Windows MediaPlayer extension set; system codec support may be missing.";
        }

        if (args.Error == MediaPlayerError.SourceNotSupported)
        {
            return "Windows MediaPlayer reported SourceNotSupported; codec, container, or stream profile may not be supported by the system media pipeline.";
        }

        if (args.Error == MediaPlayerError.DecodingError)
        {
            return "Windows MediaPlayer reported DecodingError; the file may be corrupt or uses a codec/profile the installed decoder cannot decode.";
        }

        if (args.Error == MediaPlayerError.NetworkError)
        {
            return "Windows MediaPlayer reported NetworkError for a local source; verify path accessibility and storage health.";
        }

        return "Windows MediaPlayer failed; inspect ExtendedError and media diagnostics for codec/container clues.";
    }

    private static long TryGetFileSize(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : -1;
        }
        catch
        {
            return -1;
        }
    }

    private void SetInternalShuffleSelection(MediaItemViewModel media)
    {
        _pendingInternalShuffleSelectionId = media.Id;
        _libraryViewModel.SelectedMedia = media;
    }

    private void ClearShuffleHistory(string reason)
    {
        _shuffle.ClearHistory(reason);
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

    private void UpdateTimeDisplay()
    {
        _viewModel.CurrentTimeText = _currentTimeTextOverride ?? FormatTime(_lastPlaybackPosition);
        _viewModel.TotalTimeText = _totalTimeTextOverride ?? FormatTime(_lastPlaybackDuration);

        var progressMaximum = _progressMaximumOverrideSeconds ?? _lastPlaybackDuration.TotalSeconds;
        if (progressMaximum <= 0)
        {
            progressMaximum = 1;
        }

        _transportBarView.ProgressSlider.Maximum = progressMaximum;
        if (!_isSeeking)
        {
            var progressValue = _progressValueOverrideSeconds ?? _lastPlaybackPosition.TotalSeconds;
            _transportBarView.ProgressSlider.Value = Math.Clamp(progressValue, 0, progressMaximum);
        }
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

    private void ShowVolumeFlyout()
    {
        if (_transportBarView.VolumeFlyoutPopup.IsOpen)
        {
            _transportBarView.VolumeSlider.Focus(FocusState.Programmatic);
            return;
        }

        _transportBarView.VolumeFlyoutPopup.IsOpen = true;
    }

    private void ShowVideoLoadingPreview(MediaItemViewModel media, int mediaSourceVersion)
    {
        var hasCurrentThumbnail = media.Thumbnail != null;
        LogPlaybackTrace($"ShowVideoLoadingPreview hasCurrentThumbnail={hasCurrentThumbnail}");
        _videoViewportView.VideoLoadingPreviewImage.Source = media.Thumbnail;
        _videoViewportView.VideoLoadingPreviewHost.Visibility = Visibility.Visible;

        if (hasCurrentThumbnail)
        {
            return;
        }

        _ = LoadVideoLoadingPreviewThumbnailAsync(media, mediaSourceVersion);
    }

    private void HideVideoLoadingPreview()
    {
        LogPlaybackTrace("HideVideoLoadingPreview invoked.");
        _videoViewportView.VideoLoadingPreviewHost.Visibility = Visibility.Collapsed;
        _videoViewportView.VideoLoadingPreviewImage.Source = null;
    }

    private async Task LoadVideoLoadingPreviewThumbnailAsync(MediaItemViewModel media, int mediaSourceVersion)
    {
        try
        {
            await _libraryViewModel.EnsureThumbnailAsync(media);
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("VideoPlayback", $"mediaId={media.Id} Loading preview thumbnail failed.", ex);
            return;
        }

        _dispatcherQueue.TryEnqueue(() =>
        {
            if (IsDisposed()
                || mediaSourceVersion != Volatile.Read(ref _mediaSourceVersion)
                || !string.Equals(_libraryViewModel.SelectedMedia?.Id, media.Id, StringComparison.Ordinal)
                || media.Thumbnail == null)
            {
                return;
            }

            _videoViewportView.VideoLoadingPreviewImage.Source = media.Thumbnail;
            _videoViewportView.VideoLoadingPreviewHost.Visibility = Visibility.Visible;
            LogPlaybackTrace($"Video loading preview thumbnail applied. SourceVersion={mediaSourceVersion}, MediaId='{media.Id}'.");
        });
    }

    private void CloseVolumeFlyout()
    {
        if (!_transportBarView.VolumeFlyoutPopup.IsOpen && !_viewModel.IsVolumeFlyoutOpen)
        {
            return;
        }

        _transportBarView.VolumeFlyoutPopup.IsOpen = false;
    }

    private void VolumeFlyoutPopup_Opened(object? sender, object e)
    {
        _viewModel.IsVolumeFlyoutOpen = true;
        _transportBarView.VolumeSlider.Focus(FocusState.Programmatic);
        ShowControls();
    }

    private void VolumeFlyoutPopup_Closed(object? sender, object e)
    {
        _viewModel.IsVolumeFlyoutOpen = false;
        RestartControlsHideTimer();
    }

    private bool IsVolumeInteractionSource(DependencyObject? source)
    {
        while (source != null)
        {
            if (ReferenceEquals(source, _transportBarView.VolumeButton)
                || ReferenceEquals(source, _transportBarView.VolumeFlyoutPopup)
                || ReferenceEquals(source, _transportBarView.VolumeFlyoutContent)
                || ReferenceEquals(source, _transportBarView.VolumeSlider)
                || ReferenceEquals(source, _transportBarView.VolumeButtonHost))
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void LogPlaybackTrace(string message)
    {
        var mediaId = _libraryViewModel.SelectedMedia?.Id ?? "<none>";
        AppTraceLogger.Log("VideoPlayback", $"mediaId={mediaId} {message}");
    }

    private void LogPlaybackTraceSampled(string sampleKey, string message, TimeSpan minimumInterval)
    {
        var mediaId = _libraryViewModel.SelectedMedia?.Id ?? "<none>";
        AppTraceLogger.LogSampled("VideoPlayback", $"{sampleKey}:{mediaId}", $"mediaId={mediaId} {message}", minimumInterval);
    }

    private bool IsDisposed()
    {
        return Volatile.Read(ref _isDisposed) == 1;
    }

    private bool IsCurrentMediaSourceActive(int mediaSourceVersion)
    {
        return mediaSourceVersion == Volatile.Read(ref _mediaSourceVersion)
            && mediaSourceVersion != Volatile.Read(ref _failedMediaSourceVersion);
    }

    private void TryStopFailedPlaybackSource(MediaPlayer player, int mediaSourceVersion)
    {
        if (!ReferenceEquals(player, _player) || mediaSourceVersion != Volatile.Read(ref _mediaSourceVersion))
        {
            return;
        }

        try
        {
            player.Pause();
            player.Source = null;
            LogPlaybackTrace($"Failed media source stopped. SourceVersion={mediaSourceVersion}.");
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("VideoPlayback", $"Failed to stop failed media source. SourceVersion={mediaSourceVersion}.", ex);
        }
    }

    private void EnqueueSafeAsync(Func<Task> action, string failureMessage)
    {
        ArgumentNullException.ThrowIfNull(action);

        var queued = _dispatcherQueue.TryEnqueue(() =>
        {
            _ = RunSafelyAsync(action, failureMessage);
        });

        if (!queued)
        {
            AppTraceLogger.Log("VideoPlayback", $"DispatcherQueue rejected async callback. {failureMessage}");
        }
    }

    private static async Task RunSafelyAsync(Func<Task> action, string failureMessage)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("VideoPlayback", failureMessage, ex);
        }
    }

    private void RequestPlaybackStart(int mediaSourceVersion, string reason)
    {
        if (!IsCurrentMediaSourceActive(mediaSourceVersion))
        {
            return;
        }

        var player = _player;
        if (player == null)
        {
            return;
        }

        try
        {
            player.Play();
            LogPlaybackTrace($"Playback start requested. SourceVersion={mediaSourceVersion}, Reason={reason}.");
            _watchdog.SchedulePlaybackStartWatchdog(mediaSourceVersion);
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("VideoPlayback", $"mediaId={_libraryViewModel.SelectedMedia?.Id ?? "<none>"} Playback start failed. SourceVersion={mediaSourceVersion}, Reason={reason}.", ex);
        }
    }

    private void VerifyPlaybackStartedOnUiThread(int mediaSourceVersion)
    {
        var queued = _dispatcherQueue.TryEnqueue(() =>
        {
            if (IsDisposed() || !IsCurrentMediaSourceActive(mediaSourceVersion))
            {
                return;
            }

            var player = _player;
            if (player == null)
            {
                return;
            }

            var session = player.PlaybackSession;
            var position = session.Position;
            var state = session.PlaybackState;
            LogPlaybackTrace($"Playback watchdog. SourceVersion={mediaSourceVersion}, State={state}, Position={position.TotalSeconds:F3}s, Duration={session.NaturalDuration.TotalSeconds:F3}s, Width={session.NaturalVideoWidth}, Height={session.NaturalVideoHeight}.");
            if (state == MediaPlaybackState.Playing)
            {
                return;
            }

            try
            {
                player.Play();
                LogPlaybackTrace($"Playback watchdog retried Play. SourceVersion={mediaSourceVersion}, PreviousState={state}.");
            }
            catch (Exception ex)
            {
                AppTraceLogger.LogException("VideoPlayback", $"mediaId={_libraryViewModel.SelectedMedia?.Id ?? "<none>"} Playback watchdog retry failed. SourceVersion={mediaSourceVersion}, PreviousState={state}.", ex);
            }
        });

        if (!queued)
        {
            LogPlaybackTrace($"Playback watchdog dispatcher rejected. SourceVersion={mediaSourceVersion}.");
        }
    }

    private Task<bool> TryRevealVideoSurfaceOnUiThreadAsync(int mediaSourceVersion, int attempt, bool isLastAttempt)
    {
        var completion = new TaskCompletionSource<bool>();
        var queued = _dispatcherQueue.TryEnqueue(() =>
        {
            if (!IsCurrentMediaSourceActive(mediaSourceVersion))
            {
                completion.TrySetResult(true);
                return;
            }

            if (IsDisposed())
            {
                completion.TrySetResult(true);
                return;
            }

            var player = _player;
            var session = player?.PlaybackSession;
            var playbackState = session?.PlaybackState ?? MediaPlaybackState.None;
            var videoWidth = session?.NaturalVideoWidth ?? 0;
            var videoHeight = session?.NaturalVideoHeight ?? 0;
            var isReady = playbackState == MediaPlaybackState.Playing && videoWidth > 0 && videoHeight > 0;
            if (!isReady && !isLastAttempt)
            {
                LogPlaybackTrace($"RevealVideoSurface waiting. SourceVersion={mediaSourceVersion}, Attempt={attempt}, State={playbackState}, Width={videoWidth}, Height={videoHeight}.");
                completion.TrySetResult(false);
                return;
            }

            _videoPlayer.Visibility = Visibility.Visible;
            _videoPlayer.InvalidateMeasure();
            _videoViewportView.InvalidateMeasure();
            _videoViewportView.UpdateLayout();
            _videoPlayer.Opacity = 1;
            HideVideoLoadingPreview();
            _telemetry.LogSwitchTrace(
                mediaSourceVersion,
                "SurfaceRevealed",
                $"SelectedToSurfaceRevealedMs={PlaybackSessionTelemetry.FormatElapsedMilliseconds(_telemetry.SwitchSelectionChangedTimestamp)}, SourceAssignedToSurfaceRevealedMs={PlaybackSessionTelemetry.FormatElapsedMilliseconds(_telemetry.SourceAssignedTimestamp)}, FirstPlayingToSurfaceRevealedMs={PlaybackSessionTelemetry.FormatElapsedMilliseconds(_telemetry.FirstPlayingTimestamp)}, Attempt={attempt}, Ready={isReady}, State={playbackState}, NaturalSize={videoWidth}x{videoHeight}.");
            LogPlaybackTrace($"RevealVideoSurface sourceVersion={mediaSourceVersion} attempt={attempt} ready={isReady} state={playbackState} width={videoWidth} height={videoHeight}");
            completion.TrySetResult(true);
        });

        if (!queued)
        {
            completion.TrySetResult(true);
        }

        return completion.Task;
    }

}
