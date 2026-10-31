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
        await MarkSearchIndexHealthyAsync(connection, transaction, cancellationToken);
    }

    private static async Task RefreshSearchIndexBatchAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyCollection<string> mediaIds,
        CancellationToken cancellationToken = default)
    {
        var ids = mediaIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0 || !await IsSearchIndexAvailableAsync(connection, transaction, cancellationToken))
        {
            return;
        }

        const int maxIdsPerBatch = 500;
        for (var offset = 0; offset < ids.Count; offset += maxIdsPerBatch)
        {
            var count = Math.Min(maxIdsPerBatch, ids.Count - offset);
            await using var cmd = CreateCommand(connection, transaction);
            var parameters = new List<string>(count);
            for (var index = 0; index < count; index++)
            {
                var parameterName = $"$mediaId{index}";
                parameters.Add(parameterName);
                cmd.Parameters.AddWithValue(parameterName, ids[offset + index]);
            }

            var inClause = string.Join(", ", parameters);
            cmd.CommandText = $@"
DELETE FROM media_search WHERE mediaId IN ({inClause});
INSERT INTO media_search(mediaId, filename, tags)
SELECT m.id, m.filename, COALESCE(group_concat(t.name, ' '), '')
FROM media m
LEFT JOIN tags t ON t.mediaId = m.id
WHERE m.id IN ({inClause})
GROUP BY m.id, m.filename;
";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            await MarkSearchIndexHealthyAsync(connection, transaction, cancellationToken);
        }
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
            if (await cmd.ExecuteScalarAsync(cancellationToken) == null)
            {
                return false;
            }

            // FTS is a derived index. If a previous rebuild failed, callers
            // should use the LIKE fallback until startup maintenance repairs it
            // instead of returning silently stale search results.
            cmd.CommandText = "SELECT isDirty FROM media_search_state WHERE id = 1;";
            var dirty = await cmd.ExecuteScalarAsync(cancellationToken);
            return dirty == null || Convert.ToInt64(dirty) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task MarkSearchIndexHealthyAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var cmd = CreateCommand(connection, transaction);
        cmd.CommandText = @"
UPDATE media_search_state
SET indexedGeneration = indexedGeneration + 1,
    isDirty = 0,
    lastError = NULL,
    updatedAt = unixepoch()
WHERE id = 1;
";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
