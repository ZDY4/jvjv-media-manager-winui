using System.Text;
using Microsoft.Data.Sqlite;
using JvJvMediaManager.Models;

namespace JvJvMediaManager.Data;

public sealed class MediaDb
{
    public readonly record struct MediaEntryRef(string Id, string Path);

    private readonly string _dbPath;
    private readonly string _connectionString;

    public MediaDb(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _dbPath = Path.Combine(dataDir, "media.db");
        _connectionString = $"Data Source={_dbPath}";
    }

    public void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS media (
  id TEXT PRIMARY KEY,
  path TEXT NOT NULL,
  filename TEXT NOT NULL,
  type TEXT NOT NULL,
  size INTEGER NOT NULL,
  width INTEGER,
  height INTEGER,
  duration REAL,
  thumbnail TEXT,
  createdAt INTEGER NOT NULL,
  modifiedAt INTEGER NOT NULL,
  lastPlayed INTEGER,
  playCount INTEGER
);
CREATE INDEX IF NOT EXISTS idx_media_modifiedAt ON media(modifiedAt DESC);
CREATE INDEX IF NOT EXISTS idx_media_filename_nocase ON media(filename COLLATE NOCASE);
CREATE INDEX IF NOT EXISTS idx_media_path_nocase ON media(path COLLATE NOCASE);
CREATE TABLE IF NOT EXISTS tags (
  mediaId TEXT NOT NULL,
  name TEXT NOT NULL,
  createdAt INTEGER NOT NULL,
  PRIMARY KEY (mediaId, name)
);
CREATE INDEX IF NOT EXISTS idx_tags_mediaId ON tags(mediaId);
CREATE INDEX IF NOT EXISTS idx_tags_name ON tags(name);
CREATE INDEX IF NOT EXISTS idx_tags_name_nocase ON tags(name COLLATE NOCASE);
CREATE INDEX IF NOT EXISTS idx_tags_mediaId_name_nocase ON tags(mediaId, name COLLATE NOCASE);

CREATE TABLE IF NOT EXISTS playlists (
  id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  sortOrder INTEGER NOT NULL DEFAULT 0,
  createdAt INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS playlist_media (
  playlistId TEXT NOT NULL,
  mediaId TEXT NOT NULL,
  sortOrder INTEGER NOT NULL DEFAULT 0,
  addedAt INTEGER NOT NULL,
  PRIMARY KEY (playlistId, mediaId)
);
CREATE INDEX IF NOT EXISTS idx_playlists_sortOrder ON playlists(sortOrder);
CREATE INDEX IF NOT EXISTS idx_playlist_media_playlistId ON playlist_media(playlistId);
CREATE INDEX IF NOT EXISTS idx_playlist_media_mediaId ON playlist_media(mediaId);
";
        cmd.ExecuteNonQuery();

        EnsurePlaylistSchema(connection);
    }

    public void AddMedia(MediaFile media)
    {
        UpsertMediaBatch(new[] { media });
    }

    public void UpsertMediaBatch(IEnumerable<MediaFile> mediaItems)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var tx = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT OR REPLACE INTO media (
  id, path, filename, type, size, width, height, duration, thumbnail, createdAt, modifiedAt, lastPlayed, playCount
) VALUES (
  $id, $path, $filename, $type, $size, $width, $height, $duration, $thumbnail, $createdAt, $modifiedAt, $lastPlayed, $playCount
);
";

