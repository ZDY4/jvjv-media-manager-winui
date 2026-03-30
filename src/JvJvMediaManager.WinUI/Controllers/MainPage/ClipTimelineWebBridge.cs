using System.IO;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace JvJvMediaManager.Controllers.MainPage;

internal sealed class ClipTimelineWebBridge : IClipTimelineWebBridge
{
    private const string TimelineHostName = "clip-editor.local";
    private static readonly Uri TimelineEditorUri = new($"https://{TimelineHostName}/editor.html");
    private WebView2? _webView;
    private string? _pendingStateJson;
    private string? _lastPublishedStateJson;
    private bool _isInitializing;

    public bool IsReady { get; private set; }

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
        _webView.NavigationCompleted += WebView_NavigationCompleted;
        _webView.WebMessageReceived += WebView_WebMessageReceived;
        _ = InitializeWebViewAsync();
    }

    public async Task PublishStateAsync(ClipTimelineWebState state)
    {
        var stateJson = JsonSerializer.Serialize(new
        {
            type = "timelineState",
            payload = state
        });

        if (string.Equals(stateJson, _lastPublishedStateJson, StringComparison.Ordinal))
        {
            return;
        }

        _pendingStateJson = stateJson;
        _lastPublishedStateJson = stateJson;
        await TryPostPendingStateAsync();
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

        await TryPostPendingStateAsync();
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
            await TryPostPendingStateAsync();
            CommandReceived?.Invoke(this, new ClipTimelineWebCommand(type, payload));
            return;
        }

        CommandReceived?.Invoke(this, new ClipTimelineWebCommand(type, payload));
    }

    private Task TryPostPendingStateAsync()
    {
        if (_webView?.CoreWebView2 == null || !IsReady || string.IsNullOrWhiteSpace(_pendingStateJson))
        {
            return Task.CompletedTask;
        }

        _webView.CoreWebView2.PostWebMessageAsJson(_pendingStateJson);
        _pendingStateJson = null;
        return Task.CompletedTask;
    }

    private async Task InitializeWebViewAsync()
    {
        if (_webView == null || _isInitializing)
        {
            return;
        }

        _isInitializing = true;
        try
        {
            await _webView.EnsureCoreWebView2Async();
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
}
