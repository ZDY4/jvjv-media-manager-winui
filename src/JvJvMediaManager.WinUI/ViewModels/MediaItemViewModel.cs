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
    private bool _isNowPlaying;
    private int _thumbnailRequested;

    public MediaItemViewModel(MediaFile media)
    {
        Media = media;
        Tags = new ObservableCollection<string>(media.Tags);
    }

    public MediaFile Media { get; }

    public string Id => Media.Id;
    public string Path => Media.Path;
    public string FileSystemPath => PathHelpers.ToNativePath(Media.Path);
    public string FolderPath => GetFolderPath(Media.Path);
    public string FolderDisplayName => GetFolderDisplayName(FolderPath);
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

    public bool IsNowPlaying
    {
        get => _isNowPlaying;
        set => SetProperty(ref _isNowPlaying, value);
    }

    public void UpdateFrom(MediaFile media)
    {
        Media.Path = media.Path;
        Media.FileName = media.FileName;
        Media.Type = media.Type;
        Media.Size = media.Size;
        Media.Duration = media.Duration;
        Media.Width = media.Width;
        Media.Height = media.Height;
        Media.Thumbnail = media.Thumbnail;
        Media.CreatedAt = media.CreatedAt;
        Media.ModifiedAt = media.ModifiedAt;
        Media.LastPlayed = media.LastPlayed;
        Media.PlayCount = media.PlayCount;
        Media.Tags = media.Tags.ToList();

        UpdateTags(media.Tags);
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(FileSystemPath));
        OnPropertyChanged(nameof(FolderPath));
        OnPropertyChanged(nameof(FolderDisplayName));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(Type));
        OnPropertyChanged(nameof(Size));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(ResolutionText));
        OnPropertyChanged(nameof(DurationText));
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

    private static string GetFolderPath(string path)
    {
        var nativePath = PathHelpers.ToNativePath(path);
        var folder = System.IO.Path.GetDirectoryName(nativePath);
        return string.IsNullOrWhiteSpace(folder)
            ? string.Empty
            : PathHelpers.NormalizeFolderPath(folder);
    }

    private static string GetFolderDisplayName(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return "未知文件夹";
        }

        var nativePath = PathHelpers.ToNativePath(folderPath);
        var name = System.IO.Path.GetFileName(nativePath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? folderPath : name;
    }
}
