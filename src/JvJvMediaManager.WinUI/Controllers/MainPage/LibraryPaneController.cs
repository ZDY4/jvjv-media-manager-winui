using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using JvJvMediaManager.Models;
using JvJvMediaManager.ViewModels.MainPage;
using JvJvMediaManager.Views.MainPageParts;

namespace JvJvMediaManager.Controllers.MainPage;

public sealed class LibraryPaneController : IDisposable
{
    private const double MinLibraryPaneWidth = 240;
    private const double MaxLibraryPaneWidth = 640;

    private readonly JvJvMediaManager.Views.MainPage _page;
    private readonly LibraryShellViewModel _viewModel;
    private readonly LibraryPaneView _libraryPane;

    public LibraryPaneController(JvJvMediaManager.Views.MainPage page, LibraryShellViewModel viewModel, LibraryPaneView libraryPane)
    {
        _page = page;
        _viewModel = viewModel;
        _libraryPane = libraryPane;

        _libraryPane.PaneResizer.DragStarted += LibraryPaneResizer_DragStarted;
        _libraryPane.PaneResizer.DragDelta += LibraryPaneResizer_DragDelta;
        _libraryPane.PaneResizer.DragCompleted += LibraryPaneResizer_DragCompleted;
    }

    public bool IsPaneOpen => _viewModel.IsLibraryPaneOpen;

    public void Dispose()
    {
        _libraryPane.PaneResizer.DragStarted -= LibraryPaneResizer_DragStarted;
        _libraryPane.PaneResizer.DragDelta -= LibraryPaneResizer_DragDelta;
        _libraryPane.PaneResizer.DragCompleted -= LibraryPaneResizer_DragCompleted;
    }

    public void EnsurePaneState(bool preferOpen = false)
    {
        if (preferOpen)
        {
            SetPaneOpen(true);
        }
    }

    public void ToggleMediaLibrary()
    {
        if (IsPaneOpen && _viewModel.SelectedPlaylist == null)
        {
            SetPaneOpen(false);
            return;
        }

        ActivateMediaLibrary(openPane: true);
    }

    public void ActivateMediaLibrary(bool openPane)
    {
        _viewModel.SelectedPlaylist = null;
        if (openPane)
        {
            SetPaneOpen(true);
        }
    }

    public void TogglePlaylist(Playlist playlist)
    {
        var isCurrentPlaylist = string.Equals(_viewModel.SelectedPlaylist?.Id, playlist.Id, StringComparison.Ordinal);
        if (IsPaneOpen && isCurrentPlaylist)
        {
            SetPaneOpen(false);
            return;
        }

        ActivatePlaylist(playlist, openPane: true);
    }

    public void ActivatePlaylist(Playlist playlist, bool openPane)
    {
        _viewModel.SelectedPlaylist = _viewModel.Playlists.FirstOrDefault(item => string.Equals(item.Id, playlist.Id, StringComparison.Ordinal))
            ?? playlist;
        if (openPane)
        {
            SetPaneOpen(true);
        }
    }

    public void SetPaneOpen(bool isOpen)
    {
        _viewModel.IsLibraryPaneOpen = isOpen;
    }

    private void LibraryPaneResizer_DragStarted(object sender, DragStartedEventArgs e)
    {
        _viewModel.SetLibraryPaneResizing(true);
        SetPaneOpen(true);
    }

    private void LibraryPaneResizer_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var maxWidth = _page.RootLayout.ActualWidth > 0
            ? Math.Min(MaxLibraryPaneWidth, Math.Max(MinLibraryPaneWidth, _page.RootLayout.ActualWidth - 200))
            : MaxLibraryPaneWidth;

        _viewModel.LibraryPaneWidth = Math.Clamp(_viewModel.LibraryPaneWidth + e.HorizontalChange, MinLibraryPaneWidth, maxWidth);
    }

    private void LibraryPaneResizer_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _viewModel.SetLibraryPaneResizing(false);
    }
}
