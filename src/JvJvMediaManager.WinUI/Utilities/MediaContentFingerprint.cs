using System.Buffers;
using System.Security.Cryptography;

namespace JvJvMediaManager.Utilities;

internal static class MediaContentFingerprint
{
    public const int Version = 1;

    private const int ChunkSize = 1024 * 1024;
    private const int ReadBufferSize = 64 * 1024;

    public static async Task<string> ComputeAsync(string path, long fileSize, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            ReadBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        AppendInt64(hash, fileSize);

        var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        try
        {
            foreach (var offset in GetChunkOffsets(fileSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppendInt64(hash, offset);
                await AppendChunkAsync(hash, stream, offset, buffer, cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
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

    private static async Task AppendChunkAsync(
        IncrementalHash hash,
        FileStream stream,
        long offset,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        stream.Position = Math.Min(offset, stream.Length);
        var remaining = (int)Math.Min(ChunkSize, Math.Max(0, stream.Length - stream.Position));
        AppendInt32(hash, remaining);

        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var toRead = Math.Min(buffer.Length, remaining);
            var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
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
