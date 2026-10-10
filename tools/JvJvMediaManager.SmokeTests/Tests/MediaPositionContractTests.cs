using JvJvMediaManager.Data;
using JvJvMediaManager.Models;
using JvJvMediaManager.SmokeTests.Harness;

namespace JvJvMediaManager.SmokeTests.Tests;

// H3: contract test for QueryMediaPositionAsync — the COUNT-based lookup that
// deep navigation uses instead of serial 120-item paging. The reported
// position must equal the row's index in the full ordered query for every
// supported sort, and with filters applied.
internal static class MediaPositionContractTests
{
    private const string Suite = "db-paging";

    private static readonly MediaSortField[] PositionFields =
    {
        MediaSortField.FileName,
        MediaSortField.ModifiedAt,
        MediaSortField.Type,
        MediaSortField.Size,
        MediaSortField.Duration
    };

    public static void Register(TestRegistry registry)
    {
        foreach (var field in PositionFields)
        {
            foreach (var order in Enum.GetValues<MediaSortOrder>())
            {
                var capturedField = field;
                var capturedOrder = order;
                registry.Add(
                    Suite,
                    $"position lookup matches ordered index for {field}/{order}",
                    () => AssertPositionsMatchOrderedIndex(capturedField, capturedOrder));
            }
        }

        registry.Add(Suite, "position lookup with folder filter, tags and excluded ids", AssertPositionsWithFilters);
        registry.Add(Suite, "position lookup returns null for random and resolution sorts", AssertUnsupportedSortsReturnNull);
    }

    private static async Task AssertPositionsMatchOrderedIndex(MediaSortField sortField, MediaSortOrder sortOrder)
    {
        using var ws = new TempWorkspace($"position-{sortField}-{sortOrder}");
        var db = new MediaDb(ws.Root);
        db.Initialize();
        await db.StartupMaintenanceTask;

        var random = new Random(1701);
        var items = new List<MediaFile>();
        for (var folder = 0; folder < 4; folder++)
        {
            for (var i = 0; i < 53; i++)
            {
                var isVideo = i % 2 == 0;
                items.Add(MediaFactory.Create(
                    $"C:/lib/f{folder}/item-{i:D3}.{(isVideo ? "mp4" : "png")}",
                    type: isVideo ? MediaType.Video : MediaType.Image,
                    size: random.Next(1, 80),
                    modifiedAt: random.Next(1, 40),
                    duration: isVideo ? random.Next(0, 25) : null,
                    tags: i % 5 == 0 ? new[] { "tag-x" } : null));
            }
        }

        await db.UpsertMediaBatchAsync(items);

        var query = new MediaQuery
        {
            SortField = sortField,
            SortOrder = sortOrder,
            Limit = int.MaxValue
        };
        var full = await db.QueryMediaPageAsync(query);
        var fullIds = full.Items.Select(m => m.Id).ToList();

        // Spot-check rows spread across the result, re-querying the target
        // through GetMediaByIdAsync like the real navigation path does.
        var probes = new[] { 0, 1, 37, 100, fullIds.Count / 2, fullIds.Count - 2, fullIds.Count - 1 }
            .Where(index => index >= 0 && index < fullIds.Count)
            .Distinct()
            .ToList();
        foreach (var probe in probes)
        {
            var target = await db.GetMediaByIdAsync(fullIds[probe]);
            Assert.NotNull(target, $"Target media exists for probe {probe}");
            var position = await db.QueryMediaPositionAsync(query, target!);
            Assert.NotNull(position, $"Position lookup returns a value for {sortField}/{sortOrder} at probe {probe}");
            Assert.Equal(probe, position!.Value, $"Position lookup matches ordered index for {sortField}/{sortOrder} at probe {probe}");
        }
    }

    private static async Task AssertPositionsWithFilters()
    {
        using var ws = new TempWorkspace("position-filtered");
        var db = new MediaDb(ws.Root);
        db.Initialize();
        await db.StartupMaintenanceTask;

        var random = new Random(1702);
        var items = new List<MediaFile>();
        for (var folder = 0; folder < 4; folder++)
        {
            for (var i = 0; i < 40; i++)
            {
                var isVideo = i % 2 == 0;
                items.Add(MediaFactory.Create(
                    $"C:/lib/f{folder}/item-{i:D3}.{(isVideo ? "mp4" : "png")}",
                    type: isVideo ? MediaType.Video : MediaType.Image,
                    size: random.Next(1, 60),
                    modifiedAt: random.Next(1, 30),
                    duration: isVideo ? random.Next(0, 20) : null,
                    tags: i % 5 == 0 ? new[] { "tag-y" } : null));
            }
        }

        await db.UpsertMediaBatchAsync(items);

        var excluded = items
            .Where((_, index) => index % 3 == 0)
            .Take(20)
            .Select(item => item.Id)
            .ToList();
        var query = new MediaQuery
        {
            SortField = MediaSortField.FileName,
            SortOrder = MediaSortOrder.Desc,
            IncludedFolderPaths = new List<string> { "C:/lib/f1", "C:/lib/f2" },
            SelectedTags = new List<string> { "tag-y" },
            ExcludedMediaIds = excluded,
            Limit = int.MaxValue
        };
        var full = await db.QueryMediaPageAsync(query);
        var fullIds = full.Items.Select(m => m.Id).ToList();
        Assert.True(fullIds.Count > 5, "Filtered query has enough rows");

        foreach (var probe in new[] { 0, fullIds.Count / 2, fullIds.Count - 1 })
        {
            var target = await db.GetMediaByIdAsync(fullIds[probe]);
            Assert.NotNull(target, $"Target media exists for probe {probe}");
            var position = await db.QueryMediaPositionAsync(query, target!);
            Assert.NotNull(position, $"Filtered position lookup returns a value at probe {probe}");
            Assert.Equal(probe, position!.Value, $"Filtered position lookup matches at probe {probe}");
        }
    }

    private static async Task AssertUnsupportedSortsReturnNull()
    {
        using var ws = new TempWorkspace("position-unsupported");
        var db = new MediaDb(ws.Root);
        db.Initialize();
        await db.StartupMaintenanceTask;

        var items = Enumerable.Range(0, 40)
            .Select(i => MediaFactory.Create($"C:/lib/item-{i:D3}.png", width: i, height: i))
            .ToList();
        await db.UpsertMediaBatchAsync(items);

        var target = await db.GetMediaByIdAsync(items[10].Id);
        Assert.NotNull(target, "Target exists");

        Assert.Null(
            await db.QueryMediaPositionAsync(new MediaQuery { SortField = MediaSortField.Resolution, Limit = 20 }, target!),
            "Resolution sort is not positionable");
        Assert.Null(
            await db.QueryMediaPositionAsync(new MediaQuery { RandomOrder = true, Limit = 20 }, target!),
            "Random order is not positionable");
    }
}
