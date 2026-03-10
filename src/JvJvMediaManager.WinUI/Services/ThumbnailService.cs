using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace JvJvMediaManager.Services;

public sealed class ThumbnailService
{
    public async Task<ImageSource?> GetThumbnailAsync(string path, bool preferVideo)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var mode = preferVideo ? ThumbnailMode.VideosView : ThumbnailMode.PicturesView;
            using var thumb = await file.GetThumbnailAsync(mode, 200, ThumbnailOptions.UseCurrentScale);
            if (thumb != null)
            {
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(thumb);
                return bitmap;
            }
        }
        catch
        {
            // Ignore thumbnail errors
        }

        try
        {
            var bitmap = new BitmapImage(new Uri(path));
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
