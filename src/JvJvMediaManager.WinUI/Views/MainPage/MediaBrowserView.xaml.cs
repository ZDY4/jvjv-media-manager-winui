using System.Collections.Specialized;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using JvJvMediaManager.Utilities;
using JvJvMediaManager.ViewModels;
using JvJvMediaManager.ViewModels.MainPage;

namespace JvJvMediaManager.Views.MainPageParts;

public sealed partial class MediaBrowserView : UserControl
{
    private readonly HashSet<MediaFolderGroupViewModel> _observedGroups = new();
    private LibraryShellViewModel? _viewModel;
    private int _groupedSourceRefreshQueued;

    public MediaBrowserView()
    {
        InitializeComponent();
        DataContextChanged += MediaBrowserView_DataContextChanged;
        Loaded += MediaBrowserView_Loaded;
        Unloaded += MediaBrowserView_Unloaded;
        UpdateGroupedMediaItemsSource();
    }

    private void MediaBrowserView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null && DataContext is LibraryShellViewModel viewModel)
        {
            _viewModel = viewModel;
            AttachViewModel();
        }

        UpdateGroupedMediaItemsSource("loaded");
    }

    private void MediaBrowserView_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (!ReferenceEquals(_viewModel, args.NewValue))
        {
            DetachViewModel();
            _viewModel = args.NewValue as LibraryShellViewModel;
            AttachViewModel();
        }

        UpdateGroupedMediaItemsSource();
    }

    private void MediaBrowserView_Unloaded(object sender, RoutedEventArgs e)
    {
        DetachViewModel();
    }

    private void AttachViewModel()
    {
        if (_viewModel == null)
        {
            return;
        }

        _viewModel.MediaFolderGroups.CollectionChanged += MediaFolderGroups_CollectionChanged;
        foreach (var group in _viewModel.MediaFolderGroups)
        {
            AttachGroup(group);
        }

        LogGroupedSourceState("attach-view-model");
    }

    private void DetachViewModel()
    {
        var detachedGroupCount = _observedGroups.Count;
        if (_viewModel != null)
        {
            _viewModel.MediaFolderGroups.CollectionChanged -= MediaFolderGroups_CollectionChanged;
        }

        foreach (var group in _observedGroups.ToList())
        {
            DetachGroup(group);
        }

        _viewModel = null;
        AppTraceLogger.LogSampled(
            "MediaBrowserView",
            "grouped-source-detach",
            $"Detached media browser view model. DetachedGroupCount={detachedGroupCount}.",
            TimeSpan.FromSeconds(2));
    }

    private void MediaFolderGroups_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var oldCount = e.OldItems?.Count ?? 0;
        var newCount = e.NewItems?.Count ?? 0;
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            SyncObservedGroups();
        }
        else
        {
            if (e.OldItems != null)
            {
                foreach (var item in e.OldItems.OfType<MediaFolderGroupViewModel>())
                {
                    DetachGroup(item);
                }
            }

            if (e.NewItems != null)
            {
                foreach (var item in e.NewItems.OfType<MediaFolderGroupViewModel>())
                {
                    AttachGroup(item);
                }
            }
        }

        AppTraceLogger.LogSampled(
            "MediaBrowserView",
            "grouped-source-groups-changed",
            () => $"Media folder groups changed. Action={e.Action}, NewCount={newCount}, OldCount={oldCount}, ObservedGroupCount={_observedGroups.Count}, {BuildGroupProjectionDiagnostics(_viewModel)}.",
            TimeSpan.FromSeconds(1));
        ScheduleGroupedMediaItemsSourceRefresh("groups-changed");
    }

    private void MediaFolderGroupVisibleItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var group = _observedGroups.FirstOrDefault(candidate => ReferenceEquals(candidate.VisibleItems, sender));
        AppTraceLogger.LogSampled(
            "MediaBrowserView",
            "grouped-source-visible-items-changed",
            () => $"Media folder visible items changed. Action={e.Action}, NewCount={e.NewItems?.Count ?? 0}, OldCount={e.OldItems?.Count ?? 0}, Folder='{group?.FolderPath ?? "<unknown>"}', GroupVisibleCount={group?.VisibleItems.Count ?? -1}, {BuildGroupProjectionDiagnostics(_viewModel)}.",
            TimeSpan.FromSeconds(1));
        ScheduleGroupedMediaItemsSourceRefresh($"visible-items-{e.Action}");
    }

    private void SyncObservedGroups()
    {
        if (_viewModel == null)
        {
            return;
        }

        var nextGroups = _viewModel.MediaFolderGroups.ToHashSet();
        foreach (var group in _observedGroups.Where(group => !nextGroups.Contains(group)).ToList())
        {
            DetachGroup(group);
        }

        foreach (var group in nextGroups)
        {
            AttachGroup(group);
        }
    }

    private void AttachGroup(MediaFolderGroupViewModel group)
    {
        if (!_observedGroups.Add(group))
        {
            return;
        }

        group.VisibleItems.CollectionChanged += MediaFolderGroupVisibleItems_CollectionChanged;
        AppTraceLogger.LogSampled(
            "MediaBrowserView",
            "grouped-source-group-observe",
            $"Observing media folder group. Folder='{group.FolderPath}', Collapsed={group.IsCollapsed}, VisibleCount={group.VisibleItems.Count}, TotalCount={group.TotalCount}, ObservedGroupCount={_observedGroups.Count}.",
            TimeSpan.FromSeconds(2));
    }

    private void DetachGroup(MediaFolderGroupViewModel group)
    {
        if (!_observedGroups.Remove(group))
        {
            return;
        }

        group.VisibleItems.CollectionChanged -= MediaFolderGroupVisibleItems_CollectionChanged;
    }

    private void ScheduleGroupedMediaItemsSourceRefresh(string reason)
    {
        if (Interlocked.Exchange(ref _groupedSourceRefreshQueued, 1) == 1)
        {
            AppTraceLogger.LogSampled(
                "MediaBrowserView",
                "grouped-source-refresh-coalesced",
                () => $"Grouped media source refresh coalesced. Reason={reason}, {BuildGroupProjectionDiagnostics(_viewModel)}.",
                TimeSpan.FromSeconds(2));
            return;
        }

        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                Interlocked.Exchange(ref _groupedSourceRefreshQueued, 0);
                UpdateGroupedMediaItemsSource(reason);
            }))
        {
            Interlocked.Exchange(ref _groupedSourceRefreshQueued, 0);
            UpdateGroupedMediaItemsSource(reason);
        }
    }

    private void UpdateGroupedMediaItemsSource()
        => UpdateGroupedMediaItemsSource("direct");

    private void UpdateGroupedMediaItemsSource(string reason)
    {
        var viewModel = DataContext as LibraryShellViewModel;
        var listSourceRefreshed = RefreshGroupedMediaItemsSource("ListGroupedMediaItemsSource", viewModel);
        var gridSourceRefreshed = RefreshGroupedMediaItemsSource("GridGroupedMediaItemsSource", viewModel);
        if (viewModel == null)
        {
            AppTraceLogger.LogSampled(
                "MediaBrowserView",
                "grouped-source-refresh-no-view-model",
                $"Grouped media source refreshed without view model. Reason={reason}, ListSource={listSourceRefreshed}, GridSource={gridSourceRefreshed}.",
                TimeSpan.FromSeconds(2));
            return;
        }

        AppTraceLogger.LogSampled(
            "MediaBrowserView",
            "grouped-source-refresh",
            () => $"Grouped media source refreshed. Reason={reason}, ListSource={listSourceRefreshed}, GridSource={gridSourceRefreshed}, ListItems={ListView.Items.Count}, GridItems={GridView.Items.Count}, ObservedGroupCount={_observedGroups.Count}, {BuildGroupProjectionDiagnostics(viewModel)}.",
            TimeSpan.FromSeconds(1));
    }

    private bool RefreshGroupedMediaItemsSource(string resourceKey, LibraryShellViewModel? viewModel)
    {
        if (Resources[resourceKey] is not CollectionViewSource source)
        {
            AppTraceLogger.LogSampled(
                "MediaBrowserView",
                $"grouped-source-missing:{resourceKey}",
                $"Grouped media source resource missing. ResourceKey={resourceKey}.",
                TimeSpan.FromSeconds(5));
            return false;
        }

        var nextSource = viewModel?.MediaFolderGroups;
        if (ReferenceEquals(source.Source, nextSource))
        {
            return false;
        }

        source.Source = nextSource;
        return true;
    }

    private void LogGroupedSourceState(string reason)
    {
        AppTraceLogger.LogSampled(
            "MediaBrowserView",
            $"grouped-source-state:{reason}",
            $"Grouped media source state. Reason={reason}, ObservedGroupCount={_observedGroups.Count}, {BuildGroupProjectionDiagnostics(_viewModel)}.",
            TimeSpan.FromSeconds(2));
    }

    private static string BuildGroupProjectionDiagnostics(LibraryShellViewModel? viewModel)
    {
        if (viewModel == null)
        {
            return "GroupCount=0, VisibleItemCount=0, NonEmptyGroupCount=0, FirstVisibleGroup='<null>', FirstVisibleGroupCount=0";
        }

        var groupCount = viewModel.MediaFolderGroups.Count;
        var visibleItemCount = 0;
        var nonEmptyGroupCount = 0;
        MediaFolderGroupViewModel? firstVisibleGroup = null;
        foreach (var group in viewModel.MediaFolderGroups)
        {
            var groupVisibleCount = group.VisibleItems.Count;
            visibleItemCount += groupVisibleCount;
            if (groupVisibleCount <= 0)
            {
                continue;
            }

            nonEmptyGroupCount++;
            firstVisibleGroup ??= group;
        }

        return $"GroupCount={groupCount}, VisibleItemCount={visibleItemCount}, NonEmptyGroupCount={nonEmptyGroupCount}, FirstVisibleGroup='{firstVisibleGroup?.FolderPath ?? "<null>"}', FirstVisibleGroupCount={firstVisibleGroup?.VisibleItems.Count ?? 0}";
    }
}
