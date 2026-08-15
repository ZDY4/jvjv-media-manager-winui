using Microsoft.Data.Sqlite;

namespace JvJvMediaManager.Data;

internal sealed class MediaDbConnectionFactory
{
    private const int BusyTimeoutMilliseconds = 5000;

    private const string ConnectionPragmaSql =
        "PRAGMA busy_timeout=5000;" +
        "PRAGMA foreign_keys=ON;" +
        "PRAGMA synchronous=NORMAL;";

    private readonly string _connectionString;

    public MediaDbConnectionFactory(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = true,
            DefaultTimeout = BusyTimeoutMilliseconds / 1000
        };
        _connectionString = builder.ToString();
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        ApplyConnectionPragmas(connection);
        return connection;
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await ApplyConnectionPragmasAsync(connection, cancellationToken);
        return connection;
    }

    private static void ApplyConnectionPragmas(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = ConnectionPragmaSql;
        command.ExecuteNonQuery();
    }

    private static async Task ApplyConnectionPragmasAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = ConnectionPragmaSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
