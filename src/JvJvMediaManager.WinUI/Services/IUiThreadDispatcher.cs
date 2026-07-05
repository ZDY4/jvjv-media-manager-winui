using Microsoft.UI.Dispatching;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.Services;

public interface IUiThreadDispatcher
{
    bool HasThreadAccess { get; }

    bool Enqueue(Action action, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal, string? failureContext = null);

    Task EnqueueAsync(Action action, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal, string? failureContext = null);

    Task<T> EnqueueAsync<T>(Func<T> action, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal, string? failureContext = null);

    void EnqueueSafeAsync(Func<Task> action, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal, string? failureContext = null);
}

public sealed class UiThreadDispatcher : IUiThreadDispatcher
{
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly string _logCategory;

    public UiThreadDispatcher(DispatcherQueue? dispatcherQueue, string logCategory = "UiThreadDispatcher")
    {
        _dispatcherQueue = dispatcherQueue;
        _logCategory = string.IsNullOrWhiteSpace(logCategory) ? "UiThreadDispatcher" : logCategory;
    }

    public bool HasThreadAccess => _dispatcherQueue == null || _dispatcherQueue.HasThreadAccess;

    public bool Enqueue(Action action, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal, string? failureContext = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (HasThreadAccess)
        {
            action();
            return true;
        }

        var queued = _dispatcherQueue!.TryEnqueue(priority, () =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                AppTraceLogger.LogException(_logCategory, failureContext ?? "UI callback failed.", ex);
            }
        });

        if (!queued)
        {
            AppTraceLogger.Log(_logCategory, $"DispatcherQueue rejected callback. Context='{failureContext ?? "<none>"}'.");
        }

        return queued;
    }

    public Task EnqueueAsync(Action action, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal, string? failureContext = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = _dispatcherQueue!.TryEnqueue(priority, () =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                AppTraceLogger.LogException(_logCategory, failureContext ?? "UI callback failed.", ex);
                tcs.SetException(ex);
            }
        });

        if (!queued)
        {
            AppTraceLogger.Log(_logCategory, $"DispatcherQueue rejected callback. Context='{failureContext ?? "<none>"}'.");
            tcs.SetException(new InvalidOperationException("无法切换到 UI 线程。"));
        }

        return tcs.Task;
    }

    public Task<T> EnqueueAsync<T>(Func<T> action, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal, string? failureContext = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (HasThreadAccess)
        {
            return Task.FromResult(action());
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = _dispatcherQueue!.TryEnqueue(priority, () =>
        {
            try
            {
                tcs.SetResult(action());
            }
            catch (Exception ex)
            {
                AppTraceLogger.LogException(_logCategory, failureContext ?? "UI callback failed.", ex);
                tcs.SetException(ex);
            }
        });

        if (!queued)
        {
            AppTraceLogger.Log(_logCategory, $"DispatcherQueue rejected callback. Context='{failureContext ?? "<none>"}'.");
            tcs.SetException(new InvalidOperationException("无法切换到 UI 线程。"));
        }

        return tcs.Task;
    }

    public void EnqueueSafeAsync(Func<Task> action, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal, string? failureContext = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        var queued = Enqueue(
            () => _ = RunSafelyAsync(action, failureContext),
            priority,
            failureContext);
        if (!queued && HasThreadAccess)
        {
            _ = RunSafelyAsync(action, failureContext);
        }
    }

    private async Task RunSafelyAsync(Func<Task> action, string? failureContext)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException(_logCategory, failureContext ?? "Async UI callback failed.", ex);
        }
    }
}
