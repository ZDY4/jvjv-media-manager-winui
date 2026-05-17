using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.Controllers.MainPage;

internal sealed class ClipTimelineWebBridge : IClipTimelineWebBridge
{
    private const string TimelineHostName = "clip-editor.local";
    private static readonly Uri TimelineEditorUri = new($"https://{TimelineHostName}/editor.html");
    private WebView2? _webView;
    private string? _pendingFullStateJson;
    private string? _pendingDeltaStateJson;
    private string? _lastFullStateJson;
    private bool _isInitializing;
    private bool _initializationFailed;

    public bool IsReady { get; private set; }
    public string StatusText { get; private set; } = "正在连接时间线编辑器...";

    public event EventHandler<ClipTimelineWebCommand>? CommandReceived;

    public void Attach(WebView2 webView)
    {
        if (ReferenceEquals(_webView, webView))
        {
            return;
        }

        if (_webView != null)
        {
            _webView.NavigationCompleted -= WebView_NavigationCompleted;
            _webView.WebMessageReceived -= WebView_WebMessageReceived;
        }

        _webView = webView;
        IsReady = false;
        _initializationFailed = false;
        StatusText = "正在连接时间线编辑器...";
        _webView.NavigationCompleted += WebView_NavigationCompleted;
        _webView.WebMessageReceived += WebView_WebMessageReceived;
        _ = InitializeWebViewSafeAsync();
    }

    public async Task PublishStateAsync(ClipTimelineWebState state)
    {
        await PublishMessageAsync(state);
    }

    public async Task PublishStateDeltaAsync(object payload)
    {
        await PublishMessageAsync(payload);
    }

    public void FocusPlayhead()
    {
        if (_webView?.CoreWebView2 == null || !IsReady)
        {
            return;
        }

        _webView.CoreWebView2.PostWebMessageAsJson("{\"type\":\"focusPlayhead\"}");
    }

    public void Dispose()
    {
        if (_webView != null)
        {
            _webView.NavigationCompleted -= WebView_NavigationCompleted;
            _webView.WebMessageReceived -= WebView_WebMessageReceived;
        }
    }

    private async void WebView_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (sender.CoreWebView2 != null)
        {
            sender.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            sender.CoreWebView2.Settings.IsStatusBarEnabled = false;
            sender.CoreWebView2.Settings.AreDevToolsEnabled = true;
        }

        if (!args.IsSuccess)
        {
            return;
        }

