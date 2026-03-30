using Microsoft.UI.Xaml;
using JvJvMediaManager.Services.MainPage;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager;

public partial class App : Application
{
    private static string LogDirectory => AppDataPaths.GetLogsDirectory();
    public static Window? MainWindow { get; private set; }
    public MainPageModuleFactory MainPageModules { get; } = new();

    public App()
    {
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        UnhandledException += App_UnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
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
    }

    internal static void WriteExceptionLog(string source, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var logPath = Path.Combine(LogDirectory, $"crash-{DateTime.Now:yyyyMMdd}.log");
            var lines = new[]
            {
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {source}",
                exception?.ToString() ?? "No managed exception details were available.",
                string.Empty
            };
            File.AppendAllLines(logPath, lines);
        }
        catch
        {
            // Best-effort crash logging only.
        }
    }
}
