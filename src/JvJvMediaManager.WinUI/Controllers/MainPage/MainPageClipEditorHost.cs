using JvJvMediaManager.ViewModels;
using JvJvMediaManager.ViewModels.MainPage;

namespace JvJvMediaManager.Controllers.MainPage;

internal sealed class MainPageClipEditorHost : IClipEditorHost
{
    private readonly LibraryShellViewModel _library;
    private readonly VideoPlaybackController _videoPlaybackController;
    private readonly Func<IEnumerable<string>, Task> _registerOutputPathsAsync;
    private readonly Action _showControls;

    public MainPageClipEditorHost(
        LibraryShellViewModel library,
        VideoPlaybackController videoPlaybackController,
        Func<IEnumerable<string>, Task> registerOutputPathsAsync,
        Action showControls)
    {
        _library = library;
        _videoPlaybackController = videoPlaybackController;
        _registerOutputPathsAsync = registerOutputPathsAsync;
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

    public Task RegisterOutputPathsAsync(IEnumerable<string> paths)
    {
        return _registerOutputPathsAsync(paths);
    }

    public void ShowControls()
    {
        _showControls();
    }
}
