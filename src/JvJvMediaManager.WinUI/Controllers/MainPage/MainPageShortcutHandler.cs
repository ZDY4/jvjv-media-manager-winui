using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
using JvJvMediaManager.Models;
using JvJvMediaManager.ViewModels;

namespace JvJvMediaManager.Controllers.MainPage;

internal sealed class MainPageShortcutHandler
{
    private readonly Func<bool> _isDisposed;
    private readonly Func<bool> _isTextInputFocused;
    private readonly Func<MediaItemViewModel?> _getCurrentMedia;
    private readonly Func<bool> _isClipModeActive;
    private readonly Action _toggleNumpadShortcutHint;
    private readonly Func<bool> _openSelectedMediaFolder;
    private readonly Func<Task<bool>> _addSelectionToPlaylistAsync;
    private readonly Func<Task<bool>> _removeSelectionFromPlaylistAsync;
    private readonly Func<Task> _addFolderAsync;
    private readonly Action _focusSearch;
    private readonly Func<Task> _editSelectedTagsAsync;
    private readonly Func<VirtualKey, Task<bool>> _applyNumpadTagShortcutAsync;
    private readonly Func<bool> _togglePlayback;
    private readonly Func<Task<bool>> _deleteSelectedAsync;
    private readonly Func<int, Task> _navigateRelativeAsync;
    private readonly Action<double> _seekRelative;
    private readonly Func<Task> _exportCurrentClipAsync;
    private readonly Func<bool> _splitClip;
    private readonly Action<double> _zoomImageBy;
    private readonly Action _resetImageZoom;
    private readonly Func<VirtualKey, bool> _shouldHandleBossKeyFunc;
    private readonly Action _triggerBossKey;

    public MainPageShortcutHandler(
        Func<bool> isDisposed,
        Func<bool> isTextInputFocused,
        Func<MediaItemViewModel?> getCurrentMedia,
        Func<bool> isClipModeActive,
        Action toggleNumpadShortcutHint,
        Func<bool> openSelectedMediaFolder,
        Func<Task<bool>> addSelectionToPlaylistAsync,
        Func<Task<bool>> removeSelectionFromPlaylistAsync,
        Func<Task> addFolderAsync,
        Action focusSearch,
        Func<Task> editSelectedTagsAsync,
        Func<VirtualKey, Task<bool>> applyNumpadTagShortcutAsync,
        Func<bool> togglePlayback,
        Func<Task<bool>> deleteSelectedAsync,
        Func<int, Task> navigateRelativeAsync,
        Action<double> seekRelative,
        Func<Task> exportCurrentClipAsync,
        Func<bool> splitClip,
        Action<double> zoomImageBy,
        Action resetImageZoom,
        Func<VirtualKey, bool> shouldHandleBossKeyFunc,
        Action triggerBossKey)
    {
        _isDisposed = isDisposed;
        _isTextInputFocused = isTextInputFocused;
        _getCurrentMedia = getCurrentMedia;
        _isClipModeActive = isClipModeActive;
        _toggleNumpadShortcutHint = toggleNumpadShortcutHint;
        _openSelectedMediaFolder = openSelectedMediaFolder;
        _addSelectionToPlaylistAsync = addSelectionToPlaylistAsync;
        _removeSelectionFromPlaylistAsync = removeSelectionFromPlaylistAsync;
        _addFolderAsync = addFolderAsync;
        _focusSearch = focusSearch;
        _editSelectedTagsAsync = editSelectedTagsAsync;
        _applyNumpadTagShortcutAsync = applyNumpadTagShortcutAsync;
        _togglePlayback = togglePlayback;
        _deleteSelectedAsync = deleteSelectedAsync;
        _navigateRelativeAsync = navigateRelativeAsync;
        _seekRelative = seekRelative;
        _exportCurrentClipAsync = exportCurrentClipAsync;
        _splitClip = splitClip;
        _zoomImageBy = zoomImageBy;
        _resetImageZoom = resetImageZoom;
        _shouldHandleBossKeyFunc = shouldHandleBossKeyFunc;
        _triggerBossKey = triggerBossKey;
    }

    public bool ShouldHandleWindowShortcutKey(VirtualKey key)
    {
        if (_isDisposed())
        {
            return false;
        }

        if (key == VirtualKey.F1)
        {
            return true;
        }

        if (_shouldHandleBossKeyFunc(key))
        {
            return true;
        }

        var ctrlDown = IsCtrlKeyDown();
        if (ctrlDown)
        {
            return key is VirtualKey.O or VirtualKey.A or VirtualKey.R or VirtualKey.F or VirtualKey.T;
        }

        if (_isTextInputFocused())
        {
            return false;
        }

        if (IsNumpadTagShortcutKey(key))
        {
            return true;
        }

        return IsShortcutKeyForCurrentSelection(key);
    }

