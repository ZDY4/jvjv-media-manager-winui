using Microsoft.Data.Sqlite;

namespace JvJvMediaManager.Data;

// WP-E: batched tag writes. Replaces the per-tag AddTagAsync/RemoveTagAsync
// loop (each with its own connection and FTS refresh) with a single
// transaction: chunked tag deletion, batched insertion, and one search-index
// refresh per media id — all serialized behind the repository write gate.
internal sealed partial class MediaRepository
{
    public async Task AddTagAsync(string mediaId, string tag, CancellationToken cancellationToken = default)
    {
        var normalizedTag = tag?.Trim();
        if (string.IsNullOrWhiteSpace(mediaId) || string.IsNullOrWhiteSpace(normalizedTag))
        {
            return;
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await MediaTagRepository.AddTagsAsync(connection, tx, mediaId, new[] { normalizedTag }, cancellationToken);
            await RefreshSearchIndexAsync(connection, tx, mediaId, cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task RemoveTagAsync(string mediaId, string tag, CancellationToken cancellationToken = default)
    {
        var normalizedTag = tag?.Trim();
        if (string.IsNullOrWhiteSpace(mediaId) || string.IsNullOrWhiteSpace(normalizedTag))
        {
            return;
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var command = CreateCommand(connection, tx);
            command.CommandText = "DELETE FROM tags WHERE mediaId = $mediaId AND name = $name COLLATE NOCASE;";
            command.Parameters.AddWithValue("$mediaId", mediaId);
            command.Parameters.AddWithValue("$name", normalizedTag);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await RefreshSearchIndexAsync(connection, tx, mediaId, cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task SetTagsForMediaAsync(
        IReadOnlyList<(string MediaId, IReadOnlyList<string> Tags)> updates,
        CancellationToken cancellationToken = default)
    {
        if (updates.Count == 0)
        {
            return;
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
            await using var tx = await connection.BeginTransactionAsync(cancellationToken);
            var sqliteTx = (SqliteTransaction)tx;

            // Normalize once up front so the SQL work below is pure batching.
            var normalizedUpdates = new List<(string MediaId, List<string> Tags)>(updates.Count);
            var affectedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (mediaId, tags) in updates)
            {
                if (string.IsNullOrWhiteSpace(mediaId))
                {
                    continue;
                }

                var normalized = tags
                    .Select(tag => tag.Trim())
                    .Where(tag => !string.IsNullOrWhiteSpace(tag))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                normalizedUpdates.Add((mediaId, normalized));
                affectedIds.Add(mediaId);
            }

            if (affectedIds.Count == 0)
            {
                await tx.CommitAsync(cancellationToken);
                return;
            }

            await DeleteTagsForMediaIdsAsync(connection, sqliteTx, affectedIds, cancellationToken);

            await using (var insertCmd = CreateCommand(connection, sqliteTx))
            {
                insertCmd.CommandText = @"
INSERT OR IGNORE INTO tags (mediaId, name, createdAt)
VALUES ($mediaId, $name, $createdAt);
";
                var mediaIdParam = insertCmd.Parameters.Add("$mediaId", SqliteType.Text);
                var nameParam = insertCmd.Parameters.Add("$name", SqliteType.Text);
                var createdAtParam = insertCmd.Parameters.Add("$createdAt", SqliteType.Integer);
                var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                foreach (var (mediaId, tags) in normalizedUpdates)
                {
                    foreach (var tag in tags)
                    {
                        mediaIdParam.Value = mediaId;
                        nameParam.Value = tag;
                        createdAtParam.Value = createdAt;
                        await insertCmd.ExecuteNonQueryAsync(cancellationToken);
                    }
                }
            }

            // Rewrite all affected search rows in bounded batches inside the
            // same transaction as the tag writes.
            await RefreshSearchIndexBatchAsync(connection, sqliteTx, affectedIds, cancellationToken);

            await tx.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task DeleteTagsForMediaIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<string> mediaIds,
        CancellationToken cancellationToken)
    {
        var orderedIds = mediaIds.ToList();
        for (var chunkStart = 0; chunkStart < orderedIds.Count; chunkStart += MaxInClauseParameters)
        {
            var chunkCount = Math.Min(MaxInClauseParameters, orderedIds.Count - chunkStart);
            await using var cmd = CreateCommand(connection, transaction);
            var parameterNames = new List<string>(chunkCount);
            for (var i = 0; i < chunkCount; i++)
            {
                var parameterName = $"$deleteId{i}";
                parameterNames.Add(parameterName);
                cmd.Parameters.AddWithValue(parameterName, orderedIds[chunkStart + i]);
            }

            cmd.CommandText = $"DELETE FROM tags WHERE mediaId IN ({string.Join(", ", parameterNames)});";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
