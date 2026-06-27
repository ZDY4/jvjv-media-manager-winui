using Microsoft.UI.Input;
using Windows.System;
using JvJvMediaManager.Models;
using JvJvMediaManager.ViewModels;

namespace JvJvMediaManager.Controllers.MainPage;

internal sealed class IndependentMediaWindowShortcutHandler
{
    private readonly Func<MediaItemViewModel?> _getCurrentMedia;
    private readonly Action _close;
    private readonly Action _toggleShortcutHint;
    private readonly Func<VirtualKey, Task<bool>> _applyNumpadTagShortcutAsync;
    private readonly Func<Task> _deleteCurrentMediaAsync;
    private readonly Func<int, Task> _navigateRelativeAsync;
    private readonly Action _togglePlayPause;
    private readonly Action<double> _seekRelative;
    private readonly Action<double> _zoomImageBy;
    private readonly Action _resetImageZoom;
    private readonly BossKeyController _bossKeyController;

    public IndependentMediaWindowShortcutHandler(
        Func<MediaItemViewModel?> getCurrentMedia,
        Action close,
        Action toggleShortcutHint,
        Func<VirtualKey, Task<bool>> applyNumpadTagShortcutAsync,
        Func<Task> deleteCurrentMediaAsync,
        Func<int, Task> navigateRelativeAsync,
        Action togglePlayPause,
        Action<double> seekRelative,
        Action<double> zoomImageBy,
        Action resetImageZoom,
        BossKeyController bossKeyController)
    {
        _getCurrentMedia = getCurrentMedia;
        _close = close;
        _toggleShortcutHint = toggleShortcutHint;
        _applyNumpadTagShortcutAsync = applyNumpadTagShortcutAsync;
        _deleteCurrentMediaAsync = deleteCurrentMediaAsync;
        _navigateRelativeAsync = navigateRelativeAsync;
        _togglePlayPause = togglePlayPause;
        _seekRelative = seekRelative;
        _zoomImageBy = zoomImageBy;
        _resetImageZoom = resetImageZoom;
        _bossKeyController = bossKeyController;
    }

    public async Task<bool> HandleKeyAsync(VirtualKey key)
    {
        if (IsCtrlKeyDown() && key == (VirtualKey)87) // Ctrl+W
        {
            _bossKeyController.HandleBossKeyTrigger();
            return true;
        }

        if (_bossKeyController.ShouldHandleBossKey(key, IsCtrlKeyDown()))
        {
            return true;
        }

        if (key == VirtualKey.Escape)
        {
            _close();
            return true;
        }

        if (key == VirtualKey.F1)
        {
            _toggleShortcutHint();
            return true;
        }

        if (await _applyNumpadTagShortcutAsync(key))
        {
            return true;
        }

        if (key == VirtualKey.Delete)
        {
            await _deleteCurrentMediaAsync();
            return true;
        }

        if (key == VirtualKey.Left || key == VirtualKey.PageUp)
        {
            await _navigateRelativeAsync(-1);
            return true;
        }

        if (key == VirtualKey.Right || key == VirtualKey.PageDown)
        {
            await _navigateRelativeAsync(1);
            return true;
        }

        var currentMedia = _getCurrentMedia();
        if (key == VirtualKey.Space)
        {
            if (currentMedia?.Type == MediaType.Video)
            {
                _togglePlayPause();
                return true;
            }

            return false;
        }

        if (currentMedia?.Type == MediaType.Video)
        {
            if (key == VirtualKey.Up)
            {
                _seekRelative(-5);
                return true;
            }

            if (key == VirtualKey.Down)
            {
                _seekRelative(5);
                return true;
            }
        }

        if (currentMedia?.Type == MediaType.Image)
        {
            if (key == VirtualKey.Add || key == (VirtualKey)187)
            {
                _zoomImageBy(0.1);
                return true;
            }

            if (key == VirtualKey.Subtract || key == (VirtualKey)189)
            {
                _zoomImageBy(-0.1);
                return true;
            }

            if (key == VirtualKey.Number0)
            {
                _resetImageZoom();
                return true;
            }
        }

        return false;
    }

    private static bool IsCtrlKeyDown()
    {
        return (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
    }
}
