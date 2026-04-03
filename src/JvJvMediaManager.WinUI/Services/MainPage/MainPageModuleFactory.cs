using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using JvJvMediaManager.Controllers.MainPage;
using JvJvMediaManager.Coordinators.MainPage;
using JvJvMediaManager.ViewModels;
using JvJvMediaManager.ViewModels.MainPage;
using JvJvMediaManager.Views.MainPageParts;

namespace JvJvMediaManager.Services.MainPage;

public sealed class MainPageModuleFactory
{
    public IContentDialogService CreateContentDialogService()
    {
        return new ContentDialogService();
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
        Func<IReadOnlyList<MediaItemViewModel>, Task> applyTagEditorAsync,
        Func<IReadOnlyList<MediaItemViewModel>, Task> addToPlaylistAsync,
        Func<IReadOnlyList<MediaItemViewModel>, Task> deleteSelectionAsync)
    {
        return new MediaContextMenuCoordinator(library, dialogService, applyTagEditorAsync, addToPlaylistAsync, deleteSelectionAsync);
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
        Action<IEnumerable<string>, bool> updateWatchedFolders,
        Func<string, string, Task> showInfoAsync)
    {
        return new MediaBrowserController(
            page,
            library,
            libraryPane,
            contextMenuCoordinator,
            updateWatchedFolders,
            showInfoAsync);
    }

    public VideoPlaybackController CreateVideoPlaybackController(
        LibraryShellViewModel library,
        VideoPlaybackViewModel viewModel,
        TransportControlBarView transportBarView,
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
            library,
            viewModel,
            transportBarView,
            videoViewportView,
            videoPlayer,
            dispatcherQueue,
            getAppWindow,
            navigateRelativeAsync,
            canAutoHideControls,
            focusHost,
            refreshNavigationHotspots,
            handleMediaOpened,
            notifyPlaybackProgressChanged);
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
            clipBarView);
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
