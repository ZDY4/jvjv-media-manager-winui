using System.Runtime.InteropServices;

namespace JvJvMediaManager.Utilities;

internal static class RecycleBinHelper
{
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoConfirmMkDir = 0x0200;
    private const ushort FofNoErrorUi = 0x0400;
    private const ushort SilentRecycleFlags = FofSilent | FofNoConfirmation | FofAllowUndo | FofNoConfirmMkDir | FofNoErrorUi;

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
        Exception? lastError = null;
        for (var attempt = 0; ; attempt++)
        {
            if (!File.Exists(fullPath))
            {
                return;
            }

            try
            {
                MoveToRecycleBinSilent(fullPath);
                return;
            }
            catch (Exception ex) when (IsTransientRecycleFailure(ex) && attempt < RetryDelays.Length)
            {
                lastError = ex;
                var delay = RetryDelays[attempt];
                AppTraceLogger.Log(
                    "RecycleBin",
                    $"Recycle-bin operation failed transiently; retrying. Path='{fullPath}', Attempt={attempt + 1}, DelayMs={delay.TotalMilliseconds:0}, ErrorType={ex.GetType().Name}.");
                Thread.Sleep(delay);
            }
            catch (Exception ex)
            {
                lastError = ex;
                break;
            }
        }

        if (!File.Exists(fullPath))
        {
            return;
        }

        throw new IOException(
            $"Failed to move the file to the recycle bin after {RetryDelays.Length + 1} attempts. " +
            "The file may be in use, or the drive may need an error check.",
            lastError);
    }

    private static void MoveToRecycleBinSilent(string fullPath)
    {
        // SHFileOperation runs on the calling thread and does not marshal back
        // to the WinUI dispatcher. StorageFile.DeleteAsync did, so recycle-bin
        // I/O (and in-use retries) froze the player switch after delete.
        var fileOp = new ShFileOpStruct
        {
            hwnd = IntPtr.Zero,
            wFunc = FoDelete,
            pFrom = fullPath + "\0",
            pTo = null,
            fFlags = SilentRecycleFlags,
            fAnyOperationsAborted = false,
            hNameMappings = IntPtr.Zero,
            lpszProgressTitle = null
        };

        var result = SHFileOperation(ref fileOp);
        if (fileOp.fAnyOperationsAborted)
        {
            throw new OperationCanceledException($"Recycle-bin operation was aborted. Path='{fullPath}'.");
        }

        if (result == 0 || !File.Exists(fullPath))
        {
            return;
        }

        throw new IOException(
            $"Failed to move the file to the recycle bin. Path='{fullPath}', Status={result}.",
            result);
    }

    private static bool IsTransientRecycleFailure(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is OperationCanceledException)
            {
                return true;
            }

            var native = current.HResult & 0xFFFF;
            // ERROR_ACCESS_DENIED, ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
            if (native is 5 or 32 or 33)
            {
                return true;
            }

            if (current is IOException)
            {
                return true;
            }
        }

        return false;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct fileOp);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszProgressTitle;
    }
}
