using System.Diagnostics;
using JvJvMediaManager.Utilities;
using Windows.System;

namespace JvJvMediaManager.Controllers.MainPage;

internal sealed class BossKeyController
{
    private readonly Action _enterBossMode;
    private readonly Action _exitBossMode;
    private readonly Func<bool> _isDisposed;

    private int _isBossMode;
    private long _lastEscapePressTimestamp;
    private static readonly TimeSpan DoubleEscInterval = TimeSpan.FromMilliseconds(500);

    public bool IsBossMode => Volatile.Read(ref _isBossMode) == 1;

    public BossKeyController(
        Action enterBossMode,
        Action exitBossMode,
        Func<bool> isDisposed)
    {
        _enterBossMode = enterBossMode;
        _exitBossMode = exitBossMode;
        _isDisposed = isDisposed;
    }

    public bool ShouldHandleBossKey(VirtualKey key, bool ctrlDown)
    {
        if (_isDisposed())
        {
            return false;
        }

        // Ctrl+W triggers boss mode
        if (ctrlDown && (key == (VirtualKey)87)) // 'W'
        {
            return true;
        }

        // Double ESC triggers boss mode
        if (key == VirtualKey.Escape)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastEscapePressTimestamp));
            Interlocked.Exchange(ref _lastEscapePressTimestamp, now);

            if (_lastEscapePressTimestamp != 0 && elapsed <= DoubleEscInterval)
            {
                Interlocked.Exchange(ref _lastEscapePressTimestamp, 0);
                return true;
            }

            return false;
        }

        return false;
    }

    public void HandleBossKeyTrigger()
    {
        if (_isDisposed())
        {
            return;
        }

        if (Volatile.Read(ref _isBossMode) == 1)
        {
            return;
        }

        Interlocked.Exchange(ref _isBossMode, 1);
        AppTraceLogger.Log("BossKey", "Entering boss mode.");
        _enterBossMode();
    }

    public void ExitBossMode()
    {
        if (_isDisposed())
        {
            return;
        }

        if (Interlocked.Exchange(ref _isBossMode, 0) == 0)
        {
            return;
        }

        AppTraceLogger.Log("BossKey", "Exiting boss mode.");
        _exitBossMode();
    }
}
