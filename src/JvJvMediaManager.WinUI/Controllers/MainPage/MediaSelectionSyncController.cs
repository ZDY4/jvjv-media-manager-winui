using JvJvMediaManager.ViewModels;

namespace JvJvMediaManager.Controllers.MainPage;

internal sealed class MediaSelectionSyncController
{
    private readonly Dictionary<string, MediaItemViewModel> _loadedMediaById = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loadedMediaIds = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, MediaItemViewModel> LoadedMediaById => _loadedMediaById;
    public int LoadedCount => _loadedMediaById.Count;

    public bool Contains(string mediaId)
    {
        return _loadedMediaIds.Contains(mediaId);
    }

    public bool TryGet(string mediaId, out MediaItemViewModel item)
    {
        return _loadedMediaById.TryGetValue(mediaId, out item!);
    }

    public IReadOnlyCollection<MediaItemViewModel> LoadedItems => _loadedMediaById.Values;

    public void Rebuild(IEnumerable<MediaItemViewModel> items)
    {
        _loadedMediaById.Clear();
        _loadedMediaIds.Clear();
        foreach (var item in items)
        {
            Add(item);
        }
    }

    public void Add(MediaItemViewModel item)
    {
        _loadedMediaById[item.Id] = item;
        _loadedMediaIds.Add(item.Id);
    }

    public void Remove(MediaItemViewModel item)
    {
        _loadedMediaById.Remove(item.Id);
        _loadedMediaIds.Remove(item.Id);
    }
}
