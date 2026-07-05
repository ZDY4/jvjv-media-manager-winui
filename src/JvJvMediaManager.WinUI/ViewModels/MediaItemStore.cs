using System.Collections.Concurrent;
using JvJvMediaManager.Models;

namespace JvJvMediaManager.ViewModels;

public sealed class MediaItemStore
{
    private readonly ConcurrentDictionary<string, MediaItemViewModel> _items = new(StringComparer.Ordinal);

    public int Count
    {
        get
        {
            return _items.Count;
        }
    }

    public MediaItemViewModel GetOrUpdate(MediaFile media)
    {
        if (string.IsNullOrWhiteSpace(media.Id))
        {
            return new MediaItemViewModel(media);
        }

        var existing = _items.GetOrAdd(media.Id, _ => new MediaItemViewModel(media));

        existing.UpdateFrom(media);
        return existing;
    }

    public IReadOnlyList<MediaItemViewModel> GetOrUpdateRange(IEnumerable<MediaFile> mediaFiles)
    {
        return mediaFiles.Select(GetOrUpdate).ToList();
    }

    public int PruneExcept(IEnumerable<string> retainedMediaIds)
    {
        var retainedIds = retainedMediaIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);

        var removed = 0;
        foreach (var mediaId in _items.Keys)
        {
            if (retainedIds.Contains(mediaId))
            {
                continue;
            }

            if (_items.TryRemove(mediaId, out _))
            {
                removed++;
            }
        }

        return removed;
    }

    public void RemoveMany(IEnumerable<string> mediaIds)
    {
        foreach (var mediaId in mediaIds)
        {
            if (!string.IsNullOrWhiteSpace(mediaId))
            {
                _items.TryRemove(mediaId, out _);
            }
        }
    }

    public void Clear()
    {
        _items.Clear();
    }
}
