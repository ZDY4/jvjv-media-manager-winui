using JvJvMediaManager.Utilities;
using JvJvMediaManager.ViewModels;
using JvJvMediaManager.ViewModels.MainPage;

namespace JvJvMediaManager.Services.MainPage;

public sealed class MediaDeletionWorkflow
{
    private readonly LibraryShellViewModel _viewModel;
    private readonly Func<int, Task<bool>> _confirmBatchDeleteAsync;
    private readonly Func<string, string, Task> _showInfoAsync;
    private readonly Action _releasePreviewHandles;
    private readonly Action _clearPlayerSelection;
    private readonly Action<MediaItemViewModel> _updatePlayer;
    private readonly Action<MediaItemViewModel> _forceUpdatePlayer;
    private readonly Action<MediaItemViewModel?> _syncSelection;
    private readonly Func<MediaItemViewModel?, IDisposable> _preserveSelectionDuringCollectionMutation;

    public MediaDeletionWorkflow(
        LibraryShellViewModel viewModel,
        Func<int, Task<bool>> confirmBatchDeleteAsync,
        Func<string, string, Task> showInfoAsync,
        Action releasePreviewHandles,
        Action clearPlayerSelection,
        Action<MediaItemViewModel> updatePlayer,
        Action<MediaItemViewModel> forceUpdatePlayer,
        Action<MediaItemViewModel?> syncSelection,
        Func<MediaItemViewModel?, IDisposable> preserveSelectionDuringCollectionMutation)
    {
        _viewModel = viewModel;
        _confirmBatchDeleteAsync = confirmBatchDeleteAsync;
        _showInfoAsync = showInfoAsync;
        _releasePreviewHandles = releasePreviewHandles;
        _clearPlayerSelection = clearPlayerSelection;
        _updatePlayer = updatePlayer;
        _forceUpdatePlayer = forceUpdatePlayer;
        _syncSelection = syncSelection;
        _preserveSelectionDuringCollectionMutation = preserveSelectionDuringCollectionMutation;
    }

    public async Task DeleteAsync(IEnumerable<MediaItemViewModel> initialSelection)
    {
        var selected = NormalizeSelection(initialSelection);
        if (selected.Count == 0)
        {
            AppTraceLogger.Log("MediaDeletion", "DeleteAsync skipped. SelectionCount=0.");
            return;
        }

        if (selected.Count > 1 && !await _confirmBatchDeleteAsync(selected.Count))
        {
            AppTraceLogger.Log("MediaDeletion", $"DeleteAsync canceled by user. SelectionCount={selected.Count}.");
            return;
        }

        var currentMediaId = _viewModel.SelectedMedia?.Id;
        var isDeletingCurrent = !string.IsNullOrWhiteSpace(currentMediaId)
            && selected.Any(item => string.Equals(item.Id, currentMediaId, StringComparison.Ordinal));

        if (isDeletingCurrent)
        {
            AppTraceLogger.Log("MediaDeletion", $"DeleteAsync releasing preview handles. CurrentMediaId='{currentMediaId}'.");
            _releasePreviewHandles();
            await SelectReplacementBeforeDeleteAsync(selected);
            await Task.Yield();
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
            using var selectionMutation = isDeletingCurrent
                ? _preserveSelectionDuringCollectionMutation(_viewModel.SelectedMedia)
                : null;
            nextSelection = await _viewModel.DeleteMediaAsync(deleted);
            _viewModel.StatusMessage = $"已将 {deleted.Count} 个文件移到回收站。";
        }

        if (nextSelection == null && deleted.Count > 0)
        {
            _clearPlayerSelection();
        }
        else if (isDeletingCurrent && nextSelection != null && deleted.Any(item => string.Equals(item.Id, currentMediaId, StringComparison.Ordinal)))
        {
            _syncSelection(nextSelection);
            _forceUpdatePlayer(nextSelection);
            AppTraceLogger.Log(
                "MediaDeletion",
                $"DeleteAsync forced replacement player update after deleting current media. ReplacementId='{nextSelection.Id}', Deleted={deleted.Count}.");
        }

        var currentDeleteFailed = !string.IsNullOrWhiteSpace(currentMediaId)
            && selected.Any(item => string.Equals(item.Id, currentMediaId, StringComparison.Ordinal))
            && deleted.All(item => !string.Equals(item.Id, currentMediaId, StringComparison.Ordinal));
        if (currentDeleteFailed && _viewModel.SelectedMedia != null)
        {
            _updatePlayer(_viewModel.SelectedMedia);
        }

        if (failed.Count > 0)
        {
            var detail = string.Join(Environment.NewLine, failed.Take(5));
            var suffix = failed.Count > 5 ? $"{Environment.NewLine}... 另有 {failed.Count - 5} 个文件移到回收站失败。" : string.Empty;
            await _showInfoAsync("部分文件移到回收站失败", $"{detail}{suffix}");
        }

        _syncSelection(_viewModel.SelectedMedia);
        AppTraceLogger.Log("MediaDeletion", $"DeleteAsync completed. Requested={selected.Count}, Deleted={deleted.Count}, Failed={failed.Count}, NextSelection='{_viewModel.SelectedMedia?.Id ?? "<null>"}'.");
    }

    private async Task SelectReplacementBeforeDeleteAsync(IReadOnlyList<MediaItemViewModel> selected)
    {
        var replacement = await FindReplacementSelectionAsync(selected);
        if (replacement == null)
        {
            return;
        }

        _viewModel.SelectedMedia = replacement;
        _syncSelection(replacement);
        _forceUpdatePlayer(replacement);
        AppTraceLogger.Log(
            "MediaDeletion",
            $"Selected replacement before file delete and forced player update. ReplacementId='{replacement.Id}', PendingDeleteCount={selected.Count}.");
    }

    private async Task<MediaItemViewModel?> FindReplacementSelectionAsync(IReadOnlyList<MediaItemViewModel> selected)
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
            : items.IndexOf(_viewModel.SelectedMedia);
        if (currentIndex < 0)
        {
            currentIndex = selected
                .Select(item => items.IndexOf(item))
                .Where(index => index >= 0)
                .DefaultIfEmpty(0)
                .Min();
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

    private static void MoveMediaFileToRecycleBin(MediaItemViewModel media)
    {
        var path = media.FileSystemPath;
        if (!File.Exists(path))
        {
            return;
        }

        RecycleBinHelper.SendToRecycleBin(path);
    }
}
