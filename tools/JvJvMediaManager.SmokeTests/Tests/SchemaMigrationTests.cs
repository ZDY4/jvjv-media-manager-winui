using JvJvMediaManager.Data;
using JvJvMediaManager.SmokeTests.Harness;
using Microsoft.Data.Sqlite;

namespace JvJvMediaManager.SmokeTests.Tests;

internal static class SchemaMigrationTests
{
    private const string Suite = "db-migration";

    public static void Register(TestRegistry registry)
    {
        registry.Add(Suite, "new databases record the explicit schema history", async () =>
        {
            using var ws = new TempWorkspace("schema-new");
            var db = new MediaDb(ws.Root);
            db.Initialize();
            await db.StartupMaintenanceTask;

            var factory = new MediaDbConnectionFactory(Path.Combine(ws.Root, "media.db"));
            using var connection = factory.OpenConnection();
            Assert.Equal(3L, ReadScalar<long>(connection, "PRAGMA user_version;"), "current schema version");
            Assert.Equal(3L, ReadScalar<long>(connection, "SELECT COUNT(*) FROM schema_migrations;"), "migration history rows");
        });

        registry.Add(Suite, "legacy databases upgrade in one initialization", () =>
        {
            using var ws = new TempWorkspace("schema-legacy");
            var dbPath = Path.Combine(ws.Root, "media.db");
            var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
CREATE TABLE media (id TEXT PRIMARY KEY);
PRAGMA user_version = 0;
";
                command.ExecuteNonQuery();
            }

            var db = new MediaDb(ws.Root);
            db.Initialize();

            var factory = new MediaDbConnectionFactory(dbPath);
            using var upgraded = factory.OpenConnection();
            Assert.Equal(3L, ReadScalar<long>(upgraded, "PRAGMA user_version;"), "legacy database schema version");
            Assert.Equal(1L, ReadScalar<long>(upgraded, "SELECT COUNT(*) FROM pragma_table_info('media') WHERE name = 'folderPath';"), "legacy media columns upgraded");
            Assert.Equal(3L, ReadScalar<long>(upgraded, "SELECT COUNT(*) FROM schema_migrations;"), "legacy migration history rows");
        });

        registry.Add(Suite, "future schema versions fail closed", () =>
        {
            using var ws = new TempWorkspace("schema-future");
            var dbPath = Path.Combine(ws.Root, "media.db");
            var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version = 999;";
                command.ExecuteNonQuery();
            }

            try
            {
                new MediaDb(ws.Root).Initialize();
            }
            catch (InvalidOperationException)
            {
                return;
            }

            throw new AssertionException("A database newer than the app should fail closed.");
        });
    }

    private static T ReadScalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }
}
