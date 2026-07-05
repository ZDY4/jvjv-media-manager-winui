using JvJvMediaManager.Models;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.Services;

internal sealed record IndependentMediaWindowMedia(
    string Id,
    string FileSystemPath,
    string FileName,
    MediaType Type,
    string ResolutionText,
    string? ContentHash,
    int FingerprintVersion,
    IReadOnlyList<string> Tags)
{
    public MediaFile ToMediaFile()
    {
        return new MediaFile
        {
            Id = Id,
            Path = PathHelpers.NormalizePath(FileSystemPath),
            FileName = FileName,
            Type = Type,
            Size = TryGetFileSize(),
            Width = TryParseResolutionWidth(),
            Height = TryParseResolutionHeight(),
            ModifiedAt = File.Exists(FileSystemPath)
                ? new DateTimeOffset(File.GetLastWriteTimeUtc(FileSystemPath)).ToUnixTimeSeconds()
                : 0,
            ContentHash = ContentHash,
            FingerprintVersion = FingerprintVersion,
            Tags = Tags
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private int? TryParseResolutionWidth()
    {
        return TryParseResolutionPart(0);
    }

    private int? TryParseResolutionHeight()
    {
        return TryParseResolutionPart(1);
    }

    private int? TryParseResolutionPart(int partIndex)
    {
        var parts = ResolutionText.Split('x', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > partIndex && int.TryParse(parts[partIndex], out var value)
            ? value
            : null;
    }

    private long TryGetFileSize()
    {
        try
        {
            return File.Exists(FileSystemPath)
                ? new FileInfo(FileSystemPath).Length
                : 0;
        }
        catch
        {
            return 0;
        }
    }
}
