using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using JvJvMediaManager.ViewModels;

namespace JvJvMediaManager.Views.MainPageParts;

public sealed class MediaBrowserTemplateSelector : DataTemplateSelector
{
    public DataTemplate? HeaderTemplate { get; set; }

    public DataTemplate? GridTemplate { get; set; }

    public DataTemplate? ListTemplate { get; set; }

    public DataTemplate? RowTemplate { get; set; }

    public bool UseGrid { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => Select(item);

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => Select(item);

    private DataTemplate? Select(object item)
    {
        if (item is MediaFolderGroupViewModel)
        {
            return HeaderTemplate;
        }

        if (item is MediaTileRow)
        {
            return RowTemplate;
        }

        return UseGrid ? GridTemplate : ListTemplate;
    }
}
