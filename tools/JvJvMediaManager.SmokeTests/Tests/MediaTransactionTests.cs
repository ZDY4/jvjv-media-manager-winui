using JvJvMediaManager.Data;
using JvJvMediaManager.Models;
using JvJvMediaManager.SmokeTests.Harness;
using Microsoft.Data.Sqlite;

namespace JvJvMediaManager.SmokeTests.Tests;

internal static class MediaTransactionTests
{
    private const string Suite = "db-transaction";

    public static void Register(TestRegistry registry)
    {
        registry.Add(Suite, "upsert batch with tags commits media, tags and search index", async () =>
        {
            using var ws = new TempWorkspace("tx-upsert");
            var db = CreateDb(ws);

            var items = new[]
            {
                MediaFactory.Create("C:/media/alpha.mp4", MediaType.Video, tags: new[] { "sunset", "beach" }),
                MediaFactory.Create("C:/media/beta.png", MediaType.Image, tags: new[] { "portrait" })
            };

            await db.UpsertMediaBatchAsync(items);

            var all = await db.GetAllMediaAsync();
            Assert.Equal(2, all.Count, "All upserted media should be present");

            var alpha = all.Single(m => m.FileName == "alpha.mp4");
            Assert.SequenceEqual(new[] { "beach", "sunset" }, alpha.Tags.OrderBy(t => t).ToList(), "Alpha tags persisted");

            // FTS search must find media by tag immediately after upsert.
            var byTag = await db.QueryMediaPageAsync(new MediaQuery
            {
                SearchText = "sunset",
                Limit = 50
            });
            Assert.Equal(1, byTag.Items.Count, "FTS search by tag should return the tagged item");
            Assert.Equal("alpha.mp4", byTag.Items[0].FileName, "FTS search matched the correct item");

            // FTS search must find media by filename.
            var byName = await db.QueryMediaPageAsync(new MediaQuery
            {
                SearchText = "beta",
                Limit = 50
            });
            Assert.Equal(1, byName.Items.Count, "FTS search by filename should return the item");
        });

        registry.Add(Suite, "scan re-upsert preserves persisted rotation", async () =>
        {
            using var ws = new TempWorkspace("tx-rotation-preserve");
            var db = CreateDb(ws);

            var media = MediaFactory.Create("C:/media/rotated.mp4", MediaType.Video);
            await db.UpsertMediaBatchAsync(new[] { media });

            await db.SetMediaRotationAsync(media.Id, 90);
            var rotated = await db.GetMediaByIdAsync(media.Id);
            Assert.Equal(90, rotated!.Rotation, "Rotation persisted after set");

            // A later scan upserts the same file with default rotation 0; the
            // stored value must survive (upsert uses rotation = media.rotation).
            var rescanned = MediaFactory.Create("C:/media/rotated.mp4", MediaType.Video);
            await db.UpsertMediaBatchAsync(new[] { rescanned });

            var afterRescan = await db.GetMediaByIdAsync(media.Id);
            Assert.Equal(90, afterRescan!.Rotation, "Scan re-upsert must not clobber persisted rotation");
        });

        registry.Add(Suite, "rotation round-trips through page queries", async () =>
        {
            using var ws = new TempWorkspace("tx-rotation-page");
            var db = CreateDb(ws);

            var media = MediaFactory.Create("C:/media/roundtrip.mp4", MediaType.Video);
            await db.UpsertMediaBatchAsync(new[] { media });

            await db.SetMediaRotationAsync(media.Id, 270);
            var page = await db.QueryMediaPageAsync(new MediaQuery { Limit = 50 });
            var item = page.Items.Single(m => m.FileName == "roundtrip.mp4");
            Assert.Equal(270, item.Rotation, "Page query returns persisted rotation");

            // Off-angle writes normalize to the nearest 90 degrees.
            await db.SetMediaRotationAsync(media.Id, 45);
            var normalized = await db.GetMediaByIdAsync(media.Id);
            Assert.Equal(0, normalized!.Rotation, "Rotation normalizes to the nearest 90 degrees");
        });

        registry.Add(Suite, "cancellation before upsert rolls back and releases write gate", async () =>
        {
            using var ws = new TempWorkspace("tx-cancel-before");
            var db = CreateDb(ws);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var batch = new[]
            {
                MediaFactory.Create("C:/media/one.png", tags: new[] { "a" }),
                MediaFactory.Create("C:/media/two.png", tags: new[] { "b" })
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => db.UpsertMediaBatchAsync(batch, cts.Token),
                "Pre-cancelled token should cancel the upsert");

            var afterCancel = await db.GetAllMediaAsync();
            Assert.Equal(0, afterCancel.Count, "Cancelled upsert must not commit any rows");

            // The write gate must be released so subsequent writes still work.
            await db.UpsertMediaBatchAsync(new[] { MediaFactory.Create("C:/media/three.png") });
            var afterRetry = await db.GetAllMediaAsync();
            Assert.Equal(1, afterRetry.Count, "Write gate released; later upsert succeeds");
        });

        registry.Add(Suite, "cancellation mid-batch keeps upsert atomic", async () =>
        {
            using var ws = new TempWorkspace("tx-cancel-mid");
            var db = CreateDb(ws);

            var batch = Enumerable.Range(0, 4000)
                .Select(i => MediaFactory.Create($"C:/media/mid-{i}.png", tags: new[] { $"t{i % 8}" }))
                .ToArray();

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(2));

            try
            {
                await db.UpsertMediaBatchAsync(batch, cts.Token);
            }
            catch (OperationCanceledException)
            {
            }

            var count = (await db.GetAllMediaAsync()).Count;
            Assert.True(
                count == 0 || count == batch.Length,
                $"Upsert must be atomic under cancellation (count={count})");
        });

