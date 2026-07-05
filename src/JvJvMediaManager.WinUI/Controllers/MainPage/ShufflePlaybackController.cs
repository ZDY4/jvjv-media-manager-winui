using JvJvMediaManager.Models;
using JvJvMediaManager.ViewModels;

namespace JvJvMediaManager.Controllers.MainPage;

internal sealed class ShufflePlaybackController
{
    private const int MaxHistoryCount = 200;
    private readonly List<string> _backHistory = new();
    private readonly List<string> _forwardHistory = new();
    private readonly Func<bool> _isVideoOnly;
    private readonly Action<string> _log;

    public ShufflePlaybackController(Func<bool> isVideoOnly, Action<string> log)
    {
        _isVideoOnly = isVideoOnly;
        _log = log;
    }

    public int BackCount => _backHistory.Count;

    public int ForwardCount => _forwardHistory.Count;

    public IReadOnlyCollection<string> BuildRandomExcludedIds(MediaItemViewModel current, bool includeHistory)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal)
        {
            current.Id
        };

        if (!includeHistory)
        {
            return ids;
        }

        foreach (var mediaId in _backHistory)
        {
            ids.Add(mediaId);
        }

        foreach (var mediaId in _forwardHistory)
        {
            ids.Add(mediaId);
        }

        return ids;
    }

    public bool TryPopBack(out string mediaId)
    {
        return TryPop(_backHistory, out mediaId);
    }

    public bool TryPopForward(out string mediaId)
    {
        return TryPop(_forwardHistory, out mediaId);
    }

    public void RecordRandomSelection(MediaItemViewModel previous)
    {
        if (IsHistoryEligible(previous))
        {
            Push(_backHistory, previous.Id);
        }

        ClearForwardHistory("shuffle-random");
    }

    public void RecordHistorySelection(MediaItemViewModel previous, bool navigatingBack)
    {
        if (!IsHistoryEligible(previous))
        {
            return;
        }

        Push(navigatingBack ? _forwardHistory : _backHistory, previous.Id);
    }

    public bool IsHistoryEligible(MediaItemViewModel media)
    {
        return !_isVideoOnly() || media.Type == MediaType.Video;
    }

    public void ClearHistory(string reason)
    {
        var backCount = _backHistory.Count;
        var forwardCount = _forwardHistory.Count;
        if (backCount == 0 && forwardCount == 0)
        {
            return;
        }

        _backHistory.Clear();
        _forwardHistory.Clear();
        _log($"Shuffle history cleared. Reason={reason}, PreviousBackCount={backCount}, PreviousForwardCount={forwardCount}.");
    }

    private void ClearForwardHistory(string reason)
    {
        if (_forwardHistory.Count == 0)
        {
            return;
        }

        var forwardCount = _forwardHistory.Count;
        _forwardHistory.Clear();
        _log($"Shuffle forward history cleared. Reason={reason}, PreviousForwardCount={forwardCount}.");
    }

    private static bool TryPop(List<string> history, out string mediaId)
    {
        if (history.Count == 0)
        {
            mediaId = string.Empty;
            return false;
        }

        var index = history.Count - 1;
        mediaId = history[index];
        history.RemoveAt(index);
        return true;
    }

    private static void Push(List<string> history, string mediaId)
    {
        history.Add(mediaId);
        if (history.Count > MaxHistoryCount)
        {
            history.RemoveRange(0, history.Count - MaxHistoryCount);
        }
    }
}
