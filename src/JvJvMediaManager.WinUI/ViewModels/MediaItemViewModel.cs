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
    private int _thumbnailFailureCount;
    private int _thumbnailIdentityVersion;
    private long _thumbnailRetryAfterTicks;
    private string _fileSystemPath;
    private string _folderPath;
    private string _folderDisplayName;
    private string _sizeText;
    private string _resolutionText;
    private string _playerDetailsText;
    private string _durationText;
    private string _tagsText;

    public MediaItemViewModel(MediaFile media)
    {
        Media = media;
        Tags = new ObservableCollection<string>(media.Tags);
        _fileSystemPath = PathHelpers.ToNativePath(Media.Path);
        _folderPath = GetFolderPath(Media.Path);
        _folderDisplayName = GetFolderDisplayName(_folderPath);
        _sizeText = FormatHelpers.FormatSize(Media.Size);
        _resolutionText = FormatResolution(Media);
        _playerDetailsText = BuildPlayerDetailsText(_resolutionText, _sizeText);
        _durationText = FormatHelpers.FormatDuration(Media.Duration);
        _tagsText = BuildTagsText(Tags);
    }

    public MediaFile Media { get; }

    public string Id => Media.Id;
    public string Path => Media.Path;
    public string FileSystemPath => _fileSystemPath;
    public string FolderPath => _folderPath;
    public string FolderDisplayName => _folderDisplayName;
    public string FileName => Media.FileName;
    public MediaType Type => Media.Type;

    public long Size => Media.Size;
    public string SizeText => _sizeText;

    public string ResolutionText => _resolutionText;

    public string PlayerDetailsText => _playerDetailsText;

    public string DurationText => _durationText;

    public string? ContentHash => Media.ContentHash;

    public int FingerprintVersion => Media.FingerprintVersion;

    public int Rotation => Media.Rotation;

    public ObservableCollection<string> Tags { get; }

    public string TagsText => _tagsText;

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }

    public int ThumbnailIdentityVersion => Volatile.Read(ref _thumbnailIdentityVersion);

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
        var pathChanged = !string.Equals(Media.Path, media.Path, StringComparison.Ordinal);
        var fileNameChanged = !string.Equals(Media.FileName, media.FileName, StringComparison.Ordinal);
        var typeChanged = Media.Type != media.Type;
        var sizeChanged = Media.Size != media.Size;
        var durationChanged = Media.Duration != media.Duration;
        var resolutionChanged = Media.Width != media.Width || Media.Height != media.Height;
        var modifiedAtChanged = Media.ModifiedAt != media.ModifiedAt;
        var contentHashChanged = !string.Equals(Media.ContentHash, media.ContentHash, StringComparison.Ordinal);
        var fingerprintVersionChanged = Media.FingerprintVersion != media.FingerprintVersion;
        var thumbnailIdentityChanged = pathChanged
            || typeChanged
            || sizeChanged
            || modifiedAtChanged
            || resolutionChanged
            || contentHashChanged;
        var nextTags = media.Tags.ToList();
        var tagsChanged = !Tags.SequenceEqual(nextTags, StringComparer.Ordinal);

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
        Media.ContentHash = media.ContentHash;
        Media.FingerprintVersion = media.FingerprintVersion;
        Media.Rotation = media.Rotation;
        Media.Tags = nextTags;

        if (pathChanged)
        {
            _fileSystemPath = PathHelpers.ToNativePath(Media.Path);
            _folderPath = GetFolderPath(Media.Path);
            _folderDisplayName = GetFolderDisplayName(_folderPath);
        }

        if (sizeChanged)
        {
            _sizeText = FormatHelpers.FormatSize(Media.Size);
        }

        if (resolutionChanged)
        {
            _resolutionText = FormatResolution(Media);
        }

        if (sizeChanged || resolutionChanged)
        {
            _playerDetailsText = BuildPlayerDetailsText(_resolutionText, _sizeText);
        }

        if (durationChanged)
        {
            _durationText = FormatHelpers.FormatDuration(Media.Duration);
        }

        if (tagsChanged)
        {
            UpdateTags(nextTags);
        }

        if (thumbnailIdentityChanged)
        {
            Interlocked.Increment(ref _thumbnailIdentityVersion);
            Thumbnail = null;
            ResetThumbnailLoadState();
        }

        if (pathChanged)
        {
            OnPropertyChanged(nameof(Path));
            OnPropertyChanged(nameof(FileSystemPath));
            OnPropertyChanged(nameof(FolderPath));
            OnPropertyChanged(nameof(FolderDisplayName));
        }

        if (fileNameChanged)
        {
            OnPropertyChanged(nameof(FileName));
        }

        if (typeChanged)
        {
            OnPropertyChanged(nameof(Type));
        }

        if (sizeChanged)
        {
            OnPropertyChanged(nameof(Size));
            OnPropertyChanged(nameof(SizeText));
            OnPropertyChanged(nameof(PlayerDetailsText));
        }

        if (resolutionChanged)
        {
            OnPropertyChanged(nameof(ResolutionText));
            OnPropertyChanged(nameof(PlayerDetailsText));
        }

        if (durationChanged)
        {
            OnPropertyChanged(nameof(DurationText));
        }

        if (contentHashChanged)
        {
            OnPropertyChanged(nameof(ContentHash));
        }

        if (fingerprintVersionChanged)
        {
            OnPropertyChanged(nameof(FingerprintVersion));
        }
    }

    public void UpdateTags(IEnumerable<string> tags)
    {
        var nextTags = tags.ToList();
        if (Tags.SequenceEqual(nextTags, StringComparer.Ordinal))
        {
            return;
        }

        Tags.Clear();
        foreach (var tag in nextTags)
        {
            Tags.Add(tag);
        }
        _tagsText = BuildTagsText(Tags);
        OnPropertyChanged(nameof(TagsText));
    }

    public bool TryBeginThumbnailLoad()
    {
        if (GetThumbnailRetryRemaining() > TimeSpan.Zero)
        {
            return false;
        }

        return Interlocked.CompareExchange(ref _thumbnailRequested, 1, 0) == 0;
    }

    public TimeSpan GetThumbnailRetryRemaining()
    {
        var retryAfterTicks = Interlocked.Read(ref _thumbnailRetryAfterTicks);
        if (retryAfterTicks <= 0)
        {
            return TimeSpan.Zero;
        }

        var remaining = retryAfterTicks - DateTime.UtcNow.Ticks;
        return remaining > 0 ? TimeSpan.FromTicks(remaining) : TimeSpan.Zero;
    }

    public void ResetThumbnailLoadState()
    {
        Interlocked.Exchange(ref _thumbnailRequested, 0);
        Interlocked.Exchange(ref _thumbnailFailureCount, 0);
        Interlocked.Exchange(ref _thumbnailRetryAfterTicks, 0);
    }

    public void CancelThumbnailLoad()
    {
        Interlocked.Exchange(ref _thumbnailRequested, 0);
    }

    public void RecordThumbnailLoadFailure()
    {
        var failureCount = Math.Min(Interlocked.Increment(ref _thumbnailFailureCount), 5);
        // First miss retries quickly so a cancelled ffmpeg/shell decode does
        // not leave a visible tile empty until the next scroll.
        var retryDelay = failureCount switch
        {
            1 => TimeSpan.FromMilliseconds(400),
            2 => TimeSpan.FromSeconds(2),
            3 => TimeSpan.FromSeconds(8),
            _ => TimeSpan.FromSeconds(30)
        };
        Interlocked.Exchange(ref _thumbnailRetryAfterTicks, DateTime.UtcNow.Add(retryDelay).Ticks);
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

    private static string FormatResolution(MediaFile media)
    {
        return media.Width.HasValue && media.Height.HasValue
            ? $"{media.Width}x{media.Height}"
            : string.Empty;
    }

    private static string BuildPlayerDetailsText(string resolutionText, string sizeText)
    {
        if (string.IsNullOrWhiteSpace(resolutionText))
        {
            return sizeText;
        }

        return string.IsNullOrWhiteSpace(sizeText)
            ? resolutionText
            : $"{resolutionText} · {sizeText}";
    }

    private static string BuildTagsText(IEnumerable<string> tags)
    {
        return string.Join(", ", tags);
    }
}
