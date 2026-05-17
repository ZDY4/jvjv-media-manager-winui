using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using JvJvMediaManager.Models;
using JvJvMediaManager.Services;
using JvJvMediaManager.Utilities;
using JvJvMediaManager.ViewModels;
using JvJvMediaManager.ViewModels.MainPage;
using JvJvMediaManager.Views.MainPageParts;

namespace JvJvMediaManager.Controllers.MainPage;

public sealed class VideoPlaybackController
{
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
    private readonly Random _random = new();
    private readonly DispatcherTimer _playbackTimer = new();
    private readonly DispatcherTimer _controlsHideTimer = new();
    private readonly DispatcherTimer _progressPreviewDebounceTimer = new();
    private readonly VideoHoverPreviewFrameService _hoverPreviewFrameService = new();
    private readonly MenuFlyout _playbackModeFlyout;
    private readonly RadioMenuFlyoutItem _listLoopModeItem;
    private readonly RadioMenuFlyoutItem _singleLoopModeItem;
    private readonly RadioMenuFlyoutItem _shuffleModeItem;
    private readonly ToggleMenuFlyoutItem _shuffleVideoOnlyMenuItem;
    private readonly ToggleMenuFlyoutItem _autoAdvanceMenuItem;
    private readonly List<string> _shuffleBackHistory = new();
    private readonly List<string> _shuffleForwardHistory = new();
    private readonly Dictionary<string, ImageSource> _progressPreviewFrameCache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _progressPreviewFrameLru = new();

    private MediaPlayer? _player;
    private bool _isSeeking;
    private bool _areControlsVisible = true;
    private bool _isTransportSuppressed;
    private bool _progressSliderHandlersAttached;
    private double _lastNonZeroVolume = 0.8;
    private bool _autoAdvanceEnabled;
    private bool _shuffleVideoOnly;
    private PlaybackMode _playbackMode = PlaybackMode.ListLoop;
    private TimeSpan _lastPlaybackPosition;
    private TimeSpan _lastPlaybackDuration;
    private string? _currentTimeTextOverride;
    private string? _totalTimeTextOverride;
    private double? _progressValueOverrideSeconds;
    private double? _progressMaximumOverrideSeconds;
    private Func<TimeSpan, TimeSpan>? _displayToPlaybackPositionOverride;
    private CancellationTokenSource? _progressPreviewFrameCts;
    private ProgressPreviewRequest? _pendingProgressPreviewRequest;
    private ImageSource? _lastProgressPreviewSource;
    private string? _progressPreviewCacheMediaId;
    private string? _progressPreviewTargetCacheKey;
    private string? _progressPreviewInFlightCacheKey;
    private int _progressPreviewRequestVersion;
    private const double VolumeWheelStep = 0.05;
    private const int MaxShuffleHistoryCount = 200;
    private const int ProgressPreviewFrameWidth = 180;
    private const int ProgressPreviewFrameHeight = 102;
    private const int MaxProgressPreviewCachedFrames = 60;
    private const int MaxProgressPreviewConsecutiveFailures = 3;
    private const double ProgressPreviewTimeBucketSeconds = 0.5;
    private const double ProgressPreviewBubbleWidth = 188;
    private const double ProgressPreviewBubbleHeight = 132;
    private static readonly TimeSpan ProgressPreviewDebounceDelay = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan ProgressPreviewFailureCooldown = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan PlaybackStartWatchdogDelay = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan VideoSurfaceRevealPollInterval = TimeSpan.FromMilliseconds(50);
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
    private const int MaxVideoSurfaceRevealAttempts = 8;
    private int _mediaSourceVersion;
    private int _playbackStartWatchdogVersion;
    private string? _pendingInternalShuffleSelectionId;
    private long _sourceLoadStartTimestamp;
    private int _sourceLoadStartVersion;
    private bool _firstPlayingLoggedForSource;
    private int _progressPreviewConsecutiveFailures;
    private long _progressPreviewFailureCooldownUntilTicks;
    private string? _progressPreviewFailureMediaId;
    private int _isDisposed;

    private sealed record ProgressPreviewRequest(
        int Version,
        string CacheKey,
        string MediaId,
        string MediaPath,
        long ModifiedAt,
        TimeSpan PlaybackTime,
        TimeSpan BucketedPlaybackTime,
        ImageSource? FallbackSource);

