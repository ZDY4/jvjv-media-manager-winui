using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;

namespace JvJvMediaManager.Views.MainPageParts;

public sealed partial class MediaFilterBarView : UserControl
{
    public event EventHandler<string>? TagRemoveRequested;

    public MediaFilterBarView()
    {
        InitializeComponent();
    }

    private void TagChipButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
        {
            TagRemoveRequested?.Invoke(this, tag);
        }
    }
}
