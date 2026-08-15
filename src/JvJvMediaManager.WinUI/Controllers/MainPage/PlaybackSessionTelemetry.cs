using System.Diagnostics;
using JvJvMediaManager.Utilities;
using JvJvMediaManager.ViewModels;

namespace JvJvMediaManager.Controllers.MainPage;

// WP-10 (11.4): owns the playback switch/first-frame/surface timing state and the
// structured switch trace. Extracted from VideoPlaybackController so the player
// controller keeps only source/player lifecycle and transport concerns. Log
// output is byte-for-byte identical to the previous inline implementation.
internal sealed class PlaybackSessionTelemetry
{
    private readonly Func<MediaItemViewModel?> _getSelectedMedia;

    private long _sourceLoadStartTimestamp;
    private int _sourceLoadStartVersion;
    private long _sourceAssignedTimestamp;
    private long _switchSelectionChangedTimestamp;
    private long _firstPlayingTimestamp;
    private int _switchTraceId;
    private int _switchTraceSourceVersion;
    private bool _firstPlayingLoggedForSource;

    public PlaybackSessionTelemetry(Func<MediaItemViewModel?> getSelectedMedia)
    {
        _getSelectedMedia = getSelectedMedia;
    }

    public long SourceAssignedTimestamp => _sourceAssignedTimestamp;

    public long SwitchSelectionChangedTimestamp => _switchSelectionChangedTimestamp;

    public long FirstPlayingTimestamp => _firstPlayingTimestamp;

    public bool FirstPlayingLoggedForSource => _firstPlayingLoggedForSource;

    public void BeginSwitch(long showVideoStartTimestamp, int mediaSourceVersion, long selectionChangedTimestamp, int switchTraceId)
    {
        _sourceLoadStartTimestamp = showVideoStartTimestamp;
        _sourceLoadStartVersion = mediaSourceVersion;
        _sourceAssignedTimestamp = 0;
        _firstPlayingTimestamp = 0;
        _switchSelectionChangedTimestamp = selectionChangedTimestamp;
        _switchTraceId = switchTraceId;
        _switchTraceSourceVersion = mediaSourceVersion;
        _firstPlayingLoggedForSource = false;
    }

    public void MarkSourceAssigned()
    {
        _sourceAssignedTimestamp = Stopwatch.GetTimestamp();
    }

    public bool TryMarkFirstPlaying()
    {
        if (_firstPlayingLoggedForSource)
        {
            return false;
        }

        _firstPlayingLoggedForSource = true;
        _firstPlayingTimestamp = Stopwatch.GetTimestamp();
        return true;
    }

    public double GetSourceLoadElapsedMilliseconds(int sourceVersion)
    {
        if (_sourceLoadStartTimestamp <= 0 || _sourceLoadStartVersion != sourceVersion)
        {
            return -1;
        }

        return Stopwatch.GetElapsedTime(_sourceLoadStartTimestamp).TotalMilliseconds;
    }

    public void LogSwitchTrace(int mediaSourceVersion, string stage, string details)
    {
        if (mediaSourceVersion != _switchTraceSourceVersion)
        {
            return;
        }

        var media = _getSelectedMedia();
        AppTraceLogger.Log(
            "PlayerSwitch",
            $"TraceId={_switchTraceId} SourceVersion={mediaSourceVersion} Stage={stage} MediaId='{media?.Id ?? "<null>"}', Type='{media?.Type.ToString() ?? "<null>"}', File='{media?.FileName ?? "<null>"}'. {details}");
    }

    public static string FormatElapsedMilliseconds(long startTimestamp)
    {
        return startTimestamp <= 0
            ? "<none>"
            : Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds.ToString("0.###");
    }
}