    public VideoPlaybackController(
        SettingsService settings,
        LibraryShellViewModel libraryViewModel,
        VideoPlaybackViewModel viewModel,
        TransportControlBarView transportBarView,
        VideoViewportView videoViewportView,
        MediaPlayerElement videoPlayer,
        DispatcherQueue dispatcherQueue,
        Func<AppWindow?> getAppWindow,
        Func<int, Task> navigateRelativeAsync,
        Func<bool> canAutoHideControls,
        Action focusHost,
        Action refreshNavigationHotspots,
        Action<TimeSpan> handleMediaOpened,
        Action notifyPlaybackProgressChanged)
    {
        _settings = settings;
        _libraryViewModel = libraryViewModel;
        _viewModel = viewModel;
        _transportBarView = transportBarView;
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
        _transportBarView.PlaybackModeButton.Click += PlaybackModeButton_Click;
        _transportBarView.FullScreenButton.Click += FullScreenButton_Click;
        _transportBarView.ControlBar.PointerMoved += ControlBar_PointerMoved;
        _transportBarView.ControlBar.PointerPressed += ControlBar_PointerPressed;

        _playbackTimer.Interval = TimeSpan.FromMilliseconds(250);
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _controlsHideTimer.Interval = TimeSpan.FromSeconds(2.5);
        _controlsHideTimer.Tick += ControlsHideTimer_Tick;
        _progressPreviewDebounceTimer.Interval = ProgressPreviewDebounceDelay;
        _progressPreviewDebounceTimer.Tick += ProgressPreviewDebounceTimer_Tick;
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
        {
            return;
        }

        Interlocked.Increment(ref _mediaSourceVersion);
        LogPlaybackTrace("Dispose start.");
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
        _transportBarView.PlaybackModeButton.Click -= PlaybackModeButton_Click;
        _transportBarView.FullScreenButton.Click -= FullScreenButton_Click;
        _transportBarView.ControlBar.PointerMoved -= ControlBar_PointerMoved;
        _transportBarView.ControlBar.PointerPressed -= ControlBar_PointerPressed;
        if (_progressSliderHandlersAttached)
        {
            _transportBarView.ProgressSlider.RemoveHandler(UIElement.PointerPressedEvent, new PointerEventHandler(ProgressSlider_PointerPressed));
            _transportBarView.ProgressSlider.RemoveHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(ProgressSlider_PointerReleased));
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
        _progressPreviewDebounceTimer.Tick -= ProgressPreviewDebounceTimer_Tick;
        _progressPreviewDebounceTimer.Stop();
        CancelProgressPreviewFrameRequest();
        ClearProgressPreviewCache(null);
        HideProgressPreview();

        if (_player != null)
        {
            DetachMediaPlayer(_player);
            _player.Dispose();
            _player = null;
        }

        LogPlaybackTrace("Dispose completed.");
    }

    public void ShowVideo(MediaItemViewModel media)
    {
        if (IsDisposed())
        {
            return;
        }

        var mediaSourceVersion = Interlocked.Increment(ref _mediaSourceVersion);
        var player = EnsureMediaPlayer();
        _sourceLoadStartTimestamp = Stopwatch.GetTimestamp();
        _sourceLoadStartVersion = mediaSourceVersion;
        _firstPlayingLoggedForSource = false;
        LogPlaybackTrace($"ShowVideo file={media.FileName} path={media.FileSystemPath} sourceVersion={mediaSourceVersion} {DescribeMediaPlaybackDiagnostics(media)}");
        ResetProgressPreviewForMedia(media.Id);
        ShowVideoLoadingPreview(media);
        _videoPlayer.Visibility = Visibility.Visible;
        _videoPlayer.InvalidateMeasure();
        LogPlaybackTrace($"Assigning MediaPlayer source. SourceVersion={mediaSourceVersion}, UriScheme='{new Uri(media.FileSystemPath).Scheme}', PathExists={File.Exists(media.FileSystemPath)}.");
        player.Source = MediaSource.CreateFromUri(new Uri(media.FileSystemPath));
        RequestPlaybackStart(mediaSourceVersion, "ShowVideo");
        SetVideoState();
        ShowControls();
        _focusHost();
    }

    public void PauseAndClearSource()
    {
        Interlocked.Increment(ref _mediaSourceVersion);
        LogPlaybackTrace("PauseAndClearSource invoked.");
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
        HideProgressPreview();
        ClearProgressPreviewCache(null);
        CancelProgressPreviewFrameRequest();
        _videoPlayer.Visibility = Visibility.Collapsed;
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
        HideProgressPreview();
        ClearProgressPreviewCache(null);
        CancelProgressPreviewFrameRequest();
        _videoPlayer.Visibility = Visibility.Collapsed;
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
        _refreshNavigationHotspots();
        RestartControlsHideTimer();
    }

    public void HandlePointerExited()
    {
        if (IsDisposed())
        {
            return;
        }

        RestartControlsHideTimer();
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

        HideProgressPreview();
        CancelProgressPreviewFrameRequest();
        CloseVolumeFlyout();
        _viewModel.ControlBarOpacity = 0;
        _transportBarView.ControlBar.IsHitTestVisible = false;
        _areControlsVisible = false;
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
        }
        else
        {
            player.Play();
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
            HideProgressPreview();
            CancelProgressPreviewFrameRequest();
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
        LogPlaybackTraceSampled("seek-to", $"SeekTo requested={requested.TotalSeconds:F3}s applied={position.TotalSeconds:F3}s duration={duration.TotalSeconds:F3}s", TimeSpan.FromSeconds(2));
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
            _areControlsVisible = true;
            _refreshNavigationHotspots();
            _controlsHideTimer.Stop();
            return;
        }

