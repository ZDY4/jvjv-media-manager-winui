using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using JvJvMediaManager.Controllers.MainPage;
using JvJvMediaManager.Coordinators.MainPage;
using JvJvMediaManager.ViewModels;
using JvJvMediaManager.ViewModels.MainPage;
using JvJvMediaManager.Views.MainPageParts;
using Microsoft.Extensions.DependencyInjection;

namespace JvJvMediaManager.Services.MainPage;

public sealed class MainPageModuleFactory
{
    private readonly IServiceProvider _services;

    public MainPageModuleFactory(IServiceProvider services)
    {
        _services = services;
    }

    public IContentDialogService CreateContentDialogService()
    {
        return _services.GetRequiredService<IContentDialogService>();
    }

    public DialogWorkflowCoordinator CreateDialogWorkflowCoordinator(LibraryShellViewModel library, IContentDialogService dialogService)
    {
        return new DialogWorkflowCoordinator(library, dialogService);
    }

    public LibraryPaneController CreateLibraryPaneController(JvJvMediaManager.Views.MainPage page, LibraryShellViewModel library, LibraryPaneView libraryPane)
    {
        return new LibraryPaneController(page, library, libraryPane);
    }

    public MediaContextMenuCoordinator CreateMediaContextMenuCoordinator(
        LibraryShellViewModel library,
        IContentDialogService dialogService,
        Func<IReadOnlyList<MediaItemViewModel>, Task> focusInMediaLibraryAsync,
        Func<IReadOnlyList<MediaItemViewModel>, Task> applyTagEditorAsync,
        Func<IReadOnlyList<MediaItemViewModel>, Task> addToPlaylistAsync,
        Func<IReadOnlyList<MediaItemViewModel>, Task> deleteSelectionAsync,
        Action<IReadOnlyList<MediaItemViewModel>> openInNewWindows)
    {
        return new MediaContextMenuCoordinator(
            library,
            dialogService,
            focusInMediaLibraryAsync,
            applyTagEditorAsync,
            addToPlaylistAsync,
            deleteSelectionAsync,
            openInNewWindows);
    }

    public MediaDeletionWorkflow CreateMediaDeletionWorkflow(
        LibraryShellViewModel library,
        Func<int, Task<bool>> confirmBatchDeleteAsync,
        Func<string, string, Task> showInfoAsync,
        Action<string?> releasePreviewHandlesForMedia,
        Action clearPlayerSelection,
        Action<MediaItemViewModel> forceUpdatePlayer,
        Action<MediaItemViewModel?> syncSelection,
        Func<MediaItemViewModel?, IDisposable> preserveSelectionDuringCollectionMutation)
    {
        return new MediaDeletionWorkflow(
            library,
            confirmBatchDeleteAsync,
            showInfoAsync,
            releasePreviewHandlesForMedia,
            clearPlayerSelection,
            forceUpdatePlayer,
            syncSelection,
            preserveSelectionDuringCollectionMutation);
    }

    public PlaylistRailCoordinator CreatePlaylistRailCoordinator(
        LibraryShellViewModel library,
        PlaylistRailView playlistRailView,
        LibraryHeaderView headerView,
        LibraryPaneController libraryPaneController,
        IContentDialogService dialogService,
        Func<string, string, Task> showInfoAsync,
        Func<string, string, string, Task<bool>> confirmAsync)
    {
        return new PlaylistRailCoordinator(
            library,
            playlistRailView,
            headerView,
            libraryPaneController,
            dialogService,
            showInfoAsync,
            confirmAsync);
    }

    public MediaBrowserController CreateMediaBrowserController(
        JvJvMediaManager.Views.MainPage page,
        LibraryShellViewModel library,
        LibraryPaneView libraryPane,
        MediaContextMenuCoordinator contextMenuCoordinator,
        Func<IEnumerable<string>, bool, Task> updateWatchedFoldersAsync,
        Func<string, string, Task> showInfoAsync)
    {
        return new MediaBrowserController(
            page,
            library,
            libraryPane,
            contextMenuCoordinator,
            updateWatchedFoldersAsync,
            showInfoAsync);
    }

    public VideoPlaybackController CreateVideoPlaybackController(
        SettingsService settings,
        LibraryShellViewModel library,
        VideoPlaybackViewModel viewModel,
        TransportControlBarView transportBarView,
        PlayerNavigationOverlayView navigationOverlayView,
        VideoViewportView videoViewportView,
        Microsoft.UI.Xaml.Controls.MediaPlayerElement videoPlayer,
        DispatcherQueue dispatcherQueue,
        Func<AppWindow?> getAppWindow,
        Func<int, Task> navigateRelativeAsync,
        Func<bool> canAutoHideControls,
        Action focusHost,
        Action refreshNavigationHotspots,
        Action<TimeSpan> handleMediaOpened,
        Action notifyPlaybackProgressChanged)
    {
        return new VideoPlaybackController(
            settings,
            library,
            viewModel,
            transportBarView,
            navigationOverlayView,
            videoViewportView,
            videoPlayer,
            dispatcherQueue,
            getAppWindow,
            navigateRelativeAsync,
            canAutoHideControls,
            focusHost,
            refreshNavigationHotspots,
            handleMediaOpened,
            notifyPlaybackProgressChanged,
            _services.GetRequiredService<PlaybackResourceCoordinator>());
    }

    public IClipTimelineEditor CreateClipEditorController(
        IClipEditorHost host,
        ClipEditorViewModel viewModel,
        Microsoft.UI.Xaml.Controls.Button clipModeToggleButton,
        ClipEditorBarView clipBarView)
    {
        return new ClipEditorController(
            host,
            viewModel,
            clipModeToggleButton,
            clipBarView,
            _services.GetRequiredService<PlaybackResourceCoordinator>());
    }

    public ImagePreviewController CreateImagePreviewController(
        LibraryShellViewModel library,
        ImagePreviewViewModel viewModel,
        ImageViewportView imageViewportView,
        Microsoft.UI.Xaml.UIElement playerOverlay,
        Action refreshNavigationHotspots)
    {
        return new ImagePreviewController(
            library,
            viewModel,
            imageViewportView,
            playerOverlay,
            refreshNavigationHotspots);
    }
}
