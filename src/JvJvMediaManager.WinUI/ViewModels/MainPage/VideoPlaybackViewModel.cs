using Microsoft.UI.Xaml;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.ViewModels.MainPage;

public sealed class VideoPlaybackViewModel : ObservableObject
{
    private Visibility _controlBarVisibility = Visibility.Collapsed;
    private double _controlBarOpacity;
    private string _currentTimeText = "0:00";
    private string _totalTimeText = "0:00";
    private bool _isFullScreen;
    private bool _isVolumeFlyoutOpen;

    public Visibility ControlBarVisibility
    {
        get => _controlBarVisibility;
        set => SetProperty(ref _controlBarVisibility, value);
    }

    public double ControlBarOpacity
    {
        get => _controlBarOpacity;
        set => SetProperty(ref _controlBarOpacity, value);
    }

    public string CurrentTimeText
    {
        get => _currentTimeText;
        set => SetProperty(ref _currentTimeText, value);
    }

    public string TotalTimeText
    {
        get => _totalTimeText;
        set => SetProperty(ref _totalTimeText, value);
    }

    public bool IsFullScreen
    {
        get => _isFullScreen;
        set => SetProperty(ref _isFullScreen, value);
    }

    public bool IsVolumeFlyoutOpen
    {
        get => _isVolumeFlyoutOpen;
        set => SetProperty(ref _isVolumeFlyoutOpen, value);
    }
}
