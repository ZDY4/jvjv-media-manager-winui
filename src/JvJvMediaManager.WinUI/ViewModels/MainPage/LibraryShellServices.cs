using JvJvMediaManager.Data;
using JvJvMediaManager.Services;
using Microsoft.Extensions.DependencyInjection;

namespace JvJvMediaManager.ViewModels.MainPage;

public sealed class LibraryShellServices
{
    public LibraryShellServices(
        SettingsService settings,
        MediaDb database,
        MediaLibraryService library,
        ThumbnailService thumbnails,
        TimelineThumbnailStripService timelineThumbnails,
        PlaybackResourceCoordinator playbackResources)
    {
        Settings = settings;
        Database = database;
        Library = library;
        Thumbnails = thumbnails;
        TimelineThumbnails = timelineThumbnails;
        PlaybackResources = playbackResources;
    }

    public SettingsService Settings { get; }

    public MediaDb Database { get; }

    public MediaLibraryService Library { get; }

    public ThumbnailService Thumbnails { get; }

    public TimelineThumbnailStripService TimelineThumbnails { get; }

    public PlaybackResourceCoordinator PlaybackResources { get; }

    public static LibraryShellServices CreateDefault(IServiceProvider? serviceProvider = null)
    {
        if (serviceProvider != null)
        {
            return serviceProvider.GetRequiredService<LibraryShellServices>();
        }

        if (Microsoft.UI.Xaml.Application.Current is App app)
        {
            return app.Services.GetRequiredService<LibraryShellServices>();
        }

        var settings = new SettingsService();
        var database = new MediaDb(settings.DataDir);
        database.Initialize();

        var playbackResources = new PlaybackResourceCoordinator();
        return new LibraryShellServices(
            settings,
            database,
            new MediaLibraryService(database, playbackResources),
            new ThumbnailService(settings.GetThumbnailCacheDir(), playbackResources),
            new TimelineThumbnailStripService(settings.GetTimelineThumbnailCacheDir(), playbackResources: playbackResources),
            playbackResources);
    }
}