        var idParam = cmd.Parameters.Add("$id", SqliteType.Text);
        var pathParam = cmd.Parameters.Add("$path", SqliteType.Text);
        var fileNameParam = cmd.Parameters.Add("$filename", SqliteType.Text);
        var typeParam = cmd.Parameters.Add("$type", SqliteType.Text);
        var sizeParam = cmd.Parameters.Add("$size", SqliteType.Integer);
        var widthParam = cmd.Parameters.Add("$width", SqliteType.Integer);
        var heightParam = cmd.Parameters.Add("$height", SqliteType.Integer);
        var durationParam = cmd.Parameters.Add("$duration", SqliteType.Real);
        var thumbnailParam = cmd.Parameters.Add("$thumbnail", SqliteType.Text);
        var createdAtParam = cmd.Parameters.Add("$createdAt", SqliteType.Integer);
        var modifiedAtParam = cmd.Parameters.Add("$modifiedAt", SqliteType.Integer);
        var lastPlayedParam = cmd.Parameters.Add("$lastPlayed", SqliteType.Integer);
        var playCountParam = cmd.Parameters.Add("$playCount", SqliteType.Integer);

        foreach (var media in mediaItems)
        {
            idParam.Value = media.Id;
            pathParam.Value = media.Path;
            fileNameParam.Value = media.FileName;
            typeParam.Value = media.Type.ToString().ToLowerInvariant();
            sizeParam.Value = media.Size;
            widthParam.Value = (object?)media.Width ?? DBNull.Value;
            heightParam.Value = (object?)media.Height ?? DBNull.Value;
            durationParam.Value = (object?)media.Duration ?? DBNull.Value;
            thumbnailParam.Value = (object?)media.Thumbnail ?? DBNull.Value;
            createdAtParam.Value = media.CreatedAt;
            modifiedAtParam.Value = media.ModifiedAt;
            lastPlayedParam.Value = (object?)media.LastPlayed ?? DBNull.Value;
            playCountParam.Value = (object?)media.PlayCount ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public IReadOnlyList<MediaFile> GetAllMedia()
    {
        return QueryMediaPage(new MediaQuery
        {
            Offset = 0,
            Limit = int.MaxValue,
            SortField = MediaSortField.ModifiedAt,
            SortOrder = MediaSortOrder.Desc
        }).Items;
    }

    public MediaPageResult QueryMediaPage(MediaQuery query)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var normalizedQuery = query.SearchText.Trim();
        var normalizedTags = query.SelectedTags
            .Select(tag => tag.Trim())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var excludedFolders = query.ExcludedFolderPaths
            .Select(path => path.Trim())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var limit = query.Limit <= 0 ? 200 : query.Limit;
        var fetchLimit = limit == int.MaxValue ? limit : limit + 1;

        using var cmd = connection.CreateCommand();
        BuildQueryCommand(cmd, normalizedQuery, normalizedTags, excludedFolders, query.PlaylistId, query.SortField, query.SortOrder, query.Offset, fetchLimit);

        using var reader = cmd.ExecuteReader();
        var items = new List<MediaFile>();
        while (reader.Read())
        {
            items.Add(ReadMedia(reader));
        }

        var hasMore = items.Count > limit;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        AttachTags(connection, items);

        return new MediaPageResult
        {
            Items = items,
            HasMore = hasMore
        };
    }

    public MediaFile? GetMediaById(string id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, path, filename, type, size, width, height, duration, thumbnail, createdAt, modifiedAt, lastPlayed, playCount
FROM media
WHERE id = $id;
";
        cmd.Parameters.AddWithValue("$id", id);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var media = ReadMedia(reader);
        media.Tags = GetTags(media.Id).ToList();
        return media;
    }

    public void DeleteMedia(string id)
    {
        DeleteMedia(new[] { id });
    }

    public void DeleteMedia(IEnumerable<string> ids)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var tx = connection.BeginTransaction();
        using var playlistCmd = connection.CreateCommand();
        using var tagCmd = connection.CreateCommand();
        using var mediaCmd = connection.CreateCommand();

        playlistCmd.CommandText = "DELETE FROM playlist_media WHERE mediaId = $id;";
        tagCmd.CommandText = "DELETE FROM tags WHERE mediaId = $id;";
        mediaCmd.CommandText = "DELETE FROM media WHERE id = $id;";

        var playlistIdParam = playlistCmd.Parameters.Add("$id", SqliteType.Text);
        var tagIdParam = tagCmd.Parameters.Add("$id", SqliteType.Text);
        var mediaIdParam = mediaCmd.Parameters.Add("$id", SqliteType.Text);

