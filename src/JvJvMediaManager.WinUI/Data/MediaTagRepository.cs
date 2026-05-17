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

    public void AddTag(string mediaId, string tag)
    {
        using var connection = _connections.OpenConnection();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT OR IGNORE INTO tags (mediaId, name, createdAt)
VALUES ($mediaId, $name, $createdAt);
";
        cmd.Parameters.AddWithValue("$mediaId", mediaId);
        cmd.Parameters.AddWithValue("$name", tag);
        cmd.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }

    public void RemoveTag(string mediaId, string tag)
    {
        using var connection = _connections.OpenConnection();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM tags WHERE mediaId = $mediaId AND name = $name;";
        cmd.Parameters.AddWithValue("$mediaId", mediaId);
        cmd.Parameters.AddWithValue("$name", tag);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<string> GetTags(string mediaId)
    {
        using var connection = _connections.OpenConnection();
        return GetTags(connection, mediaId);
    }

    public IReadOnlyList<string> GetAllTags()
    {
        using var connection = _connections.OpenConnection();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT name FROM tags ORDER BY name;";
        using var reader = cmd.ExecuteReader();
        var tags = new List<string>();
        while (reader.Read())
        {
            tags.Add(reader.GetString(0));
        }

        AppTraceLogger.LogSampled("MediaTagRepository", "get-all-tags", $"GetAllTags completed. Count={tags.Count}.", TimeSpan.FromSeconds(2));
        return tags;
    }

    public void AttachTags(SqliteConnection connection, List<MediaFile> media)
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

}
