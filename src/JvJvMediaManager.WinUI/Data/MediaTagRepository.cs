using Microsoft.Data.Sqlite;
using JvJvMediaManager.Models;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.Data;

internal sealed class MediaTagRepository
{
    private readonly MediaDbConnectionFactory _connections;

    public MediaTagRepository(MediaDbConnectionFactory connections)
    {
        _connections = connections;
    }

    public async Task AddTagAsync(string mediaId, string tag, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT OR IGNORE INTO tags (mediaId, name, createdAt)
VALUES ($mediaId, $name, $createdAt);
";
        cmd.Parameters.AddWithValue("$mediaId", mediaId);
        cmd.Parameters.AddWithValue("$name", tag);
        cmd.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task AddTagsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string mediaId,
        IEnumerable<string> tags,
        CancellationToken cancellationToken = default)
    {
        var normalized = tags
            .Select(tag => tag.Trim())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalized.Count == 0)
        {
            return;
        }

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
INSERT OR IGNORE INTO tags (mediaId, name, createdAt)
VALUES ($mediaId, $name, $createdAt);
";
        var mediaIdParam = cmd.Parameters.Add("$mediaId", SqliteType.Text);
        var nameParam = cmd.Parameters.Add("$name", SqliteType.Text);
        var createdAtParam = cmd.Parameters.Add("$createdAt", SqliteType.Integer);
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var tag in normalized)
        {
            mediaIdParam.Value = mediaId;
            nameParam.Value = tag;
            createdAtParam.Value = createdAt;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task RemoveTagAsync(string mediaId, string tag, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM tags WHERE mediaId = $mediaId AND name = $name;";
        cmd.Parameters.AddWithValue("$mediaId", mediaId);
        cmd.Parameters.AddWithValue("$name", tag);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetTagsAsync(string mediaId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        return await GetTagsAsync(connection, mediaId, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetAllTagsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT name FROM tags ORDER BY name;";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var tags = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tags.Add(reader.GetString(0));
        }

        AppTraceLogger.LogSampled("MediaTagRepository", "get-all-tags", $"GetAllTags completed. Count={tags.Count}.", TimeSpan.FromSeconds(2));
        return tags;
    }

    public async Task AttachTagsAsync(SqliteConnection connection, List<MediaFile> media, CancellationToken cancellationToken = default)
    {
        if (media.Count == 0)
        {
            return;
        }

        var tagsByMedia = media.ToDictionary(item => item.Id, _ => new List<string>(), StringComparer.Ordinal);

        // WP-C: chunk the IN clause so very large loaded windows stay below
        // SQLite's 32766-variable limit (previously one parameter per row).
        for (var chunkStart = 0; chunkStart < media.Count; chunkStart += MaxInClauseParameters)
        {
            var chunkCount = Math.Min(MaxInClauseParameters, media.Count - chunkStart);
            await using var cmd = connection.CreateCommand();
            var parameterNames = new List<string>(chunkCount);
            for (var i = 0; i < chunkCount; i++)
            {
                var parameterName = $"$id{i}";
                parameterNames.Add(parameterName);
                cmd.Parameters.AddWithValue(parameterName, media[chunkStart + i].Id);
            }

            cmd.CommandText = $@"
SELECT mediaId, name
FROM tags
WHERE mediaId IN ({string.Join(", ", parameterNames)})
ORDER BY mediaId, name;
";

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var mediaId = reader.GetString(0);
                if (tagsByMedia.TryGetValue(mediaId, out var tags))
                {
                    tags.Add(reader.GetString(1));
                }
            }
        }

        foreach (var item in media)
        {
            item.Tags = tagsByMedia[item.Id];
        }

        AppTraceLogger.LogSampled("MediaTagRepository", "attach-tags", $"AttachTagsAsync completed. MediaCount={media.Count}.", TimeSpan.FromSeconds(1));
    }

    private const int MaxInClauseParameters = 500;

    public static async Task<IReadOnlyList<string>> GetTagsAsync(SqliteConnection connection, string mediaId, CancellationToken cancellationToken = default)
    {
        return await GetTagsAsync(connection, null, mediaId, cancellationToken);
    }

    public static async Task<IReadOnlyList<string>> GetTagsAsync(SqliteConnection connection, SqliteTransaction? transaction, string mediaId, CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT name FROM tags WHERE mediaId = $mediaId ORDER BY name;";
        cmd.Parameters.AddWithValue("$mediaId", mediaId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var tags = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tags.Add(reader.GetString(0));
        }

        return tags;
    }

}
