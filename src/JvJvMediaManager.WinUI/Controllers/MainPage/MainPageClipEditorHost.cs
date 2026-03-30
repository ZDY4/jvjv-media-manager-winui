using JvJvMediaManager.ViewModels;
using JvJvMediaManager.ViewModels.MainPage;

namespace JvJvMediaManager.Controllers.MainPage;

internal sealed class MainPageClipEditorHost : IClipEditorHost
{
    private readonly LibraryShellViewModel _library;
    private readonly VideoPlaybackController _videoPlaybackController;
    private readonly Action<IEnumerable<string>> _registerOutputPaths;
    private readonly Action _showControls;

    public MainPageClipEditorHost(
        LibraryShellViewModel library,
        VideoPlaybackController videoPlaybackController,
        Action<IEnumerable<string>> registerOutputPaths,
        Action showControls)
    {
        _library = library;
        _videoPlaybackController = videoPlaybackController;
        _registerOutputPaths = registerOutputPaths;
        _showControls = showControls;
    }

    public MediaItemViewModel? SelectedMedia => _library.SelectedMedia;

    public TimeSpan CurrentPlaybackPosition => _videoPlaybackController.GetCurrentPlaybackPosition();

    public TimeSpan CurrentVideoDuration => _videoPlaybackController.GetCurrentVideoDuration();

    public bool IsPlaybackPlaying => _videoPlaybackController.IsPlaying;

    public void SeekPlaybackPosition(TimeSpan position)
    {
        _videoPlaybackController.SeekTo(position);
    }

    public void TogglePlayPause()
    {
        _videoPlaybackController.TogglePlayPause();
    }

    public void SetPlaybackDisplayOverride(
        string? positionText,
        string? durationText,
        double? positionSeconds,
        double? durationSeconds,
        Func<TimeSpan, TimeSpan>? mapDisplayPosition)
    {
        _videoPlaybackController.SetPlaybackDisplayOverride(
            positionText,
            durationText,
            positionSeconds,
            durationSeconds,
            mapDisplayPosition);
    }

    public void SetTransportSuppressed(bool suppressed)
    {
        _videoPlaybackController.SetTransportSuppressed(suppressed);
    }

    public Task AddOutputFilesAsync(IEnumerable<string> paths)
    {
        return _library.AddFilesAsync(paths);
    }

    public void RegisterOutputPaths(IEnumerable<string> paths)
    {
        _registerOutputPaths(paths);
    }

    public void ShowControls()
    {
        _showControls();
    }
}
