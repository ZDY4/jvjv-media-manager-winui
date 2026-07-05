using JvJvMediaManager.Utilities;
using JvJvMediaManager.ViewModels;
using JvJvMediaManager.Views;

namespace JvJvMediaManager.Services;

public sealed class IndependentMediaWindowService : IDisposable
{
    private readonly List<IndependentMediaWindow> _windows = new();
    private readonly Func<string, IReadOnlyList<string>, Task>? _mediaTagsChangedAsync;
    private readonly Func<IReadOnlyList<string>, Task>? _mediaDeletedAsync;
    private bool _isDisposed;

    public IndependentMediaWindowService(
        Func<string, IReadOnlyList<string>, Task>? mediaTagsChangedAsync = null,
        Func<IReadOnlyList<string>, Task>? mediaDeletedAsync = null)
    {
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

        var navigationSnapshots = CreateNavigationSnapshots(navigationItems, mediaItems);
        foreach (var media in mediaItems)
        {
            OpenMediaWindow(media, navigationSnapshots);
        }
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
            window.Closed += IndependentMediaWindow_Closed;
            window.MediaTagsChanged += IndependentMediaWindow_MediaTagsChanged;
            window.MediaDeleted += IndependentMediaWindow_MediaDeleted;
            _windows.Add(window);
            window.Activate();
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

    private static IReadOnlyList<IndependentMediaWindowMedia> CreateNavigationSnapshots(
        IReadOnlyList<MediaItemViewModel> navigationItems,
        IReadOnlyList<MediaItemViewModel> selectedItems)
    {
        var snapshots = new List<IndependentMediaWindowMedia>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var media in navigationItems.Concat(selectedItems))
        {
            if (!seenIds.Add(media.Id))
            {
                continue;
            }

            snapshots.Add(CreateSnapshot(media));
        }

        return snapshots;
    }

    private static IndependentMediaWindowMedia CreateSnapshot(MediaItemViewModel media)
    {
        return new IndependentMediaWindowMedia(
            media.Id,
            media.FileSystemPath,
            media.FileName,
            media.Type,
            media.ResolutionText,
            media.ContentHash,
            media.FingerprintVersion,
            media.Tags.ToList());
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
        AppTraceLogger.Log("IndependentMediaWindow", "IndependentMediaWindowService disposed.");
    }

    private void IndependentMediaWindow_Closed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
    {
        if (sender is not IndependentMediaWindow window)
        {
            return;
        }

        window.Closed -= IndependentMediaWindow_Closed;
        window.MediaTagsChanged -= IndependentMediaWindow_MediaTagsChanged;
        window.MediaDeleted -= IndependentMediaWindow_MediaDeleted;
        _ = _windows.Remove(window);
        AppTraceLogger.Log("IndependentMediaWindow", $"Closed. ActiveWindowCount={_windows.Count}.");
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
