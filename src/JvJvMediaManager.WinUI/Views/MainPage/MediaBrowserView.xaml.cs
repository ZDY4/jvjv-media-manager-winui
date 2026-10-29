using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using JvJvMediaManager.Models;
using JvJvMediaManager.ViewModels;
using JvJvMediaManager.ViewModels.MainPage;

namespace JvJvMediaManager.Views.MainPageParts;

public readonly record struct RealizedMediaElement(FrameworkElement Element, MediaItemViewModel Media, int Index);

public sealed class MediaDragRequestEventArgs : EventArgs
{
    public MediaDragRequestEventArgs(MediaItemViewModel media, DragStartingEventArgs drag)
    {
        Media = media;
        Drag = drag;
    }

    public MediaItemViewModel Media { get; }

    public DragStartingEventArgs Drag { get; }
}

public sealed partial class MediaBrowserView : UserControl
{
    private const string GroupItemsTag = "GroupItems";
    private readonly List<ItemsRepeater> _groupRepeaters = new();
    private LibraryShellViewModel? _viewModel;
    private DataTemplate? _gridTemplate;
    private DataTemplate? _listTemplate;
    private bool? _appliedGrid;

    public MediaBrowserView()
    {
        InitializeComponent();
        MediaRepeater.ElementPrepared += MediaRepeater_ElementPrepared;
        MediaRepeater.ElementClearing += MediaRepeater_ElementClearing;
        MediaRepeater.Tapped += MediaRepeater_Tapped;
        MediaScrollViewer.SizeChanged += MediaScrollViewer_SizeChanged;
        DataContextChanged += MediaBrowserView_DataContextChanged;
        Loaded += MediaBrowserView_Loaded;
        Unloaded += MediaBrowserView_Unloaded;
    }

    public event EventHandler<MediaItemViewModel>? MediaInvoked;

    public event EventHandler<MediaItemViewModel>? MediaPrepared;

    public event EventHandler<MediaItemViewModel>? MediaCleared;

    public event EventHandler<MediaDragRequestEventArgs>? MediaDragStarting;

    public void RefreshPresentation()
    {
        if (_viewModel == null)
        {
            return;
        }

        EnsureTemplates();
        var grid = _viewModel.ViewMode == MediaViewMode.Grid;
        var modeChanged = _appliedGrid != grid;
        _appliedGrid = grid;
        foreach (var repeater in _groupRepeaters)
        {
            ConfigureGroupRepeater(repeater, modeChanged);
        }
    }

    public void BringMediaIntoView(MediaItemViewModel media)
    {
        if (media == null)
        {
            return;
        }

        foreach (var repeater in _groupRepeaters)
        {
            var source = repeater.ItemsSourceView;
            if (source == null)
            {
                continue;
            }

            for (var index = 0; index < source.Count; index++)
            {
                if (source.GetAt(index) is not MediaItemViewModel candidate
                    || !string.Equals(candidate.Id, media.Id, StringComparison.Ordinal))
                {
                    continue;
                }

                var element = repeater.TryGetElement(index) as UIElement ?? repeater.GetOrCreateElement(index);
                element.StartBringIntoView();
                return;
            }
        }
    }

    public bool TryGetRealizedElement(MediaItemViewModel media, out FrameworkElement? element)
    {
        element = null;
        foreach (var realized in GetRealizedMedia())
        {
            if (string.Equals(realized.Media.Id, media.Id, StringComparison.Ordinal))
            {
                element = realized.Element;
                return true;
            }
        }

        return false;
    }

    public IReadOnlyList<RealizedMediaElement> GetRealizedMedia()
    {
        var result = new List<RealizedMediaElement>();
        foreach (var repeater in _groupRepeaters)
        {
            var source = repeater.ItemsSourceView;
            if (source == null)
            {
                continue;
            }

            for (var index = 0; index < source.Count; index++)
            {
                if (repeater.TryGetElement(index) is not FrameworkElement element
                    || element.DataContext is not MediaItemViewModel media)
                {
                    continue;
                }

                result.Add(new RealizedMediaElement(element, media, index));
            }
        }

        return result;
    }

