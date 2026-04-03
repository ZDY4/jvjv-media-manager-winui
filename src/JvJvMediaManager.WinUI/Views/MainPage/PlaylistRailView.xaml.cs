using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using JvJvMediaManager.Models;
using System;
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
    }

    public void HighlightPlaylist(string playlistId)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            for (int i = 0; i < PlaylistRailListView.Items.Count; i++)
            {
                if (PlaylistRailListView.Items[i] is Playlist playlist &&
                    string.Equals(playlist.Id, playlistId, StringComparison.Ordinal))
                {
                    if (PlaylistRailListView.ContainerFromIndex(i) is ListViewItem container)
                    {
                        var border = FindChild<Border>(container, "ItemBorder");
                        if (border != null)
                        {
                            VisualStateManager.GoToState(container, "Highlight", true);

                            if (_highlightTimers.TryGetValue(playlistId, out var oldTimer))
                            {
                                oldTimer.Stop();
                            }

                            var timer = DispatcherQueue.CreateTimer();
                            timer.Interval = TimeSpan.FromSeconds(1);
                            timer.Tick += (_, _) =>
                            {
                                VisualStateManager.GoToState(container, "Normal", true);
                                timer.Stop();
                                _highlightTimers.Remove(playlistId);
                            };
                            timer.Start();
                            _highlightTimers[playlistId] = timer;
                        }
                    }
                    break;
                }
            }
        });
    }

    private void PlaylistItem_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Properties.TryGetValue("MediaIds", out _))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.Handled = true;
        }
    }

    private async void PlaylistItem_Drop(object sender, DragEventArgs e)
    {
        if (sender is Border border && border.DataContext is Playlist playlist &&
            e.DataView.Properties.TryGetValue("MediaIds", out var mediaIdsObj) &&
            mediaIdsObj is List<string> mediaIds)
        {
            if (PlaylistDropRequested != null)
            {
                await PlaylistDropRequested.Invoke(playlist.Id, mediaIds);
            }
            e.Handled = true;
        }
    }

    private static T? FindChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        if (parent == null) return null;

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);

            if (child is T element && element.Name == name)
            {
                return element;
            }

            var result = FindChild<T>(child, name);
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }
}