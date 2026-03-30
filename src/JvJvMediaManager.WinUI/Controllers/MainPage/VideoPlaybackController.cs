using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using JvJvMediaManager.Models;
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
    private readonly MenuFlyout _playbackModeFlyout;
    private readonly RadioMenuFlyoutItem _listLoopModeItem;
    private readonly RadioMenuFlyoutItem _singleLoopModeItem;
    private readonly RadioMenuFlyoutItem _shuffleModeItem;
    private readonly ToggleMenuFlyoutItem _autoAdvanceMenuItem;

    private MediaPlayer? _player;
    private bool _isSeeking;
    private bool _areControlsVisible = true;
    private bool _isTransportSuppressed;
    private bool _progressSliderHandlersAttached;
    private double _lastNonZeroVolume = 0.8;
    private bool _autoAdvanceEnabled = true;
    private PlaybackMode _playbackMode = PlaybackMode.ListLoop;
    private TimeSpan _lastPlaybackPosition;
    private TimeSpan _lastPlaybackDuration;
    private string? _currentTimeTextOverride;
    private string? _totalTimeTextOverride;
    private double? _progressValueOverrideSeconds;
    private double? _progressMaximumOverrideSeconds;
    private Func<TimeSpan, TimeSpan>? _displayToPlaybackPositionOverride;
    private const double VolumeWheelStep = 0.05;

    public VideoPlaybackController(
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
        _playbackModeFlyout = new MenuFlyout();
        _listLoopModeItem = CreatePlaybackModeItem("列表循环", PlaybackMode.ListLoop);
        _singleLoopModeItem = CreatePlaybackModeItem("单曲循环", PlaybackMode.SingleLoop);
        _shuffleModeItem = CreatePlaybackModeItem("随机播放", PlaybackMode.Shuffle);
        _autoAdvanceMenuItem = new ToggleMenuFlyoutItem
        {
            Text = "是否自动切换下一个媒体",
            IsChecked = true
        };

        _transportBarView.ProgressSlider.Loaded += ProgressSlider_Loaded;
        _transportBarView.ProgressSlider.KeyDown += ProgressSlider_KeyDown;
        _transportBarView.ProgressSlider.ValueChanged += ProgressSlider_ValueChanged;
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
        _autoAdvanceMenuItem.Click += AutoAdvanceMenuItem_Click;

        _playbackModeFlyout.Items.Add(_listLoopModeItem);
        _playbackModeFlyout.Items.Add(_singleLoopModeItem);
        _playbackModeFlyout.Items.Add(_shuffleModeItem);
        _playbackModeFlyout.Items.Add(new MenuFlyoutSeparator());
        _playbackModeFlyout.Items.Add(_autoAdvanceMenuItem);
        UpdatePlaybackModeUi();
        UpdateFullScreenButtonUi();
        UpdateVolumeButtonUi();
        SetPassiveState(showImageNavigation: false);
    }

    public bool AreControlsVisible => _areControlsVisible;

    public bool IsSeeking => _isSeeking;

    public bool IsPlaying => _player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

    public bool IsShuffleMode => _playbackMode == PlaybackMode.Shuffle;

    public void Dispose()
    {
        _transportBarView.ProgressSlider.Loaded -= ProgressSlider_Loaded;
        _transportBarView.ProgressSlider.KeyDown -= ProgressSlider_KeyDown;
        _transportBarView.ProgressSlider.ValueChanged -= ProgressSlider_ValueChanged;
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
        _transportBarView.ProgressSlider.PointerCaptureLost -= ProgressSlider_PointerCaptureLost;
        _listLoopModeItem.Click -= PlaybackModeMenuItem_Click;
        _singleLoopModeItem.Click -= PlaybackModeMenuItem_Click;
        _shuffleModeItem.Click -= PlaybackModeMenuItem_Click;
        _autoAdvanceMenuItem.Click -= AutoAdvanceMenuItem_Click;
        _playbackTimer.Stop();
        _controlsHideTimer.Stop();

        if (_player != null)
        {
            _player.MediaOpened -= Player_MediaOpened;
            _player.MediaEnded -= Player_MediaEnded;
            _player.PlaybackSession.PlaybackStateChanged -= PlaybackSession_PlaybackStateChanged;
        }
    }

    public void ShowVideo(MediaItemViewModel media)
    {
        var player = EnsureMediaPlayer();
        LogPlaybackTrace($"ShowVideo file={media.FileName} path={media.FileSystemPath}");
        ShowVideoLoadingPreview(media);
        player.Source = MediaSource.CreateFromUri(new Uri(media.FileSystemPath));
        _videoPlayer.Visibility = Visibility.Visible;
        SetVideoState();
        ShowControls();
        _focusHost();
    }

    public void PauseAndClearSource()
    {
        if (_player == null)
        {
            return;
        }

        _player.Pause();
        _player.Source = null;
    }

    public void Clear()
    {
        PauseAndClearSource();
        CloseVolumeFlyout();
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
        PauseAndClearSource();
        CloseVolumeFlyout();
        _videoPlayer.Visibility = Visibility.Collapsed;
        HideVideoLoadingPreview();
        SetPassiveState(showImageNavigation: true);
    }

    public void ShowControls()
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

    public void HandlePointerExited()
    {
        RestartControlsHideTimer();
    }

    public void HideControlsImmediately()
    {
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
        _refreshNavigationHotspots();
        _controlsHideTimer.Stop();
    }

    public void TogglePlayPause()
    {
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
        if (_isTransportSuppressed == suppressed)
        {
            return;
        }

        _isTransportSuppressed = suppressed;
        CloseVolumeFlyout();

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
        LogPlaybackTrace($"SeekTo requested={requested.TotalSeconds:F3}s applied={position.TotalSeconds:F3}s duration={duration.TotalSeconds:F3}s");
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
        _currentTimeTextOverride = string.IsNullOrWhiteSpace(currentTimeText) ? null : currentTimeText;
        _totalTimeTextOverride = string.IsNullOrWhiteSpace(totalTimeText) ? null : totalTimeText;
        _progressValueOverrideSeconds = progressValueSeconds;
        _progressMaximumOverrideSeconds = progressMaximumSeconds;
        _displayToPlaybackPositionOverride = displayToPlaybackPosition;
        LogPlaybackTrace($"SetPlaybackDisplayOverride current={_currentTimeTextOverride ?? "<default>"} total={_totalTimeTextOverride ?? "<default>"} progressValue={_progressValueOverrideSeconds?.ToString("F3") ?? "<default>"} progressMax={_progressMaximumOverrideSeconds?.ToString("F3") ?? "<default>"} mapper={(_displayToPlaybackPositionOverride != null)}");
        UpdateTimeDisplay();
    }

    private void SetVideoState()
    {
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
        CloseVolumeFlyout();
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
        if (_player != null)
        {
            return _player;
        }

        _player = new MediaPlayer();
        _player.MediaOpened += Player_MediaOpened;
        _player.MediaEnded += Player_MediaEnded;
        _player.PlaybackSession.PlaybackStateChanged += PlaybackSession_PlaybackStateChanged;
        _player.Volume = _transportBarView.VolumeSlider.Value;
        _videoPlayer.SetMediaPlayer(_player);
        _playbackTimer.Start();
        return _player;
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
            _ => ("\uE8B1", "播放模式：随机播放")
        };

        SetButtonGlyph(_transportBarView.PlaybackModeButton, glyph);
        ToolTipService.SetToolTip(_transportBarView.PlaybackModeButton, description);
        _listLoopModeItem.IsChecked = _playbackMode == PlaybackMode.ListLoop;
        _singleLoopModeItem.IsChecked = _playbackMode == PlaybackMode.SingleLoop;
        _shuffleModeItem.IsChecked = _playbackMode == PlaybackMode.Shuffle;
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
        EnsureProgressSliderHandlers();
    }

    private void EnsureProgressSliderHandlers()
    {
        if (_progressSliderHandlersAttached)
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
        _isSeeking = true;
        ShowControls();
    }

    private void ProgressSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        CompleteSliderSeek();
    }

    private void ProgressSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_isSeeking)
        {
            CompleteSliderSeek();
        }
    }

    private void CompleteSliderSeek()
    {
        if (!_isSeeking)
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
        if (_player == null || !_isSeeking)
        {
            return;
        }

        var displayTarget = TimeSpan.FromSeconds(_transportBarView.ProgressSlider.Value);
        var target = _displayToPlaybackPositionOverride?.Invoke(displayTarget) ?? displayTarget;
        SeekTo(target);
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
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
        if (sender is not RadioMenuFlyoutItem item || item.Tag is not PlaybackMode mode || _playbackMode == mode)
        {
            return;
        }

        _playbackMode = mode;
        UpdatePlaybackModeUi();
        ShowControls();
    }

    private void AutoAdvanceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _autoAdvanceEnabled = _autoAdvanceMenuItem.IsChecked;
        UpdatePlaybackModeUi();
        ShowControls();
    }

    private void FullScreenButton_Click(object sender, RoutedEventArgs e)
    {
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
        ShowControls();
    }

    private void ControlBar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_viewModel.IsVolumeFlyoutOpen && !IsVolumeInteractionSource(e.OriginalSource as DependencyObject))
        {
            CloseVolumeFlyout();
        }

        ShowControls();
    }

    private void VolumeInteraction_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
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
        _controlsHideTimer.Stop();
        HideControls();
    }

    private void PlaybackTimer_Tick(object? sender, object e)
    {
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
        var duration = sender.PlaybackSession.NaturalDuration;
        _dispatcherQueue.TryEnqueue(() =>
        {
            HideVideoLoadingPreview();
            LogPlaybackTrace($"MediaOpened duration={duration.TotalSeconds:F3}s");
            _lastPlaybackPosition = TimeSpan.Zero;
            _lastPlaybackDuration = duration;
            UpdateTimeDisplay();
            _handleMediaOpened(duration);
            _notifyPlaybackProgressChanged();
        });
    }

    private void Player_MediaEnded(MediaPlayer sender, object args)
    {
        _dispatcherQueue.TryEnqueue(async () =>
        {
            if (_libraryViewModel.SelectedMedia == null)
            {
                return;
            }

            if (!_autoAdvanceEnabled || _playbackMode == PlaybackMode.SingleLoop)
            {
                sender.PlaybackSession.Position = TimeSpan.Zero;
                sender.Play();
                return;
            }

            if (_playbackMode == PlaybackMode.Shuffle)
            {
                if (!TrySelectRandomMedia())
                {
                    sender.PlaybackSession.Position = TimeSpan.Zero;
                    sender.Play();
                }

                return;
            }

            await _navigateRelativeAsync(1);
        });
    }

    private void PlaybackSession_PlaybackStateChanged(MediaPlaybackSession sender, object args)
    {
        _dispatcherQueue.TryEnqueue(UpdatePlayPauseState);
    }

    public bool TrySelectRandomMedia()
    {
        var list = _libraryViewModel.FilteredMediaItems;
        if (list.Count == 0 || _libraryViewModel.SelectedMedia == null)
        {
            return false;
        }

        var currentMediaId = _libraryViewModel.SelectedMedia.Id;
        var candidates = list
            .Where(item => !string.Equals(item.Id, currentMediaId, StringComparison.Ordinal))
            .ToList();
        if (candidates.Count == 0)
        {
            return false;
        }

        _libraryViewModel.SelectedMedia = candidates[_random.Next(candidates.Count)];
        return true;
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
        _videoViewportView.VideoLoadingPreviewImage.Source = media.Thumbnail;
        _videoViewportView.VideoLoadingPreviewHost.Visibility = media.Thumbnail == null
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void HideVideoLoadingPreview()
    {
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

}
