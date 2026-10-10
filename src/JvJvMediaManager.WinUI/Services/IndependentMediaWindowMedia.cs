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
    IReadOnlyList<string> Tags,
    int Rotation = 0,
    long Size = 0,
    long ModifiedAt = 0,
    double? Duration = null,
    int? Width = null,
    int? Height = null)
{
    public MediaFile ToMediaFile()
    {
        return new MediaFile
        {
            Id = Id,
            Path = PathHelpers.NormalizePath(FileSystemPath),
            FileName = FileName,
            Type = Type,
            Size = Size,
            Duration = Duration,
            Width = Width ?? TryParseResolutionWidth(),
            Height = Height ?? TryParseResolutionHeight(),
            ModifiedAt = ModifiedAt,
            ContentHash = ContentHash,
            FingerprintVersion = FingerprintVersion,
            Rotation = Rotation,
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
}
