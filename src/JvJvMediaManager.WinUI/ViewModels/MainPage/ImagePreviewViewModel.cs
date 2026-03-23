using Microsoft.UI.Xaml;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.ViewModels.MainPage;

public sealed class ImagePreviewViewModel : ObservableObject
{
    private Visibility _zoomBadgeVisibility = Visibility.Collapsed;
    private string _zoomText = "100%";

    public Visibility ZoomBadgeVisibility
    {
        get => _zoomBadgeVisibility;
        set => SetProperty(ref _zoomBadgeVisibility, value);
    }

    public string ZoomText
    {
        get => _zoomText;
        set => SetProperty(ref _zoomText, value);
    }
}