    public bool TryGetMediaRange(MediaItemViewModel anchor, MediaItemViewModel current, List<MediaItemViewModel> range)
    {
        range.Clear();
        if (_viewModel == null)
        {
            return false;
        }

        var flat = new List<MediaItemViewModel>();
        foreach (var group in _viewModel.MediaFolderGroups)
        {
            if (group.IsCollapsed)
            {
                continue;
            }

            foreach (var item in group.VisibleItems)
            {
                flat.Add(item);
            }
        }

        var start = flat.FindIndex(item => string.Equals(item.Id, anchor.Id, StringComparison.Ordinal));
        var end = flat.FindIndex(item => string.Equals(item.Id, current.Id, StringComparison.Ordinal));
        if (start < 0 || end < 0)
        {
            return false;
        }

        if (start > end)
        {
            (start, end) = (end, start);
        }

        for (var index = start; index <= end; index++)
        {
            range.Add(flat[index]);
        }

        return range.Count > 0;
    }

    public static bool IsFolderChrome(DependencyObject? origin)
    {
        return FindContext(origin, out MediaItemViewModel? _, out var header) && header;
    }

    public static bool TryGetMedia(DependencyObject? origin, out MediaItemViewModel? media)
    {
        FindContext(origin, out media, out _);
        return media != null;
    }

