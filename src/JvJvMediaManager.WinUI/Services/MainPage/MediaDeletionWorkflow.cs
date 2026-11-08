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

        if (selected.Count > 1 && !await _viewModel.RunUiAsync<bool>(() => _confirmBatchDeleteAsync(selected.Count)))
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
            plannedReplacement = await _viewModel.InvokeOnUiThreadAsync(() => FindLoadedReplacementSelection(selected))
                ?? await FindReplacementSelectionAsync(selected);
            if (plannedReplacement != null)
            {
                // Switch the player before recycle-bin I/O so the next media
                // starts opening immediately. Recycle-bin retries must not
                // run on the UI thread or the new source cannot paint.
                AppTraceLogger.Log(
                    "MediaDeletion",
                    $"Selected replacement before file delete. CurrentMediaId='{currentMediaId}', ReplacementId='{plannedReplacement.Id}'.");
                await _viewModel.InvokeOnUiThreadAsync(() =>
                {
                    _viewModel.SelectedMedia = plannedReplacement;
                    _syncSelection(plannedReplacement);
                    _forceUpdatePlayer(plannedReplacement);
                });
                switchedToReplacement = true;
                // There is no SynchronizationContext, so UiThreadYield cannot
                // pump the dispatcher. Post a turn explicitly and let MediaPlayer
                // release the outgoing file before recycle-bin I/O starts.
                await _viewModel.YieldUiThreadAsync();
            }
            else
            {
                AppTraceLogger.Log(
                    "MediaDeletion",
                    $"DeleteAsync releasing preview handles; no replacement available. CurrentMediaId='{currentMediaId}'.");
                await _viewModel.InvokeOnUiThreadAsync(() => _releasePreviewHandlesForMedia(currentMediaId));
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
        IDisposable? selectionMutation = null;
        try
        {
            if (deleted.Count > 0)
            {
                AppTraceLogger.Log("MediaDeletion", $"DeleteAsync deleting database records. DeletedFiles={deleted.Count}, FailedFiles={failed.Count}.");
                var preservedSelection = plannedReplacement ?? _viewModel.SelectedMedia;
                selectionMutation = await _viewModel.InvokeOnUiThreadAsync(() => _preserveSelectionDuringCollectionMutation(preservedSelection));
                nextSelection = await _viewModel.DeleteMediaAsync(deleted);
                var deletedCount = deleted.Count;
                await _viewModel.InvokeOnUiThreadAsync(() =>
                {
                    _viewModel.StatusMessage = $"已将 {deletedCount} 个文件移到回收站。";
                });
            }
        }
        finally
        {
            if (selectionMutation != null)
            {
                try
                {
                    await _viewModel.InvokeOnUiThreadAsync(selectionMutation.Dispose);
                }
                catch (Exception ex)
                {
                    AppTraceLogger.LogException("MediaDeletion", "Selection mutation scope could not be disposed on the UI thread.", ex);
                }
            }
        }

        if (isDeletingCurrent && deleted.Count > 0)
            {
                if (nextSelection == null || deleted.Any(item => string.Equals(item.Id, nextSelection.Id, StringComparison.Ordinal)))
                {
                    nextSelection = plannedReplacement ?? await FindReplacementSelectionAsync(deleted);
                }

                if (nextSelection != null)
                {
                    var replacement = nextSelection;
                    var alreadyPlayingReplacement = switchedToReplacement
                        && plannedReplacement != null
                        && string.Equals(plannedReplacement.Id, replacement.Id, StringComparison.Ordinal);
                    await _viewModel.InvokeOnUiThreadAsync(() =>
                    {
                        _viewModel.SelectedMedia = replacement;
                        _syncSelection(replacement);
                        if (!alreadyPlayingReplacement)
                        {
                            _forceUpdatePlayer(replacement);
                        }
                    });

                    AppTraceLogger.Log(
                        "MediaDeletion",
                        $"DeleteAsync applied replacement after current media deletion. ReplacementId='{replacement.Id}', Deleted={deleted.Count}, AlreadyPlaying={alreadyPlayingReplacement}.");
                }
                else
                {
                    await _viewModel.InvokeOnUiThreadAsync(() => _clearPlayerSelection());
                }
            }

            var currentDeleteFailed = !string.IsNullOrWhiteSpace(currentMediaId)
                && selected.Any(item => string.Equals(item.Id, currentMediaId, StringComparison.Ordinal))
                && deleted.All(item => !string.Equals(item.Id, currentMediaId, StringComparison.Ordinal));
            if (currentDeleteFailed && originalCurrentMedia != null)
            {
                var restored = originalCurrentMedia;
                await _viewModel.InvokeOnUiThreadAsync(() =>
                {
                    _viewModel.SelectedMedia = restored;
                    _syncSelection(restored);
                    _forceUpdatePlayer(restored);
                });
                AppTraceLogger.Log(
                    "MediaDeletion",
                    $"DeleteAsync restored original current media after delete failure. MediaId='{restored.Id}', Failed={failed.Count}.");
            }

            if (failed.Count > 0)
            {
                var detail = string.Join(Environment.NewLine, failed.Take(5));
                var suffix = failed.Count > 5 ? $"{Environment.NewLine}... 另有 {failed.Count - 5} 个文件移到回收站失败。" : string.Empty;
                await _viewModel.RunUiAsync(() => _showInfoAsync("部分文件移到回收站失败", $"{detail}{suffix}"));
            }

            await _viewModel.InvokeOnUiThreadAsync(() => _syncSelection(_viewModel.SelectedMedia));
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

        var initial = await _viewModel.InvokeOnUiThreadAsync(() => DescribeLoadedWindow());
        if (initial.Count == 0)
        {
            if (!initial.HasMore)
            {
                return null;
            }

            await _viewModel.EnsureMediaItemLoadedAsync(0);
            initial = await _viewModel.InvokeOnUiThreadAsync(() => DescribeLoadedWindow());
            if (initial.Count == 0)
            {
                return null;
            }
        }

        var forward = await _viewModel.InvokeOnUiThreadAsync(() =>
            FindForwardReplacement(selected, removedIds));
        if (forward.Match != null)
        {
            return forward.Match;
        }

        if (forward.HasMore)
        {
            var probeIndex = forward.LoadedCount;
            while (true)
            {
                var previousCount = probeIndex;
                await _viewModel.EnsureMediaItemLoadedAsync(probeIndex);
                var page = await _viewModel.InvokeOnUiThreadAsync(() =>
                    FindReplacementInRange(removedIds, Math.Max(probeIndex, 0), previousCount));
                if (page.Match != null)
                {
                    return page.Match;
                }

                if (!page.Advanced || !page.HasMore)
                {
                    break;
                }

                probeIndex = page.LoadedCount;
            }
        }

        var currentIndex = forward.CurrentIndex;
        return await _viewModel.InvokeOnUiThreadAsync(() =>
            FindBackwardReplacement(removedIds, currentIndex));
    }

    private (int Count, bool HasMore) DescribeLoadedWindow()
    {
        var items = _viewModel.FilteredMediaItems;
        return (items.Count, items.HasMoreItems);
    }

    private (MediaItemViewModel? Match, int CurrentIndex, int LoadedCount, bool HasMore) FindForwardReplacement(
        IReadOnlyList<MediaItemViewModel> selected,
        HashSet<string> removedIds)
    {
        var items = _viewModel.FilteredMediaItems;
        var currentIndex = ResolveReplacementIndex(items, selected);
        for (var index = currentIndex + 1; index < items.Count; index++)
        {
            if (!removedIds.Contains(items[index].Id))
            {
                return (items[index], currentIndex, items.Count, items.HasMoreItems);
            }
        }

        return (null, currentIndex, items.Count, items.HasMoreItems);
    }

    private (MediaItemViewModel? Match, int LoadedCount, bool HasMore, bool Advanced) FindReplacementInRange(
        HashSet<string> removedIds,
        int startIndex,
        int previousCount)
    {
        var items = _viewModel.FilteredMediaItems;
        if (items.Count <= previousCount)
        {
            return (null, items.Count, items.HasMoreItems, false);
        }

        for (var index = Math.Max(startIndex, 0); index < items.Count; index++)
        {
            if (!removedIds.Contains(items[index].Id))
            {
                return (items[index], items.Count, items.HasMoreItems, true);
            }
        }

        return (null, items.Count, items.HasMoreItems, true);
    }

    private MediaItemViewModel? FindBackwardReplacement(HashSet<string> removedIds, int currentIndex)
    {
        var items = _viewModel.FilteredMediaItems;
        for (var index = Math.Min(currentIndex - 1, items.Count - 1); index >= 0; index--)
        {
            if (!removedIds.Contains(items[index].Id))
            {
                return items[index];
            }
        }

        return null;
    }

    private int ResolveReplacementIndex(IReadOnlyList<MediaItemViewModel> items, IReadOnlyList<MediaItemViewModel> selected)
    {
        var currentIndex = _viewModel.SelectedMedia == null
            ? -1
            : IndexOfMediaId(items, _viewModel.SelectedMedia.Id);
        if (currentIndex >= 0)
        {
            return currentIndex;
        }

        return selected
            .Select(item => IndexOfMediaId(items, item.Id))
            .Where(index => index >= 0)
            .DefaultIfEmpty(-1)
            .Min();
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
