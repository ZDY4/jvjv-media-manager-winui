using JvJvMediaManager.Views.MainPageParts;

namespace JvJvMediaManager.Controllers.MainPage;

internal sealed class ClipTimelineUIController
{
    private readonly ClipEditorBarView _view;
    private readonly double _handleWidth;

    public ClipTimelineUIController(ClipEditorBarView view, double handleWidth)
    {
        _view = view;
        _handleWidth = handleWidth;
    }

    public double TrackLeft => _handleWidth / 2;

    public double GetTrackWidth(double canvasWidth)
    {
        return Math.Max(1, canvasWidth - _handleWidth);
    }

    public double ResolveContentWidth()
    {
        var width = _view.TimelineContentCanvas.Width;
        if (double.IsNaN(width) || width <= 0)
        {
            width = _view.TimelineContentCanvas.ActualWidth;
        }

        return width;
    }

    public double PositionToRatio(double x)
    {
        var width = ResolveContentWidth();
        var trackWidth = GetTrackWidth(width);
        if (trackWidth <= 0)
        {
            return 0;
        }

        return Math.Clamp((x - TrackLeft) / trackWidth, 0, 1);
    }

    public TimeSpan PositionToTime(double x, TimeSpan duration)
    {
        return duration <= TimeSpan.Zero
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(duration.TotalSeconds * PositionToRatio(x));
    }

    public double TimeToX(TimeSpan value, TimeSpan duration, double canvasWidth)
    {
        var trackWidth = GetTrackWidth(canvasWidth);
        if (duration <= TimeSpan.Zero || trackWidth <= 0)
        {
            return TrackLeft;
        }

        var ratio = Math.Clamp(value.TotalSeconds / duration.TotalSeconds, 0, 1);
        return TrackLeft + (trackWidth * ratio);
    }
}
