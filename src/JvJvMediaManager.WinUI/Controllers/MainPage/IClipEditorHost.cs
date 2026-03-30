using JvJvMediaManager.ViewModels;

namespace JvJvMediaManager.Controllers.MainPage;

public interface IClipEditorHost
{
    MediaItemViewModel? SelectedMedia { get; }

    TimeSpan CurrentPlaybackPosition { get; }

    TimeSpan CurrentVideoDuration { get; }

    bool IsPlaybackPlaying { get; }

    void SeekPlaybackPosition(TimeSpan position);

    void TogglePlayPause();

    void SetPlaybackDisplayOverride(
        string? positionText,
        string? durationText,
        double? positionSeconds,
        double? durationSeconds,
        Func<TimeSpan, TimeSpan>? mapDisplayPosition);

    void SetTransportSuppressed(bool suppressed);

    Task AddOutputFilesAsync(IEnumerable<string> paths);

    void RegisterOutputPaths(IEnumerable<string> paths);

    void ShowControls();
}
