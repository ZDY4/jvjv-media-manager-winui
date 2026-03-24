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
        Func<IReadOnlyList<MediaItemViewModel>, Task> applyTagEditorAsync,
        Func<IReadOnlyList<MediaItemViewModel>, Task> deleteSelectionAsync)
    {
        return new MediaContextMenuCoordinator(library, applyTagEditorAsync, deleteSelectionAsync);
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
        Microsoft.UI.Xaml.Controls.MediaPlayerElement videoPlayer,
        DispatcherQueue dispatcherQueue,
        Func<AppWindow?> getAppWindow,
        Func<int, Task> navigateRelativeAsync,
        Func<bool> canAutoHideControls,
        Action focusHost,
        Action refreshNavigationHotspots,
        Action<TimeSpan> handleMediaOpened)
    {
        return new VideoPlaybackController(
            library,
            viewModel,
            transportBarView,
            videoPlayer,
            dispatcherQueue,
            getAppWindow,
            navigateRelativeAsync,
            canAutoHideControls,
            focusHost,
            refreshNavigationHotspots,
            handleMediaOpened);
    }

    public ClipEditorController CreateClipEditorController(
        LibraryShellViewModel library,
        ClipEditorViewModel viewModel,
        Microsoft.UI.Xaml.Controls.Button clipModeToggleButton,
        ClipEditorBarView clipBarView,
        DialogWorkflowCoordinator dialogCoordinator,
        Func<TimeSpan> getCurrentPlaybackPosition,
        Func<TimeSpan> getCurrentVideoDuration,
        Action<IEnumerable<string>> updateWatchedFolders,
        Action showControls)
    {
        return new ClipEditorController(
            library,
            viewModel,
            clipModeToggleButton,
            clipBarView,
            dialogCoordinator,
            getCurrentPlaybackPosition,
            getCurrentVideoDuration,
            updateWatchedFolders,
            showControls);
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
