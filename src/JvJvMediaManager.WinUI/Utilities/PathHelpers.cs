using System.Security.Cryptography;
using System.Text;

namespace JvJvMediaManager.Utilities;

public static class PathHelpers
{
    public static string NormalizePath(string path) => path.Replace('\\', '/');

    public static string ToNativePath(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    public static string NormalizeFolderPath(string path)
    {
        return NormalizePath(path).TrimEnd('/');
    }

    public static bool IsPathUnderFolder(string path, string folderPath)
    {
        var normalizedPath = NormalizePath(path);
        var normalizedFolder = NormalizeFolderPath(folderPath);
        return normalizedPath.StartsWith($"{normalizedFolder}/", StringComparison.OrdinalIgnoreCase);
    }

    public static string ComputeStableId(string path)
    {
        var normalized = NormalizePath(path);
        var bytes = Encoding.UTF8.GetBytes(normalized);
        var hash = MD5.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }
}
