using System.Text.Json;

namespace JvJvMediaManager.Services;

internal sealed record MediaPlayerSessionTransfer(
    IReadOnlyList<IndependentMediaWindowMedia> MediaItems,
    int CurrentIndex,
    double PositionSeconds,
    bool WasPlaying)
{
    public const string DataFormat = "JvJvMediaManager.PlayerSession";

    public string Serialize() => JsonSerializer.Serialize(this);

    public static MediaPlayerSessionTransfer? Deserialize(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<MediaPlayerSessionTransfer>(value);
        }
        catch
        {
            return null;
        }
    }
}
