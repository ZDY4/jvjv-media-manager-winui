using System.Collections.ObjectModel;
using System.Threading;
using Microsoft.UI.Xaml.Media;
using JvJvMediaManager.Models;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.ViewModels;

public sealed class MediaItemViewModel : ObservableObject
{
    private ImageSource? _thumbnail;
    private bool _isSelected;
    private int _thumbnailRequested;

    public MediaItemViewModel(MediaFile media)
    {
        Media = media;
        Tags = new ObservableCollection<string>(media.Tags);
    }

    public MediaFile Media { get; }

    public string Id => Media.Id;
    public string Path => Media.Path;
    public string FileName => Media.FileName;
    public MediaType Type => Media.Type;

    public long Size => Media.Size;
    public string SizeText => FormatHelpers.FormatSize(Media.Size);

    public string ResolutionText => Media.Width.HasValue && Media.Height.HasValue
        ? $"{Media.Width}x{Media.Height}"
        : string.Empty;

    public string DurationText => FormatHelpers.FormatDuration(Media.Duration);

    public ObservableCollection<string> Tags { get; }

    public string TagsText => Tags.Count == 0 ? string.Empty : string.Join(", ", Tags);

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public void UpdateTags(IEnumerable<string> tags)
    {
        Tags.Clear();
        foreach (var tag in tags)
        {
            Tags.Add(tag);
        }
        OnPropertyChanged(nameof(TagsText));
    }

    public bool TryBeginThumbnailLoad()
    {
        return Interlocked.CompareExchange(ref _thumbnailRequested, 1, 0) == 0;
    }

    public void ResetThumbnailLoadState()
    {
        Interlocked.Exchange(ref _thumbnailRequested, 0);
    }
}
