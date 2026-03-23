namespace JvJvMediaManager.ViewModels.MainPage;

public sealed class PlayerShellViewModel
{
    public PlayerShellViewModel(SelectionViewModel selection)
    {
        Selection = selection;
        VideoPlayback = new VideoPlaybackViewModel();
        ImagePreview = new ImagePreviewViewModel();
        ClipEditor = new ClipEditorViewModel();
    }

    public SelectionViewModel Selection { get; }

    public VideoPlaybackViewModel VideoPlayback { get; }

    public ImagePreviewViewModel ImagePreview { get; }

    public ClipEditorViewModel ClipEditor { get; }
}
