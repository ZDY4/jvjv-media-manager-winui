using JvJvMediaManager.Data;
using JvJvMediaManager.Services.MainPage;
using JvJvMediaManager.ViewModels.MainPage;
using Microsoft.Extensions.DependencyInjection;

namespace JvJvMediaManager.Services;

public static class AppServiceRegistration
{
    public static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<SettingsService>();
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
            return new ThumbnailService(settings.GetThumbnailCacheDir());
        });
        services.AddSingleton(provider =>
        {
            var settings = provider.GetRequiredService<SettingsService>();
            return new TimelineThumbnailStripService(settings.GetTimelineThumbnailCacheDir());
        });
        services.AddSingleton<LibraryShellServices>();
        services.AddTransient<IContentDialogService, ContentDialogService>();
        services.AddTransient<MainPageModuleFactory>();

        return services.BuildServiceProvider(validateScopes: true);
    }
}