        await RestoreAndFlushPendingMessagesAsync();
    }

    private async void WebView_WebMessageReceived(WebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        using var document = JsonDocument.Parse(args.WebMessageAsJson);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var typeElement))
        {
            return;
        }

        var type = typeElement.GetString() ?? string.Empty;
        var payload = root.TryGetProperty("payload", out var payloadElement)
            ? payloadElement.Clone()
            : JsonDocument.Parse("{}").RootElement.Clone();

        if (string.Equals(type, "ready", StringComparison.Ordinal))
        {
            IsReady = true;
            StatusText = string.Empty;
            await RestoreAndFlushPendingMessagesAsync();
            CommandReceived?.Invoke(this, new ClipTimelineWebCommand(type, payload));
            return;
        }

        CommandReceived?.Invoke(this, new ClipTimelineWebCommand(type, payload));
    }

    private Task FlushPendingMessagesAsync()
    {
        if (_webView?.CoreWebView2 == null || !IsReady)
        {
            return Task.CompletedTask;
        }

        if (!string.IsNullOrWhiteSpace(_pendingFullStateJson))
        {
            _webView.CoreWebView2.PostWebMessageAsJson(_pendingFullStateJson);
            _pendingFullStateJson = null;
        }

        if (!string.IsNullOrWhiteSpace(_pendingDeltaStateJson))
        {
            _webView.CoreWebView2.PostWebMessageAsJson(_pendingDeltaStateJson);
            _pendingDeltaStateJson = null;
        }

        return Task.CompletedTask;
    }

    private Task RestoreAndFlushPendingMessagesAsync()
    {
        if (_webView?.CoreWebView2 == null || !IsReady)
        {
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(_pendingFullStateJson))
        {
            _pendingFullStateJson = _lastFullStateJson;
        }

        return FlushPendingMessagesAsync();
    }

    private async Task PublishMessageAsync(object payload)
    {
        var stateJson = JsonSerializer.Serialize(new
        {
            type = "timelineState",
            payload
        });

        if (payload is ClipTimelineWebState)
        {
            if (!string.Equals(stateJson, _lastFullStateJson, StringComparison.Ordinal))
            {
                _pendingFullStateJson = stateJson;
                _lastFullStateJson = stateJson;
            }

            await FlushPendingMessagesAsync();
            return;
        }

        _pendingDeltaStateJson = stateJson;
        await FlushPendingMessagesAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        if (_webView == null || _isInitializing || _initializationFailed)
        {
            return;
        }

        _isInitializing = true;
        try
        {
            var environment = await CreateWebViewEnvironmentAsync();
            await _webView.EnsureCoreWebView2Async(environment);
            if (_webView.CoreWebView2 == null)
            {
                return;
            }

            var assetDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "ClipEditor");
            if (!Directory.Exists(assetDirectory))
            {
                return;
            }

            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                TimelineHostName,
                assetDirectory,
                CoreWebView2HostResourceAccessKind.Allow);

            if (_webView.Source == null || _webView.Source != TimelineEditorUri)
            {
                _webView.Source = TimelineEditorUri;
            }
        }
        finally
        {
            _isInitializing = false;
        }
    }

    private async Task<CoreWebView2Environment?> CreateWebViewEnvironmentAsync()
    {
        var userDataFolder = Path.Combine(AppDataPaths.GetStorageRoot(), "webview2");
        Directory.CreateDirectory(userDataFolder);

        var candidates = GetBrowserExecutableFolderCandidates().ToList();
        var failureDetails = new StringBuilder();

        Exception? lastException = null;
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            try
            {
                var version = CoreWebView2Environment.GetAvailableBrowserVersionString(candidate.BrowserExecutableFolder);
                var environment = await CoreWebView2Environment.CreateWithOptionsAsync(candidate.BrowserExecutableFolder, userDataFolder, options: null);
                AppTraceLogger.Log(
                    "ClipTimelineWebBridge",
                    $"WebView2 environment initialized. Label={candidate.Label}, BrowserFolder='{candidate.BrowserExecutableFolder ?? "<default>"}', Version='{version}', ProcessArch={RuntimeInformation.ProcessArchitecture}, OSArch={RuntimeInformation.OSArchitecture}, UserDataFolder='{userDataFolder}'.");
                return environment;
            }
            catch (Exception ex)
            {
                lastException = ex;
                failureDetails.AppendLine($"[{index + 1}/{candidates.Count}] Label={candidate.Label}, BrowserFolder='{candidate.BrowserExecutableFolder ?? "<default>"}': {ex.GetType().Name}: {ex.Message}");
            }
        }

        AppTraceLogger.Log(
            "ClipTimelineWebBridge",
            $"WebView2 environment creation failed. ProcessArch={RuntimeInformation.ProcessArchitecture}, OSArch={RuntimeInformation.OSArchitecture}, UserDataFolder='{userDataFolder}'. Candidates:{Environment.NewLine}{failureDetails}");

        throw new InvalidOperationException("无法创建可用的 WebView2 环境。", lastException);
    }

    private static IEnumerable<(string Label, string? BrowserExecutableFolder)> GetBrowserExecutableFolderCandidates()
    {
        yield return ("Default", null);

        foreach (var folder in EnumerateInstalledBrowserFolders("EdgeWebView", "msedgewebview2.exe"))
        {
            yield return ("EdgeWebViewRuntime", folder);
        }

        foreach (var folder in EnumerateInstalledBrowserFolders("Edge", "msedge.exe"))
        {
            yield return ("EdgeStable", folder);
        }
    }

    private static IEnumerable<string> EnumerateInstalledBrowserFolders(string productFolderName, string executableName)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        }
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            var applicationRoot = Path.Combine(root, "Microsoft", productFolderName, "Application");
            if (!Directory.Exists(applicationRoot))
            {
                continue;
            }

            IEnumerable<string> versionDirectories;
            try
            {
                versionDirectories = Directory
                    .EnumerateDirectories(applicationRoot)
                    .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                continue;
            }

            foreach (var versionDirectory in versionDirectories)
            {
                var executablePath = Path.Combine(versionDirectory, executableName);
                if (File.Exists(executablePath))
                {
                    yield return versionDirectory;
                }
            }
        }
    }

    private async Task InitializeWebViewSafeAsync()
    {
        try
        {
            await InitializeWebViewAsync();
        }
        catch (Exception ex)
        {
            _initializationFailed = true;
            IsReady = false;
            StatusText = "时间线编辑器不可用，已切换为降级模式。";
            AppTraceLogger.LogException("ClipTimelineWebBridge", "WebView2 initialization failed.", ex);

            if (_webView != null)
            {
                try
                {
                    _webView.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                }
                catch
                {
                    // Best-effort UI fallback only.
                }
            }
        }
    }
}
