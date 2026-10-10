using JvJvMediaManager.Utilities;
using JvJvMediaManager.ViewModels;
using JvJvMediaManager.ViewModels.MainPage;

namespace JvJvMediaManager.Services.MainPage;

public sealed class MediaDeletionWorkflow
{
    private readonly LibraryShellViewModel _viewModel;
    private readonly Func<int, Task<bool>> _confirmBatchDeleteAsync;
    private readonly Func<string, string, Task> _showInfoAsync;
    private readonly Action<string?> _releasePreviewHandlesForMedia;
    private readonly Action _clearPlayerSelection;
    private readonly Action<MediaItemViewModel> _forceUpdatePlayer;
    private readonly Action<MediaItemViewModel?> _syncSelection;
    private readonly Func<MediaItemViewModel?, IDisposable> _preserveSelectionDuringCollectionMutation;
    private int _deleteInProgress;

    public MediaDeletionWorkflow(
        LibraryShellViewModel viewModel,
        Func<int, Task<bool>> confirmBatchDeleteAsync,
        Func<string, string, Task> showInfoAsync,
        Action<string?> releasePreviewHandlesForMedia,
        Action clearPlayerSelection,
        Action<MediaItemViewModel> forceUpdatePlayer,
        Action<MediaItemViewModel?> syncSelection,
        Func<MediaItemViewModel?, IDisposable> preserveSelectionDuringCollectionMutation)
    {
        _viewModel = viewModel;
        _confirmBatchDeleteAsync = confirmBatchDeleteAsync;
        _showInfoAsync = showInfoAsync;
        _releasePreviewHandlesForMedia = releasePreviewHandlesForMedia;
        _clearPlayerSelection = clearPlayerSelection;
        _forceUpdatePlayer = forceUpdatePlayer;
        _syncSelection = syncSelection;
        _preserveSelectionDuringCollectionMutation = preserveSelectionDuringCollectionMutation;
    }

    public async Task<IReadOnlyList<string>> DeleteAsync(IEnumerable<MediaItemViewModel> initialSelection)
    {
        if (Interlocked.Exchange(ref _deleteInProgress, 1) == 1)
        {
            AppTraceLogger.Log("MediaDeletion", "DeleteAsync skipped because another delete is already in progress.");
            return Array.Empty<string>();
        }

        try
        {
            return await DeleteCoreAsync(initialSelection);
        }
        finally
        {
            Interlocked.Exchange(ref _deleteInProgress, 0);
        }
    }

