using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using JvJvMediaManager.Controllers.MainPage;
using JvJvMediaManager.Services.MainPage;
using JvJvMediaManager.ViewModels.MainPage;
using JvJvMediaManager.Views.MainPageParts;

namespace JvJvMediaManager.Views;

public sealed partial class MainPage : Page
{
    private readonly MainPageShellViewModel _shell = new();
    private readonly Services.MainPage.MainPageModuleFactory _modules;
    private readonly IContentDialogService _dialogService;
    private MainPageShellController? _controller;
    private MainPageShortcutRouter? _shortcutRouter;

    public MainPage()
    {
        _modules = ((App)Application.Current).MainPageModules;
        _dialogService = _modules.CreateContentDialogService();
        InitializeComponent();
        DataContext = _shell;
        Loaded += MainPage_Loaded;
        Unloaded += MainPage_Unloaded;
    }

    public Grid RootLayout => RootLayoutHost;

    public SplitView LibrarySplitView => LibrarySplitViewHost;

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        _dialogService.AttachHost(XamlRoot);

        _controller ??= new MainPageShellController(this, _shell, LibraryPaneHost, PlayerPaneHost, _dialogService, _modules);
        _shortcutRouter ??= new MainPageShortcutRouter(_controller);

        KeyDown -= MainPage_KeyDown;
        KeyDown += MainPage_KeyDown;

        await _controller.InitializeAsync();
    }

    private void MainPage_Unloaded(object sender, RoutedEventArgs e)
    {
        KeyDown -= MainPage_KeyDown;
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

    private async void MainPage_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_shortcutRouter != null)
        {
            await _shortcutRouter.HandleKeyDownAsync(e);
        }
    }

    private void PlayPauseKeyboardAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_shortcutRouter?.HandlePlayPauseAccelerator() == true)
        {
            args.Handled = true;
        }
    }

    private async void DeleteKeyboardAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_shortcutRouter != null && await _shortcutRouter.HandleDeleteAcceleratorAsync())
        {
            args.Handled = true;
        }
    }
}
