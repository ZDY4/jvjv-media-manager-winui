using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using JvJvMediaManager.Views;

namespace JvJvMediaManager;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        try
        {
            RootFrame.Content = new MainPage();
        }
        catch (Exception ex)
        {
            RootFrame.Content = CreateStartupErrorView(ex);
        }
    }

    private static UIElement CreateStartupErrorView(Exception ex)
    {
        var stackPanel = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(24)
        };

        stackPanel.Children.Add(new TextBlock
        {
            Text = "JvJv Media Manager 启动失败",
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.White)
        });

        stackPanel.Children.Add(new TextBlock
        {
            Text = "主界面初始化时发生异常。下面是当前捕获到的错误信息：",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Colors.White)
        });

        stackPanel.Children.Add(new TextBox
        {
            Text = ex.ToString(),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 320
        });

        return new Grid
        {
            Background = new SolidColorBrush(ColorHelper.FromArgb(255, 32, 32, 32)),
            Children =
            {
                new ScrollViewer
                {
                    Content = stackPanel
                }
            }
        };
    }
}