    private async Task<IReadOnlyList<string>> DeleteCoreAsync(IEnumerable<MediaItemViewModel> initialSelection)
    {
        var selected = NormalizeSelection(initialSelection);
        if (selected.Count == 0)
        {
            AppTraceLogger.Log("MediaDeletion", "DeleteAsync skipped. SelectionCount=0.");
            return Array.Empty<string>();
        }

        if (selected.Count > 1 && !await _confirmBatchDeleteAsync(selected.Count))
        {
            AppTraceLogger.Log("MediaDeletion", $"DeleteAsync canceled by user. SelectionCount={selected.Count}.");
            return Array.Empty<string>();
        }

        var originalCurrentMedia = _viewModel.SelectedMedia;
        var currentMediaId = originalCurrentMedia?.Id;
        var isDeletingCurrent = !string.IsNullOrWhiteSpace(currentMediaId)
            && selected.Any(item => string.Equals(item.Id, currentMediaId, StringComparison.Ordinal));
        MediaItemViewModel? plannedReplacement = null;
        var switchedToReplacement = false;

        if (isDeletingCurrent)
        {
            plannedReplacement = FindLoadedReplacementSelection(selected)
                ?? await FindReplacementSelectionAsync(selected);
            if (plannedReplacement != null)
            {
                // Switch the player before recycle-bin I/O so the next media
                // starts opening immediately. Recycle-bin retries must not
                // run on the UI thread or the new source cannot paint.
                AppTraceLogger.Log(
                    "MediaDeletion",
                    $"Selected replacement before file delete. CurrentMediaId='{currentMediaId}', ReplacementId='{plannedReplacement.Id}'.");
                _viewModel.SelectedMedia = plannedReplacement;
                _syncSelection(plannedReplacement);
                _forceUpdatePlayer(plannedReplacement);
                switchedToReplacement = true;
                // Let MediaPlayer finish swapping sources on this dispatcher
                // pass before recycle-bin I/O starts. Otherwise the outgoing
                // file stays locked and the next video cannot paint yet.
                await UiThreadYield.NextPassAsync();
            }
            else
            {
                AppTraceLogger.Log(
                    "MediaDeletion",
                    $"DeleteAsync releasing preview handles; no replacement available. CurrentMediaId='{currentMediaId}'.");
                _releasePreviewHandlesForMedia(currentMediaId);
            }
        }

        var deletionResult = await Task.Run(() =>
        {
            var deleted = new List<MediaItemViewModel>();
            var failed = new List<string>();

            foreach (var media in selected)
            {
                try
                {
                    MoveMediaFileToRecycleBin(media);
                    deleted.Add(media);
                }
                catch (Exception ex)
                {
                    failed.Add($"{media.FileName}: {ex.Message}");
                    AppTraceLogger.LogException("MediaDeletion", $"MoveMediaFileToRecycleBin failed. MediaId='{media.Id}', Path='{media.FileSystemPath}'.", ex);
                }
            }

            return (Deleted: deleted, Failed: failed);
        });

        var deleted = deletionResult.Deleted;
        var failed = deletionResult.Failed;

        MediaItemViewModel? nextSelection = null;
        if (deleted.Count > 0)
        {
            AppTraceLogger.Log("MediaDeletion", $"DeleteAsync deleting database records. DeletedFiles={deleted.Count}, FailedFiles={failed.Count}.");
            using var selectionMutation = _preserveSelectionDuringCollectionMutation(plannedReplacement ?? _viewModel.SelectedMedia);
            nextSelection = await _viewModel.DeleteMediaAsync(deleted);
            _viewModel.StatusMessage = $"已将 {deleted.Count} 个文件移到回收站。";
        }

        if (isDeletingCurrent && deleted.Count > 0)
        {
            if (nextSelection == null || deleted.Any(item => string.Equals(item.Id, nextSelection.Id, StringComparison.Ordinal)))
            {
                nextSelection = plannedReplacement ?? await FindReplacementSelectionAsync(deleted);
            }

            if (nextSelection != null)
            {
                _viewModel.SelectedMedia = nextSelection;
                _syncSelection(nextSelection);
                var alreadyPlayingReplacement = switchedToReplacement
                    && plannedReplacement != null
                    && string.Equals(plannedReplacement.Id, nextSelection.Id, StringComparison.Ordinal);
                if (!alreadyPlayingReplacement)
                {
                    _forceUpdatePlayer(nextSelection);
                }

                AppTraceLogger.Log(
                    "MediaDeletion",
                    $"DeleteAsync applied replacement after current media deletion. ReplacementId='{nextSelection.Id}', Deleted={deleted.Count}, AlreadyPlaying={alreadyPlayingReplacement}.");
            }
            else
            {
                _clearPlayerSelection();
            }
        }

        var currentDeleteFailed = !string.IsNullOrWhiteSpace(currentMediaId)
            && selected.Any(item => string.Equals(item.Id, currentMediaId, StringComparison.Ordinal))
            && deleted.All(item => !string.Equals(item.Id, currentMediaId, StringComparison.Ordinal));
        if (currentDeleteFailed && originalCurrentMedia != null)
        {
            _viewModel.SelectedMedia = originalCurrentMedia;
            _syncSelection(originalCurrentMedia);
            _forceUpdatePlayer(originalCurrentMedia);
            AppTraceLogger.Log(
                "MediaDeletion",
                $"DeleteAsync restored original current media after delete failure. MediaId='{originalCurrentMedia.Id}', Failed={failed.Count}.");
        }

        if (failed.Count > 0)
        {
            var detail = string.Join(Environment.NewLine, failed.Take(5));
            var suffix = failed.Count > 5 ? $"{Environment.NewLine}... 另有 {failed.Count - 5} 个文件移到回收站失败。" : string.Empty;
            await _showInfoAsync("部分文件移到回收站失败", $"{detail}{suffix}");
        }

        _syncSelection(_viewModel.SelectedMedia);
        AppTraceLogger.Log("MediaDeletion", $"DeleteAsync completed. Requested={selected.Count}, Deleted={deleted.Count}, Failed={failed.Count}, NextSelection='{_viewModel.SelectedMedia?.Id ?? "<null>"}'.");
        return deleted.Select(item => item.Id).ToList();
    }

