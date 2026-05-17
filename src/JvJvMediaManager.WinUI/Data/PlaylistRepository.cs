using Microsoft.Data.Sqlite;
using JvJvMediaManager.Models;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.Data;

internal sealed class PlaylistRepository
{
    private readonly MediaDbConnectionFactory _connections;

    public PlaylistRepository(MediaDbConnectionFactory connections)
    {
        _connections = connections;
    }

    public IReadOnlyList<Playlist> GetPlaylists()
    {
        using var connection = _connections.OpenConnection();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, name, color, sortOrder, createdAt
FROM playlists
ORDER BY sortOrder ASC, createdAt ASC, name COLLATE NOCASE ASC;
";

        using var reader = cmd.ExecuteReader();
        var playlists = new List<Playlist>();
        while (reader.Read())
        {
            playlists.Add(new Playlist
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                ColorHex = reader.IsDBNull(2) ? null : reader.GetString(2),
                SortOrder = reader.GetInt32(3),
                CreatedAt = reader.GetInt64(4)
            });
        }

        AppTraceLogger.LogSampled("PlaylistRepository", "get-playlists", $"GetPlaylists completed. Count={playlists.Count}.", TimeSpan.FromSeconds(2));
        return playlists;
    }

    public Playlist CreatePlaylist(string name)
    {
        var normalized = name.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("播放列表名称不能为空。", nameof(name));
        }

        using var connection = _connections.OpenConnection();

        using var sortOrderCmd = connection.CreateCommand();
        sortOrderCmd.CommandText = "SELECT COALESCE(MAX(sortOrder), -1) + 1 FROM playlists;";
        var sortOrder = Convert.ToInt32(sortOrderCmd.ExecuteScalar());

        var playlist = new Playlist
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = normalized,
            ColorHex = null,
            SortOrder = sortOrder,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO playlists (id, name, color, sortOrder, createdAt)
VALUES ($id, $name, $color, $sortOrder, $createdAt);
";
        cmd.Parameters.AddWithValue("$id", playlist.Id);
        cmd.Parameters.AddWithValue("$name", playlist.Name);
        cmd.Parameters.AddWithValue("$color", (object?)playlist.ColorHex ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sortOrder", playlist.SortOrder);
        cmd.Parameters.AddWithValue("$createdAt", playlist.CreatedAt);
        cmd.ExecuteNonQuery();

        AppTraceLogger.Log("PlaylistRepository", $"CreatePlaylist completed. PlaylistId='{playlist.Id}', NameLength={playlist.Name.Length}, SortOrder={playlist.SortOrder}.");
        return playlist;
    }

    public void RenamePlaylist(string playlistId, string name)
    {
        var normalized = name.Trim();
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            throw new ArgumentException("播放列表 ID 不能为空。", nameof(playlistId));
        }

        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("播放列表名称不能为空。", nameof(name));
        }

        using var connection = _connections.OpenConnection();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE playlists SET name = $name WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", playlistId);
        cmd.Parameters.AddWithValue("$name", normalized);
        var changed = cmd.ExecuteNonQuery();
        AppTraceLogger.Log("PlaylistRepository", $"RenamePlaylist completed. PlaylistId='{playlistId}', Changed={changed}, NameLength={normalized.Length}.");
    }

    public void SetPlaylistColor(string playlistId, string? colorHex)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            throw new ArgumentException("播放列表 ID 不能为空。", nameof(playlistId));
        }

        using var connection = _connections.OpenConnection();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE playlists SET color = $color WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", playlistId);
        cmd.Parameters.AddWithValue("$color", string.IsNullOrWhiteSpace(colorHex) ? DBNull.Value : colorHex.Trim());
        var changed = cmd.ExecuteNonQuery();
        AppTraceLogger.Log("PlaylistRepository", $"SetPlaylistColor completed. PlaylistId='{playlistId}', HasColor={!string.IsNullOrWhiteSpace(colorHex)}, Changed={changed}.");
    }

    public void DeletePlaylist(string playlistId)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            AppTraceLogger.Log("PlaylistRepository", "DeletePlaylist skipped. EmptyPlaylistId=True.");
            return;
        }

        using var connection = _connections.OpenConnection();

        using var tx = connection.BeginTransaction();
        using var mediaCmd = connection.CreateCommand();
        mediaCmd.CommandText = "DELETE FROM playlist_media WHERE playlistId = $id;";
        mediaCmd.Parameters.AddWithValue("$id", playlistId);
        mediaCmd.ExecuteNonQuery();

        using var playlistCmd = connection.CreateCommand();
        playlistCmd.CommandText = "DELETE FROM playlists WHERE id = $id;";
        playlistCmd.Parameters.AddWithValue("$id", playlistId);
        playlistCmd.ExecuteNonQuery();

        tx.Commit();
        AppTraceLogger.Log("PlaylistRepository", $"DeletePlaylist completed. PlaylistId='{playlistId}'.");
    }

    public void UpdatePlaylistOrder(IReadOnlyList<string> playlistIds)
    {
        using var connection = _connections.OpenConnection();

        using var tx = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE playlists SET sortOrder = $sortOrder WHERE id = $id;";
        var sortOrderParam = cmd.Parameters.Add("$sortOrder", SqliteType.Integer);
        var idParam = cmd.Parameters.Add("$id", SqliteType.Text);

        for (var i = 0; i < playlistIds.Count; i++)
        {
            sortOrderParam.Value = i;
            idParam.Value = playlistIds[i];
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        AppTraceLogger.Log("PlaylistRepository", $"UpdatePlaylistOrder completed. Count={playlistIds.Count}.");
    }

    public void AddMediaToPlaylist(string playlistId, IEnumerable<string> mediaIds)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            throw new ArgumentException("播放列表 ID 不能为空。", nameof(playlistId));
        }

        var ids = mediaIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ids.Count == 0)
        {
            AppTraceLogger.Log("PlaylistRepository", $"AddMediaToPlaylist skipped. PlaylistId='{playlistId}', MediaCount=0.");
            return;
        }

        using var connection = _connections.OpenConnection();

        using var startOrderCmd = connection.CreateCommand();
        startOrderCmd.CommandText = "SELECT COALESCE(MAX(sortOrder), -1) + 1 FROM playlist_media WHERE playlistId = $playlistId;";
        startOrderCmd.Parameters.AddWithValue("$playlistId", playlistId);
        var nextSortOrder = Convert.ToInt32(startOrderCmd.ExecuteScalar());

        using var tx = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT OR IGNORE INTO playlist_media (playlistId, mediaId, sortOrder, addedAt)
