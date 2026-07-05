namespace JvJvMediaManager.Controllers.MainPage;

internal sealed class PlaybackWatchdogController
{
    private static readonly TimeSpan PlaybackStartWatchdogDelay = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan VideoSurfaceRevealPollInterval = TimeSpan.FromMilliseconds(50);
    private const int MaxVideoSurfaceRevealAttempts = 100;

    private readonly Func<int, bool> _isCurrentSource;
    private readonly Func<bool> _isDisposed;
    private readonly Action<int> _verifyPlaybackStartedOnUiThread;
    private readonly Func<int, int, bool, Task<bool>> _tryRevealVideoSurfaceOnUiThreadAsync;
    private readonly Action<string> _log;
    private int _playbackStartWatchdogVersion;
    private int _videoSurfaceRevealRequestVersion;

    public PlaybackWatchdogController(
        Func<int, bool> isCurrentSource,
        Func<bool> isDisposed,
        Action<int> verifyPlaybackStartedOnUiThread,
        Func<int, int, bool, Task<bool>> tryRevealVideoSurfaceOnUiThreadAsync,
        Action<string> log)
    {
        _isCurrentSource = isCurrentSource;
        _isDisposed = isDisposed;
        _verifyPlaybackStartedOnUiThread = verifyPlaybackStartedOnUiThread;
        _tryRevealVideoSurfaceOnUiThreadAsync = tryRevealVideoSurfaceOnUiThreadAsync;
        _log = log;
    }

    public void SchedulePlaybackStartWatchdog(int mediaSourceVersion)
    {
        if (Interlocked.Exchange(ref _playbackStartWatchdogVersion, mediaSourceVersion) == mediaSourceVersion)
        {
            return;
        }

        _ = VerifyPlaybackStartedAsync(mediaSourceVersion);
    }

    public void ScheduleVideoSurfaceReveal(int mediaSourceVersion, string reason)
    {
        if (!_isCurrentSource(mediaSourceVersion))
        {
            return;
        }

        if (Interlocked.Exchange(ref _videoSurfaceRevealRequestVersion, mediaSourceVersion) == mediaSourceVersion)
        {
            return;
        }

        _log($"Video surface reveal scheduled. SourceVersion={mediaSourceVersion}, Reason={reason}.");
        _ = RevealVideoSurfaceAsync(mediaSourceVersion);
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

        if (_isDisposed() || !_isCurrentSource(mediaSourceVersion))
        {
            return;
        }

        _verifyPlaybackStartedOnUiThread(mediaSourceVersion);
    }

    private async Task RevealVideoSurfaceAsync(int mediaSourceVersion)
    {
        try
        {
            for (var attempt = 1; attempt <= MaxVideoSurfaceRevealAttempts; attempt++)
            {
                if (_isDisposed())
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

                if (await _tryRevealVideoSurfaceOnUiThreadAsync(mediaSourceVersion, attempt, attempt == MaxVideoSurfaceRevealAttempts))
                {
                    return;
                }
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _videoSurfaceRevealRequestVersion, 0, mediaSourceVersion);
        }
    }
}
