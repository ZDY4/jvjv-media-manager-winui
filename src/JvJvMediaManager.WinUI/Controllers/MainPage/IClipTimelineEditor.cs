using JvJvMediaManager.ViewModels;

namespace JvJvMediaManager.Controllers.MainPage;

public interface IClipTimelineEditor : IDisposable
{
    bool IsClipModeActive { get; }

    void Refresh();

    void HandleMediaChanged(MediaItemViewModel? media);

    void HandleMediaOpened(TimeSpan duration);

    void ToggleClipMode();

    void HandlePreviewSurfaceInteraction();

    void Clear();

    void SplitSegmentAtCurrentPosition();

    bool DeleteSelectedSegment();

    Task ExportCurrentClipAsync();
}