        _viewModel.ControlBarVisibility = Visibility.Visible;
        _viewModel.ControlBarOpacity = 1;
        _transportBarView.ControlBar.IsHitTestVisible = true;
        _areControlsVisible = true;
        _refreshNavigationHotspots();
        RestartControlsHideTimer();
    }

    private void SetPassiveState(bool showImageNavigation)
    {
        if (IsDisposed())
        {
            return;
        }

        CloseVolumeFlyout();
        HideProgressPreview();
        CancelProgressPreviewFrameRequest();
        _viewModel.ControlBarVisibility = Visibility.Collapsed;
        _viewModel.ControlBarOpacity = 0;
        _transportBarView.ControlBar.IsHitTestVisible = false;
        _areControlsVisible = showImageNavigation;
        _refreshNavigationHotspots();
        _controlsHideTimer.Stop();
    }

    private bool ShouldAutoHideControls()
    {
        return !_isTransportSuppressed
            && _viewModel.ControlBarVisibility == Visibility.Visible
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

    private MediaPlayer CreateMediaPlayer()
    {
        _player = new MediaPlayer();
        _player.MediaOpened += Player_MediaOpened;
        _player.MediaFailed += Player_MediaFailed;
        _player.MediaEnded += Player_MediaEnded;
        _player.PlaybackSession.PlaybackStateChanged += PlaybackSession_PlaybackStateChanged;
        _player.Volume = _transportBarView.VolumeSlider.Value;
        return _player;
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

        SetButtonGlyph(_transportBarView.PlaybackModeButton, glyph);
        ToolTipService.SetToolTip(_transportBarView.PlaybackModeButton, description);
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
        SetButtonGlyph(_transportBarView.FullScreenButton, _viewModel.IsFullScreen ? "\uE73F" : "\uE740");
        ToolTipService.SetToolTip(_transportBarView.FullScreenButton, _viewModel.IsFullScreen ? "退出全屏" : "全屏");
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

        _transportBarView.ProgressSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(ProgressSlider_PointerPressed), true);
        _transportBarView.ProgressSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(ProgressSlider_PointerReleased), true);
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
        UpdateProgressPreviewFromPointer(e);
    }

    private void ProgressSlider_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        UpdateProgressPreviewFromPointer(e);
    }

    private void ProgressSlider_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        HideProgressPreview();
        CancelProgressPreviewFrameRequest();
    }

    private void ProgressSlider_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        HideProgressPreview();
        CancelProgressPreviewFrameRequest();
    }

    private void UpdateProgressPreviewFromPointer(PointerRoutedEventArgs e)
    {
        if (!TryResolveProgressPreviewTarget(e, out var playbackTime, out var sliderX))
        {
            HideProgressPreview();
            CancelProgressPreviewFrameRequest();
            return;
        }

        var media = _libraryViewModel.SelectedMedia;
        if (media == null)
        {
            HideProgressPreview();
            CancelProgressPreviewFrameRequest();
            return;
        }

        var fallbackSource = _lastProgressPreviewSource ?? media.Thumbnail;
        ShowProgressPreview(playbackTime, sliderX, fallbackSource);

        var bucketedTime = GetProgressPreviewBucket(playbackTime, GetCurrentVideoDuration());
        var cacheKey = BuildProgressPreviewCacheKey(media, bucketedTime);
        _progressPreviewTargetCacheKey = cacheKey;
        if (TryGetProgressPreviewFrame(cacheKey, out var cachedSource))
        {
            _lastProgressPreviewSource = cachedSource;
            _transportBarView.ProgressPreviewImage.Source = cachedSource;
            _pendingProgressPreviewRequest = null;
            CancelProgressPreviewFrameRequest();
            AppTraceLogger.LogSampled(
                "VideoHoverPreview",
                "preview-cache-hit",
                $"Progress preview cache hit. MediaId='{media.Id}', Bucket={bucketedTime.TotalSeconds:F3}, Playback={playbackTime.TotalSeconds:F3}, CacheCount={_progressPreviewFrameCache.Count}.",
                TimeSpan.FromMilliseconds(500));
            return;
        }

        if (IsProgressPreviewInFailureCooldown(media.Id, out var remainingCooldown))
        {
            AppTraceLogger.LogSampled(
                "VideoHoverPreview",
                "preview-generation-cooldown",
                $"Progress preview generation skipped during failure cooldown. MediaId='{media.Id}', RemainingMs={remainingCooldown.TotalMilliseconds:0}, ConsecutiveFailures={_progressPreviewConsecutiveFailures}, FfmpegAvailable={_hoverPreviewFrameService.IsAvailable}.",
                TimeSpan.FromSeconds(1));
            return;
        }

        if (IsProgressPreviewFrameAlreadyPending(cacheKey))
        {
            AppTraceLogger.LogSampled(
                "VideoHoverPreview",
                "preview-request-already-pending",
                $"Progress preview frame already pending. MediaId='{media.Id}', Bucket={bucketedTime.TotalSeconds:F3}, Playback={playbackTime.TotalSeconds:F3}, InFlight='{_progressPreviewInFlightCacheKey ?? "<null>"}'.",
                TimeSpan.FromMilliseconds(500));
            return;
        }

        CancelProgressPreviewFrameRequest();
        _pendingProgressPreviewRequest = new ProgressPreviewRequest(
            Volatile.Read(ref _progressPreviewRequestVersion),
            cacheKey,
            media.Id,
            media.FileSystemPath,
            media.Media.ModifiedAt,
            playbackTime,
            bucketedTime,
            fallbackSource);
        _progressPreviewDebounceTimer.Stop();
        _progressPreviewDebounceTimer.Start();
        AppTraceLogger.LogSampled(
            "VideoHoverPreview",
            "preview-request-scheduled",
            $"Progress preview request scheduled. MediaId='{media.Id}', Bucket={bucketedTime.TotalSeconds:F3}, Playback={playbackTime.TotalSeconds:F3}, HasFallback={fallbackSource != null}.",
            TimeSpan.FromMilliseconds(500));
    }

    private bool TryResolveProgressPreviewTarget(PointerRoutedEventArgs e, out TimeSpan playbackTime, out double sliderX)
    {
        playbackTime = TimeSpan.Zero;
        sliderX = 0;

        if (IsDisposed()
            || _isTransportSuppressed
            || _player == null
            || _libraryViewModel.SelectedMedia?.Type != MediaType.Video
            || _transportBarView.ProgressSlider.ActualWidth <= 0)
        {
            return false;
        }

        var duration = GetCurrentVideoDuration();
        if (duration <= TimeSpan.Zero)
        {
            return false;
        }

        var point = e.GetCurrentPoint(_transportBarView.ProgressSlider).Position;
        sliderX = Math.Clamp(point.X, 0, _transportBarView.ProgressSlider.ActualWidth);
        var progressMaximum = Math.Max(0, _transportBarView.ProgressSlider.Maximum);
        if (progressMaximum <= 0)
        {
            return false;
        }

        var ratio = sliderX / _transportBarView.ProgressSlider.ActualWidth;
        var displayTime = TimeSpan.FromSeconds(progressMaximum * ratio);
        playbackTime = _displayToPlaybackPositionOverride?.Invoke(displayTime) ?? displayTime;
        playbackTime = ClampToDuration(playbackTime, duration);
        return true;
    }

    private void ShowProgressPreview(TimeSpan playbackTime, double sliderX, ImageSource? previewSource)
    {
        _transportBarView.ProgressPreviewTimeText.Text = FormatTime(playbackTime);
        if (previewSource != null)
        {
            _transportBarView.ProgressPreviewImage.Source = previewSource;
        }

        UpdateProgressPreviewPopupPosition(sliderX);
        if (!_transportBarView.ProgressPreviewPopup.IsOpen)
        {
            _transportBarView.ProgressPreviewPopup.IsOpen = true;
        }
    }

    private void UpdateProgressPreviewPopupPosition(double sliderX)
    {
        try
        {
            var sliderOrigin = _transportBarView.ProgressSlider
                .TransformToVisual(_transportBarView)
                .TransformPoint(new Point(0, 0));
            var bubbleWidth = _transportBarView.ProgressPreviewBubble.ActualWidth > 0
                ? _transportBarView.ProgressPreviewBubble.ActualWidth
                : ProgressPreviewBubbleWidth;
            var bubbleHeight = _transportBarView.ProgressPreviewBubble.ActualHeight > 0
                ? _transportBarView.ProgressPreviewBubble.ActualHeight
                : ProgressPreviewBubbleHeight;
            var availableWidth = _transportBarView.ActualWidth > 0
                ? _transportBarView.ActualWidth
                : sliderOrigin.X + _transportBarView.ProgressSlider.ActualWidth;

            var left = sliderOrigin.X + sliderX - (bubbleWidth / 2);
            var maxLeft = Math.Max(0, availableWidth - bubbleWidth);
            _transportBarView.ProgressPreviewPopup.HorizontalOffset = Math.Clamp(left, 0, maxLeft);
            _transportBarView.ProgressPreviewPopup.VerticalOffset = Math.Max(0, sliderOrigin.Y - bubbleHeight - 10);
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("VideoPlayback", "UpdateProgressPreviewPopupPosition failed.", ex);
        }
    }

    private void HideProgressPreview()
    {
        _progressPreviewDebounceTimer.Stop();
        _pendingProgressPreviewRequest = null;
        if (_transportBarView.ProgressPreviewPopup.IsOpen)
        {
            _transportBarView.ProgressPreviewPopup.IsOpen = false;
        }
    }

    private void ProgressPreviewDebounceTimer_Tick(object? sender, object e)
    {
        _progressPreviewDebounceTimer.Stop();
        var request = _pendingProgressPreviewRequest;
        if (request == null || !IsProgressPreviewRequestCurrent(request))
        {
            return;
        }

        _ = LoadProgressPreviewFrameAsync(request);
    }

    private async Task LoadProgressPreviewFrameAsync(ProgressPreviewRequest request)
    {
        if (!IsProgressPreviewRequestCurrent(request))
        {
            return;
        }

        _progressPreviewFrameCts?.Cancel();
        _progressPreviewFrameCts?.Dispose();
        var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        _progressPreviewFrameCts = cancellation;
        _progressPreviewInFlightCacheKey = request.CacheKey;
        _pendingProgressPreviewRequest = null;

        try
        {
            var frameStartTimestamp = Stopwatch.GetTimestamp();
            AppTraceLogger.LogSampled(
                "VideoHoverPreview",
                "preview-frame-generate-start",
                $"Progress preview frame generation started. MediaId='{request.MediaId}', Bucket={request.BucketedPlaybackTime.TotalSeconds:F3}, Playback={request.PlaybackTime.TotalSeconds:F3}.",
                TimeSpan.FromMilliseconds(500));
            var frameBytes = await _hoverPreviewFrameService.GenerateFrameAsync(
                request.MediaPath,
                request.BucketedPlaybackTime,
                ProgressPreviewFrameWidth,
                ProgressPreviewFrameHeight,
                cancellationToken);
            var generationElapsed = Stopwatch.GetElapsedTime(frameStartTimestamp);
            if (frameBytes == null || cancellationToken.IsCancellationRequested)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    RegisterProgressPreviewFailure(request.MediaId, "frame-empty");
                }

                AppTraceLogger.LogSampled(
                    "VideoHoverPreview",
                    "preview-frame-empty",
                    $"Progress preview frame generation returned no frame. MediaId='{request.MediaId}', Bucket={request.BucketedPlaybackTime.TotalSeconds:F3}, Canceled={cancellationToken.IsCancellationRequested}, FfmpegAvailable={_hoverPreviewFrameService.IsAvailable}, ElapsedMs={generationElapsed.TotalMilliseconds:0}.",
                    TimeSpan.FromSeconds(2));
                return;
            }

            _dispatcherQueue.TryEnqueue(async () =>
            {
                if (cancellationToken.IsCancellationRequested || !IsProgressPreviewRequestCurrent(request))
                {
                    AppTraceLogger.LogSampled(
                        "VideoHoverPreview",
                        "preview-frame-stale",
                        $"Progress preview frame discarded as stale. MediaId='{request.MediaId}', Bucket={request.BucketedPlaybackTime.TotalSeconds:F3}, TargetMatches={string.Equals(_progressPreviewTargetCacheKey, request.CacheKey, StringComparison.Ordinal)}, Canceled={cancellationToken.IsCancellationRequested}.",
                        TimeSpan.FromMilliseconds(500));
                    return;
                }

                var source = await CreateImageSourceAsync(frameBytes);
                if (source == null)
                {
                    RegisterProgressPreviewFailure(request.MediaId, "decode-empty");
                    return;
                }

                AddProgressPreviewFrame(request.CacheKey, source);
                ResetProgressPreviewFailures(request.MediaId);
                _lastProgressPreviewSource = source;
                _transportBarView.ProgressPreviewImage.Source = source;
                AppTraceLogger.LogSampled(
                    "VideoHoverPreview",
                    "preview-frame-applied",
                    $"Progress preview frame applied. MediaId='{request.MediaId}', Bucket={request.BucketedPlaybackTime.TotalSeconds:F3}, Bytes={frameBytes.Length}, CacheCount={_progressPreviewFrameCache.Count}, GenerateElapsedMs={generationElapsed.TotalMilliseconds:0}.",
                    TimeSpan.FromMilliseconds(500));
            });
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_progressPreviewFrameCts, cancellation))
            {
                _progressPreviewFrameCts = null;
            }

            if (string.Equals(_progressPreviewInFlightCacheKey, request.CacheKey, StringComparison.Ordinal))
            {
                _progressPreviewInFlightCacheKey = null;
            }

            cancellation.Dispose();
        }
    }

    private void CancelProgressPreviewFrameRequest()
    {
        _progressPreviewDebounceTimer.Stop();
        _pendingProgressPreviewRequest = null;
        _progressPreviewFrameCts?.Cancel();
        _progressPreviewFrameCts?.Dispose();
        _progressPreviewFrameCts = null;
        _progressPreviewInFlightCacheKey = null;
    }

    private bool IsProgressPreviewFrameAlreadyPending(string cacheKey)
    {
        return string.Equals(_pendingProgressPreviewRequest?.CacheKey, cacheKey, StringComparison.Ordinal)
            || string.Equals(_progressPreviewInFlightCacheKey, cacheKey, StringComparison.Ordinal);
    }

    private bool IsProgressPreviewRequestCurrent(ProgressPreviewRequest request)
    {
        return !IsDisposed()
            && request.Version == Volatile.Read(ref _progressPreviewRequestVersion)
            && string.Equals(_progressPreviewTargetCacheKey, request.CacheKey, StringComparison.Ordinal)
            && _libraryViewModel.SelectedMedia is { } selected
            && string.Equals(selected.Id, request.MediaId, StringComparison.Ordinal)
            && selected.Media.ModifiedAt == request.ModifiedAt;
    }

    private bool IsProgressPreviewInFailureCooldown(string mediaId, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        if (!string.Equals(_progressPreviewFailureMediaId, mediaId, StringComparison.Ordinal))
        {
            return false;
        }

        var nowTicks = DateTime.UtcNow.Ticks;
        var cooldownTicks = Volatile.Read(ref _progressPreviewFailureCooldownUntilTicks);
        if (cooldownTicks <= nowTicks)
        {
            return false;
        }

        remaining = TimeSpan.FromTicks(cooldownTicks - nowTicks);
        return true;
    }

    private void RegisterProgressPreviewFailure(string mediaId, string reason)
    {
        if (!string.Equals(_progressPreviewFailureMediaId, mediaId, StringComparison.Ordinal))
        {
            _progressPreviewFailureMediaId = mediaId;
            _progressPreviewConsecutiveFailures = 0;
        }

        _progressPreviewConsecutiveFailures++;
        if (_progressPreviewConsecutiveFailures < MaxProgressPreviewConsecutiveFailures)
        {
            AppTraceLogger.LogSampled(
                "VideoHoverPreview",
                "preview-frame-failure",
                $"Progress preview frame failure recorded. MediaId='{mediaId}', Reason={reason}, ConsecutiveFailures={_progressPreviewConsecutiveFailures}.",
                TimeSpan.FromSeconds(1));
            return;
        }

        var cooldownUntil = DateTime.UtcNow.Add(ProgressPreviewFailureCooldown);
        _progressPreviewFailureCooldownUntilTicks = cooldownUntil.Ticks;
        AppTraceLogger.Log(
            "VideoHoverPreview",
            $"Progress preview generation entered failure cooldown. MediaId='{mediaId}', Reason={reason}, ConsecutiveFailures={_progressPreviewConsecutiveFailures}, CooldownMs={ProgressPreviewFailureCooldown.TotalMilliseconds:0}, FfmpegAvailable={_hoverPreviewFrameService.IsAvailable}.");
    }

    private void ResetProgressPreviewFailures(string mediaId)
    {
        if (!string.Equals(_progressPreviewFailureMediaId, mediaId, StringComparison.Ordinal)
            || _progressPreviewConsecutiveFailures == 0)
        {
            return;
        }

        AppTraceLogger.Log(
            "VideoHoverPreview",
            $"Progress preview failures reset after successful frame. MediaId='{mediaId}', PreviousFailures={_progressPreviewConsecutiveFailures}.");
        _progressPreviewConsecutiveFailures = 0;
        _progressPreviewFailureCooldownUntilTicks = 0;
    }

    private void ResetProgressPreviewForMedia(string mediaId)
    {
        HideProgressPreview();
        CancelProgressPreviewFrameRequest();
        ClearProgressPreviewCache(mediaId);
    }

    private void ClearProgressPreviewCache(string? nextMediaId, bool invalidateRequests = true)
    {
        _progressPreviewFrameCache.Clear();
        _progressPreviewFrameLru.Clear();
        _lastProgressPreviewSource = null;
        _progressPreviewCacheMediaId = nextMediaId;
        _progressPreviewTargetCacheKey = null;
        _progressPreviewInFlightCacheKey = null;
        if (!string.Equals(_progressPreviewFailureMediaId, nextMediaId, StringComparison.Ordinal))
        {
            _progressPreviewFailureMediaId = nextMediaId;
            _progressPreviewConsecutiveFailures = 0;
            _progressPreviewFailureCooldownUntilTicks = 0;
        }

        if (invalidateRequests)
        {
            Interlocked.Increment(ref _progressPreviewRequestVersion);
        }
    }

    private bool TryGetProgressPreviewFrame(string cacheKey, out ImageSource source)
    {
        if (_progressPreviewFrameCache.TryGetValue(cacheKey, out source!))
        {
            _progressPreviewFrameLru.Remove(cacheKey);
            _progressPreviewFrameLru.AddLast(cacheKey);
            return true;
        }

        return false;
    }

    private void AddProgressPreviewFrame(string cacheKey, ImageSource source)
    {
        if (_progressPreviewFrameCache.ContainsKey(cacheKey))
        {
            _progressPreviewFrameLru.Remove(cacheKey);
        }

        _progressPreviewFrameCache[cacheKey] = source;
        _progressPreviewFrameLru.AddLast(cacheKey);

        while (_progressPreviewFrameCache.Count > MaxProgressPreviewCachedFrames
            && _progressPreviewFrameLru.First?.Value is { } oldestKey)
        {
            _progressPreviewFrameLru.RemoveFirst();
            _progressPreviewFrameCache.Remove(oldestKey);
        }
    }

    private string BuildProgressPreviewCacheKey(MediaItemViewModel media, TimeSpan bucketedTime)
    {
        if (!string.Equals(_progressPreviewCacheMediaId, media.Id, StringComparison.Ordinal))
        {
            ClearProgressPreviewCache(media.Id, invalidateRequests: false);
        }

        var bucketMilliseconds = (long)Math.Round(bucketedTime.TotalMilliseconds);
        return $"{media.Id}|{media.Media.ModifiedAt}|{bucketMilliseconds}|{ProgressPreviewFrameWidth}x{ProgressPreviewFrameHeight}";
    }

    private static TimeSpan GetProgressPreviewBucket(TimeSpan playbackTime, TimeSpan duration)
    {
        var bucketSeconds = Math.Round(playbackTime.TotalSeconds / ProgressPreviewTimeBucketSeconds) * ProgressPreviewTimeBucketSeconds;
        return ClampToDuration(TimeSpan.FromSeconds(bucketSeconds), duration);
    }

    private static TimeSpan ClampToDuration(TimeSpan value, TimeSpan duration)
    {
        if (value < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return duration > TimeSpan.Zero && value > duration
            ? duration
            : value;
    }

    private static async Task<ImageSource?> CreateImageSourceAsync(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return null;
        }

        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
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
        _playbackModeFlyout.ShowAt(_transportBarView.PlaybackModeButton);
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

        ShowControls();
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

        UpdateTimeDisplay();
        _notifyPlaybackProgressChanged();
    }

    private void Player_MediaOpened(MediaPlayer sender, object args)
    {
        if (IsDisposed())
        {
            return;
        }

        var duration = sender.PlaybackSession.NaturalDuration;
        var mediaSourceVersion = Volatile.Read(ref _mediaSourceVersion);
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (IsDisposed() || mediaSourceVersion != Volatile.Read(ref _mediaSourceVersion))
            {
                return;
            }

            LogPlaybackTrace(
                $"MediaOpened duration={duration.TotalSeconds:F3}s sourceVersion={mediaSourceVersion} OpenElapsedMs={GetSourceLoadElapsedMilliseconds(mediaSourceVersion):0} NaturalSize={sender.PlaybackSession.NaturalVideoWidth}x{sender.PlaybackSession.NaturalVideoHeight} SelectedId='{_libraryViewModel.SelectedMedia?.Id ?? "<null>"}'.");
            _lastPlaybackPosition = TimeSpan.Zero;
            _lastPlaybackDuration = duration;
            UpdateTimeDisplay();
            _handleMediaOpened(duration);
            _notifyPlaybackProgressChanged();
            RequestPlaybackStart(mediaSourceVersion, "MediaOpened");
            HideVideoLoadingPreview();
            _ = RevealVideoSurfaceAsync(mediaSourceVersion);
        });
    }

    private void Player_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        if (IsDisposed())
        {
            return;
        }

        var mediaSourceVersion = Volatile.Read(ref _mediaSourceVersion);
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (IsDisposed() || mediaSourceVersion != Volatile.Read(ref _mediaSourceVersion))
            {
                return;
            }

            var selected = _libraryViewModel.SelectedMedia;
            LogPlaybackTrace(
                $"MediaFailed error={args.Error} extendedError=0x{args.ExtendedErrorCode.HResult:X8} message={args.ErrorMessage} file='{selected?.FileName ?? "<none>"}' path='{selected?.FileSystemPath ?? "<none>"}' SourceVersion={mediaSourceVersion} OpenElapsedMs={GetSourceLoadElapsedMilliseconds(mediaSourceVersion):0} {DescribeMediaPlaybackDiagnostics(selected)} CompatibilityHint='{GetPlaybackCompatibilityHint(selected, args)}'.");
            _videoPlayer.Visibility = Visibility.Visible;
            HideVideoLoadingPreview();
        });
    }

    private void Player_MediaEnded(MediaPlayer sender, object args)
    {
        if (IsDisposed())
        {
            return;
        }

        _dispatcherQueue.TryEnqueue(async () =>
        {
            if (IsDisposed())
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
                if (!TryNavigateShuffle(1))
                {
                    LogPlaybackTrace("MediaEnded shuffle fallback restarting current media because no random candidate was available.");
                    sender.PlaybackSession.Position = TimeSpan.Zero;
                    sender.Play();
                }

                return;
            }

            LogPlaybackTrace("MediaEnded navigating to next media.");
            await _navigateRelativeAsync(1);
        });
    }

    private void PlaybackSession_PlaybackStateChanged(MediaPlaybackSession sender, object args)
    {
        if (IsDisposed())
        {
            return;
        }

        _dispatcherQueue.TryEnqueue(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            LogPlaybackTrace($"PlaybackStateChanged state={sender.PlaybackState} position={sender.Position.TotalSeconds:F3}s duration={sender.NaturalDuration.TotalSeconds:F3}s");
            if (sender.PlaybackState == MediaPlaybackState.Playing && !_firstPlayingLoggedForSource)
            {
                _firstPlayingLoggedForSource = true;
                var sourceVersion = Volatile.Read(ref _mediaSourceVersion);
                LogPlaybackTrace(
                    $"Playback first playing. SourceVersion={sourceVersion}, FirstPlayingElapsedMs={GetSourceLoadElapsedMilliseconds(sourceVersion):0}, Position={sender.Position.TotalSeconds:F3}s, Duration={sender.NaturalDuration.TotalSeconds:F3}s, NaturalSize={sender.NaturalVideoWidth}x{sender.NaturalVideoHeight}.");
            }

            UpdatePlayPauseState();
        });
    }

    public bool TryNavigateShuffle(int offset)
    {
        if (IsDisposed() || _playbackMode != PlaybackMode.Shuffle)
        {
            return false;
        }

        if (offset < 0)
        {
            return TryNavigateShuffleHistory(_shuffleBackHistory, _shuffleForwardHistory, "back");
        }

        if (offset > 0 && TryNavigateShuffleHistory(_shuffleForwardHistory, _shuffleBackHistory, "forward"))
        {
            return true;
        }

        return offset > 0 && TrySelectRandomMediaCore();
    }

    public bool TrySelectRandomMedia()
    {
        return TrySelectRandomMediaCore();
    }

    private bool TrySelectRandomMediaCore()
    {
        var list = _libraryViewModel.FilteredMediaItems;
        var current = _libraryViewModel.SelectedMedia;
        if (list.Count <= 1 || current == null)
        {
            LogPlaybackTraceSampled(
                "shuffle-random-unavailable",
                $"Shuffle random skipped. ListCount={list.Count}, CurrentId='{current?.Id ?? "<null>"}'.",
                TimeSpan.FromSeconds(2));
            return false;
        }

        var candidates = list
            .Where(item => IsShuffleCandidate(item, current))
            .ToList();
        if (candidates.Count == 0)
        {
            LogPlaybackTraceSampled(
                "shuffle-random-empty",
                $"Shuffle random skipped because candidates are empty. CurrentId='{current.Id}', ListCount={list.Count}, ShuffleVideoOnly={_shuffleVideoOnly}.",
                TimeSpan.FromSeconds(2));
            return false;
        }

        var selected = candidates[_random.Next(candidates.Count)];
        if (IsShuffleHistoryEligible(current))
        {
            PushShuffleHistory(_shuffleBackHistory, current.Id);
        }

        ClearShuffleForwardHistory("shuffle-random");
        SetInternalShuffleSelection(selected);
        LogPlaybackTrace(
            $"Shuffle random selected. PreviousId='{current.Id}', SelectedId='{selected.Id}', CandidateCount={candidates.Count}, BackCount={_shuffleBackHistory.Count}, ForwardCount={_shuffleForwardHistory.Count}, ShuffleVideoOnly={_shuffleVideoOnly}.");
        return true;
    }

    private bool TryNavigateShuffleHistory(List<string> sourceHistory, List<string> destinationHistory, string direction)
    {
        var current = _libraryViewModel.SelectedMedia;
        if (current == null)
        {
            LogPlaybackTrace($"Shuffle history {direction} skipped because current media is null.");
            return false;
        }

        while (sourceHistory.Count > 0)
        {
            var targetId = PopShuffleHistory(sourceHistory);
            var target = ResolveCurrentMedia(targetId);
            if (target == null)
            {
                LogPlaybackTrace($"Shuffle history {direction} skipped stale item. TargetId='{targetId}'.");
                continue;
            }

            if (!IsShuffleHistoryEligible(target))
            {
                LogPlaybackTrace($"Shuffle history {direction} skipped ineligible item. TargetId='{targetId}', Type={target.Type}, ShuffleVideoOnly={_shuffleVideoOnly}.");
                continue;
            }

            if (string.Equals(target.Id, current.Id, StringComparison.Ordinal))
            {
                LogPlaybackTrace($"Shuffle history {direction} skipped current item. TargetId='{targetId}'.");
                continue;
            }

            if (IsShuffleHistoryEligible(current))
            {
                PushShuffleHistory(destinationHistory, current.Id);
            }

            SetInternalShuffleSelection(target);
            LogPlaybackTrace(
                $"Shuffle history {direction} selected. PreviousId='{current.Id}', SelectedId='{target.Id}', BackCount={_shuffleBackHistory.Count}, ForwardCount={_shuffleForwardHistory.Count}, ShuffleVideoOnly={_shuffleVideoOnly}.");
            return true;
        }

        LogPlaybackTraceSampled(
            $"shuffle-history-{direction}-empty",
            $"Shuffle history {direction} unavailable. CurrentId='{current.Id}', BackCount={_shuffleBackHistory.Count}, ForwardCount={_shuffleForwardHistory.Count}.",
            TimeSpan.FromSeconds(2));
        return false;
    }

    private MediaItemViewModel? ResolveCurrentMedia(string mediaId)
    {
        return _libraryViewModel.FilteredMediaItems
            .FirstOrDefault(item => string.Equals(item.Id, mediaId, StringComparison.Ordinal));
    }

    private bool IsShuffleCandidate(MediaItemViewModel item, MediaItemViewModel current)
    {
        return !string.Equals(item.Id, current.Id, StringComparison.Ordinal)
            && IsShuffleHistoryEligible(item);
    }

    private bool IsShuffleHistoryEligible(MediaItemViewModel media)
    {
        return !_shuffleVideoOnly || media.Type == MediaType.Video;
    }

    private double GetSourceLoadElapsedMilliseconds(int sourceVersion)
    {
        if (_sourceLoadStartTimestamp <= 0 || _sourceLoadStartVersion != sourceVersion)
        {
            return -1;
        }

        return Stopwatch.GetElapsedTime(_sourceLoadStartTimestamp).TotalMilliseconds;
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

    private static string PopShuffleHistory(List<string> history)
    {
        var index = history.Count - 1;
        var mediaId = history[index];
        history.RemoveAt(index);
        return mediaId;
    }

    private static void PushShuffleHistory(List<string> history, string mediaId)
    {
        history.Add(mediaId);
        if (history.Count > MaxShuffleHistoryCount)
        {
            history.RemoveRange(0, history.Count - MaxShuffleHistoryCount);
        }
    }

    private void ClearShuffleHistory(string reason)
    {
        var backCount = _shuffleBackHistory.Count;
        var forwardCount = _shuffleForwardHistory.Count;
        if (backCount == 0 && forwardCount == 0)
        {
            return;
        }

        _shuffleBackHistory.Clear();
        _shuffleForwardHistory.Clear();
        LogPlaybackTrace($"Shuffle history cleared. Reason={reason}, PreviousBackCount={backCount}, PreviousForwardCount={forwardCount}.");
    }

    private void ClearShuffleForwardHistory(string reason)
    {
        if (_shuffleForwardHistory.Count == 0)
        {
            return;
        }

        var forwardCount = _shuffleForwardHistory.Count;
        _shuffleForwardHistory.Clear();
        LogPlaybackTrace($"Shuffle forward history cleared. Reason={reason}, PreviousForwardCount={forwardCount}.");
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

    private void ShowVideoLoadingPreview(MediaItemViewModel media)
    {
        var hasThumbnail = media.Thumbnail != null;
        LogPlaybackTrace($"ShowVideoLoadingPreview hasThumbnail={hasThumbnail}");
        _videoViewportView.VideoLoadingPreviewImage.Source = media.Thumbnail;
        _videoViewportView.VideoLoadingPreviewHost.Visibility = hasThumbnail
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void HideVideoLoadingPreview()
    {
        LogPlaybackTrace("HideVideoLoadingPreview invoked.");
        _videoViewportView.VideoLoadingPreviewHost.Visibility = Visibility.Collapsed;
        _videoViewportView.VideoLoadingPreviewImage.Source = null;
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

    private void RequestPlaybackStart(int mediaSourceVersion, string reason)
    {
        if (mediaSourceVersion != Volatile.Read(ref _mediaSourceVersion))
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
            SchedulePlaybackStartWatchdog(mediaSourceVersion);
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("VideoPlayback", $"mediaId={_libraryViewModel.SelectedMedia?.Id ?? "<none>"} Playback start failed. SourceVersion={mediaSourceVersion}, Reason={reason}.", ex);
        }
    }

    private void SchedulePlaybackStartWatchdog(int mediaSourceVersion)
    {
        if (Interlocked.Exchange(ref _playbackStartWatchdogVersion, mediaSourceVersion) == mediaSourceVersion)
        {
            return;
        }

        _ = VerifyPlaybackStartedAsync(mediaSourceVersion);
    }

    private async Task VerifyPlaybackStartedAsync(int mediaSourceVersion)
    {
        try
        {
            await Task.Delay(PlaybackStartWatchdogDelay);
        }
        catch
        {
            return;
        }

        _dispatcherQueue.TryEnqueue(() =>
        {
            if (IsDisposed() || mediaSourceVersion != Volatile.Read(ref _mediaSourceVersion))
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
    }

    private async Task RevealVideoSurfaceAsync(int mediaSourceVersion)
    {
        for (var attempt = 1; attempt <= MaxVideoSurfaceRevealAttempts; attempt++)
        {
            if (IsDisposed())
            {
                return;
            }

            try
            {
                await Task.Delay(VideoSurfaceRevealPollInterval);
            }
            catch
            {
                return;
            }

            if (await TryRevealVideoSurfaceOnUiThreadAsync(mediaSourceVersion, attempt))
            {
                return;
            }
        }
    }

    private Task<bool> TryRevealVideoSurfaceOnUiThreadAsync(int mediaSourceVersion, int attempt)
    {
        var completion = new TaskCompletionSource<bool>();
        var queued = _dispatcherQueue.TryEnqueue(() =>
        {
            if (mediaSourceVersion != Volatile.Read(ref _mediaSourceVersion))
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
            var videoWidth = player?.PlaybackSession.NaturalVideoWidth ?? 0;
            var videoHeight = player?.PlaybackSession.NaturalVideoHeight ?? 0;
            if ((videoWidth <= 0 || videoHeight <= 0) && attempt < MaxVideoSurfaceRevealAttempts)
            {
                LogPlaybackTrace($"RevealVideoSurface waiting for natural size. SourceVersion={mediaSourceVersion}, Attempt={attempt}, Width={videoWidth}, Height={videoHeight}.");
                completion.TrySetResult(false);
                return;
            }

            _videoPlayer.InvalidateMeasure();
            _videoPlayer.Visibility = Visibility.Visible;
            HideVideoLoadingPreview();
            LogPlaybackTrace($"RevealVideoSurface sourceVersion={mediaSourceVersion} attempt={attempt} width={videoWidth} height={videoHeight}");
            completion.TrySetResult(true);
        });

        if (!queued)
        {
            completion.TrySetResult(true);
        }

        return completion.Task;
    }

}
