using JvJvMediaManager.Data;
using JvJvMediaManager.Models;
using JvJvMediaManager.SmokeTests.Harness;

namespace JvJvMediaManager.SmokeTests.Tests;

internal static class MediaPagingContractTests
{
    private const string Suite = "db-paging";

    public static void Register(TestRegistry registry)
    {
        foreach (var sortField in Enum.GetValues<MediaSortField>())
        {
            foreach (var sortOrder in Enum.GetValues<MediaSortOrder>())
            {
                var capturedField = sortField;
                var capturedOrder = sortOrder;
                registry.Add(
                    Suite,
                    $"offset paging equals full query for {sortField}/{sortOrder}",
                    () => AssertPagingMatchesFullQuery(capturedField, capturedOrder));
            }
        }
    }

    private static async Task AssertPagingMatchesFullQuery(MediaSortField sortField, MediaSortOrder sortOrder)
    {
        using var ws = new TempWorkspace($"page-{sortField}-{sortOrder}");
        var db = new MediaDb(ws.Root);
        db.Initialize();
        await db.StartupMaintenanceTask;

        var random = new Random(20260711);
        var items = new List<MediaFile>();
        for (var folder = 0; folder < 5; folder++)
        {
            for (var i = 0; i < 43; i++)
            {
                var path = $"C:/library/folder{folder}/item-{i:D3}.{(i % 2 == 0 ? "mp4" : "png")}";
                items.Add(MediaFactory.Create(
                    path,
                    type: i % 2 == 0 ? MediaType.Video : MediaType.Image,
                    size: random.Next(1, 9000),
                    modifiedAt: random.Next(1, 5000),
                    width: random.Next(0, 1920),
                    height: random.Next(0, 1080),
                    duration: i % 2 == 0 ? random.Next(0, 3600) : null));
            }
        }

        await db.UpsertMediaBatchAsync(items);

        var fullQuery = new MediaQuery
        {
            SortField = sortField,
            SortOrder = sortOrder,
            Limit = int.MaxValue
        };
        var full = await db.QueryMediaPageAsync(fullQuery);
        var fullIds = full.Items.Select(m => m.Id).ToList();
        Assert.Equal(items.Count, fullIds.Count, "Full query returns every item");

        // Page through with OFFSET-based paging in fixed windows.
        const int pageSize = 37;
        var pagedIds = new List<string>();
        var offset = 0;
        while (true)
        {
            var page = await db.QueryMediaPageAsync(new MediaQuery
            {
                SortField = sortField,
                SortOrder = sortOrder,
                Offset = offset,
                Limit = pageSize
            });
            pagedIds.AddRange(page.Items.Select(m => m.Id));
            if (!page.HasMore)
            {
                break;
            }

            offset += pageSize;
        }

        Assert.SequenceEqual(fullIds, pagedIds, "Paged concatenation equals full query order");

        var distinctCount = pagedIds.Distinct(StringComparer.Ordinal).Count();
        Assert.Equal(pagedIds.Count, distinctCount, "Paged results contain no duplicates");
    }
}
