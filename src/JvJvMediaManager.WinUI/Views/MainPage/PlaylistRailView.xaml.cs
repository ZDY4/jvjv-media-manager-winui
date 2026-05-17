using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using JvJvMediaManager.Models;
using JvJvMediaManager.Utilities;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.ApplicationModel.DataTransfer;

namespace JvJvMediaManager.Views.MainPageParts;

public sealed partial class PlaylistRailView : UserControl
{
    private readonly Dictionary<string, DispatcherQueueTimer> _highlightTimers = new();
    private readonly DispatcherQueueTimer _playlistTooltipTimer;
    private Grid? _playlistTooltipOverlayRoot;
    private Canvas? _playlistTooltipHost;
    private Border? _playlistTooltipBorder;
    private TextBlock? _playlistTooltipText;
    private Playlist? _pendingPlaylistTooltip;
    private Point _pendingPlaylistTooltipPosition;
    private bool _isPlaylistTooltipVisible;
    private bool _isPlaylistTooltipTimerRunning;

    public event Func<string, List<string>, Task>? PlaylistDropRequested;

    public PlaylistRailView()
    {
        InitializeComponent();
        InitializePlaylistTooltipOverlay();
        _playlistTooltipTimer = DispatcherQueue.CreateTimer();
        _playlistTooltipTimer.Interval = TimeSpan.FromMilliseconds(80);
        _playlistTooltipTimer.Tick += PlaylistTooltipTimer_Tick;
        PlaylistRailListView.DragOver += PlaylistRailListView_DragOver;
        PlaylistRailListView.Drop += PlaylistRailListView_Drop;
        PlaylistRailListView.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler(PlaylistRailListView_PointerEntered), true);
        PlaylistRailListView.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(PlaylistRailListView_PointerMoved), true);
        PlaylistRailListView.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler(PlaylistRailListView_PointerExited), true);
    }

    private void InitializePlaylistTooltipOverlay()
    {
        if (Content is not Border borderRoot)
        {
            return;
        }

        var overlayRoot = new Grid();
        var overlayHost = new Canvas
        {
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var tooltipBorder = new Border
        {
            Visibility = Visibility.Collapsed,
            Background = FindResourceBrush("MediaCardBrush"),
            BorderBrush = FindResourceBrush("SurfaceStrokeStrongBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = TryGetCornerRadius("RadiusSmall"),
            Padding = new Thickness(10, 6, 10, 6),
            MaxWidth = 320
        };

        var tooltipText = new TextBlock
        {
            Foreground = FindResourceBrush("TextBrush"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };

        tooltipBorder.Child = tooltipText;
        overlayHost.Children.Add(tooltipBorder);
        Content = null;
        overlayRoot.Children.Add(borderRoot);
        overlayRoot.Children.Add(overlayHost);

        Content = overlayRoot;
        _playlistTooltipOverlayRoot = overlayRoot;
        _playlistTooltipHost = overlayHost;
        _playlistTooltipBorder = tooltipBorder;
        _playlistTooltipText = tooltipText;
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

    private void PlaylistRailListView_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        UpdatePlaylistTooltipState(e);
    }

    private void PlaylistRailListView_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        UpdatePlaylistTooltipState(e);
    }

    private void PlaylistRailListView_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        HidePlaylistTooltip();
    }

    private void PlaylistTooltipTimer_Tick(object? sender, object e)
    {
        _playlistTooltipTimer.Stop();
        _isPlaylistTooltipTimerRunning = false;
        if (_pendingPlaylistTooltip == null)
        {
            return;
        }

        ShowPlaylistTooltip(_pendingPlaylistTooltip, _pendingPlaylistTooltipPosition);
    }

    private void UpdatePlaylistTooltipState(PointerRoutedEventArgs e)
    {
        var playlist = ResolvePlaylistFromEventSource(e.OriginalSource as DependencyObject);
        if (playlist == null)
        {
            HidePlaylistTooltip();
            return;
        }

        _pendingPlaylistTooltip = playlist;
        UIElement pointerRoot = _playlistTooltipOverlayRoot is UIElement overlayRoot ? overlayRoot : this;
        _pendingPlaylistTooltipPosition = e.GetCurrentPoint(pointerRoot).Position;

        if (_isPlaylistTooltipVisible)
        {
            ShowPlaylistTooltip(playlist, _pendingPlaylistTooltipPosition);
            return;
        }

        if (!_isPlaylistTooltipTimerRunning)
        {
            _isPlaylistTooltipTimerRunning = true;
            _playlistTooltipTimer.Start();
        }
    }

    private void ShowPlaylistTooltip(Playlist playlist, Point pointerPosition)
    {
        var overlayRoot = _playlistTooltipOverlayRoot;
        if (_playlistTooltipBorder == null || _playlistTooltipText == null || _playlistTooltipHost == null || overlayRoot == null)
        {
            return;
        }

        _playlistTooltipText.Text = playlist.Name;
        _playlistTooltipBorder.Visibility = Visibility.Visible;
        _playlistTooltipBorder.UpdateLayout();

        var width = _playlistTooltipBorder.ActualWidth;
        var height = _playlistTooltipBorder.ActualHeight;
        var x = pointerPosition.X + 14;
        var y = pointerPosition.Y + 18;

        if (overlayRoot.ActualWidth > 0 && width > 0)
        {
            x = Math.Min(x, Math.Max(8, overlayRoot.ActualWidth - width - 8));
        }

        if (overlayRoot.ActualHeight > 0 && height > 0)
        {
            y = Math.Min(y, Math.Max(8, overlayRoot.ActualHeight - height - 8));
        }

        x = Math.Max(8, x);
        y = Math.Max(8, y);
        Canvas.SetLeft(_playlistTooltipBorder, x);
        Canvas.SetTop(_playlistTooltipBorder, y);
        _isPlaylistTooltipVisible = true;
    }

    private void HidePlaylistTooltip()
    {
        _playlistTooltipTimer.Stop();
        _isPlaylistTooltipTimerRunning = false;
        _pendingPlaylistTooltip = null;
        _isPlaylistTooltipVisible = false;
        if (_playlistTooltipBorder != null)
        {
            _playlistTooltipBorder.Visibility = Visibility.Collapsed;
        }

        if (_playlistTooltipText != null)
        {
            _playlistTooltipText.Text = string.Empty;
        }
    }

    private CornerRadius TryGetCornerRadius(string resourceKey)
    {
        return Application.Current.Resources.TryGetValue(resourceKey, out var value) && value is CornerRadius cornerRadius
            ? cornerRadius
            : new CornerRadius(4);
    }

    private static Brush? FindResourceBrush(string resourceKey)
    {
        return Application.Current.Resources.TryGetValue(resourceKey, out var value) && value is Brush brush
            ? brush
            : null;
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
