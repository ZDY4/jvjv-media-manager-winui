using JvJvMediaManager.Data;
using JvJvMediaManager.Services.MainPage;
using JvJvMediaManager.ViewModels.MainPage;
using JvJvMediaManager.Views;
using Microsoft.Extensions.DependencyInjection;

namespace JvJvMediaManager.Services;

public static class AppServiceRegistration
{
    public static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<PlaybackResourceCoordinator>();
        services.AddSingleton(provider =>
        {
            var settings = provider.GetRequiredService<SettingsService>();
            var database = new MediaDb(settings.DataDir);
            database.Initialize();
            return database;
        });
        services.AddSingleton<MediaLibraryService>();
        services.AddSingleton(provider =>
        {
            var settings = provider.GetRequiredService<SettingsService>();
            var playbackResources = provider.GetRequiredService<PlaybackResourceCoordinator>();
            return new ThumbnailService(settings.GetThumbnailCacheDir(), playbackResources);
        });
        services.AddSingleton(provider =>
        {
            var settings = provider.GetRequiredService<SettingsService>();
            var playbackResources = provider.GetRequiredService<PlaybackResourceCoordinator>();
            return new TimelineThumbnailStripService(
                settings.GetTimelineThumbnailCacheDir(),
                playbackResources: playbackResources);
        });
        services.AddSingleton<LibraryShellServices>();
        services.AddTransient<SelectionViewModel>();
        services.AddTransient<MainPageShellViewModel>();
        services.AddTransient<IContentDialogService, ContentDialogService>();
        services.AddTransient<MainPageModuleFactory>();
        services.AddTransient<JvJvMediaManager.Views.MainPage>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }
}
