using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.ViewModels.MainPage;

internal enum LibraryRefreshKind
{
    FullRefresh,
    FolderSummaryRefresh,
    LoadedWindowRefresh,
    BackgroundMetadataRefresh
}

internal enum LibraryRefreshSource
{
    UserFilter,
    WatchedFolderSettings,
    FolderWatcher,
    BackgroundMetadata,
    ScanIncremental,
    ScanFinalize,
    Unknown
}

internal sealed class LibraryRefreshScheduler : IDisposable
{
    private readonly Func<LibraryRefreshRequest, Task> _refreshAsync;
    private readonly object _sync = new();
    private LibraryRefreshRequest? _pendingRequest;
    private DateTimeOffset? _pendingRequestedAt;
    private CancellationTokenSource? _debounce;
    private int _processing;
    private bool _isDisposed;

    public LibraryRefreshScheduler(Func<LibraryRefreshRequest, Task> refreshAsync)
    {
        _refreshAsync = refreshAsync;
    }

    public void Request(LibraryRefreshRequest request)
    {
        CancellationTokenSource? debounceToCancel;
        CancellationTokenSource? debounceToRun = null;
        var shouldStartImmediately = false;
        var rejected = false;
        lock (_sync)
        {
            if (_isDisposed)
            {
                rejected = true;
                debounceToCancel = null;
            }
            else
            {
                _pendingRequest = _pendingRequest == null
                    ? request
                    : _pendingRequest.Value.Merge(request);
                _pendingRequestedAt ??= DateTimeOffset.UtcNow;
                debounceToCancel = _debounce;
                _debounce = null;

                if (Interlocked.CompareExchange(ref _processing, 0, 0) == 0)
                {
                    var delay = GetDebounceDelay(_pendingRequest.Value);
                    if (delay > TimeSpan.Zero)
                    {
                        debounceToRun = new CancellationTokenSource();
                        _debounce = debounceToRun;
                    }
                    else
                    {
                        shouldStartImmediately = true;
                    }
                }
            }
        }

        if (rejected)
        {
            request.CompleteCanceled();
            return;
        }

        debounceToCancel?.Cancel();
        AppTraceLogger.LogSampled(
            "LibraryRefreshScheduler",
            "refresh-requested",
            $"Refresh requested. Kind={request.Kind}, Source={request.Source}, PreserveSelection={request.PreserveSelection}, UpdateLoadingState={request.UpdateLoadingState}, DebounceMs={GetDebounceDelay(request).TotalMilliseconds:0}.",
            TimeSpan.FromSeconds(1));
        if (debounceToRun != null)
        {
            _ = ProcessAfterDelayAsync(debounceToRun);
            return;
        }

        if (shouldStartImmediately)
        {
            _ = ProcessAsync();
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? debounce;
        LibraryRefreshRequest? pendingRequest;
        lock (_sync)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            pendingRequest = _pendingRequest;
            _pendingRequest = null;
            _pendingRequestedAt = null;
            debounce = _debounce;
            _debounce = null;
        }

        pendingRequest?.CompleteCanceled();
        debounce?.Cancel();
    }

    private async Task ProcessAfterDelayAsync(CancellationTokenSource debounce)
    {
        try
        {
            LibraryRefreshRequest? request;
            lock (_sync)
            {
                if (_isDisposed)
                {
                    return;
                }

                request = _pendingRequest;
            }

            if (request == null)
            {
                return;
            }

            var delay = GetDebounceDelay(request.Value);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, debounce.Token);
            }

            lock (_sync)
            {
                if (_isDisposed || !ReferenceEquals(_debounce, debounce))
                {
                    return;
                }

                _debounce = null;
            }

