using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.Graphics;
using JvJvMediaManager.Views;
using JvJvMediaManager.Utilities;
using WinRT.Interop;
using Windows.System;

namespace JvJvMediaManager;

public partial class MainWindow : Window
{
    private delegate IntPtr WindowSubclassProc(
        IntPtr hWnd,
        uint msg,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr uIdSubclass,
        UIntPtr dwRefData);

    private delegate IntPtr LowLevelKeyboardProc(
        int nCode,
        UIntPtr wParam,
        IntPtr lParam);

    private const uint WmKeyDown = 0x0100;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmNcDestroy = 0x0082;
    private const int WhKeyboardLl = 13;
    private static readonly UIntPtr KeyboardSubclassId = new(0x4A564A56);
    private const double DefaultAspectRatio = 16d / 9d;
    private const int PreferredWindowWidth = 1600;
    private const int MinWindowWidth = 960;
    private const int MinWindowHeight = 540;
    private readonly WindowSubclassProc _keyboardSubclassProc;
    private readonly LowLevelKeyboardProc _lowLevelKeyboardProc;
    private AppWindow? _appWindow;
    private MainPage? _mainPage;
    private IntPtr _windowHandle;
    private IntPtr _keyboardHookHandle;
    private bool _keyboardSubclassInstalled;

    public MainWindow()
    {
        AppTraceLogger.Log("MainWindow", "Constructor start.");
        _keyboardSubclassProc = WindowSubclassProcImpl;
        _lowLevelKeyboardProc = LowLevelKeyboardProcImpl;
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
            AppTraceLogger.Log("MainWindow", "MainPage created.");
            InstallKeyboardSubclass();
            InstallLowLevelKeyboardHook();
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
        catch (Exception ex)
        {
            AppTraceLogger.LogSampled(
                "MainWindow",
                "mica-backdrop-failed",
                $"MicaBackdrop unavailable. ErrorType={ex.GetType().Name}, Message='{ex.Message}'.",
                TimeSpan.FromSeconds(30));
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
        AppTraceLogger.Log("MainWindow", $"Initial window bounds configured. X={x}, Y={y}, Width={width}, Height={height}, WorkArea={workArea.Width}x{workArea.Height}.");
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
        RemoveLowLevelKeyboardHook();
        RemoveKeyboardSubclass();
        if (_appWindow != null)
        {
            _appWindow.Changed -= AppWindow_Changed;
        }
    }

    private async void TitleBarAddFolderButton_Click(object sender, RoutedEventArgs e)
    {
        await RunTitleBarActionAsync(
            () => _mainPage?.HandleAddFolderFromTitleBarAsync() ?? Task.CompletedTask,
            "TitleBar AddFolder");
    }

    private async void TitleBarAddFilesButton_Click(object sender, RoutedEventArgs e)
    {
        await RunTitleBarActionAsync(
            () => _mainPage?.HandleAddFilesFromTitleBarAsync() ?? Task.CompletedTask,
            "TitleBar AddFiles");
    }

    private async void TitleBarSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunTitleBarActionAsync(
            () => _mainPage?.HandleOpenSettingsFromTitleBarAsync() ?? Task.CompletedTask,
            "TitleBar Settings");
    }

