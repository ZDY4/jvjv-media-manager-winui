using Microsoft.UI.Xaml;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.ViewModels.MainPage;

public sealed class ClipEditorViewModel : ObservableObject
{
    private Visibility _visibility = Visibility.Collapsed;
    private Visibility _progressVisibility = Visibility.Collapsed;
    private string _clipStartText = "起点：0:00";
    private string _clipEndText = "终点：0:00";
    private string _clipDurationText = "时长：0:00";
    private string _clipModeText = "模式：保留片段";
    private string _clipSegmentCountText = "片段：1";
    private string _clipOutputText = "输出：原目录";
    private string _timelineGuideText = "亮色片段会输出；拖边界微调，单击片段后可 Delete 删除，Ctrl+滚轮缩放，右键按住平移";
    private string _statusText = string.Empty;
    private double _progressValue;

    public Visibility Visibility
    {
        get => _visibility;
        set => SetProperty(ref _visibility, value);
    }

    public Visibility ProgressVisibility
    {
        get => _progressVisibility;
        set => SetProperty(ref _progressVisibility, value);
    }

    public string ClipStartText
    {
        get => _clipStartText;
        set => SetProperty(ref _clipStartText, value);
    }

    public string ClipEndText
    {
        get => _clipEndText;
        set => SetProperty(ref _clipEndText, value);
    }

    public string ClipDurationText
    {
        get => _clipDurationText;
        set => SetProperty(ref _clipDurationText, value);
    }

    public string ClipModeText
    {
        get => _clipModeText;
        set => SetProperty(ref _clipModeText, value);
    }

    public string ClipSegmentCountText
    {
        get => _clipSegmentCountText;
        set => SetProperty(ref _clipSegmentCountText, value);
    }

    public string ClipOutputText
    {
        get => _clipOutputText;
        set => SetProperty(ref _clipOutputText, value);
    }

    public string TimelineGuideText
    {
        get => _timelineGuideText;
        set => SetProperty(ref _timelineGuideText, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        set => SetProperty(ref _progressValue, value);
    }
}
