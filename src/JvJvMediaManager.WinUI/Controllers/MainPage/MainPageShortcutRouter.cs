using Microsoft.UI.Xaml.Input;
using Windows.System;

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

    public bool ShouldHandleWindowShortcutKey(VirtualKey key)
    {
        return _controller.ShouldHandleWindowShortcutKey(key);
    }

    public Task<bool> HandleVirtualKeyDownAsync(VirtualKey key)
    {
        return _controller.HandleVirtualKeyDownAsync(key);
    }
}
