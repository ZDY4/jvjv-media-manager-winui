using Microsoft.Data.Sqlite;

namespace JvJvMediaManager.Data;

internal sealed class MediaDbConnectionFactory
{
    private readonly string _connectionString;

    public MediaDbConnectionFactory(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = true
        };
        _connectionString = builder.ToString();
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