    private static async Task RunTitleBarActionAsync(Func<Task> action, string source)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("MainWindow", $"{source} failed.", ex);
            App.WriteExceptionLog(source, ex);
        }
    }

    private void InstallKeyboardSubclass()
    {
        if (_keyboardSubclassInstalled)
        {
            return;
        }

        var hWnd = GetWindowHandle();
        if (hWnd == IntPtr.Zero)
        {
            AppTraceLogger.Log("MainWindow", "Keyboard subclass skipped because window handle is missing.");
            return;
        }

        if (SetWindowSubclass(hWnd, _keyboardSubclassProc, KeyboardSubclassId, UIntPtr.Zero))
        {
            _keyboardSubclassInstalled = true;
            AppTraceLogger.Log("MainWindow", $"Keyboard subclass installed. Hwnd=0x{hWnd.ToInt64():X}.");
        }
        else
        {
            AppTraceLogger.Log("MainWindow", $"Keyboard subclass install failed. Hwnd=0x{hWnd.ToInt64():X}, Error={Marshal.GetLastWin32Error()}.");
        }
    }

    private void RemoveKeyboardSubclass()
    {
        if (!_keyboardSubclassInstalled)
        {
            return;
        }

        var hWnd = GetWindowHandle();
        if (hWnd != IntPtr.Zero)
        {
            RemoveWindowSubclass(hWnd, _keyboardSubclassProc, KeyboardSubclassId);
        }

        _keyboardSubclassInstalled = false;
    }

    private void InstallLowLevelKeyboardHook()
    {
        if (_keyboardHookHandle != IntPtr.Zero)
        {
            return;
        }

        _keyboardHookHandle = SetWindowsHookEx(WhKeyboardLl, _lowLevelKeyboardProc, GetModuleHandle(null), 0);
        if (_keyboardHookHandle != IntPtr.Zero)
        {
            AppTraceLogger.Log("MainWindow", "Low-level keyboard hook installed.");
            return;
        }

        AppTraceLogger.Log("MainWindow", $"Low-level keyboard hook install failed. Error={Marshal.GetLastWin32Error()}.");
    }

    private void RemoveLowLevelKeyboardHook()
    {
        if (_keyboardHookHandle == IntPtr.Zero)
        {
            return;
        }

        if (!UnhookWindowsHookEx(_keyboardHookHandle))
        {
            AppTraceLogger.Log("MainWindow", $"Low-level keyboard hook remove failed. Error={Marshal.GetLastWin32Error()}.");
        }

        _keyboardHookHandle = IntPtr.Zero;
    }

    private IntPtr GetWindowHandle()
    {
        if (_windowHandle == IntPtr.Zero)
        {
            _windowHandle = WindowNative.GetWindowHandle(this);
        }

        return _windowHandle;
    }

    private bool IsAppForeground()
    {
        var hWnd = GetWindowHandle();
        var foreground = GetForegroundWindow();
        return hWnd != IntPtr.Zero
            && foreground != IntPtr.Zero
            && (foreground == hWnd || IsChild(hWnd, foreground));
    }

    private bool TryHandleShortcutFromNativeKey(VirtualKey key, string source)
    {
        if (!IsAppForeground())
        {
            return false;
        }

        if (_mainPage?.TryHandleWindowShortcutKey(key) == true)
        {
            AppTraceLogger.LogSampled("MainWindow", source, $"Native shortcut handled. Key={key}.", TimeSpan.FromSeconds(1));
            return true;
        }

        return false;
    }

    private IntPtr WindowSubclassProcImpl(
        IntPtr hWnd,
        uint msg,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr uIdSubclass,
        UIntPtr dwRefData)
    {
        if (msg == WmKeyDown || msg == WmSysKeyDown)
        {
            var key = (VirtualKey)wParam.ToUInt32();
            if (TryHandleShortcutFromNativeKey(key, "window-keydown"))
            {
                return IntPtr.Zero;
            }
        }

        if (msg == WmNcDestroy)
        {
            RemoveKeyboardSubclass();
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private IntPtr LowLevelKeyboardProcImpl(int nCode, UIntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam.ToUInt32() == WmKeyDown || wParam.ToUInt32() == WmSysKeyDown))
        {
            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            if (TryHandleShortcutFromNativeKey((VirtualKey)data.VkCode, "low-level-keydown"))
            {
                return new IntPtr(1);
            }
        }

        return CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("comctl32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, WindowSubclassProc pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("comctl32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, WindowSubclassProc pfnSubclass, UIntPtr uIdSubclass);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
