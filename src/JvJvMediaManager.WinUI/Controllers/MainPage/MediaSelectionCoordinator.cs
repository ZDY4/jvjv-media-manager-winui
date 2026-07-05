using JvJvMediaManager.ViewModels;

namespace JvJvMediaManager.Controllers.MainPage;

internal sealed class MediaSelectionCoordinator
{
    private readonly IReadOnlyDictionary<string, MediaItemViewModel> _loadedMediaById;
    private HashSet<string> _selectedItemIds = new(StringComparer.Ordinal);
    private int _mutationDepth;
    private string? _mutationPreserveId;
    private bool _mutationSuppressedEmptySelection;
    private string? _postMutationPreserveId;
    private long _postMutationSuppressSelectionUntilTicks;
    private string? _lastViewModelSelectionSyncId;
    private long _lastViewModelSelectionSyncTicks;

    public MediaSelectionCoordinator(IReadOnlyDictionary<string, MediaItemViewModel> loadedMediaById)
    {
        _loadedMediaById = loadedMediaById;
    }

    public IReadOnlySet<string> SelectedItemIds => _selectedItemIds;

    public bool HasSelection => _selectedItemIds.Count > 0;

    public int MutationDepth => _mutationDepth;

    public bool MutationSuppressedEmptySelection => _mutationSuppressedEmptySelection;

    public HashSet<string> Snapshot()
    {
        return _selectedItemIds.ToHashSet(StringComparer.Ordinal);
    }

    public void ReplaceSelection(IEnumerable<string> selectedIds)
    {
        _selectedItemIds = selectedIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
    }

    public void Clear()
    {
        _selectedItemIds.Clear();
    }

    public bool Contains(string mediaId)
    {
        return !string.IsNullOrWhiteSpace(mediaId)
            && _selectedItemIds.Contains(mediaId);
    }

    public void MarkViewModelSelectionSynced(string mediaId)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return;
        }

        _lastViewModelSelectionSyncId = mediaId;
        _lastViewModelSelectionSyncTicks = DateTime.UtcNow.Ticks;
    }

    public bool IsRecentViewModelSelectionSync(string mediaId, TimeSpan settleWindow)
    {
        return Contains(mediaId)
            && string.Equals(_lastViewModelSelectionSyncId, mediaId, StringComparison.Ordinal)
            && DateTime.UtcNow.Ticks <= _lastViewModelSelectionSyncTicks + settleWindow.Ticks;
    }

    public void BeginMutation(string? preserveId)
    {
        _mutationDepth++;
        if (!string.IsNullOrWhiteSpace(preserveId))
        {
            _mutationPreserveId = preserveId;
            _mutationSuppressedEmptySelection = false;
        }
    }

    public MutationEndResult EndMutation()
    {
        if (_mutationDepth <= 0)
        {
            return new MutationEndResult(false, 0, _mutationPreserveId, _mutationSuppressedEmptySelection);
        }

        _mutationDepth--;
        var result = new MutationEndResult(true, _mutationDepth, _mutationPreserveId, _mutationSuppressedEmptySelection);
        if (_mutationDepth == 0)
        {
            _mutationPreserveId = null;
            _mutationSuppressedEmptySelection = false;
        }

        return result;
    }

    public bool IsActiveMutationPreserve(string mediaId)
    {
        return _mutationDepth > 0
            && !string.IsNullOrWhiteSpace(_mutationPreserveId)
            && string.Equals(_mutationPreserveId, mediaId, StringComparison.Ordinal);
    }

    public void MarkMutationSuppressedEmptySelection()
    {
        _mutationSuppressedEmptySelection = true;
    }

    public void ArmPostMutationSuppression(string mediaId, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return;
        }

        _postMutationPreserveId = mediaId;
        _postMutationSuppressSelectionUntilTicks = DateTime.UtcNow.Add(duration).Ticks;
    }

    public bool IsPostMutationSuppressionActive(string mediaId)
    {
        if (string.IsNullOrWhiteSpace(_postMutationPreserveId)
            || !string.Equals(_postMutationPreserveId, mediaId, StringComparison.Ordinal))
        {
            return false;
        }

        if (DateTime.UtcNow.Ticks <= _postMutationSuppressSelectionUntilTicks)
        {
            return true;
        }

        _postMutationPreserveId = null;
        _postMutationSuppressSelectionUntilTicks = 0;
        return false;
    }

    public IReadOnlyList<MediaItemViewModel> GetSelectedItems()
    {
        return _selectedItemIds
            .Select(id => _loadedMediaById.TryGetValue(id, out var item) ? item : null)
            .OfType<MediaItemViewModel>()
            .ToList();
    }

    public void ApplySelectedStateFlags(IReadOnlySet<string> previousSelectedIds)
    {
        foreach (var removedId in previousSelectedIds)
        {
            if (_selectedItemIds.Contains(removedId))
            {
                continue;
            }

            if (_loadedMediaById.TryGetValue(removedId, out var removedItem) && removedItem.IsSelected)
            {
                removedItem.IsSelected = false;
            }
        }

        foreach (var addedId in _selectedItemIds)
        {
            if (previousSelectedIds.Contains(addedId))
            {
                continue;
            }

            if (_loadedMediaById.TryGetValue(addedId, out var addedItem) && !addedItem.IsSelected)
            {
                addedItem.IsSelected = true;
            }
        }
    }

    public sealed record MutationEndResult(
        bool HadActiveMutation,
        int Depth,
        string? PreserveId,
        bool SuppressedEmptySelection);
}
