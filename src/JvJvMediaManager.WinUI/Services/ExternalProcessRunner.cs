using System.Diagnostics;

namespace JvJvMediaManager.Services;

public sealed class ExternalProcessResult
{
    public int ExitCode { get; init; }

    public string StandardOutput { get; init; } = string.Empty;

    public string StandardError { get; init; } = string.Empty;
}

public sealed class ExternalProcessRunner
{
    private static readonly TimeSpan DefaultKillWaitTimeout = TimeSpan.FromSeconds(3);

    public async Task<ExternalProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        Func<StreamReader, CancellationToken, Task<string>>? readStandardOutputAsync = null,
        Func<StreamReader, CancellationToken, Task<string>>? readStandardErrorAsync = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = true;

        using var process = new Process { StartInfo = startInfo };
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var processCancellationToken = timeoutCts.Token;

        process.Start();

        var outputTask = (readStandardOutputAsync ?? ReadToEndAsync)(process.StandardOutput, processCancellationToken);
        var errorTask = (readStandardErrorAsync ?? ReadToEndAsync)(process.StandardError, processCancellationToken);

        try
        {
            await Task.WhenAll(outputTask, errorTask);
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch
        {
            await KillProcessTreeAndWaitAsync(process, DefaultKillWaitTimeout);
            throw;
        }

        return new ExternalProcessResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = await outputTask,
            StandardError = await errorTask
        };
    }

    public static async Task KillProcessTreeAndWaitAsync(Process? process, TimeSpan waitTimeout)
    {
        if (process == null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cancellation cleanup only.
        }

        try
        {
            using var waitCts = new CancellationTokenSource(waitTimeout);
            await process.WaitForExitAsync(waitCts.Token);
        }
        catch
        {
            // Best-effort zombie prevention only.
        }
    }

    private static Task<string> ReadToEndAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        return reader.ReadToEndAsync(cancellationToken);
    }
}
