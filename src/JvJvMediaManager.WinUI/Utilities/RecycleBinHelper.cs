using Microsoft.VisualBasic.FileIO;

namespace JvJvMediaManager.Utilities;

internal static class RecycleBinHelper
{
    public static void SendToRecycleBin(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        FileSystem.DeleteFile(
            fullPath,
            UIOption.OnlyErrorDialogs,
            RecycleOption.SendToRecycleBin);
    }
}
