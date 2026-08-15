using Microsoft.Data.Sqlite;

namespace JvJvMediaManager.Data;

// WP-10: media_search (FTS5) index maintenance. All helpers bind to the caller's
// active transaction so search rows stay consistent with media/tags writes.
internal sealed partial class MediaRepository
{
    public async Task RefreshSearchIndexAsync(string mediaId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return;
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
            await using var tx = await connection.BeginTransactionAsync(cancellationToken);
            await RefreshSearchIndexAsync(connection, (SqliteTransaction)tx, mediaId, cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task RefreshSearchIndexAsync(SqliteConnection connection, SqliteTransaction? transaction, string mediaId, CancellationToken cancellationToken = default)
    {
        if (!await IsSearchIndexAvailableAsync(connection, transaction, cancellationToken))
        {
            return;
        }

        await using var mediaCmd = CreateCommand(connection, transaction);
        mediaCmd.CommandText = "SELECT filename FROM media WHERE id = $id;";
        mediaCmd.Parameters.AddWithValue("$id", mediaId);
        var fileName = await mediaCmd.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            await DeleteSearchIndexAsync(connection, transaction, mediaId, cancellationToken);
            return;
        }

        var tags = string.Join(' ', await MediaTagRepository.GetTagsAsync(connection, transaction, mediaId, cancellationToken));
        await DeleteSearchIndexAsync(connection, transaction, mediaId, cancellationToken);

        await using var insertCmd = CreateCommand(connection, transaction);
        insertCmd.CommandText = "INSERT INTO media_search(mediaId, filename, tags) VALUES ($mediaId, $filename, $tags);";
        insertCmd.Parameters.AddWithValue("$mediaId", mediaId);
        insertCmd.Parameters.AddWithValue("$filename", fileName);
        insertCmd.Parameters.AddWithValue("$tags", tags);
        await insertCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteSearchIndexAsync(SqliteConnection connection, SqliteTransaction? transaction, string mediaId, CancellationToken cancellationToken = default)
    {
        if (!await IsSearchIndexAvailableAsync(connection, transaction, cancellationToken))
        {
            return;
        }

        await using var cmd = CreateCommand(connection, transaction);
        cmd.CommandText = "DELETE FROM media_search WHERE mediaId = $id;";
        cmd.Parameters.AddWithValue("$id", mediaId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ClearSearchIndexAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken = default)
    {
        if (!await IsSearchIndexAvailableAsync(connection, transaction, cancellationToken))
        {
            return;
        }

        await using var cmd = CreateCommand(connection, transaction);
        cmd.CommandText = "DELETE FROM media_search;";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> IsSearchIndexAvailableAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var cmd = CreateCommand(connection, transaction);
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'media_search' LIMIT 1;";
            return await cmd.ExecuteScalarAsync(cancellationToken) != null;
        }
        catch
        {
            return false;
        }
    }
}