    private void MediaBrowserView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null && DataContext is LibraryShellViewModel viewModel)
        {
            AttachViewModel(viewModel);
        }

        RefreshPresentation();
    }

    private void MediaBrowserView_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (args.NewValue is LibraryShellViewModel viewModel)
        {
            if (!ReferenceEquals(_viewModel, viewModel))
            {
                DetachViewModel();
                AttachViewModel(viewModel);
            }

            return;
        }

        DetachViewModel();
    }

    private void MediaBrowserView_Unloaded(object sender, RoutedEventArgs e)
    {
        DetachViewModel();
    }

    private void MediaScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0)
        {
            RefreshPresentation();
        }
    }

    private void AttachViewModel(LibraryShellViewModel viewModel)
    {
        _viewModel = viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        RefreshPresentation();
    }

    private void DetachViewModel()
    {
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }

        _groupRepeaters.Clear();
        _viewModel = null;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LibraryShellViewModel.ViewMode) or nameof(LibraryShellViewModel.IconSize))
        {
            RefreshPresentation();
        }
    }

    private void MediaRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is FrameworkElement element)
        {
            element.Loaded -= Section_Loaded;
            element.Loaded += Section_Loaded;
        }

        WireSection(args.Element);
    }

    private void Section_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is UIElement element)
        {
            WireSection(element);
        }
    }

    private void WireSection(UIElement element)
    {
        if (FindGroupRepeater(element) is not ItemsRepeater repeater || _groupRepeaters.Contains(repeater))
        {
            return;
        }

        repeater.ElementPrepared += GroupRepeater_ElementPrepared;
        repeater.ElementClearing += GroupRepeater_ElementClearing;
        _groupRepeaters.Add(repeater);
        ConfigureGroupRepeater(repeater, resetItems: false);
    }

    private void MediaRepeater_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (FindGroupRepeater(args.Element) is not ItemsRepeater repeater)
        {
            return;
        }

        repeater.ElementPrepared -= GroupRepeater_ElementPrepared;
        repeater.ElementClearing -= GroupRepeater_ElementClearing;
        _groupRepeaters.Remove(repeater);
    }

    private void GroupRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not FrameworkElement element || element.DataContext is not MediaItemViewModel media)
        {
            return;
        }

        element.DragStarting -= Element_DragStarting;
        element.CanDrag = true;
        element.DragStarting += Element_DragStarting;
        MediaPrepared?.Invoke(this, media);
    }

    private void GroupRepeater_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is not FrameworkElement element)
        {
            return;
        }

        element.DragStarting -= Element_DragStarting;
        element.CanDrag = false;
        if (element.DataContext is MediaItemViewModel media)
        {
            MediaCleared?.Invoke(this, media);
        }
    }

    private void MediaRepeater_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (!TryGetMedia(e.OriginalSource as DependencyObject, out var media) || media == null)
        {
            return;
        }

        MediaInvoked?.Invoke(this, media);
        e.Handled = true;
    }

    private void Element_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is not FrameworkElement element || element.DataContext is not MediaItemViewModel media)
        {
            return;
        }

        MediaDragStarting?.Invoke(this, new MediaDragRequestEventArgs(media, args));
    }

    private void ConfigureGroupRepeater(ItemsRepeater repeater, bool resetItems)
    {
        if (_viewModel == null)
        {
            return;
        }

        EnsureTemplates();
        var grid = _viewModel.ViewMode == MediaViewMode.Grid;
        var template = grid ? _gridTemplate : _listTemplate;
        var templateChanged = !ReferenceEquals(repeater.ItemTemplate, template);
        repeater.ItemTemplate = template;
        if (grid)
        {
            var icon = Math.Clamp((double)_viewModel.IconSize, 72, 260);
            var width = MediaScrollViewer.ActualWidth;
            if (width <= 1)
            {
                width = Math.Max(icon, _viewModel.LibraryPaneWidth - 24);
            }

            width = Math.Max(icon, width - 24);
            var columns = Math.Max(1, (int)Math.Floor(width / icon));
            var slot = Math.Clamp(Math.Floor(width / columns), icon, 320);
            if (repeater.Layout is not UniformGridLayout uniform)
            {
                uniform = new UniformGridLayout
                {
                    Orientation = Orientation.Horizontal,
                    ItemsStretch = UniformGridLayoutItemsStretch.None,
                    MinRowSpacing = 4,
                    MinColumnSpacing = 4
                };
                repeater.Layout = uniform;
            }

            uniform.MinItemWidth = slot;
            uniform.MinItemHeight = icon;
        }
        else if (repeater.Layout is not StackLayout)
        {
            repeater.Layout = new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4
            };
        }

        if (resetItems || templateChanged)
        {
            var source = repeater.ItemsSource;
            repeater.ItemsSource = null;
            repeater.ItemsSource = source;
        }
    }

    private void EnsureTemplates()
    {
        _gridTemplate ??= FindResource("GridItemTemplate") as DataTemplate;
        _listTemplate ??= FindResource("ListItemTemplate") as DataTemplate;
    }

    private object? FindResource(string key)
    {
        if (Resources.TryGetValue(key, out var direct))
        {
            return direct;
        }

        foreach (var dictionary in Resources.MergedDictionaries)
        {
            if (dictionary.TryGetValue(key, out var merged))
            {
                return merged;
            }
        }

        return null;
    }

    private static ItemsRepeater? FindGroupRepeater(UIElement root)
    {
        if (root is ItemsRepeater tagged && Equals(tagged.Tag, GroupItemsTag))
        {
            return tagged;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            if (VisualTreeHelper.GetChild(root, index) is UIElement child
                && FindGroupRepeater(child) is ItemsRepeater found)
            {
                return found;
            }
        }

        return null;
    }

    private static bool FindContext(DependencyObject? origin, out MediaItemViewModel? media, out bool header)
    {
        media = null;
        header = false;
        var current = origin;
        while (current != null)
        {
            if (current is FrameworkElement element)
            {
                if (element.DataContext is MediaItemViewModel item)
                {
                    media = item;
                    return true;
                }

                if (element.DataContext is MediaFolderGroupViewModel)
                {
                    header = true;
                    return true;
                }
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}
