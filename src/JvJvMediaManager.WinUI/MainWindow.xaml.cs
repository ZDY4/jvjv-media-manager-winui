using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using JvJvMediaManager.Views;
using WinRT.Interop;

namespace JvJvMediaManager;

public partial class MainWindow : Window
{
    private const double DefaultAspectRatio = 16d / 9d;
    private const int PreferredWindowWidth = 1600;
    private const int MinWindowWidth = 960;
    private const int MinWindowHeight = 540;

    public MainWindow()
    {
        InitializeComponent();
        ConfigureWindowChrome();
        ConfigureInitialWindowBounds();
        try
        {
            RootFrame.Content = new MainPage();
        }
        catch (Exception ex)
        {
            RootFrame.Content = CreateStartupErrorView(ex);
        }
    }

    private void ConfigureWindowChrome()
    {
        try
        {
            SystemBackdrop = new MicaBackdrop();
        }
        catch
        {
            // Fallback to the app-defined background brush when Mica is unavailable.
        }

        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var titleBar = appWindow.TitleBar;
        titleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = Colors.White;
        titleBar.ButtonInactiveForegroundColor = ColorHelper.FromArgb(180, 225, 233, 240);
        titleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(48, 255, 255, 255);
        titleBar.ButtonHoverForegroundColor = Colors.White;
        titleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(72, 255, 255, 255);
        titleBar.ButtonPressedForegroundColor = Colors.White;

        SyncTitleBarInsets(titleBar);
        WindowTitleText.Text = Title;
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

    private void ConfigureInitialWindowBounds()
    {
        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;

        var maxWidth = Math.Max(MinWindowWidth, (int)Math.Floor(workArea.Width * 0.9));
        var maxHeight = Math.Max(MinWindowHeight, (int)Math.Floor(workArea.Height * 0.9));

        var width = Math.Min(PreferredWindowWidth, maxWidth);
        var height = (int)Math.Round(width / DefaultAspectRatio);

        if (height > maxHeight)
        {
            height = maxHeight;
            width = (int)Math.Round(height * DefaultAspectRatio);
        }

        width = Math.Clamp(width, MinWindowWidth, workArea.Width);
        height = Math.Clamp(height, MinWindowHeight, workArea.Height);

        var x = workArea.X + Math.Max(0, (workArea.Width - width) / 2);
        var y = workArea.Y + Math.Max(0, (workArea.Height - height) / 2);

        appWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }
    private void SyncTitleBarInsets(AppWindowTitleBar titleBar)
    {
        LeftInsetColumn.Width = new GridLength(Math.Max(0, titleBar.LeftInset));
        RightInsetColumn.Width = new GridLength(Math.Max(0, titleBar.RightInset));
    }

    public void ApplyTheme(ElementTheme theme)
    {
        WindowRoot.RequestedTheme = theme;
        RootFrame.RequestedTheme = theme;

        if (RootFrame.Content is FrameworkElement element)
        {
            element.RequestedTheme = theme;
        }
    }
}
