using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace JvJvMediaManager.Services;

public sealed class ThumbnailService
{
    private readonly SemaphoreSlim _loadGate = new(4, 4);
    private readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<ImageSource?>> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public async Task<ImageSource?> GetThumbnailAsync(string path, bool preferVideo)
    {
        Task<ImageSource?> task;
        lock (_sync)
        {
            if (_cache.TryGetValue(path, out var cached))
            {
                return cached;
            }

            if (!_inflight.TryGetValue(path, out var inflightTask) || inflightTask == null)
            {
                inflightTask = LoadThumbnailCoreAsync(path, preferVideo);
                _inflight[path] = inflightTask;
            }

            task = inflightTask;
        }

        return await task;
    }

    private async Task<ImageSource?> LoadThumbnailCoreAsync(string path, bool preferVideo)
    {
        await _loadGate.WaitAsync();
        try
        {
            ImageSource? source = null;

            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                var mode = preferVideo ? ThumbnailMode.VideosView : ThumbnailMode.PicturesView;
                using var thumb = await file.GetThumbnailAsync(mode, 200, ThumbnailOptions.UseCurrentScale);
                if (thumb != null)
                {
                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(thumb);
                    source = bitmap;
                }
            }
            catch
            {
                // Ignore thumbnail errors
            }

            if (source == null)
            {
                try
                {
                    source = new BitmapImage(new Uri(path));
                }
                catch
                {
                    source = null;
                }
            }

            lock (_sync)
            {
                _cache[path] = source;
                _inflight.Remove(path);
            }

            return source;
        }
        finally
        {
            _loadGate.Release();
        }
    }
}
