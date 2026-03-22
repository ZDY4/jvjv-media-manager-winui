using Microsoft.UI.Xaml;
using JvJvMediaManager.Models;
using JvJvMediaManager.Services;

namespace JvJvMediaManager;

public partial class App : Application
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JvJvMediaManager",
        "logs");
    private readonly SettingsService _settings = new();

    public static Window? MainWindow { get; private set; }
    public static AppThemeMode CurrentThemeMode { get; private set; } = AppThemeMode.System;

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
        ApplyThemeMode(_settings.ThemeMode);
        MainWindow.Activate();
    }

    public static void ApplyThemeMode(AppThemeMode themeMode)
    {
        CurrentThemeMode = themeMode;

        if (MainWindow is MainWindow window)
        {
            window.ApplyTheme(MapThemeMode(themeMode));
        }
    }

    private static ElementTheme MapThemeMode(AppThemeMode themeMode)
    {
        return themeMode switch
        {
            AppThemeMode.Light => ElementTheme.Light,
            AppThemeMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
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

    private static void WriteExceptionLog(string source, Exception? exception)
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
