using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using JvJvMediaManager.Controllers.MainPage;
using JvJvMediaManager.Services.MainPage;
using JvJvMediaManager.Utilities;
using JvJvMediaManager.ViewModels.MainPage;
using JvJvMediaManager.Views.MainPageParts;
using Windows.System;

namespace JvJvMediaManager.Views;

public sealed partial class MainPage : Page
{
    private readonly MainPageShellViewModel _shell = new();
    private readonly Services.MainPage.MainPageModuleFactory _modules;
    private readonly IContentDialogService _dialogService;
    private readonly KeyEventHandler _keyDownHandler;
    private MainPageShellController? _controller;
    private MainPageShortcutRouter? _shortcutRouter;
    private BossKeyController? _bossKeyController;

    public MainPage()
    {
        _modules = ((App)Application.Current).MainPageModules;
        _dialogService = _modules.CreateContentDialogService();
        _keyDownHandler = MainPage_KeyDown;
        InitializeComponent();
        DataContext = _shell;
        Loaded += MainPage_Loaded;
        Unloaded += MainPage_Unloaded;
    }

    public Grid RootLayout => RootLayoutHost;

    public SplitView LibrarySplitView => LibrarySplitViewHost;

    public LibraryShellViewModel Library => _shell.Library;

    public PlayerShellViewModel Player => _shell.Player;

    public void FocusKeyboardHost()
    {
        KeyboardFocusHost.Focus(FocusState.Programmatic);
    }

    public bool TryHandleWindowShortcutKey(VirtualKey key)
    {
        if (_shortcutRouter == null || !_shortcutRouter.ShouldHandleWindowShortcutKey(key))
        {
            return false;
        }

        _ = HandleWindowShortcutKeyAsync(key);
        return true;
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _dialogService.AttachHost(XamlRoot);

            _controller ??= new MainPageShellController(this, _shell, LibraryPaneHost, PlayerPaneHost, _dialogService, _modules);
            _shortcutRouter ??= new MainPageShortcutRouter(_controller);

            RemoveHandler(UIElement.KeyDownEvent, _keyDownHandler);
            AddHandler(UIElement.KeyDownEvent, _keyDownHandler, true);

            FocusKeyboardHost();

            await _controller.InitializeAsync();
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("MainPage", "MainPage_Loaded failed.", ex);
            await _dialogService.ShowInfoAsync("初始化失败", ex.Message);
        }
    }

    private void MainPage_Unloaded(object sender, RoutedEventArgs e)
    {
        RemoveHandler(UIElement.KeyDownEvent, _keyDownHandler);
        _controller?.Dispose();
        _controller = null;
        _shortcutRouter = null;
    }

    public Task HandleAddFolderFromTitleBarAsync()
    {
        return _controller?.HandleAddFolderFromTitleBarAsync() ?? Task.CompletedTask;
    }

    public Task HandleAddFilesFromTitleBarAsync()
    {
        return _controller?.HandleAddFilesFromTitleBarAsync() ?? Task.CompletedTask;
    }

    public Task HandleOpenSettingsFromTitleBarAsync()
    {
        return _controller?.HandleOpenSettingsFromTitleBarAsync() ?? Task.CompletedTask;
    }

    public void ConfigureBossKey(BossKeyController bossKeyController)
    {
        _bossKeyController = bossKeyController;
    }

    private void BossKeyRecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        _bossKeyController?.ExitBossMode();
    }

    public void ShowBossRecoveryButton()
    {
        BossKeyRecoveryButton.Visibility = Visibility.Visible;
    }

    public void ExitBossMode()
    {
        BossKeyRecoveryButton.Visibility = Visibility.Collapsed;
    }

    private async void MainPage_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        try
        {
            if (_shortcutRouter != null)
            {
                await _shortcutRouter.HandleKeyDownAsync(e);
            }
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("MainPage", $"MainPage_KeyDown failed. Key={e.Key}.", ex);
        }
    }

    private async Task HandleWindowShortcutKeyAsync(VirtualKey key)
    {
        try
        {
            if (_shortcutRouter != null)
            {
                await _shortcutRouter.HandleVirtualKeyDownAsync(key);
            }
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("MainPage", $"HandleWindowShortcutKeyAsync failed. Key={key}.", ex);
        }
    }
}