        foreach (var id in ids.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            playlistIdParam.Value = id;
            tagIdParam.Value = id;
            mediaIdParam.Value = id;
            playlistCmd.ExecuteNonQuery();
            tagCmd.ExecuteNonQuery();
            mediaCmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void ClearAllMedia(bool includePlaylists = false)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var tx = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = includePlaylists
            ? "DELETE FROM playlist_media; DELETE FROM playlists; DELETE FROM tags; DELETE FROM media;"
            : "DELETE FROM playlist_media; DELETE FROM tags; DELETE FROM media;";
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    public IReadOnlyList<MediaEntryRef> GetMediaEntriesUnderFolders(IReadOnlyList<string> folderPaths)
    {
        if (folderPaths.Count == 0)
        {
            return Array.Empty<MediaEntryRef>();
        }

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        var filters = new List<string>(folderPaths.Count);
        for (var i = 0; i < folderPaths.Count; i++)
        {
            var parameterName = $"$folder{i}";
            filters.Add($"path LIKE {parameterName} COLLATE NOCASE");
            cmd.Parameters.AddWithValue(parameterName, $"{folderPaths[i].TrimEnd('/')}/%");
        }

        cmd.CommandText = $@"
SELECT id, path
FROM media
WHERE {string.Join(" OR ", filters)};
";

        using var reader = cmd.ExecuteReader();
        var results = new List<MediaEntryRef>();
        while (reader.Read())
        {
            results.Add(new MediaEntryRef(reader.GetString(0), reader.GetString(1)));
        }

        return results;
    }

    public void AddTag(string mediaId, string tag)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

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
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM tags WHERE mediaId = $mediaId AND name = $name;";
        cmd.Parameters.AddWithValue("$mediaId", mediaId);
        cmd.Parameters.AddWithValue("$name", tag);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<string> GetTags(string mediaId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

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

    public IReadOnlyList<string> GetAllTags()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT name FROM tags ORDER BY name;";
        using var reader = cmd.ExecuteReader();
        var tags = new List<string>();
        while (reader.Read())
        {
            tags.Add(reader.GetString(0));
        }
        return tags;
    }

    public IReadOnlyList<Playlist> GetPlaylists()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

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

        return playlists;
    }

