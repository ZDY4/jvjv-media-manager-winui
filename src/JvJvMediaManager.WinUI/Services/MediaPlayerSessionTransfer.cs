using System.Text.Json;

namespace JvJvMediaManager.Services;

internal sealed record MediaPlayerSessionTransfer(
    IReadOnlyList<IndependentMediaWindowMedia> MediaItems,
    int CurrentIndex,
    double PositionSeconds,
    bool WasPlaying,
    double RotationDegrees = 0)
{
    public const string DataFormat = "JvJvMediaManager.PlayerSession";

    public string Serialize() => JsonSerializer.Serialize(this);

    public static MediaPlayerSessionTransfer? Deserialize(string value)
    {
        try
        {
            var session = JsonSerializer.Deserialize<MediaPlayerSessionTransfer>(value);
            if (session == null
                || session.MediaItems.Count == 0
                || session.CurrentIndex < 0
                || session.CurrentIndex >= session.MediaItems.Count
                || !double.IsFinite(session.PositionSeconds)
                || session.PositionSeconds < 0
                || !double.IsFinite(session.RotationDegrees)
                || !IsRightAngleRotation(session.RotationDegrees))
            {
                return null;
            }

            var validItems = session.MediaItems
                .Where(item => !string.IsNullOrWhiteSpace(item.Id)
                    && !string.IsNullOrWhiteSpace(item.FileSystemPath)
                    && !string.IsNullOrWhiteSpace(item.FileName))
                .ToList();
            if (validItems.Count != session.MediaItems.Count
                || validItems.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != validItems.Count)
            {
                return null;
            }

            return session with
            {
                PositionSeconds = Math.Min(session.PositionSeconds, TimeSpan.FromDays(7).TotalSeconds),
                RotationDegrees = ((session.RotationDegrees % 360) + 360) % 360
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool IsRightAngleRotation(double degrees)
    {
        var normalized = ((degrees % 360) + 360) % 360;
        return Math.Abs(normalized % 90) < 0.001;
    }
}
