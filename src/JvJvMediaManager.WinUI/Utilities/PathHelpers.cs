using System.Security.Cryptography;
using System.Text;

namespace JvJvMediaManager.Utilities;

public static class PathHelpers
{
    public static string NormalizePath(string path) => path.Replace('\\', '/');

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
