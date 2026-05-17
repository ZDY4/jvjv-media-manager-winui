using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using JvJvMediaManager.ViewModels.MainPage;

namespace JvJvMediaManager.Views.MainPageParts;

public sealed partial class PlayerInfoOverlayView : UserControl
{
    public event EventHandler<string>? TagRemoveRequested;

    public PlayerInfoOverlayView()
    {
        InitializeComponent();
    }

    private void PlayerTag_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PlayerMediaTagItem tagItem } element)
        {
            return;
        }

        var flyout = new MenuFlyout();
        var removeItem = new MenuFlyoutItem
        {
            Text = "删除标签"
        };
        removeItem.Click += (_, _) => TagRemoveRequested?.Invoke(this, tagItem.Tag);
        flyout.Items.Add(removeItem);
        flyout.ShowAt(element, e.GetPosition(element));
        e.Handled = true;
    }
}
