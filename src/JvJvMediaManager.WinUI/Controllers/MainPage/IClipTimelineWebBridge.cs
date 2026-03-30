using System.Text.Json;
using Microsoft.UI.Xaml.Controls;

namespace JvJvMediaManager.Controllers.MainPage;

internal interface IClipTimelineWebBridge : IDisposable
{
    bool IsReady { get; }

    event EventHandler<ClipTimelineWebCommand>? CommandReceived;

    void Attach(WebView2 webView);

    Task PublishStateAsync(ClipTimelineWebState state);

    void FocusPlayhead();
}

internal sealed record ClipTimelineWebCommand(string Type, JsonElement Payload);

internal sealed record ClipTimelineWebSegment(
    int Index,
    double StartSeconds,
    double EndSeconds,
    bool IsSelected,
    bool IsPreview);

internal sealed record ClipTimelineWebState(
    string MediaId,
    string MediaName,
    string Mode,
    bool IsPlaying,
    bool IsExporting,
    double DurationSeconds,
    double CurrentPositionSeconds,
    double ZoomFactor,
    int SelectedSegmentIndex,
    string StatusText,
    IReadOnlyList<ClipTimelineWebSegment> Segments,
    IReadOnlyList<ClipTimelineWebSegment> PreviewSegments);
