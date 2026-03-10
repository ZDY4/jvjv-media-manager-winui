using Microsoft.Data.Sqlite;
using JvJvMediaManager.Models;

namespace JvJvMediaManager.Data;

public sealed class MediaDb
{
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
CREATE TABLE IF NOT EXISTS tags (
  mediaId TEXT NOT NULL,
  name TEXT NOT NULL,
  createdAt INTEGER NOT NULL,
  PRIMARY KEY (mediaId, name)
);
CREATE INDEX IF NOT EXISTS idx_tags_mediaId ON tags(mediaId);
CREATE INDEX IF NOT EXISTS idx_tags_name ON tags(name);

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
    }

    public void AddMedia(MediaFile media)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT OR REPLACE INTO media (
  id, path, filename, type, size, width, height, duration, thumbnail, createdAt, modifiedAt, lastPlayed, playCount
) VALUES (
  $id, $path, $filename, $type, $size, $width, $height, $duration, $thumbnail, $createdAt, $modifiedAt, $lastPlayed, $playCount
);
";
        cmd.Parameters.AddWithValue("$id", media.Id);
        cmd.Parameters.AddWithValue("$path", media.Path);
        cmd.Parameters.AddWithValue("$filename", media.FileName);
        cmd.Parameters.AddWithValue("$type", media.Type.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$size", media.Size);
        cmd.Parameters.AddWithValue("$width", (object?)media.Width ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$height", (object?)media.Height ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$duration", (object?)media.Duration ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$thumbnail", (object?)media.Thumbnail ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$createdAt", media.CreatedAt);
        cmd.Parameters.AddWithValue("$modifiedAt", media.ModifiedAt);
        cmd.Parameters.AddWithValue("$lastPlayed", (object?)media.LastPlayed ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$playCount", (object?)media.PlayCount ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<MediaFile> GetAllMedia()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, path, filename, type, size, width, height, duration, thumbnail, createdAt, modifiedAt, lastPlayed, playCount
FROM media
ORDER BY createdAt DESC;
";

        using var reader = cmd.ExecuteReader();
        var result = new List<MediaFile>();
        while (reader.Read())
        {
            var media = new MediaFile
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

            media.Tags = GetTags(media.Id).ToList();
            result.Add(media);
        }

        return result;
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

        var media = new MediaFile
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

        media.Tags = GetTags(media.Id).ToList();
        return media;
    }

    public void DeleteMedia(string id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var tx = connection.BeginTransaction();
        using var tagCmd = connection.CreateCommand();
        tagCmd.CommandText = "DELETE FROM tags WHERE mediaId = $id;";
        tagCmd.Parameters.AddWithValue("$id", id);
        tagCmd.ExecuteNonQuery();

        using var mediaCmd = connection.CreateCommand();
        mediaCmd.CommandText = "DELETE FROM media WHERE id = $id;";
        mediaCmd.Parameters.AddWithValue("$id", id);
        mediaCmd.ExecuteNonQuery();

        tx.Commit();
    }

    public void ClearAllMedia()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var tx = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM tags; DELETE FROM media;";
        cmd.ExecuteNonQuery();
        tx.Commit();
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
}
