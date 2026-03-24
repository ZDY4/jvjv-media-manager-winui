using Microsoft.UI.Xaml;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.ViewModels.MainPage;

public sealed class PlayerShellViewModel : ObservableObject
{
    private Visibility _emptyStateVisibility = Visibility.Visible;
    private Visibility _playerInfoVisibility = Visibility.Collapsed;
    private string _playerFileName = string.Empty;
    private string _playerResolution = string.Empty;

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

    public Visibility EmptyStateVisibility
    {
        get => _emptyStateVisibility;
        set => SetProperty(ref _emptyStateVisibility, value);
    }

    public Visibility PlayerInfoVisibility
    {
        get => _playerInfoVisibility;
        set => SetProperty(ref _playerInfoVisibility, value);
    }

    public string PlayerFileName
    {
        get => _playerFileName;
        set => SetProperty(ref _playerFileName, value);
    }

    public string PlayerResolution
    {
        get => _playerResolution;
        set => SetProperty(ref _playerResolution, value);
    }
}
