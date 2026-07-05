using JvJvMediaManager.Services;
using JvJvMediaManager.ViewModels;

namespace JvJvMediaManager.Controllers.MainPage;

internal sealed class IndependentMediaWindowController
{
    private readonly List<MediaItemViewModel> _mediaItems;
    private int _currentIndex;

    public IndependentMediaWindowController(
        IReadOnlyList<IndependentMediaWindowMedia> mediaItems,
        int initialIndex)
    {
        if (mediaItems.Count == 0)
        {
            throw new ArgumentException("At least one media item is required.", nameof(mediaItems));
        }

        _mediaItems = mediaItems.Select(media => new MediaItemViewModel(media.ToMediaFile())).ToList();
        _currentIndex = Math.Clamp(initialIndex, 0, _mediaItems.Count - 1);
    }

    public IReadOnlyList<MediaItemViewModel> MediaItems => _mediaItems;
    public int Count => _mediaItems.Count;
    public int CurrentIndex => _currentIndex;
    public MediaItemViewModel CurrentMedia => _mediaItems[_currentIndex];

    public MediaItemViewModel? NavigateRelative(int offset)
    {
        if (_mediaItems.Count <= 1)
        {
            return null;
        }

        _currentIndex = (_currentIndex + offset + _mediaItems.Count) % _mediaItems.Count;
        return CurrentMedia;
    }

    public DeletePlan BuildDeletePlan(string mediaId)
    {
        var removedIndex = _mediaItems.FindIndex(item => string.Equals(item.Id, mediaId, StringComparison.Ordinal));
        var replacementIndex = FindReplacementIndexForDelete(removedIndex);
        var replacement = replacementIndex >= 0 ? _mediaItems[replacementIndex] : null;
        return new DeletePlan(removedIndex, replacement);
    }

    public void RemoveDeletedMedia(DeletePlan deletePlan)
    {
        if (deletePlan.RemovedIndex >= 0)
        {
            _mediaItems.RemoveAt(deletePlan.RemovedIndex);
        }

        if (_mediaItems.Count == 0)
        {
            _currentIndex = 0;
            return;
        }

        if (deletePlan.Replacement != null)
        {
            var replacementCurrentIndex = _mediaItems.FindIndex(item =>
                string.Equals(item.Id, deletePlan.Replacement.Id, StringComparison.Ordinal));
            _currentIndex = replacementCurrentIndex >= 0
                ? replacementCurrentIndex
                : ClampCurrentIndex(deletePlan.RemovedIndex);
            return;
        }

        _currentIndex = ClampCurrentIndex(deletePlan.RemovedIndex);
    }

    private int ClampCurrentIndex(int fallbackIndex)
    {
        return Math.Clamp(fallbackIndex >= 0 ? fallbackIndex : _currentIndex, 0, _mediaItems.Count - 1);
    }

    private int FindReplacementIndexForDelete(int deletedIndex)
    {
        if (_mediaItems.Count <= 1)
        {
            return -1;
        }

        var startIndex = deletedIndex >= 0 ? deletedIndex : _currentIndex;
        var nextIndex = startIndex + 1;
        if (nextIndex < _mediaItems.Count)
        {
            return nextIndex;
        }

        var previousIndex = Math.Min(startIndex - 1, _mediaItems.Count - 1);
        return previousIndex >= 0 ? previousIndex : -1;
    }

    public sealed record DeletePlan(int RemovedIndex, MediaItemViewModel? Replacement);
}
