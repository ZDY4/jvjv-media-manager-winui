using Microsoft.UI.Xaml.Input;

namespace JvJvMediaManager.Controllers.MainPage;

public sealed class MainPageShortcutRouter
{
    private readonly MainPageShellController _controller;

    public MainPageShortcutRouter(MainPageShellController controller)
    {
        _controller = controller;
    }

    public Task HandleKeyDownAsync(KeyRoutedEventArgs e)
    {
        return _controller.HandleKeyDownAsync(e);
    }

    public bool HandlePlayPauseAccelerator()
    {
        return _controller.HandlePlayPauseAccelerator();
    }

    public Task<bool> HandleDeleteAcceleratorAsync()
    {
        return _controller.HandleDeleteAcceleratorAsync();
    }

    public bool HandleSplitClipAccelerator()
    {
        return _controller.HandleSplitClipAccelerator();
    }
}
