using JvJvMediaManager.Models;
using JvJvMediaManager.Services;
using JvJvMediaManager.ViewModels;

namespace JvJvMediaManager.Services.MainPage;

public sealed class TagEditorResult
{
    public required IReadOnlyList<string> Tags { get; init; }

    public required TagUpdateMode Mode { get; init; }
}

public sealed class PlaylistPickerResult
{
    public required Playlist Playlist { get; init; }
}

public sealed class ClipPlanResult
{
    public required IReadOnlyList<VideoClipSegment> Segments { get; init; }

    public required VideoClipMode Mode { get; init; }

    public required string? OutputDirectory { get; init; }
}

public sealed class ClipPlanDialogRequest
{
    public required TimeSpan Duration { get; init; }

    public required VideoClipMode Mode { get; init; }

    public required IReadOnlyList<VideoClipSegment> Segments { get; init; }

    public required string StartText { get; init; }

    public required string EndText { get; init; }

    public required string OutputDirectory { get; init; }
}

public sealed class SettingsDialogResult
{
    public required AppThemeMode ThemeMode { get; init; }

    public required bool PortableModeEnabled { get; init; }

    public required string DataDirectory { get; init; }

    public required string GlobalPassword { get; init; }

    public required IReadOnlyList<WatchedFolder> WatchedFolders { get; init; }
}

public sealed class FolderLockResult
{
    public required bool Changed { get; init; }
}