            _ = ProcessAsync();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer refresh request.
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_debounce, debounce))
                {
                    _debounce = null;
                }
            }

            debounce.Dispose();
        }
    }

    private async Task ProcessAsync()
    {
        lock (_sync)
        {
            if (_isDisposed)
            {
                return;
            }
        }

        if (Interlocked.Exchange(ref _processing, 1) == 1)
        {
            return;
        }

        try
        {
            LibraryRefreshRequest request;
            DateTimeOffset? requestedAt;
            lock (_sync)
            {
                if (_isDisposed)
                {
                    return;
                }

                if (_pendingRequest == null)
                {
                    return;
                }

                request = _pendingRequest.Value;
                requestedAt = _pendingRequestedAt;
                _pendingRequest = null;
                _pendingRequestedAt = null;
            }

            try
            {
                var waitMs = requestedAt.HasValue
                    ? (DateTimeOffset.UtcNow - requestedAt.Value).TotalMilliseconds
                    : 0;
                AppTraceLogger.LogSampled(
                    "LibraryRefreshScheduler",
                    "refresh-dispatch",
                    $"Dispatching refresh. Kind={request.Kind}, Source={request.Source}, PreserveSelection={request.PreserveSelection}, UpdateLoadingState={request.UpdateLoadingState}, WaitMs={waitMs:0}.",
                    TimeSpan.FromSeconds(1));
                await _refreshAsync(request);
                request.CompleteSuccessfully();
            }
            catch (Exception ex)
            {
                AppTraceLogger.LogException(
                    "LibraryRefreshScheduler",
                    $"Scheduled refresh failed. Kind={request.Kind}, Source={request.Source}.",
                    ex);
                request.CompleteWithException(ex);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _processing, 0);
            SchedulePendingAfterProcessing();
        }
    }

    private void SchedulePendingAfterProcessing()
    {
        CancellationTokenSource? debounceToRun = null;
        var runImmediately = false;
        lock (_sync)
        {
            if (_isDisposed || _pendingRequest == null)
            {
                return;
            }

            var delay = GetDebounceDelay(_pendingRequest.Value);
            if (delay > TimeSpan.Zero)
            {
                debounceToRun = new CancellationTokenSource();
                _debounce = debounceToRun;
            }
            else
            {
                runImmediately = true;
            }
        }

        if (debounceToRun != null)
        {
            _ = ProcessAfterDelayAsync(debounceToRun);
        }
        else if (runImmediately)
        {
            _ = ProcessAsync();
        }
    }

    private static TimeSpan GetDebounceDelay(LibraryRefreshRequest request)
    {
        if (request.Source == LibraryRefreshSource.ScanFinalize
            || request.Source == LibraryRefreshSource.ScanIncremental)
        {
            return TimeSpan.Zero;
        }

        return request.Source switch
        {
            LibraryRefreshSource.UserFilter => TimeSpan.FromMilliseconds(180),
            LibraryRefreshSource.FolderWatcher => TimeSpan.FromMilliseconds(350),
            LibraryRefreshSource.BackgroundMetadata => TimeSpan.FromMilliseconds(500),
            LibraryRefreshSource.WatchedFolderSettings => TimeSpan.FromMilliseconds(120),
            _ => TimeSpan.Zero
        };
    }
}

internal readonly record struct LibraryRefreshRequest(
    LibraryRefreshKind Kind,
    LibraryRefreshSource Source,
    bool PreserveSelection,
    bool UpdateLoadingState,
    TaskCompletionSource? Completion = null)
{
    public LibraryRefreshRequest Merge(LibraryRefreshRequest next)
    {
        var currentRefreshRank = RefreshRank(Kind);
        var nextRefreshRank = RefreshRank(next.Kind);
        var selected = nextRefreshRank > currentRefreshRank
            ? next
            : nextRefreshRank < currentRefreshRank
                ? this
                : SourceRank(next.Source) >= SourceRank(Source)
                    ? next
                    : this;
        return new LibraryRefreshRequest(
            selected.Kind,
            selected.Source,
            PreserveSelection || next.PreserveSelection,
            UpdateLoadingState || next.UpdateLoadingState,
            MergeCompletion(Completion, next.Completion));
    }

    public void CompleteSuccessfully()
    {
        Completion?.TrySetResult();
    }

    public void CompleteWithException(Exception ex)
    {
        Completion?.TrySetException(ex);
    }

    public void CompleteCanceled()
    {
        Completion?.TrySetCanceled();
    }

    private static TaskCompletionSource? MergeCompletion(TaskCompletionSource? first, TaskCompletionSource? second)
    {
        if (first == null)
        {
            return second;
        }

        if (second == null)
        {
            return first;
        }

        var merged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = merged.Task.ContinueWith(
            task =>
            {
                if (task.IsFaulted)
                {
                    var exception = task.Exception?.InnerException;
                    if (exception == null)
                    {
                        exception = task.Exception;
                    }

                    if (exception == null)
                    {
                        exception = new InvalidOperationException("Scheduled refresh failed.");
                    }

                    first.TrySetException(exception);
                    second.TrySetException(exception);
                    return;
                }

                if (task.IsCanceled)
                {
                    first.TrySetCanceled();
                    second.TrySetCanceled();
                    return;
                }

                first.TrySetResult();
                second.TrySetResult();
            },
            TaskScheduler.Default);
        return merged;
    }

    private static int RefreshRank(LibraryRefreshKind kind)
    {
        return kind switch
        {
            LibraryRefreshKind.FullRefresh => 4,
            LibraryRefreshKind.FolderSummaryRefresh => 3,
            LibraryRefreshKind.LoadedWindowRefresh => 2,
            LibraryRefreshKind.BackgroundMetadataRefresh => 1,
            _ => 0
        };
    }

    private static int SourceRank(LibraryRefreshSource source)
    {
        return source switch
        {
            LibraryRefreshSource.ScanFinalize => 6,
            LibraryRefreshSource.ScanIncremental => 5,
            LibraryRefreshSource.UserFilter => 4,
            LibraryRefreshSource.WatchedFolderSettings => 3,
            LibraryRefreshSource.FolderWatcher => 2,
            LibraryRefreshSource.BackgroundMetadata => 1,
            _ => 0
        };
    }
}