    private MediaItemViewModel? FindLoadedReplacementSelection(IReadOnlyList<MediaItemViewModel> selected)
    {
        var removedIds = selected
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var items = _viewModel.FilteredMediaItems;
        if (items.Count == 0)
        {
            return null;
        }

        var currentIndex = _viewModel.SelectedMedia == null
            ? -1
            : IndexOfMediaId(items, _viewModel.SelectedMedia.Id);
        if (currentIndex < 0)
        {
            currentIndex = selected
                .Select(item => IndexOfMediaId(items, item.Id))
                .Where(index => index >= 0)
                .DefaultIfEmpty(-1)
                .Min();
        }

        for (var index = currentIndex + 1; index < items.Count; index++)
        {
            if (!removedIds.Contains(items[index].Id))
            {
                return items[index];
            }
        }

        for (var index = Math.Min(currentIndex - 1, items.Count - 1); index >= 0; index--)
        {
            if (!removedIds.Contains(items[index].Id))
            {
                return items[index];
            }
        }

        return null;
    }

    private async Task<MediaItemViewModel?> FindReplacementSelectionAsync(IReadOnlyList<MediaItemViewModel> selected)
    {
        var removedIds = selected
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var items = _viewModel.FilteredMediaItems;
        if (items.Count == 0)
        {
            if (!items.HasMoreItems)
            {
                return null;
            }

            await _viewModel.EnsureMediaItemLoadedAsync(0);
            if (items.Count == 0)
            {
                return null;
            }
        }

        var currentIndex = _viewModel.SelectedMedia == null
            ? -1
            : IndexOfMediaId(items, _viewModel.SelectedMedia.Id);
        if (currentIndex < 0)
        {
            var selectedIndex = selected
                .Select(item => IndexOfMediaId(items, item.Id))
                .Where(index => index >= 0)
                .DefaultIfEmpty(-1)
                .Min();
            currentIndex = selectedIndex;
        }

        for (var index = currentIndex + 1; index < items.Count; index++)
        {
            if (!removedIds.Contains(items[index].Id))
            {
                return items[index];
            }
        }

        if (items.HasMoreItems)
        {
            var probeIndex = items.Count;
            while (items.HasMoreItems)
            {
                var previousCount = items.Count;
                await _viewModel.EnsureMediaItemLoadedAsync(probeIndex);
                if (items.Count <= previousCount)
                {
                    break;
                }

                for (var index = Math.Max(probeIndex, 0); index < items.Count; index++)
                {
                    if (!removedIds.Contains(items[index].Id))
                    {
                        return items[index];
                    }
                }

                probeIndex = items.Count;
            }
        }

        for (var index = Math.Min(currentIndex - 1, items.Count - 1); index >= 0; index--)
        {
            if (!removedIds.Contains(items[index].Id))
            {
                return items[index];
            }
        }

        return null;
    }

    private List<MediaItemViewModel> NormalizeSelection(IEnumerable<MediaItemViewModel> initialSelection)
    {
        var selected = initialSelection.ToList();
        if (selected.Count == 0 && _viewModel.SelectedMedia != null)
        {
            selected.Add(_viewModel.SelectedMedia);
        }

        return selected
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    private static int IndexOfMediaId(IReadOnlyList<MediaItemViewModel> items, string mediaId)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return -1;
        }

        for (var index = 0; index < items.Count; index++)
        {
            if (string.Equals(items[index].Id, mediaId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static void MoveMediaFileToRecycleBin(MediaItemViewModel media)
    {
        var path = media.FileSystemPath;
        if (File.Exists(path))
        {
            RecycleBinHelper.SendToRecycleBin(path);
        }

    }
}
