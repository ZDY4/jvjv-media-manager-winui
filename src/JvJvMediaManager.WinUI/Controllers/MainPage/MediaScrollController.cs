namespace JvJvMediaManager.Controllers.MainPage;

internal sealed class MediaScrollController
{
    private int _requestId;
    private int _pendingRequestId;
    private string? _pendingMediaId;
    private string? _pendingReason;
    private string? _lastCandidateId;
    private long _lastCandidateTicks;
    private string? _recentUserVisibleSelectionId;
    private long _recentUserVisibleSelectionTicks;

    public int RequestId => Volatile.Read(ref _requestId);
    public int PendingRequestId => Volatile.Read(ref _pendingRequestId);
    public string? PendingMediaId => _pendingMediaId;
    public string? PendingReason => _pendingReason;

    public int StartRequest(string mediaId, string reason)
    {
        var requestId = Interlocked.Increment(ref _requestId);
        StorePending(requestId, mediaId, reason);
        RememberCandidate(mediaId);
        return requestId;
    }

    public bool IsCurrentRequest(int requestId)
    {
        return requestId == RequestId;
    }

    public void StorePending(int requestId, string mediaId, string reason)
    {
        Volatile.Write(ref _pendingRequestId, requestId);
        _pendingMediaId = mediaId;
        _pendingReason = reason;
    }

    public void ClearPending(int requestId, string mediaId)
    {
        if (PendingRequestId == requestId
            && string.Equals(_pendingMediaId, mediaId, StringComparison.Ordinal))
        {
            Volatile.Write(ref _pendingRequestId, 0);
            _pendingMediaId = null;
            _pendingReason = null;
        }
    }

    public void StoreRecentUserVisibleSelection(string mediaId)
    {
        RememberCandidate(mediaId);
        _recentUserVisibleSelectionId = mediaId;
        _recentUserVisibleSelectionTicks = DateTime.UtcNow.Ticks;
    }

    public void RememberCandidate(string mediaId)
    {
        _lastCandidateId = mediaId;
        _lastCandidateTicks = DateTime.UtcNow.Ticks;
    }

    public bool ShouldVerifyAfterItemsChanged(string mediaId, TimeSpan window)
    {
        return string.Equals(_pendingMediaId, mediaId, StringComparison.Ordinal)
            || IsRecentMediaId(_lastCandidateId, _lastCandidateTicks, mediaId, window)
            || IsRecentMediaId(_recentUserVisibleSelectionId, _recentUserVisibleSelectionTicks, mediaId, window);
    }

    public bool ShouldSuppressRevealForRecentUserSelection(string operationName, string mediaId, TimeSpan window)
    {
        return string.Equals(operationName, "RevealSelectedMedia", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(_recentUserVisibleSelectionId)
            && string.Equals(_recentUserVisibleSelectionId, mediaId, StringComparison.Ordinal)
            && IsRecentMediaId(_recentUserVisibleSelectionId, _recentUserVisibleSelectionTicks, mediaId, window);
    }

    private static bool IsRecentMediaId(string? storedMediaId, long storedTicks, string mediaId, TimeSpan window)
    {
        if (string.IsNullOrWhiteSpace(storedMediaId) || !string.Equals(storedMediaId, mediaId, StringComparison.Ordinal))
        {
            return false;
        }

        var elapsedTicks = DateTime.UtcNow.Ticks - storedTicks;
        return elapsedTicks >= 0 && elapsedTicks <= window.Ticks;
    }
}
