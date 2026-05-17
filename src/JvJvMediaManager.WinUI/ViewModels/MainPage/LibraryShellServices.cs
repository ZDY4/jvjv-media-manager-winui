using JvJvMediaManager.Data;
using JvJvMediaManager.Services;

namespace JvJvMediaManager.ViewModels.MainPage;

public sealed class LibraryShellServices
{
    public LibraryShellServices(
        SettingsService settings,
        MediaDb database,
        MediaLibraryService library,
        ThumbnailService thumbnails,
        TimelineThumbnailStripService timelineThumbnails)
    {
        Settings = settings;
        Database = database;
        Library = library;
        Thumbnails = thumbnails;
        TimelineThumbnails = timelineThumbnails;
    }

    public SettingsService Settings { get; }

    public MediaDb Database { get; }

    public MediaLibraryService Library { get; }

    public ThumbnailService Thumbnails { get; }

    public TimelineThumbnailStripService TimelineThumbnails { get; }

    public static LibraryShellServices CreateDefault()
    {
        var settings = new SettingsService();
        var database = new MediaDb(settings.DataDir);
        database.Initialize();

        return new LibraryShellServices(
            settings,
            database,
            new MediaLibraryService(database),
            new ThumbnailService(settings.GetThumbnailCacheDir()),
            new TimelineThumbnailStripService(settings.GetTimelineThumbnailCacheDir()));
    }
}