VALUES ($playlistId, $mediaId, $sortOrder, $addedAt);
";

        var playlistParam = cmd.Parameters.Add("$playlistId", SqliteType.Text);
        var mediaParam = cmd.Parameters.Add("$mediaId", SqliteType.Text);
        var sortOrderParam = cmd.Parameters.Add("$sortOrder", SqliteType.Integer);
        var addedAtParam = cmd.Parameters.Add("$addedAt", SqliteType.Integer);

        foreach (var mediaId in ids)
        {
            playlistParam.Value = playlistId;
            mediaParam.Value = mediaId;
            sortOrderParam.Value = nextSortOrder++;
            addedAtParam.Value = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        AppTraceLogger.Log("PlaylistRepository", $"AddMediaToPlaylist completed. PlaylistId='{playlistId}', MediaCount={ids.Count}.");
    }

    public void RemoveMediaFromPlaylist(string playlistId, IEnumerable<string> mediaIds)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            AppTraceLogger.Log("PlaylistRepository", "RemoveMediaFromPlaylist skipped. EmptyPlaylistId=True.");
            return;
        }

        var ids = mediaIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0)
        {
            AppTraceLogger.Log("PlaylistRepository", $"RemoveMediaFromPlaylist skipped. PlaylistId='{playlistId}', MediaCount=0.");
            return;
        }

        using var connection = _connections.OpenConnection();

        using var tx = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM playlist_media WHERE playlistId = $playlistId AND mediaId = $mediaId;";
        var playlistParam = cmd.Parameters.Add("$playlistId", SqliteType.Text);
        var mediaParam = cmd.Parameters.Add("$mediaId", SqliteType.Text);

        foreach (var mediaId in ids)
        {
            playlistParam.Value = playlistId;
            mediaParam.Value = mediaId;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        AppTraceLogger.Log("PlaylistRepository", $"RemoveMediaFromPlaylist completed. PlaylistId='{playlistId}', MediaCount={ids.Count}.");
    }

    public bool AreAllMediaInPlaylist(string playlistId, IEnumerable<string> mediaIds)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return false;
        }

        var mediaIdList = mediaIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();

        if (mediaIdList.Count == 0)
        {
            return false;
        }

        using var connection = _connections.OpenConnection();

        var placeholders = string.Join(",", mediaIdList.Select((_, i) => $"@id{i}"));
        var sql = $@"
            SELECT COUNT(DISTINCT mediaId)
            FROM playlist_media
            WHERE playlistId = @playlistId
            AND mediaId IN ({placeholders})";

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@playlistId", playlistId);

        for (var i = 0; i < mediaIdList.Count; i++)
        {
            command.Parameters.AddWithValue($"@id{i}", mediaIdList[i]);
        }

        var count = Convert.ToInt64(command.ExecuteScalar() ?? 0L);
        AppTraceLogger.LogSampled(
            "PlaylistRepository",
            "are-all-media-in-playlist",
            $"AreAllMediaInPlaylist completed. PlaylistId='{playlistId}', Requested={mediaIdList.Count}, Matched={count}.",
            TimeSpan.FromSeconds(2));
        return count == mediaIdList.Count;
    }
}
