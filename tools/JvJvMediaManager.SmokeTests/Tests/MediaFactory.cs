using JvJvMediaManager.Models;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.SmokeTests.Tests;

internal static class MediaFactory
{
    public static MediaFile Create(
        string path,
        MediaType type = MediaType.Image,
        long size = 1024,
        long modifiedAt = 0,
        long createdAt = 0,
        int? width = null,
        int? height = null,
        double? duration = null,
        string? contentHash = null,
        IEnumerable<string>? tags = null)
    {
        var normalized = PathHelpers.NormalizePath(path);
        var fileName = Path.GetFileName(normalized);
        return new MediaFile
        {
            Id = PathHelpers.ComputeStableId(normalized),
            Path = normalized,
            FileName = fileName,
            Type = type,
            Size = size,
            ModifiedAt = modifiedAt,
            CreatedAt = createdAt,
            Width = width,
            Height = height,
            Duration = duration,
            ContentHash = contentHash,
            FingerprintVersion = contentHash is null ? 0 : MediaContentFingerprintVersionProxy.Version,
            Tags = tags?.ToList() ?? new List<string>()
        };
    }
}

internal static class MediaContentFingerprintVersionProxy
{
    // MediaContentFingerprint is internal; the smoke assembly links it, but keep
    // a stable indirection so tests do not depend on its access level changing.
    public const int Version = 1;
}
