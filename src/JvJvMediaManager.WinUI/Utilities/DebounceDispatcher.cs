using Microsoft.UI.Dispatching;

namespace JvJvMediaManager.Utilities;

public sealed class DebounceDispatcher
{
    private DispatcherQueueTimer? _timer;

    public void Debounce(TimeSpan delay, Action action)
    {
        _timer?.Stop();
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = delay;
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) =>
        {
            _timer?.Stop();
            action();
        };
        _timer.Start();
    }
}
