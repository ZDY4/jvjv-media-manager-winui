using Microsoft.UI.Xaml;
using Microsoft.Extensions.DependencyInjection;
using JvJvMediaManager.Services;
using JvJvMediaManager.Services.MainPage;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }
    public ServiceProvider Services { get; }
    public MainPageModuleFactory MainPageModules { get; }
    private int _servicesDisposed;

    public App()
    {
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        Services = AppServiceRegistration.CreateServiceProvider();
        MainPageModules = Services.GetRequiredService<MainPageModuleFactory>();
        AppTraceLogger.LogSessionStart();
        UnhandledException += App_UnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        InitializeComponent();
        AppTraceLogger.Log("App", "Application initialized.");
        WerReportHarvester.HarvestPreviousCrashReports();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppTraceLogger.Log("App", "OnLaunched start.");
        MainWindow = new MainWindow();
        MainWindow.Activate();
        AppTraceLogger.Log("App", "OnLaunched completed.");
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        WriteExceptionLog("WinUI UnhandledException", e.Exception);
    }

    internal void DisposeServices()
    {
        if (Interlocked.Exchange(ref _servicesDisposed, 1) == 1)
        {
            return;
        }

        try
        {
            Services.Dispose();
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("App", "Service provider dispose failed.", ex);
        }
    }

    private static void CurrentDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        WriteExceptionLog("AppDomain UnhandledException", e.ExceptionObject as Exception);
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteExceptionLog("TaskScheduler UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    internal static void WriteExceptionLog(string source, Exception? exception)
    {
        if (exception != null)
        {
            AppTraceLogger.LogException("App", $"Unhandled exception captured. Source='{source}'.", exception);
        }
        else
        {
            AppTraceLogger.Log("App", $"Unhandled exception captured without managed exception details. Source='{source}'.");
        }

        AppTraceLogger.FlushNow();
        CrashDiagnostics.WriteCrashArtifacts(source, exception);
    }
}
