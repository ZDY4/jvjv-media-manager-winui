using Microsoft.UI.Xaml.Controls;
using JvJvMediaManager.Services;

namespace JvJvMediaManager.Views.Controls;

public sealed partial class ClipPlanPanel : UserControl
{
    public ClipPlanPanel(TimeSpan duration, VideoClipMode clipMode, string startText, string endText, string outputDirectory)
    {
        InitializeComponent();

        SourceDurationText.Text = $"源视频时长：{duration:h\\:mm\\:ss}";
        StartBox.Text = startText;
        EndBox.Text = endText;
        OutputDirBox.Text = outputDirectory;

        ModeComboBox.ItemsSource = new[]
        {
            new ComboBoxItem { Content = "保留片段", Tag = VideoClipMode.Keep },
            new ComboBoxItem { Content = "删除片段", Tag = VideoClipMode.Delete }
        };
        ModeComboBox.SelectedIndex = clipMode == VideoClipMode.Keep ? 0 : 1;
    }
}
