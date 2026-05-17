using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using JvJvMediaManager.ViewModels.MainPage;

namespace JvJvMediaManager.Views.MainPageParts;

public sealed partial class MediaBrowserView : UserControl
{
    public MediaBrowserView()
    {
        InitializeComponent();
        DataContextChanged += MediaBrowserView_DataContextChanged;
        UpdateGroupedMediaItemsSource();
    }

    private void MediaBrowserView_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        UpdateGroupedMediaItemsSource();
    }

    private void UpdateGroupedMediaItemsSource()
    {
        if (Resources["GroupedMediaItemsSource"] is not CollectionViewSource source)
        {
            return;
        }

        source.Source = DataContext is LibraryShellViewModel viewModel
            ? viewModel.MediaFolderGroups
            : null;
    }
}
