using System.Security.Cryptography;

namespace JvJvMediaManager.Utilities;

internal static class MediaContentFingerprint
{
    public const int Version = 1;

    private const int ChunkSize = 1024 * 1024;

    public static string Compute(string path, long fileSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        AppendInt64(hash, fileSize);
        foreach (var offset in GetChunkOffsets(fileSize))
        {
            AppendInt64(hash, offset);
            AppendChunk(hash, stream, offset);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static IEnumerable<long> GetChunkOffsets(long fileSize)
    {
        if (fileSize <= 0)
        {
            yield return 0;
            yield break;
        }

        var offsets = new[]
        {
            0,
            Math.Max(0, (fileSize - ChunkSize) / 2),
            Math.Max(0, fileSize - ChunkSize)
        };

        foreach (var offset in offsets.Distinct())
        {
            yield return offset;
        }
    }

    private static void AppendChunk(IncrementalHash hash, Stream stream, long offset)
    {
        stream.Position = Math.Min(offset, stream.Length);
        var remaining = (int)Math.Min(ChunkSize, Math.Max(0, stream.Length - stream.Position));
        AppendInt32(hash, remaining);

        var buffer = new byte[Math.Min(64 * 1024, Math.Max(1, remaining))];
        while (remaining > 0)
        {
            var read = stream.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read <= 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BitConverter.TryWriteBytes(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BitConverter.TryWriteBytes(bytes, value);
        hash.AppendData(bytes);
    }
}
