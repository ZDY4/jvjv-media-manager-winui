using JvJvMediaManager.Data;
using JvJvMediaManager.Models;
using JvJvMediaManager.SmokeTests.Harness;

namespace JvJvMediaManager.SmokeTests.Tests;

// WP-C: large-library keyset contract test. Verifies that cursor-based paging
// reproduces the exact OFFSET ordering (no gaps, no duplicates) at a scale
// where the cursor chain spans dozens of pages across many folders, with heavy
// duplicate sort values stressing the id tiebreaker, and filtered queries.
internal static class KeysetScaleContractTests
{
    private const string Suite = "db-keyset";
    private const int FolderCount = 16;
    private const int ItemsPerFolder = 1000;
    private const int PageSize = 347; // Prime page size so pages misalign with folders.

    private static readonly MediaSortField[] ScaleFields =
    {
        MediaSortField.FileName,
        MediaSortField.ModifiedAt,
        MediaSortField.Type,
        MediaSortField.Size,
        MediaSortField.Duration
    };

    public static void Register(TestRegistry registry)
    {
        // One fixture shared across all sort fields keeps the suite runtime
        // reasonable; the 16k-row upsert is the dominant cost.
        registry.Add(
            Suite,
            $"keyset scale contract for all supported sorts ({FolderCount * ItemsPerFolder} items)",
            AssertKeysetMatchesFullQueryAtScale);
    }

    private static List<MediaFile> CreateFixture(int seed)
    {
        var random = new Random(seed);
        var items = new List<MediaFile>(FolderCount * ItemsPerFolder);
        for (var folder = 0; folder < FolderCount; folder++)
        {
            var folderName = $"Photos-{folder:D2}";
            for (var i = 0; i < ItemsPerFolder; i++)
            {
                var isVideo = i % 3 == 0;
                var name = $"item-{i:D4}";
                var extension = isVideo ? "mkv" : "png";
                items.Add(MediaFactory.Create(
                    $"C:/{folderName}/{name}.{extension}",
                    type: isVideo ? MediaType.Video : MediaType.Image,
                    // Small value ranges guarantee many duplicate sort values,
                    // stressing the id tiebreaker across folder boundaries.
                    size: random.Next(1, 40),
                    modifiedAt: random.Next(1, 60),
                    duration: isVideo ? random.Next(0, 30) : null));
            }
        }

        return items;
    }

    private static async Task AssertKeysetMatchesFullQueryAtScale()
    {
        using var ws = new TempWorkspace("keyset-scale");
        var db = new MediaDb(ws.Root);
        db.Initialize();
        await db.StartupMaintenanceTask;

        var items = CreateFixture(seed: 20260814);
        await db.UpsertMediaBatchAsync(items);

        foreach (var sortField in ScaleFields)
        {
            foreach (var sortOrder in Enum.GetValues<MediaSortOrder>())
            {
                await AssertKeysetMatchesFullQuery(db, sortField, sortOrder);
            }
        }

        await AssertKeysetWithFilters(db, items);
    }

    private static async Task AssertKeysetMatchesFullQuery(MediaDb db, MediaSortField sortField, MediaSortOrder sortOrder)
    {
        var full = await db.QueryMediaPageAsync(new MediaQuery
        {
            SortField = sortField,
            SortOrder = sortOrder,
            Limit = int.MaxValue
        });
        var fullIds = full.Items.Select(m => m.Id).ToList();
        Assert.Equal(FolderCount * ItemsPerFolder, fullIds.Count, $"Full query returns every seeded row ({sortField}/{sortOrder})");

        var pagedIds = new List<string>();
        MediaPageCursor? cursor = null;
        var guard = 0;
        while (true)
        {
            if (++guard > 10_000)
            {
                throw new AssertionException($"Keyset paging did not terminate at scale ({sortField}/{sortOrder})");
            }

            var page = await db.QueryMediaPageAsync(new MediaQuery
            {
                SortField = sortField,
                SortOrder = sortOrder,
                Limit = PageSize,
                Cursor = cursor
            });

            pagedIds.AddRange(page.Items.Select(m => m.Id));
            if (!page.HasMore)
            {
                Assert.Null(page.NextCursor, $"Final page yields no cursor at scale ({sortField}/{sortOrder})");
                break;
            }

            Assert.NotNull(page.NextCursor, $"Non-final page yields a cursor at scale ({sortField}/{sortOrder})");
            cursor = page.NextCursor;
        }

        Assert.SequenceEqual(
            fullIds,
            pagedIds,
            $"Keyset paging equals full query for {sortField}/{sortOrder} at scale");
        Assert.Equal(
            pagedIds.Count,
            pagedIds.Distinct(StringComparer.Ordinal).Count(),
            $"Keyset results contain no duplicates at scale ({sortField}/{sortOrder})");
    }

    private static async Task AssertKeysetWithFilters(MediaDb db, IReadOnlyList<MediaFile> items)
    {
        // Two folders plus a spread of excluded ids exercise the keyset
        // predicate together with filters and the chunked NOT IN clause.
        var included = new List<string> { "C:/Photos-00", "C:/Photos-15" };
        var excluded = items
            .Where((_, index) => index % 7 == 0)
            .Take(400)
            .Select(item => item.Id)
            .ToList();

        var expected = await db.QueryMediaPageAsync(new MediaQuery
        {
            SortField = MediaSortField.FileName,
            SortOrder = MediaSortOrder.Asc,
            IncludedFolderPaths = included,
            ExcludedMediaIds = excluded,
            Limit = int.MaxValue
        });

        var pagedIds = new List<string>();
        MediaPageCursor? cursor = null;
        var guard = 0;
        while (true)
        {
            if (++guard > 10_000)
            {
                throw new AssertionException("Filtered keyset paging did not terminate");
            }

            var page = await db.QueryMediaPageAsync(new MediaQuery
            {
                SortField = MediaSortField.FileName,
                SortOrder = MediaSortOrder.Asc,
                IncludedFolderPaths = included,
                ExcludedMediaIds = excluded,
                Limit = PageSize,
                Cursor = cursor
            });

            pagedIds.AddRange(page.Items.Select(m => m.Id));
            if (!page.HasMore)
            {
                break;
            }

            Assert.NotNull(page.NextCursor, "Filtered non-final page yields a cursor");
            cursor = page.NextCursor;
        }

        Assert.SequenceEqual(
            expected.Items.Select(m => m.Id).ToList(),
            pagedIds,
            "Filtered keyset paging equals full query");
    }
}
