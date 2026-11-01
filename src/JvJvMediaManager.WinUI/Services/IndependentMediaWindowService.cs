using JvJvMediaManager.Utilities;
using JvJvMediaManager.ViewModels;
using JvJvMediaManager.Views;
using Windows.System;

namespace JvJvMediaManager.Services;

public sealed class IndependentMediaWindowService : IDisposable
{
    private readonly List<IndependentMediaWindow> _windows = new();
    private readonly ActivePlaybackTarget _activePlaybackTarget;
    private readonly Func<string, IReadOnlyList<string>, Task>? _mediaTagsChangedAsync;
    private readonly Func<IReadOnlyList<string>, Task>? _mediaDeletedAsync;
    private bool _isDisposed;

    internal IndependentMediaWindowService(
        ActivePlaybackTarget activePlaybackTarget,
        Func<string, IReadOnlyList<string>, Task>? mediaTagsChangedAsync = null,
        Func<IReadOnlyList<string>, Task>? mediaDeletedAsync = null)
    {
        _activePlaybackTarget = activePlaybackTarget;
        _mediaTagsChangedAsync = mediaTagsChangedAsync;
        _mediaDeletedAsync = mediaDeletedAsync;
    }

    public void OpenMediaWindows(
        IReadOnlyList<MediaItemViewModel> mediaItems,
        IReadOnlyList<MediaItemViewModel> navigationItems)
    {
        if (_isDisposed)
        {
            AppTraceLogger.Log("IndependentMediaWindow", "OpenMediaWindows skipped because service is disposed.");
            return;
        }

        if (mediaItems.Count == 0)
        {
            return;
        }

        foreach (var media in mediaItems)
        {
            // Each window only needs a prev/next neighborhood around its
            // starting item. Sharing the entire library snapshot would
            // copy thousands of view-models onto the UI thread.
            var navigationSnapshots = CreateNavigationSnapshots(navigationItems, new[] { media });
            OpenMediaWindow(media, navigationSnapshots);
        }
    }

    internal void OpenTransferredSession(MediaPlayerSessionTransfer session)
    {
        if (_isDisposed || session.MediaItems.Count == 0) return;
        var window = new IndependentMediaWindow(session);
        TrackAndActivate(window);
    }

    internal bool TryPlayInActiveIndependentWindow(IndependentMediaWindowMedia media)
    {
        if (_isDisposed || _activePlaybackTarget.Kind != ActivePlaybackTargetKind.Independent)
        {
            return false;
        }

        var window = _activePlaybackTarget.IndependentTarget;
        if (window == null || !_windows.Contains(window))
        {
            return false;
        }

        window.PlayMedia(media);
        return true;
    }

    internal void ApplyExternalTags(string mediaId, IReadOnlyList<string> tags)
    {
        if (_isDisposed || string.IsNullOrWhiteSpace(mediaId))
        {
            return;
        }

        foreach (var window in _windows.ToList())
        {
            window.ApplyExternalTags(mediaId, tags);
        }
    }

    internal void RemoveDeletedMedia(IReadOnlyList<string> mediaIds)
    {
        if (_isDisposed || mediaIds.Count == 0)
        {
            return;
        }

        foreach (var window in _windows.ToList())
        {
            window.RemoveDeletedMedia(mediaIds);
        }
    }

    internal Task<bool> TryHandleActiveIndependentShortcutAsync(VirtualKey key)
    {
        if (_isDisposed || _activePlaybackTarget.Kind != ActivePlaybackTargetKind.Independent)
        {
            return Task.FromResult(false);
        }

        var window = _activePlaybackTarget.IndependentTarget;
        if (window == null || !_windows.Contains(window))
        {
            return Task.FromResult(false);
        }

        return window.TryHandleShortcutAsync(key);
    }

