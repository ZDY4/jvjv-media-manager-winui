using JvJvMediaManager.Data;
using JvJvMediaManager.Models;
using JvJvMediaManager.SmokeTests.Harness;

namespace JvJvMediaManager.SmokeTests.Tests;

internal static class KeysetPaginationTests
{
    private const string Suite = "db-keyset";

    private static readonly MediaSortField[] KeysetFields =
    {
        MediaSortField.FileName,
        MediaSortField.ModifiedAt,
        MediaSortField.Type,
        MediaSortField.Size,
        MediaSortField.Duration
    };

    public static void Register(TestRegistry registry)
    {
        foreach (var field in KeysetFields)
        {
            foreach (var order in Enum.GetValues<MediaSortOrder>())
            {
                var capturedField = field;
                var capturedOrder = order;
                registry.Add(
                    Suite,
                    $"keyset paging equals full query for {field}/{order}",
                    () => AssertKeysetMatchesFullQuery(capturedField, capturedOrder));
            }
        }

        registry.Add(Suite, "keyset paging with duplicate sort values has no gaps or dupes", async () =>
        {
            using var ws = new TempWorkspace("keyset-dupes");
            var db = new MediaDb(ws.Root);
            db.Initialize();
            await db.StartupMaintenanceTask;

            // Many rows sharing the same modifiedAt to stress the id tiebreaker.
            var items = new List<MediaFile>();
            for (var folder = 0; folder < 3; folder++)
            {
                for (var i = 0; i < 50; i++)
                {
                    items.Add(MediaFactory.Create(
                        $"C:/lib/f{folder}/dupe-{i:D3}.png",
                        modifiedAt: 1000));
                }
            }

            await db.UpsertMediaBatchAsync(items);
            await AssertKeysetMatchesFullQueryCore(db, MediaSortField.ModifiedAt, MediaSortOrder.Desc);
        });

        registry.Add(Suite, "resolution sort is not keyset-supported and returns no cursor", async () =>
        {
            using var ws = new TempWorkspace("keyset-resolution");
            var db = new MediaDb(ws.Root);
            db.Initialize();
            await db.StartupMaintenanceTask;

            var items = Enumerable.Range(0, 60)
                .Select(i => MediaFactory.Create($"C:/lib/res-{i:D3}.png", width: i, height: i))
                .ToList();
            await db.UpsertMediaBatchAsync(items);

            var page = await db.QueryMediaPageAsync(new MediaQuery
            {
                SortField = MediaSortField.Resolution,
                SortOrder = MediaSortOrder.Desc,
                Limit = 20
            });

            Assert.True(page.HasMore, "Resolution page has more");
            Assert.Null(page.NextCursor, "Resolution sort yields no keyset cursor (OFFSET fallback)");
        });

        registry.Add(Suite, "stale cursor is ignored when query filters change", async () =>
        {
            using var ws = new TempWorkspace("keyset-stale-cursor");
            var db = new MediaDb(ws.Root);
            db.Initialize();
            await db.StartupMaintenanceTask;

            var items = Enumerable.Range(0, 40)
                .Select(i => MediaFactory.Create($"C:/lib/item-{i:D3}.png", tags: i % 2 == 0 ? new[] { "even" } : new[] { "odd" }))
                .ToList();
            await db.UpsertMediaBatchAsync(items);

            var first = await db.QueryMediaPageAsync(new MediaQuery
            {
                SortField = MediaSortField.FileName,
                SortOrder = MediaSortOrder.Asc,
                Limit = 10
            });
            Assert.NotNull(first.NextCursor, "First page yields a cursor");

            var changedQuery = new MediaQuery
            {
                SortField = MediaSortField.FileName,
                SortOrder = MediaSortOrder.Asc,
                SelectedTags = new[] { "odd" },
                Limit = 10,
                Cursor = first.NextCursor
            };
            var actual = await db.QueryMediaPageAsync(changedQuery);
            var expected = await db.QueryMediaPageAsync(new MediaQuery
            {
                SortField = changedQuery.SortField,
                SortOrder = changedQuery.SortOrder,
                SelectedTags = changedQuery.SelectedTags,
                Limit = changedQuery.Limit,
                Offset = 0
            });

            Assert.SequenceEqual(
                expected.Items.Select(item => item.Id).ToList(),
                actual.Items.Select(item => item.Id).ToList(),
                "Cursor from a different filter must be ignored rather than applied");
        });
    }

    private static async Task AssertKeysetMatchesFullQuery(MediaSortField sortField, MediaSortOrder sortOrder)
    {
        using var ws = new TempWorkspace($"keyset-{sortField}-{sortOrder}");
        var db = new MediaDb(ws.Root);
        db.Initialize();
        await db.StartupMaintenanceTask;

        var random = new Random(4242);
        var items = new List<MediaFile>();
        for (var folder = 0; folder < 4; folder++)
        {
            for (var i = 0; i < 51; i++)
            {
                var path = $"C:/library/folder{folder}/item-{i:D3}.{(i % 2 == 0 ? "mp4" : "png")}";
                items.Add(MediaFactory.Create(
                    path,
                    type: i % 2 == 0 ? MediaType.Video : MediaType.Image,
                    size: random.Next(1, 50),
                    modifiedAt: random.Next(1, 30),
                    duration: i % 2 == 0 ? random.Next(0, 20) : null));
            }
        }

        await db.UpsertMediaBatchAsync(items);
        await AssertKeysetMatchesFullQueryCore(db, sortField, sortOrder);
    }

    private static async Task AssertKeysetMatchesFullQueryCore(MediaDb db, MediaSortField sortField, MediaSortOrder sortOrder)
    {
        var full = await db.QueryMediaPageAsync(new MediaQuery
        {
            SortField = sortField,
            SortOrder = sortOrder,
            Limit = int.MaxValue
        });
        var fullIds = full.Items.Select(m => m.Id).ToList();

        const int pageSize = 20;
        var pagedIds = new List<string>();
        MediaPageCursor? cursor = null;
        var guard = 0;
        while (true)
        {
            if (++guard > 10_000)
            {
                throw new AssertionException("Keyset paging did not terminate");
            }

            var page = await db.QueryMediaPageAsync(new MediaQuery
            {
                SortField = sortField,
                SortOrder = sortOrder,
                Limit = pageSize,
                Cursor = cursor
            });

            pagedIds.AddRange(page.Items.Select(m => m.Id));
            if (!page.HasMore)
            {
                Assert.Null(page.NextCursor, "Last page has no cursor");
                break;
            }

            Assert.NotNull(page.NextCursor, "Non-final page yields a cursor");
            cursor = page.NextCursor;
        }

        Assert.SequenceEqual(fullIds, pagedIds, $"Keyset paging equals full query for {sortField}/{sortOrder}");
        var distinct = pagedIds.Distinct(StringComparer.Ordinal).Count();
        Assert.Equal(pagedIds.Count, distinct, "Keyset results contain no duplicates");
    }
}