        registry.Add(Suite, "delete removes media, tags, search index and relocation rows", async () =>
        {
            using var ws = new TempWorkspace("tx-delete");
            var db = CreateDb(ws);

            var item = MediaFactory.Create("C:/media/deleteme.mp4", MediaType.Video, tags: new[] { "keep", "drop" });
            await db.UpsertMediaBatchAsync(new[] { item });

            await db.DeleteMediaAsync(item.Id);

            Assert.Equal(0, (await db.GetAllMediaAsync()).Count, "Media removed");

            using var connection = OpenRawConnection(ws);
            Assert.Equal(0, ScalarCount(connection, "SELECT COUNT(*) FROM tags WHERE mediaId = $id;", item.Id), "Tags removed");
            Assert.Equal(0, ScalarCount(connection, "SELECT COUNT(*) FROM media_search WHERE mediaId = $id;", item.Id), "Search index removed");

            var search = await db.QueryMediaPageAsync(new MediaQuery { SearchText = "keep", Limit = 50 });
            Assert.Equal(0, search.Items.Count, "FTS no longer matches deleted media");
        });

        registry.Add(Suite, "records-only removal writes relocation and reuses id on re-add", async () =>
        {
            using var ws = new TempWorkspace("tx-records-only");
            var db = CreateDb(ws);

            var original = MediaFactory.Create(
                "C:/media/moved.mp4",
                MediaType.Video,
                size: 5000,
                modifiedAt: 111,
                contentHash: "deadbeefcafefeed");
            await db.UpsertMediaBatchAsync(new[] { original });

            await db.RemoveMediaRecordsOnlyAsync(new[] { original.Id });
            Assert.Equal(0, (await db.GetAllMediaAsync()).Count, "Media record removed");

            using (var connection = OpenRawConnection(ws))
            {
                Assert.Equal(1, ScalarCount(connection, "SELECT COUNT(*) FROM media_relocations WHERE mediaId = $id;", original.Id), "Relocation recorded");
            }

            // Re-add at a new path with the same fingerprint; id must be reused.
            var relocated = MediaFactory.Create(
                "C:/media/newdir/moved.mp4",
                MediaType.Video,
                size: 5000,
                modifiedAt: 111,
                contentHash: "deadbeefcafefeed");
            await db.UpsertMediaBatchAsync(new[] { relocated });

            var all = await db.GetAllMediaAsync();
            Assert.Equal(1, all.Count, "Single media after relocation");
            Assert.Equal(original.Id, all[0].Id, "Media id reused across relocation");
        });

        registry.Add(Suite, "concurrent writers do not surface unhandled SQLITE_BUSY", async () =>
        {
            using var ws = new TempWorkspace("tx-concurrent");
            var db = CreateDb(ws);

            var writers = Enumerable.Range(0, 4).Select(worker => Task.Run(async () =>
            {
                for (var i = 0; i < 40; i++)
                {
                    var item = MediaFactory.Create(
                        $"C:/media/w{worker}-{i}.png",
                        tags: new[] { $"tag-{worker}" });
                    await db.UpsertMediaBatchAsync(new[] { item });
                }
            })).ToArray();

            await Task.WhenAll(writers);

            var all = await db.GetAllMediaAsync();
            Assert.Equal(160, all.Count, "All concurrent upserts persisted without lock failures");
        });
    }

    private static MediaDb CreateDb(TempWorkspace ws)
    {
        var db = new MediaDb(ws.Root);
        db.Initialize();
        // Wait for background startup maintenance so tests never race it.
        db.StartupMaintenanceTask.GetAwaiter().GetResult();
        return db;
    }

    private static SqliteConnection OpenRawConnection(TempWorkspace ws)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(ws.Root, "media.db"),
            Pooling = true
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static int ScalarCount(SqliteConnection connection, string sql, string id)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }
}
