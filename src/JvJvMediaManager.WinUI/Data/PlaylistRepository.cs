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

    public async Task<IReadOnlyList<Playlist>> GetPlaylistsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, name, color, sortOrder, createdAt
FROM playlists
ORDER BY sortOrder ASC, createdAt ASC, name COLLATE NOCASE ASC;
";

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var playlists = new List<Playlist>();
        while (await reader.ReadAsync(cancellationToken))
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

    public async Task<Playlist> CreatePlaylistAsync(string name, CancellationToken cancellationToken = default)
    {
        var normalized = name.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("播放列表名称不能为空。", nameof(name));
        }

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);

        await using var sortOrderCmd = connection.CreateCommand();
        sortOrderCmd.CommandText = "SELECT COALESCE(MAX(sortOrder), -1) + 1 FROM playlists;";
        var sortOrder = Convert.ToInt32(await sortOrderCmd.ExecuteScalarAsync(cancellationToken));

        var playlist = new Playlist
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = normalized,
            ColorHex = null,
            SortOrder = sortOrder,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO playlists (id, name, color, sortOrder, createdAt)
VALUES ($id, $name, $color, $sortOrder, $createdAt);
";
        cmd.Parameters.AddWithValue("$id", playlist.Id);
        cmd.Parameters.AddWithValue("$name", playlist.Name);
        cmd.Parameters.AddWithValue("$color", (object?)playlist.ColorHex ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sortOrder", playlist.SortOrder);
        cmd.Parameters.AddWithValue("$createdAt", playlist.CreatedAt);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        AppTraceLogger.Log("PlaylistRepository", $"CreatePlaylist completed. PlaylistId='{playlist.Id}', NameLength={playlist.Name.Length}, SortOrder={playlist.SortOrder}.");
        return playlist;
    }

    public async Task RenamePlaylistAsync(string playlistId, string name, CancellationToken cancellationToken = default)
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

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE playlists SET name = $name WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", playlistId);
        cmd.Parameters.AddWithValue("$name", normalized);
        var changed = await cmd.ExecuteNonQueryAsync(cancellationToken);
        AppTraceLogger.Log("PlaylistRepository", $"RenamePlaylist completed. PlaylistId='{playlistId}', Changed={changed}, NameLength={normalized.Length}.");
    }

    public async Task SetPlaylistColorAsync(string playlistId, string? colorHex, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            throw new ArgumentException("播放列表 ID 不能为空。", nameof(playlistId));
        }

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE playlists SET color = $color WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", playlistId);
        cmd.Parameters.AddWithValue("$color", string.IsNullOrWhiteSpace(colorHex) ? DBNull.Value : colorHex.Trim());
        var changed = await cmd.ExecuteNonQueryAsync(cancellationToken);
        AppTraceLogger.Log("PlaylistRepository", $"SetPlaylistColor completed. PlaylistId='{playlistId}', HasColor={!string.IsNullOrWhiteSpace(colorHex)}, Changed={changed}.");
    }

    public async Task DeletePlaylistAsync(string playlistId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            AppTraceLogger.Log("PlaylistRepository", "DeletePlaylist skipped. EmptyPlaylistId=True.");
            return;
        }

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);

        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await using var mediaCmd = connection.CreateCommand();
        mediaCmd.Transaction = (SqliteTransaction)tx;
        mediaCmd.CommandText = "DELETE FROM playlist_media WHERE playlistId = $id;";
        mediaCmd.Parameters.AddWithValue("$id", playlistId);
        await mediaCmd.ExecuteNonQueryAsync(cancellationToken);

        await using var playlistCmd = connection.CreateCommand();
        playlistCmd.Transaction = (SqliteTransaction)tx;
        playlistCmd.CommandText = "DELETE FROM playlists WHERE id = $id;";
        playlistCmd.Parameters.AddWithValue("$id", playlistId);
        await playlistCmd.ExecuteNonQueryAsync(cancellationToken);

        await tx.CommitAsync(cancellationToken);
        AppTraceLogger.Log("PlaylistRepository", $"DeletePlaylist completed. PlaylistId='{playlistId}'.");
    }

    public async Task UpdatePlaylistOrderAsync(IReadOnlyList<string> playlistIds, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);

        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = "UPDATE playlists SET sortOrder = $sortOrder WHERE id = $id;";
        var sortOrderParam = cmd.Parameters.Add("$sortOrder", SqliteType.Integer);
        var idParam = cmd.Parameters.Add("$id", SqliteType.Text);

        for (var i = 0; i < playlistIds.Count; i++)
        {
            sortOrderParam.Value = i;
            idParam.Value = playlistIds[i];
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        AppTraceLogger.Log("PlaylistRepository", $"UpdatePlaylistOrder completed. Count={playlistIds.Count}.");
    }

    public async Task AddMediaToPlaylistAsync(string playlistId, IEnumerable<string> mediaIds, CancellationToken cancellationToken = default)
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

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);

        await using var startOrderCmd = connection.CreateCommand();
        startOrderCmd.CommandText = "SELECT COALESCE(MAX(sortOrder), -1) + 1 FROM playlist_media WHERE playlistId = $playlistId;";
        startOrderCmd.Parameters.AddWithValue("$playlistId", playlistId);
        var nextSortOrder = Convert.ToInt32(await startOrderCmd.ExecuteScalarAsync(cancellationToken));

        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
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
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        AppTraceLogger.Log("PlaylistRepository", $"AddMediaToPlaylist completed. PlaylistId='{playlistId}', MediaCount={ids.Count}.");
    }

    public async Task RemoveMediaFromPlaylistAsync(string playlistId, IEnumerable<string> mediaIds, CancellationToken cancellationToken = default)
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

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);

        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = "DELETE FROM playlist_media WHERE playlistId = $playlistId AND mediaId = $mediaId;";
        var playlistParam = cmd.Parameters.Add("$playlistId", SqliteType.Text);
        var mediaParam = cmd.Parameters.Add("$mediaId", SqliteType.Text);

        foreach (var mediaId in ids)
        {
            playlistParam.Value = playlistId;
            mediaParam.Value = mediaId;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        AppTraceLogger.Log("PlaylistRepository", $"RemoveMediaFromPlaylist completed. PlaylistId='{playlistId}', MediaCount={ids.Count}.");
    }

    public async Task<bool> AreAllMediaInPlaylistAsync(string playlistId, IEnumerable<string> mediaIds, CancellationToken cancellationToken = default)
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

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);

        var placeholders = string.Join(",", mediaIdList.Select((_, i) => $"@id{i}"));
        var sql = $@"
            SELECT COUNT(DISTINCT mediaId)
            FROM playlist_media
            WHERE playlistId = @playlistId
            AND mediaId IN ({placeholders})";

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@playlistId", playlistId);

        for (var i = 0; i < mediaIdList.Count; i++)
        {
            command.Parameters.AddWithValue($"@id{i}", mediaIdList[i]);
        }

        var count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
        AppTraceLogger.LogSampled(
            "PlaylistRepository",
            "are-all-media-in-playlist",
            $"AreAllMediaInPlaylist completed. PlaylistId='{playlistId}', Requested={mediaIdList.Count}, Matched={count}.",
            TimeSpan.FromSeconds(2));
        return count == mediaIdList.Count;
    }
}
