using JvJvMediaManager.Data;
using JvJvMediaManager.SmokeTests.Harness;
using Microsoft.Data.Sqlite;

namespace JvJvMediaManager.SmokeTests.Tests;

internal static class ConnectionConfigTests
{
    private const string Suite = "db-connection";

    public static void Register(TestRegistry registry)
    {
        registry.Add(Suite, "every connection reports the configured pragmas", async () =>
        {
            using var ws = new TempWorkspace("conn-pragmas");
            var db = new MediaDb(ws.Root);
            db.Initialize();
            await db.StartupMaintenanceTask;

            var factory = new MediaDbConnectionFactory(Path.Combine(ws.Root, "media.db"));
            using var connection = factory.OpenConnection();

            Assert.Equal(5000L, ReadPragmaLong(connection, "busy_timeout"), "busy_timeout=5000");
            Assert.Equal(1L, ReadPragmaLong(connection, "foreign_keys"), "foreign_keys=ON");
            // synchronous NORMAL == 1.
            Assert.Equal(1L, ReadPragmaLong(connection, "synchronous"), "synchronous=NORMAL");
        });

        registry.Add(Suite, "database uses WAL journal mode after initialize", async () =>
        {
            using var ws = new TempWorkspace("conn-wal");
            var db = new MediaDb(ws.Root);
            db.Initialize();
            await db.StartupMaintenanceTask;

            var factory = new MediaDbConnectionFactory(Path.Combine(ws.Root, "media.db"));
            using var connection = factory.OpenConnection();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode;";
            var mode = cmd.ExecuteScalar() as string ?? string.Empty;
            Assert.Equal("wal", mode.ToLowerInvariant(), "journal_mode=WAL persisted");
        });
    }

    private static long ReadPragmaLong(SqliteConnection connection, string pragma)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA {pragma};";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }
}
