using System.Runtime.InteropServices;

namespace JvJvMediaManager.Utilities;

/// <summary>
/// Why a media path probe failed. Only <see cref="Missing"/> justifies removing
/// a library entry; <see cref="Unavailable"/> means the storage stack could not
/// answer and must never be treated as a deletion.
/// </summary>
public enum MediaFileAvailability
{
    Available,
    Missing,
    Unavailable,
}

public readonly record struct MediaFileProbeResult(MediaFileAvailability Availability, int Win32Error);

/// <summary>
/// Classifies whether a media file is truly gone or merely unreachable right
/// now (offline/stalled drive, I/O error, access denied, timeout). A stalled
/// USB volume reports "not found" for every path on it, so a bare
/// <see cref="File.Exists"/> is not safe for destructive decisions.
/// </summary>
public static class MediaFileProbe
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int Win32MaxPath = 260;
    private const uint InvalidFileAttributes = 0xFFFFFFFF;
    private const uint FileAttributeDirectory = 0x10;

    public static MediaFileProbeResult Probe(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return new MediaFileProbeResult(MediaFileAvailability.Missing, 0);
            }

            // GetFileAttributes stops at MAX_PATH (ERROR_FILENAME_EXCED_RANGE)
            // even when the file is there. .NET's own file APIs escape that limit;
            // probing without the same prefix reports an existing long path as
            // unreachable and can never confirm that a deleted one is gone.
            var probePath = PrepareProbePath(path);
            var attributes = GetFileAttributes(probePath);
            if (attributes != InvalidFileAttributes)
            {
                // A directory is not playable media, so the row itself is stale.
                return new MediaFileProbeResult(
                    (attributes & FileAttributeDirectory) != 0 ? MediaFileAvailability.Missing : MediaFileAvailability.Available,
                    0);
            }

            var error = Marshal.GetLastWin32Error();
            if (error is not (ErrorFileNotFound or ErrorPathNotFound))
            {
                // Device not ready, I/O error, access denied, timeout, ...
                return new MediaFileProbeResult(MediaFileAvailability.Unavailable, error);
            }

            return new MediaFileProbeResult(
                IsContainingFolderReachable(probePath) ? MediaFileAvailability.Missing : MediaFileAvailability.Unavailable,
                error);
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("MediaFileProbe", $"Media file probe failed. Path='{path}'.", ex);
            return new MediaFileProbeResult(MediaFileAvailability.Unavailable, 0);
        }
    }

    private static bool IsContainingFolderReachable(string path)
    {
        // "Not found" only means "media deleted" when its volume and its own
        // folder are still there. If the volume vanished (unplugged drive) or
        // the folder was moved/renamed, the whole subtree looks missing and the
        // entries must be kept for a later rescan to reconcile.
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                return false;
            }

            var parent = Path.GetDirectoryName(path);
            // An empty parent is not confirmation that the folder is still there.
            // Missing is the destructive outcome, so an unknown folder stays Unavailable.
            return !string.IsNullOrEmpty(parent) && Directory.Exists(parent);
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("MediaFileProbe", $"Containing folder probe failed. Path='{path}'.", ex);
            return false;
        }
    }

    private static string PrepareProbePath(string path)
    {
        if (path.Length < Win32MaxPath || path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path;
        }

        var native = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (native.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return @"\\?\UNC\" + native.Substring(2);
        }

        if (native.Length >= 3
            && char.IsLetter(native[0])
            && native[1] == ':'
            && native[2] == Path.DirectorySeparatorChar)
        {
            return @"\\?\" + native;
        }

        return path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributes(string lpFileName);
}
