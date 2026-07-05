using JvJvMediaManager.Models;
using JvJvMediaManager.Services;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.ViewModels.MainPage;

internal sealed class SettingsAndStateManager
{
    private readonly SettingsService _settings;

    public SettingsAndStateManager(SettingsService settings)
    {
        _settings = settings;
    }

    public MediaLibraryViewState LoadMediaLibraryViewState()
    {
        return _settings.MediaLibraryViewState;
    }

    public IReadOnlyList<WatchedFolder> LoadWatchedFolders()
    {
        return _settings.WatchedFolders;
    }

    public IReadOnlyList<string> LoadNumpadTagShortcuts()
    {
        return _settings.NumpadTagShortcuts;
    }

    public MediaLibraryScanState LoadMediaLibraryScanState()
    {
        return _settings.MediaLibraryScanState;
    }

    public void SaveWatchedFolders(IReadOnlyList<WatchedFolder> folders)
    {
        _settings.SetWatchedFolders(folders.ToList());
    }

    public void SaveNumpadTagShortcuts(IReadOnlyList<string> shortcuts)
    {
        _settings.SetNumpadTagShortcuts(shortcuts);
    }

    public void SaveMediaLibraryScanState(
        MediaLibraryScanStatus status,
        string operation,
        long startedAt,
        int? addedOrUpdated = null,
        string? error = null)
    {
        _settings.SetMediaLibraryScanState(new MediaLibraryScanState
        {
            State = status,
            Operation = operation,
            StartedAt = startedAt,
            FinishedAt = status == MediaLibraryScanStatus.Running
                ? null
                : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            AddedOrUpdated = addedOrUpdated,
            Error = error
        });
    }

    public void SaveMediaLibraryViewState(MediaLibraryViewState viewState)
    {
        _settings.SetMediaLibraryViewState(new MediaLibraryViewState
        {
            ViewMode = viewState.ViewMode,
            SortField = viewState.SortField,
            SortOrder = viewState.SortOrder,
            TypeFilter = viewState.TypeFilter,
            IconSize = viewState.IconSize,
            IsLibraryPaneOpen = viewState.IsLibraryPaneOpen,
            LibraryPaneWidth = viewState.LibraryPaneWidth,
            LibraryPaneRatio = viewState.LibraryPaneRatio,
            FolderCollapseStates = viewState.FolderCollapseStates
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
                .ToDictionary(
                    pair => PathHelpers.NormalizeFolderPath(pair.Key),
                    pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase)
        });
    }
}
