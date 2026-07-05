using ObservablePropertyAttribute = CommunityToolkit.Mvvm.ComponentModel.ObservablePropertyAttribute;
using Microsoft.UI.Xaml;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.ViewModels.MainPage;

public sealed partial class VideoPlaybackViewModel : ObservableObject
{
    [ObservableProperty]
    public partial Visibility ControlBarVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial double ControlBarOpacity { get; set; }

    [ObservableProperty]
    public partial string CurrentTimeText { get; set; } = "0:00";

    [ObservableProperty]
    public partial string TotalTimeText { get; set; } = "0:00";

    [ObservableProperty]
    public partial bool IsFullScreen { get; set; }

    [ObservableProperty]
    public partial bool IsVolumeFlyoutOpen { get; set; }
}
