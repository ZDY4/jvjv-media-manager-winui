using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using JvJvMediaManager.Views;
using JvJvMediaManager.Utilities;
using WinRT.Interop;

namespace JvJvMediaManager;

public partial class MainWindow : Window
{
    private const double DefaultAspectRatio = 16d / 9d;
    private const int PreferredWindowWidth = 1600;
    private const int MinWindowWidth = 960;
    private const int MinWindowHeight = 540;
    private AppWindow? _appWindow;
    private MainPage? _mainPage;

    public MainWindow()
    {
        InitializeComponent();
        WindowRoot.RequestedTheme = ElementTheme.Dark;
        RootFrame.RequestedTheme = ElementTheme.Dark;
        Closed += MainWindow_Closed;
        ConfigureWindowChrome();
        ConfigureInitialWindowBounds();
        try
        {
            _mainPage = new MainPage();
            RootFrame.Content = _mainPage;
        }
        catch (Exception ex)
        {
            App.WriteExceptionLog("MainPage startup", ex);
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
        _appWindow = AppWindow.GetFromWindowId(windowId);

        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var titleBar = _appWindow.TitleBar;
        titleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = ThemeResourceHelper.GetColor("TitleBarButtonForegroundColor", Colors.White);
        titleBar.ButtonInactiveForegroundColor = ThemeResourceHelper.GetColor("TitleBarButtonInactiveForegroundColor", ColorHelper.FromArgb(180, 209, 209, 209));
        titleBar.ButtonHoverBackgroundColor = ThemeResourceHelper.GetColor("TitleBarButtonHoverBackgroundColor", ColorHelper.FromArgb(36, 255, 255, 255));
        titleBar.ButtonHoverForegroundColor = ThemeResourceHelper.GetColor("TitleBarButtonForegroundColor", Colors.White);
        titleBar.ButtonPressedBackgroundColor = ThemeResourceHelper.GetColor("TitleBarButtonPressedBackgroundColor", ColorHelper.FromArgb(54, 255, 255, 255));
        titleBar.ButtonPressedForegroundColor = ThemeResourceHelper.GetColor("TitleBarButtonForegroundColor", Colors.White);

        SyncTitleBarInsets(titleBar);
        WindowTitleText.Text = Title;
        _appWindow.Changed += AppWindow_Changed;
    }

    private static UIElement CreateStartupErrorView(Exception ex)
    {
        var textBrush = ThemeResourceHelper.GetBrush("TextBrush", Colors.White);
        var backdropBrush = ThemeResourceHelper.GetBrush("WindowBackdropBrush", ColorHelper.FromArgb(255, 21, 21, 21));

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
            Foreground = textBrush
        });

        stackPanel.Children.Add(new TextBlock
        {
            Text = "主界面初始化时发生异常。下面是当前捕获到的错误信息：",
            TextWrapping = TextWrapping.Wrap,
            Foreground = textBrush
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
            Background = backdropBrush,
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
        var appWindow = _appWindow ?? AppWindow.GetFromWindowId(windowId);
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

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange && !args.DidPresenterChange)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() => SyncTitleBarInsets(sender.TitleBar));
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_appWindow != null)
        {
            _appWindow.Changed -= AppWindow_Changed;
        }
    }

    private async void TitleBarAddFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mainPage != null)
        {
            await _mainPage.HandleAddFolderFromTitleBarAsync();
        }
    }

    private async void TitleBarAddFilesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mainPage != null)
        {
            await _mainPage.HandleAddFilesFromTitleBarAsync();
        }
    }

    private async void TitleBarSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mainPage != null)
        {
            await _mainPage.HandleOpenSettingsFromTitleBarAsync();
        }
    }
}
