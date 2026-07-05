using ObservablePropertyAttribute = CommunityToolkit.Mvvm.ComponentModel.ObservablePropertyAttribute;
using Microsoft.UI.Xaml;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.ViewModels.MainPage;

public sealed partial class ImagePreviewViewModel : ObservableObject
{
    [ObservableProperty]
    public partial Visibility ZoomBadgeVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial string ZoomText { get; set; } = "100%";
}
