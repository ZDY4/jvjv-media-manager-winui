using Microsoft.VisualBasic.FileIO;

namespace JvJvMediaManager.Utilities;

internal static class RecycleBinHelper
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(150),
        TimeSpan.FromMilliseconds(400),
        TimeSpan.FromMilliseconds(900)
    ];

    public static void SendToRecycleBin(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        for (var attempt = 0; ; attempt++)
        {
            if (!File.Exists(fullPath))
            {
                return;
            }

            try
            {
                FileSystem.DeleteFile(
                    fullPath,
                    UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin);
                return;
            }
            catch (OperationCanceledException) when (attempt < RetryDelays.Length)
            {
                var delay = RetryDelays[attempt];
                AppTraceLogger.Log(
                    "RecycleBin",
                    $"Recycle-bin operation was canceled; retrying. Path='{fullPath}', Attempt={attempt + 1}, DelayMs={delay.TotalMilliseconds:0}.");
                Thread.Sleep(delay);
            }
            catch (OperationCanceledException ex)
            {
                if (!File.Exists(fullPath))
                {
                    return;
                }

                throw new IOException(
                    $"Windows canceled the recycle-bin operation after {RetryDelays.Length + 1} attempts. " +
                    "The file may be in use, or the drive may need an error check.",
                    ex);
            }
        }
    }
}
