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

    internal void AddTags(SqliteConnection connection, string mediaId, IEnumerable<string> tags)
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

        using var cmd = connection.CreateCommand();
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
            cmd.ExecuteNonQuery();
        }
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

    internal void AttachTags(SqliteConnection connection, List<MediaFile> media)
    {
        if (media.Count == 0)
        {
            return;
        }

        using var cmd = connection.CreateCommand();
        var parameterNames = new List<string>(media.Count);
        for (var i = 0; i < media.Count; i++)
        {
            var parameterName = $"$id{i}";
            parameterNames.Add(parameterName);
            cmd.Parameters.AddWithValue(parameterName, media[i].Id);
        }

        cmd.CommandText = $@"
SELECT mediaId, name
FROM tags
WHERE mediaId IN ({string.Join(", ", parameterNames)})
ORDER BY mediaId, name;
";

        var tagsByMedia = media.ToDictionary(item => item.Id, _ => new List<string>(), StringComparer.Ordinal);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var mediaId = reader.GetString(0);
            if (tagsByMedia.TryGetValue(mediaId, out var tags))
            {
                tags.Add(reader.GetString(1));
            }
        }

        foreach (var item in media)
        {
            item.Tags = tagsByMedia[item.Id];
        }
        AppTraceLogger.LogSampled("MediaTagRepository", "attach-tags", $"AttachTags completed. MediaCount={media.Count}.", TimeSpan.FromSeconds(1));
    }

    public async Task AttachTagsAsync(SqliteConnection connection, List<MediaFile> media, CancellationToken cancellationToken = default)
    {
        if (media.Count == 0)
        {
            return;
        }

        await using var cmd = connection.CreateCommand();
        var parameterNames = new List<string>(media.Count);
        for (var i = 0; i < media.Count; i++)
        {
            var parameterName = $"$id{i}";
            parameterNames.Add(parameterName);
            cmd.Parameters.AddWithValue(parameterName, media[i].Id);
        }

        cmd.CommandText = $@"
SELECT mediaId, name
FROM tags
WHERE mediaId IN ({string.Join(", ", parameterNames)})
ORDER BY mediaId, name;
";

        var tagsByMedia = media.ToDictionary(item => item.Id, _ => new List<string>(), StringComparer.Ordinal);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var mediaId = reader.GetString(0);
            if (tagsByMedia.TryGetValue(mediaId, out var tags))
            {
                tags.Add(reader.GetString(1));
            }
        }

        foreach (var item in media)
        {
            item.Tags = tagsByMedia[item.Id];
        }

        AppTraceLogger.LogSampled("MediaTagRepository", "attach-tags", $"AttachTagsAsync completed. MediaCount={media.Count}.", TimeSpan.FromSeconds(1));
    }

    public static IReadOnlyList<string> GetTags(SqliteConnection connection, string mediaId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM tags WHERE mediaId = $mediaId ORDER BY name;";
        cmd.Parameters.AddWithValue("$mediaId", mediaId);

        using var reader = cmd.ExecuteReader();
        var tags = new List<string>();
        while (reader.Read())
        {
            tags.Add(reader.GetString(0));
        }

        return tags;
    }

    public static async Task<IReadOnlyList<string>> GetTagsAsync(SqliteConnection connection, string mediaId, CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
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
