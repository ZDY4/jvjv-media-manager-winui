using Microsoft.UI.Xaml;
using JvJvMediaManager.Services.MainPage;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }
    public MainPageModuleFactory MainPageModules { get; } = new();

    public App()
    {
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
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
