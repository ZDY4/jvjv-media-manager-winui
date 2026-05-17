using Microsoft.Data.Sqlite;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.Data;

internal static class MediaDbSchema
{
    private const string CreateSchemaSql = @"
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
CREATE INDEX IF NOT EXISTS idx_media_type_nocase ON media(type COLLATE NOCASE);
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

    public static void Initialize(SqliteConnection connection)
    {
        AppTraceLogger.Log("MediaDbSchema", "Initialize start.");
        ApplyConnectionPragmas(connection);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = CreateSchemaSql;
        cmd.ExecuteNonQuery();

        EnsurePlaylistColorColumn(connection);
        AppTraceLogger.Log("MediaDbSchema", "Initialize completed.");
    }

    private static void ApplyConnectionPragmas(SqliteConnection connection)
    {
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
    }

    private static void EnsurePlaylistColorColumn(SqliteConnection connection)
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
            AppTraceLogger.LogSampled("MediaDbSchema", "playlist-color-column-exists", "Playlist color column already exists.", TimeSpan.FromMinutes(1));
            return;
        }

        using var alterCmd = connection.CreateCommand();
        alterCmd.CommandText = "ALTER TABLE playlists ADD COLUMN color TEXT;";
        alterCmd.ExecuteNonQuery();
        AppTraceLogger.Log("MediaDbSchema", "Added missing playlist color column.");
    }
}
