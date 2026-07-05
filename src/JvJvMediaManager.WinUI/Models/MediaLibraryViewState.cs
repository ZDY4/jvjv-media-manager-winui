namespace JvJvMediaManager.Models;

public sealed class MediaLibraryViewState
{
    public MediaViewMode ViewMode { get; set; } = MediaViewMode.List;
    public MediaSortField SortField { get; set; } = MediaSortField.ModifiedAt;
    public MediaSortOrder SortOrder { get; set; } = MediaSortOrder.Desc;
    public MediaType? TypeFilter { get; set; }
    public int IconSize { get; set; } = 120;
    public bool IsLibraryPaneOpen { get; set; } = true;
    public double LibraryPaneWidth { get; set; } = 360;
    public double? LibraryPaneRatio { get; set; }
    public Dictionary<string, bool> FolderCollapseStates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
