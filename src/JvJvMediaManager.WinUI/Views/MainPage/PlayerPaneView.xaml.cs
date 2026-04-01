using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JvJvMediaManager.Views.MainPageParts;

public sealed partial class PlayerPaneView : UserControl
{
    public event EventHandler<int>? ShortcutHintInvoked;

    public PlayerPaneView()
    {
        InitializeComponent();
    }

    private void ShortcutHintButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int digit })
        {
            ShortcutHintInvoked?.Invoke(this, digit);
        }
    }
}