    private void OpenMediaWindow(
        MediaItemViewModel media,
        IReadOnlyList<IndependentMediaWindowMedia> navigationItems)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(media.FileSystemPath) || !File.Exists(media.FileSystemPath))
            {
                AppTraceLogger.Log(
                    "IndependentMediaWindow",
                    $"Skipped missing media. MediaId='{media.Id}', File='{media.FileName}', Path='{media.FileSystemPath}'.");
                return;
            }

            var initialIndex = FindNavigationIndex(navigationItems, media.Id);
            var window = new IndependentMediaWindow(navigationItems, initialIndex);
            TrackAndActivate(window);
            AppTraceLogger.Log(
                "IndependentMediaWindow",
                $"Opened. MediaId='{media.Id}', File='{media.FileName}', Type={media.Type}, InitialIndex={initialIndex}, QueueCount={navigationItems.Count}, ActiveWindowCount={_windows.Count}.");
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException(
                "IndependentMediaWindow",
                $"Open failed. MediaId='{media.Id}', File='{media.FileName}', Path='{media.FileSystemPath}'.",
                ex);
        }
    }

    private void TrackAndActivate(IndependentMediaWindow window)
    {
        window.Closed += IndependentMediaWindow_Closed;
        window.PlaybackTargetActivated += IndependentMediaWindow_PlaybackTargetActivated;
        window.MediaTagsChanged += IndependentMediaWindow_MediaTagsChanged;
        window.MediaDeleted += IndependentMediaWindow_MediaDeleted;
        _windows.Add(window);
        try
        {
            _activePlaybackTarget.SetIndependent(window);
            window.Activate();
        }
        catch
        {
            _windows.Remove(window);
            window.Closed -= IndependentMediaWindow_Closed;
            window.PlaybackTargetActivated -= IndependentMediaWindow_PlaybackTargetActivated;
            window.MediaTagsChanged -= IndependentMediaWindow_MediaTagsChanged;
            window.MediaDeleted -= IndependentMediaWindow_MediaDeleted;
            _activePlaybackTarget.ClearIndependentIfActive(window);
            try
            {
                window.Close();
            }
            catch
            {
                // Best effort cleanup after activation failure.
            }

            throw;
        }
    }

    internal static IReadOnlyList<IndependentMediaWindowMedia> CreateNavigationSnapshots(
        IReadOnlyList<MediaItemViewModel> navigationItems,
        IReadOnlyList<MediaItemViewModel> selectedItems,
        int radius = PlayerNavigationWindow.DefaultRadius)
    {
        var window = PlayerNavigationWindow.SliceAround(
            navigationItems,
            selectedItems,
            static media => media.Id,
            radius);

        var snapshots = new List<IndependentMediaWindowMedia>(window.Count);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var media in window)
        {
            if (!seenIds.Add(media.Id))
            {
                continue;
            }

            snapshots.Add(CreateSnapshot(media));
        }

        return snapshots;
    }

    internal static IndependentMediaWindowMedia CreateSnapshot(MediaItemViewModel media)
    {
        return new IndependentMediaWindowMedia(
            media.Id,
            media.FileSystemPath,
            media.FileName,
            media.Type,
            media.ResolutionText,
            media.ContentHash,
            media.FingerprintVersion,
            media.Tags.ToList(),
            media.Rotation,
            media.Size,
            media.Media.ModifiedAt,
            media.Media.Duration,
            media.Media.Width,
            media.Media.Height);
    }

    private static int FindNavigationIndex(IReadOnlyList<IndependentMediaWindowMedia> navigationItems, string mediaId)
    {
        for (var index = 0; index < navigationItems.Count; index++)
        {
            if (string.Equals(navigationItems[index].Id, mediaId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return 0;
    }

    public void HideAllWindows()
    {
        foreach (var window in _windows.ToList())
        {
            window.MinimizeForBossMode();
        }
    }

    public void RestoreAllWindowsFromBossMode()
    {
        foreach (var window in _windows.ToList())
        {
            window.RestoreFromBossMode();
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        foreach (var window in _windows.ToList())
        {
            window.Closed -= IndependentMediaWindow_Closed;
            window.PlaybackTargetActivated -= IndependentMediaWindow_PlaybackTargetActivated;
            window.MediaTagsChanged -= IndependentMediaWindow_MediaTagsChanged;
            window.MediaDeleted -= IndependentMediaWindow_MediaDeleted;
            try
            {
                window.Close();
            }
            catch (Exception ex)
            {
                AppTraceLogger.LogException("IndependentMediaWindow", "Failed to close independent media window during service disposal.", ex);
            }
        }

        _windows.Clear();
        _activePlaybackTarget.SetMain();
        AppTraceLogger.Log("IndependentMediaWindow", "IndependentMediaWindowService disposed.");
    }

    private void IndependentMediaWindow_Closed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
    {
        if (sender is not IndependentMediaWindow window)
        {
            return;
        }

        window.Closed -= IndependentMediaWindow_Closed;
        window.PlaybackTargetActivated -= IndependentMediaWindow_PlaybackTargetActivated;
        window.MediaTagsChanged -= IndependentMediaWindow_MediaTagsChanged;
        window.MediaDeleted -= IndependentMediaWindow_MediaDeleted;
        _ = _windows.Remove(window);
        _activePlaybackTarget.ClearIndependentIfActive(window);
        if (_windows.Count > 0 && _activePlaybackTarget.Kind == ActivePlaybackTargetKind.Main)
        {
            _activePlaybackTarget.SetIndependent(_windows[^1]);
        }
        AppTraceLogger.Log("IndependentMediaWindow", $"Closed. ActiveWindowCount={_windows.Count}.");
    }

    private void IndependentMediaWindow_PlaybackTargetActivated(object? sender, EventArgs e)
    {
        if (sender is IndependentMediaWindow window)
        {
            _activePlaybackTarget.SetIndependent(window);
        }
    }

    private void IndependentMediaWindow_MediaTagsChanged(object? sender, IndependentMediaWindowTagsChangedEventArgs e)
    {
        var callback = _mediaTagsChangedAsync;
        if (callback == null)
        {
            return;
        }

        _ = NotifyMediaTagsChangedAsync(callback, e);
    }

    private static async Task NotifyMediaTagsChangedAsync(
        Func<string, IReadOnlyList<string>, Task> callback,
        IndependentMediaWindowTagsChangedEventArgs e)
    {
        try
        {
            await callback(e.MediaId, e.Tags);
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException(
                "IndependentMediaWindow",
                $"Main window tag sync failed. MediaId='{e.MediaId}', TagCount={e.Tags.Count}.",
                ex);
        }
    }

    private void IndependentMediaWindow_MediaDeleted(object? sender, IndependentMediaWindowDeletedEventArgs e)
    {
        var callback = _mediaDeletedAsync;
        if (callback == null || e.MediaIds.Count == 0)
        {
            return;
        }

        _ = NotifyMediaDeletedAsync(callback, e);
    }

    private static async Task NotifyMediaDeletedAsync(
        Func<IReadOnlyList<string>, Task> callback,
        IndependentMediaWindowDeletedEventArgs e)
    {
        try
        {
            await callback(e.MediaIds);
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException(
                "IndependentMediaWindow",
                $"Main window delete sync failed. MediaCount={e.MediaIds.Count}.",
                ex);
        }
    }
}
