using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using JvJvMediaManager.Models;
using JvJvMediaManager.Utilities;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace JvJvMediaManager.Views.MainPageParts;

public sealed partial class PlaylistRailView : UserControl
{
    private readonly Dictionary<string, DispatcherQueueTimer> _highlightTimers = new();

    public event Func<string, List<string>, Task>? PlaylistDropRequested;

    public PlaylistRailView()
    {
        InitializeComponent();
        PlaylistRailListView.DragOver += PlaylistRailListView_DragOver;
        PlaylistRailListView.Drop += PlaylistRailListView_Drop;
    }

    public void HighlightPlaylist(string playlistId)
    {
        DispatcherQueue.TryEnqueue(() => TryHighlightPlaylist(playlistId, remainingAttempts: 4));
    }

    private void TryHighlightPlaylist(string playlistId, int remainingAttempts)
    {
        var container = ResolvePlaylistContainer(playlistId);
        if (container == null)
        {
            if (remainingAttempts <= 0)
            {
                return;
            }

            var retryTimer = DispatcherQueue.CreateTimer();
            retryTimer.Interval = TimeSpan.FromMilliseconds(80);
            retryTimer.Tick += (_, _) =>
            {
                retryTimer.Stop();
                TryHighlightPlaylist(playlistId, remainingAttempts - 1);
            };
            retryTimer.Start();
            return;
        }

        VisualStateManager.GoToState(container, "Highlight", true);

        if (_highlightTimers.TryGetValue(playlistId, out var oldTimer))
        {
            oldTimer.Stop();
        }

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += (_, _) =>
        {
            var activeContainer = ResolvePlaylistContainer(playlistId);
            if (activeContainer != null)
            {
                VisualStateManager.GoToState(activeContainer, "Normal", true);
            }

            timer.Stop();
            _highlightTimers.Remove(playlistId);
        };
        timer.Start();
        _highlightTimers[playlistId] = timer;
    }

    private void PlaylistRailListView_DragOver(object sender, DragEventArgs e)
    {
        if (!TryGetDraggedMediaIds(e.DataView, out _))
        {
            return;
        }

        var playlist = ResolvePlaylistFromEventSource(e.OriginalSource as DependencyObject);
        if (playlist == null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.Handled = true;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = $"加入“{playlist.Name}”";
        e.Handled = true;
    }

    private async void PlaylistRailListView_Drop(object sender, DragEventArgs e)
    {
        var playlist = ResolvePlaylistFromEventSource(e.OriginalSource as DependencyObject);
        if (playlist == null || !TryGetDraggedMediaIds(e.DataView, out var mediaIds))
        {
            return;
        }

        if (PlaylistDropRequested != null)
        {
            await PlaylistDropRequested.Invoke(playlist.Id, mediaIds);
        }

        e.Handled = true;
    }

    private ListViewItem? ResolvePlaylistContainer(string playlistId)
    {
        for (var i = 0; i < PlaylistRailListView.Items.Count; i++)
        {
            if (PlaylistRailListView.Items[i] is Playlist playlist
                && string.Equals(playlist.Id, playlistId, StringComparison.Ordinal))
            {
                PlaylistRailListView.UpdateLayout();
                return PlaylistRailListView.ContainerFromIndex(i) as ListViewItem;
            }
        }

        return null;
    }

    private Playlist? ResolvePlaylistFromEventSource(DependencyObject? source)
    {
        var container = FindAncestor<ListViewItem>(source);
        return container?.Content as Playlist;
    }

    private static bool TryGetDraggedMediaIds(DataPackageView dataView, out List<string> mediaIds)
    {
        mediaIds = new List<string>();
        if (!dataView.Properties.TryGetValue(InternalDragData.MediaDragMarkerProperty, out _)
            || !dataView.Properties.TryGetValue(InternalDragData.MediaIdsProperty, out var mediaIdsObj)
            || mediaIdsObj is not List<string> ids)
        {
            return false;
        }

        mediaIds = ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return mediaIds.Count > 0;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source != null)
        {
            if (source is T match)
            {
                return match;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }
}