    public async Task<bool> HandleShortcutKeyAsync(VirtualKey key)
    {
        if (_isDisposed())
        {
            return false;
        }

        if (key == VirtualKey.F1)
        {
            _toggleNumpadShortcutHint();
            return true;
        }

        if (_shouldHandleBossKeyFunc(key))
        {
            _triggerBossKey();
            return true;
        }

        var ctrlDown = IsCtrlKeyDown();
        var shiftDown = IsShiftKeyDown();

        if (ctrlDown)
        {
            if (shiftDown && key == VirtualKey.O)
            {
                return _openSelectedMediaFolder();
            }

            if (shiftDown && key == VirtualKey.A)
            {
                return await _addSelectionToPlaylistAsync();
            }

            if (shiftDown && key == VirtualKey.R)
            {
                return await _removeSelectionFromPlaylistAsync();
            }

            if (key == VirtualKey.O)
            {
                await _addFolderAsync();
                return true;
            }

            if (key == VirtualKey.F)
            {
                _focusSearch();
                return true;
            }

            if (key == VirtualKey.T)
            {
                await _editSelectedTagsAsync();
                return true;
            }
        }

        if (_isTextInputFocused())
        {
            return false;
        }

        if (await _applyNumpadTagShortcutAsync(key))
        {
            return true;
        }

        var currentMedia = _getCurrentMedia();
        if (currentMedia == null)
        {
            return false;
        }

        if (key == VirtualKey.Space)
        {
            return _togglePlayback();
        }

        if (key == VirtualKey.Delete)
        {
            return await _deleteSelectedAsync();
        }

        if (key == VirtualKey.Left)
        {
            await _navigateRelativeAsync(-1);
            return true;
        }

        if (key == VirtualKey.Right)
        {
            await _navigateRelativeAsync(1);
            return true;
        }

        if (key == VirtualKey.Up && currentMedia.Type == MediaType.Video)
        {
            _seekRelative(-5);
            return true;
        }

        if (key == VirtualKey.Down && currentMedia.Type == MediaType.Video)
        {
            _seekRelative(5);
            return true;
        }

        if (key == VirtualKey.PageUp)
        {
            await _navigateRelativeAsync(-1);
            return true;
        }

        if (key == VirtualKey.PageDown)
        {
            await _navigateRelativeAsync(1);
            return true;
        }

        if (currentMedia.Type == MediaType.Video && _isClipModeActive() && key == VirtualKey.E)
        {
            await _exportCurrentClipAsync();
            return true;
        }

        if (currentMedia.Type == MediaType.Video && _isClipModeActive() && key == VirtualKey.K)
        {
            return _splitClip();
        }

        if (currentMedia.Type == MediaType.Image)
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

    private bool IsShortcutKeyForCurrentSelection(VirtualKey key)
    {
        var selected = _getCurrentMedia();
        if (selected == null)
        {
            return false;
        }

        if (key is VirtualKey.Space
            or VirtualKey.Delete
            or VirtualKey.Left
            or VirtualKey.Right
            or VirtualKey.PageUp
            or VirtualKey.PageDown)
        {
            return true;
        }

        if (selected.Type == MediaType.Video)
        {
            return key is VirtualKey.Up
                or VirtualKey.Down
                || (_isClipModeActive() && key is VirtualKey.E or VirtualKey.K);
        }

        if (selected.Type == MediaType.Image)
        {
            return key is VirtualKey.Add
                or VirtualKey.Subtract
                or VirtualKey.Number0
                or (VirtualKey)187
                or (VirtualKey)189;
        }

        return false;
    }

    private static bool IsNumpadTagShortcutKey(VirtualKey key)
    {
        return key is VirtualKey.NumberPad1
            or VirtualKey.NumberPad2
            or VirtualKey.NumberPad3
            or VirtualKey.NumberPad4
            or VirtualKey.NumberPad5
            or VirtualKey.NumberPad6
            or VirtualKey.NumberPad7
            or VirtualKey.NumberPad8
            or VirtualKey.NumberPad9;
    }

    private static bool IsCtrlKeyDown()
    {
        return (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
    }

    private static bool IsShiftKeyDown()
    {
        return (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
    }
}
