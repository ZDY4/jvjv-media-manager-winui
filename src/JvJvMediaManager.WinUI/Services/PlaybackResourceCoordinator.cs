using System.Collections.Concurrent;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.Services;

public enum PlaybackActivityState
{
    Inactive,
    Opening,
    Buffering,
    Playing
}

public interface IPlaybackActivityLease : IDisposable
{
    void SetState(PlaybackActivityState state);
}

public sealed class PlaybackResourceCoordinator : IDisposable
{
    private static readonly TimeSpan ActivePlaybackDelay = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _activePlaybackBackgroundGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly ConcurrentDictionary<Lease, byte> _leases = new();
    private readonly object _lifecycleSync = new();
    private int _activeStateLeaseCount;
    private bool _disposed;

    public bool IsPlaybackActive => Volatile.Read(ref _activeStateLeaseCount) > 0;

    public int ActiveLeaseCount => _leases.Count;

    public IPlaybackActivityLease Acquire(string ownerId)
    {
        Lease lease;
        lock (_lifecycleSync)
        {
            ThrowIfDisposed();
            lease = new Lease(this, ownerId);
            _leases[lease] = 0;
        }
        AppTraceLogger.LogSampled(
            "PlaybackResources",
            $"lease-acquired-{ownerId}",
            $"Playback lease acquired. Owner={ownerId}, ActiveLeases={_leases.Count}.",
            LogInterval);
        return lease;
    }

    public async Task<IDisposable?> EnterBackgroundIoAsync(string operationName, CancellationToken cancellationToken = default)
    {
        CancellationToken shutdownToken;
        lock (_lifecycleSync)
        {
            ThrowIfDisposed();
            shutdownToken = _disposeCancellation.Token;
        }

        if (!IsPlaybackActive)
        {
            return null;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdownToken);
        cancellationToken = linkedCancellation.Token;

        var waitStartedAt = Environment.TickCount64;
        AppTraceLogger.LogSampled(
            "PlaybackResources",
            $"background-throttled-{operationName}",
            $"Background IO throttled while playback is active. Operation={operationName}.",
            LogInterval);

        await Task.Delay(ActivePlaybackDelay, cancellationToken);
        await _activePlaybackBackgroundGate.WaitAsync(cancellationToken);
        // Playback may have stopped while we were waiting. Avoid holding the
        // gate (and delaying otherwise idle work) when throttling is no longer
        // needed. The second disposed check also closes the shutdown race.
        if (Volatile.Read(ref _disposed) || !IsPlaybackActive)
        {
            _activePlaybackBackgroundGate.Release();
            return null;
        }

        var waitMs = Environment.TickCount64 - waitStartedAt;
        AppTraceLogger.LogSampled(
            "PlaybackResources",
            $"background-entered-{operationName}",
            $"Background IO entered playback throttle gate. Operation={operationName}, WaitMs={waitMs}.",
            LogInterval);
        return new GateLease(_activePlaybackBackgroundGate, operationName);
    }

    public int GetScanDegreeOfParallelism(int defaultDegree)
    {
        return IsPlaybackActive ? 1 : defaultDegree;
    }

    public void Dispose()
    {
        lock (_lifecycleSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        // Cancel callers waiting to enter the throttle gate. Do not dispose the
        // semaphore here: an operation that already acquired it can still
        // release its GateLease after service-provider shutdown begins.
        try
        {
            _disposeCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The CTS intentionally remains undisposed for late waiters.
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PlaybackResourceCoordinator));
        }
    }

    private static bool IsActiveState(PlaybackActivityState state)
        => state is PlaybackActivityState.Opening or PlaybackActivityState.Buffering or PlaybackActivityState.Playing;

    private void OnLeaseStateChanged(bool wasActive, bool isActive)
    {
        if (wasActive == isActive)
        {
            return;
        }

        if (isActive)
        {
            Interlocked.Increment(ref _activeStateLeaseCount);
        }
        else
        {
            Interlocked.Decrement(ref _activeStateLeaseCount);
        }
    }

    private void OnLeaseReleased(Lease lease, bool wasActive)
    {
        if (_leases.TryRemove(lease, out _))
        {
            if (wasActive)
            {
                Interlocked.Decrement(ref _activeStateLeaseCount);
            }

            AppTraceLogger.LogSampled(
                "PlaybackResources",
                $"lease-released-{lease.OwnerId}",
                $"Playback lease released. Owner={lease.OwnerId}, ActiveLeases={_leases.Count}.",
                LogInterval);
        }
    }

    private sealed class Lease : IPlaybackActivityLease
    {
        private readonly PlaybackResourceCoordinator _coordinator;
        private readonly object _sync = new();
        private PlaybackActivityState _state = PlaybackActivityState.Inactive;
        private bool _released;

        public Lease(PlaybackResourceCoordinator coordinator, string ownerId)
        {
            _coordinator = coordinator;
            OwnerId = string.IsNullOrWhiteSpace(ownerId) ? "unknown" : ownerId;
        }

        public string OwnerId { get; }

        public void SetState(PlaybackActivityState state)
        {
            lock (_sync)
            {
                if (_released)
                {
                    return;
                }

                var wasActive = IsActiveState(_state);
                var isActive = IsActiveState(state);
                _state = state;
                _coordinator.OnLeaseStateChanged(wasActive, isActive);
            }
        }

        public void Dispose()
        {
            bool wasActive;
            lock (_sync)
            {
                if (_released)
                {
                    return;
                }

                _released = true;
                wasActive = IsActiveState(_state);
                _state = PlaybackActivityState.Inactive;
            }

            _coordinator.OnLeaseReleased(this, wasActive);
        }
    }

    private sealed class GateLease : IDisposable
    {
        private readonly string _operationName;
        private readonly long _startedAt;
        private SemaphoreSlim? _gate;

        public GateLease(SemaphoreSlim gate, string operationName)
        {
            _gate = gate;
            _operationName = operationName;
            _startedAt = Environment.TickCount64;
        }

        public void Dispose()
        {
            var gate = Interlocked.Exchange(ref _gate, null);
            if (gate == null)
            {
                return;
            }

            gate.Release();
            var heldMs = Environment.TickCount64 - _startedAt;
            if (heldMs >= 250)
            {
                AppTraceLogger.LogSampled(
                    "PlaybackResources",
                    $"background-held-{_operationName}",
                    $"Background IO held playback throttle gate. Operation={_operationName}, HeldMs={heldMs}.",
                    LogInterval);
            }
        }
    }
}
