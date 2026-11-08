using JvJvMediaManager.ViewModels;

namespace JvJvMediaManager.Views.MainPageParts;

public sealed class MediaTileRow
{
    public MediaTileRow(double tileSize)
    {
        TileSize = tileSize;
    }

    public List<MediaItemViewModel> Items { get; } = new();

    public double TileSize { get; }
}