    public Playlist CreatePlaylist(string name)
    {
        var normalized = name.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("播放列表名称不能为空。", nameof(name));
        }

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

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

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE playlists SET name = $name WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", playlistId);
        cmd.Parameters.AddWithValue("$name", normalized);
        cmd.ExecuteNonQuery();
    }

    public void SetPlaylistColor(string playlistId, string? colorHex)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            throw new ArgumentException("播放列表 ID 不能为空。", nameof(playlistId));
        }

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE playlists SET color = $color WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", playlistId);
        cmd.Parameters.AddWithValue("$color", string.IsNullOrWhiteSpace(colorHex) ? DBNull.Value : colorHex.Trim());
        cmd.ExecuteNonQuery();
    }

    public void DeletePlaylist(string playlistId)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return;
        }

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

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
    }

    public void UpdatePlaylistOrder(IReadOnlyList<string> playlistIds)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

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
            return;
        }

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

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
    }

    public void RemoveMediaFromPlaylist(string playlistId, IEnumerable<string> mediaIds)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return;
        }

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var tx = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM playlist_media WHERE playlistId = $playlistId AND mediaId = $mediaId;";
        var playlistParam = cmd.Parameters.Add("$playlistId", SqliteType.Text);
        var mediaParam = cmd.Parameters.Add("$mediaId", SqliteType.Text);

        foreach (var mediaId in mediaIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            playlistParam.Value = playlistId;
            mediaParam.Value = mediaId;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static MediaFile ReadMedia(SqliteDataReader reader)
    {
        return new MediaFile
        {
            Id = reader.GetString(0),
            Path = reader.GetString(1),
            FileName = reader.GetString(2),
            Type = reader.GetString(3) == "video" ? MediaType.Video : MediaType.Image,
            Size = reader.GetInt64(4),
            Width = reader.IsDBNull(5) ? null : reader.GetInt32(5),
            Height = reader.IsDBNull(6) ? null : reader.GetInt32(6),
            Duration = reader.IsDBNull(7) ? null : reader.GetDouble(7),
            Thumbnail = reader.IsDBNull(8) ? null : reader.GetString(8),
            CreatedAt = reader.GetInt64(9),
            ModifiedAt = reader.GetInt64(10),
            LastPlayed = reader.IsDBNull(11) ? null : reader.GetInt64(11),
            PlayCount = reader.IsDBNull(12) ? null : reader.GetInt32(12)
        };
    }

    private static void EnsurePlaylistSchema(SqliteConnection connection)
    {
        using var columnCmd = connection.CreateCommand();
        columnCmd.CommandText = "PRAGMA table_info(playlists);";

        var hasColorColumn = false;
        using (var reader = columnCmd.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "color", StringComparison.OrdinalIgnoreCase))
                {
                    hasColorColumn = true;
                    break;
                }
            }
        }

        if (hasColorColumn)
        {
            return;
        }

        using var alterCmd = connection.CreateCommand();
        alterCmd.CommandText = "ALTER TABLE playlists ADD COLUMN color TEXT;";
        alterCmd.ExecuteNonQuery();
    }

    private static void BuildQueryCommand(
        SqliteCommand cmd,
        string searchText,
        IReadOnlyList<string> selectedTags,
        IReadOnlyList<string> excludedFolders,
        string? playlistId,
        MediaSortField sortField,
        MediaSortOrder sortOrder,
        int offset,
        int limit)
    {
        var sql = new StringBuilder(@"
SELECT id, path, filename, type, size, width, height, duration, thumbnail, createdAt, modifiedAt, lastPlayed, playCount
FROM media m
");

        if (!string.IsNullOrWhiteSpace(playlistId))
        {
            sql.Append(@"
INNER JOIN playlist_media pm
  ON pm.mediaId = m.id
 AND pm.playlistId = $playlistId
");
            cmd.Parameters.AddWithValue("$playlistId", playlistId);
        }

        sql.Append("WHERE 1 = 1\n");

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            sql.Append(@"
  AND (
    m.filename LIKE $search COLLATE NOCASE
    OR EXISTS (
      SELECT 1
      FROM tags searchTags
      WHERE searchTags.mediaId = m.id
        AND searchTags.name LIKE $search COLLATE NOCASE
    )
  )
");
            cmd.Parameters.AddWithValue("$search", $"%{searchText}%");
        }

        for (var i = 0; i < selectedTags.Count; i++)
        {
            var parameterName = $"$tag{i}";
            sql.Append($@"
  AND EXISTS (
    SELECT 1
    FROM tags tag{i}
    WHERE tag{i}.mediaId = m.id
      AND tag{i}.name = {parameterName} COLLATE NOCASE
  )
");
            cmd.Parameters.AddWithValue(parameterName, selectedTags[i]);
        }

        for (var i = 0; i < excludedFolders.Count; i++)
        {
            var parameterName = $"$excludedFolder{i}";
            sql.Append($@"
  AND m.path NOT LIKE {parameterName} COLLATE NOCASE
");
            cmd.Parameters.AddWithValue(parameterName, $"{excludedFolders[i].TrimEnd('/')}/%");
        }

        sql.Append("ORDER BY ");
        sql.Append(sortField == MediaSortField.FileName ? "m.filename COLLATE NOCASE" : "m.modifiedAt");
        sql.Append(sortOrder == MediaSortOrder.Asc ? " ASC" : " DESC");
        sql.Append(", m.id ASC ");
        sql.Append("LIMIT $limit OFFSET $offset;");

        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        cmd.CommandText = sql.ToString();
    }

    private static void AttachTags(SqliteConnection connection, List<MediaFile> media)
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
    }
}
