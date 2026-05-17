using Microsoft.Data.Sqlite;

namespace JvJvMediaManager.Data;

internal sealed class MediaDbConnectionFactory
{
    private readonly string _connectionString;

    public MediaDbConnectionFactory(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
