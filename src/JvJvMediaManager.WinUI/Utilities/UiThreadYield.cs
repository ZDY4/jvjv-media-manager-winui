namespace JvJvMediaManager.Utilities;

internal static class UiThreadYield
{
    public static Task NextPassAsync()
    {
        var context = SynchronizationContext.Current;
        if (context == null)
        {
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Post(_ => tcs.TrySetResult(), null);
        return tcs.Task;
    }
}
