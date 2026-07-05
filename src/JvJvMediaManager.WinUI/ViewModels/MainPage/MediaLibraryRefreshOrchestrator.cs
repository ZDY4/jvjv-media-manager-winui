namespace JvJvMediaManager.ViewModels.MainPage;

internal sealed class MediaLibraryRefreshOrchestrator : IDisposable
{
    private readonly LibraryRefreshScheduler _scheduler;

    public MediaLibraryRefreshOrchestrator(Func<LibraryRefreshRequest, Task> refreshAsync)
    {
        _scheduler = new LibraryRefreshScheduler(refreshAsync);
    }

    public void Request(
        LibraryRefreshKind kind,
        LibraryRefreshSource source,
        bool preserveSelection,
        bool updateLoadingState,
        TaskCompletionSource? completion = null)
    {
        _scheduler.Request(new LibraryRefreshRequest(kind, source, preserveSelection, updateLoadingState, completion));
    }

    public Task RequestAsync(
        LibraryRefreshKind kind,
        LibraryRefreshSource source,
        bool preserveSelection,
        bool updateLoadingState)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Request(kind, source, preserveSelection, updateLoadingState, completion);
        return completion.Task;
    }

    public void Dispose()
    {
        _scheduler.Dispose();
    }
}
