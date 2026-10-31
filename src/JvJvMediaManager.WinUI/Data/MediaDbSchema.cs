using Microsoft.Data.Sqlite;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.Data;

internal static class MediaDbSchema
{
    // Keep schema evolution explicit. Version 1 is the legacy schema that was
    // previously assembled by Ensure* helpers on every startup. Version 2
    // records migration history so future changes can be applied once and
    // diagnosed without re-running the entire compatibility path.
    private const int CurrentSchemaVersion = 2;

    private const string CreateSchemaSql = @"
CREATE TABLE IF NOT EXISTS media (
  id TEXT PRIMARY KEY,
  path TEXT NOT NULL,
  folderPath TEXT NOT NULL DEFAULT '',
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
  playCount INTEGER,
  contentHash TEXT,
  fingerprintVersion INTEGER NOT NULL DEFAULT 0,
  rotation INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS media_relocations (
  mediaId TEXT PRIMARY KEY,
  path TEXT,
  filename TEXT,
  type TEXT,
  size INTEGER,
  modifiedAt INTEGER,
  contentHash TEXT NOT NULL,
  fingerprintVersion INTEGER NOT NULL,
  createdAt INTEGER NOT NULL
);
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
";

    public static void Initialize(SqliteConnection connection)
    {
        AppTraceLogger.Log("MediaDbSchema", "Initialize start.");
        InitializeWalJournalMode(connection);

        var previousVersion = GetUserVersion(connection);
        if (previousVersion > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"数据库版本 {previousVersion} 高于当前应用支持的版本 {CurrentSchemaVersion}。请先升级应用。" );
        }

        using (var tx = connection.BeginTransaction())
        {
            if (previousVersion < 1)
            {
                ApplySchemaVersion1(connection, tx);
            }

            if (previousVersion < 2)
            {
                ApplySchemaVersion2(connection, tx);
            }

            SetUserVersion(connection, tx, CurrentSchemaVersion);
            tx.Commit();
        }

        AppTraceLogger.Log("MediaDbSchema", $"Initialize completed (schema only). PreviousVersion={previousVersion}, CurrentVersion={CurrentSchemaVersion}. Heavy maintenance runs on a background thread (RunStartupMaintenance).");
    }

    private static void ApplySchemaVersion1(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = CreateSchemaSql;
        cmd.ExecuteNonQuery();

        // These helpers remain idempotent so databases created by older
        // previews (which left user_version at 0) can still be upgraded.
        EnsureMediaCoreColumns(connection, transaction);
        EnsurePlaylistCoreColumns(connection, transaction);
        EnsurePlaylistMediaCoreColumns(connection, transaction);
        EnsureTagIndexes(connection, transaction);
        EnsurePlaylistColorColumn(connection, transaction);
        EnsureMediaFingerprintColumns(connection, transaction);
        EnsureMediaRelocationColumns(connection, transaction);
        EnsurePlaylistIndexes(connection, transaction);
        AppTraceLogger.Log("MediaDbSchema", "Applied schema migration version 1.");
    }

    private static void ApplySchemaVersion2(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var cmd = CreateCommand(connection, transaction);
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS schema_migrations (
  version INTEGER PRIMARY KEY,
  appliedAt INTEGER NOT NULL
);
INSERT OR IGNORE INTO schema_migrations(version, appliedAt)
VALUES (1, unixepoch());
INSERT OR IGNORE INTO schema_migrations(version, appliedAt)
VALUES (2, unixepoch());
";
        cmd.ExecuteNonQuery();
        AppTraceLogger.Log("MediaDbSchema", "Applied schema migration version 2 and recorded migration history.");
    }

    /// <summary>
    /// WP-A: heavy one-time startup work (duplicate path merge, FTS rebuild,
    /// sort-tuple indexes, orphan audit). Runs on a background thread after the
    /// schema DDL so first paint is not blocked on large libraries. Safe to run
    /// on any connection; the schema DDL above must have completed first.
    /// </summary>
    public static void RunStartupMaintenance(SqliteConnection connection)
    {
        AppTraceLogger.Log("MediaDbSchema", "Startup maintenance start.");
        var mergedDuplicatePaths = MergeDuplicateMediaPaths(connection);
        EnsureMediaSearchIndex(connection, forceRebuild: mergedDuplicatePaths);
        EnsureMediaSortIndexes(connection);
        AuditOrphanRows(connection);
        AppTraceLogger.Log("MediaDbSchema", "Startup maintenance completed.");
    }

    private static void AuditOrphanRows(SqliteConnection connection)
    {
        // WP-02 step 1: enable FK enforcement (done per-connection) and audit
        // orphan data before declaring real foreign keys with ON DELETE CASCADE
        // in a future table-rebuild migration. This audit is diagnostic only.
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT
  (SELECT COUNT(*) FROM tags t WHERE NOT EXISTS (SELECT 1 FROM media m WHERE m.id = t.mediaId)),
  (SELECT COUNT(*) FROM playlist_media pm WHERE NOT EXISTS (SELECT 1 FROM media m WHERE m.id = pm.mediaId)),
  (SELECT COUNT(*) FROM playlist_media pm WHERE NOT EXISTS (SELECT 1 FROM playlists p WHERE p.id = pm.playlistId));
";
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return;
            }

            var orphanTags = reader.GetInt64(0);
            var orphanPlaylistMediaByMedia = reader.GetInt64(1);
            var orphanPlaylistMediaByPlaylist = reader.GetInt64(2);
            if (orphanTags > 0 || orphanPlaylistMediaByMedia > 0 || orphanPlaylistMediaByPlaylist > 0)
            {
                AppTraceLogger.Log(
                    "MediaDbSchema",
                    $"Orphan row audit found dangling references. OrphanTags={orphanTags}, OrphanPlaylistMediaByMedia={orphanPlaylistMediaByMedia}, OrphanPlaylistMediaByPlaylist={orphanPlaylistMediaByPlaylist}.");
            }
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("MediaDbSchema", "Orphan row audit failed.", ex);
        }
    }

    private static void InitializeWalJournalMode(SqliteConnection connection)
    {
        // WAL is a persistent, database-level journal mode. It only needs to be
        // set once during initialization/migration; per-connection PRAGMAs
        // (busy_timeout, foreign_keys, synchronous) are applied by the
        // connection factory. Verify the actual result and log any downgrade.
        try
        {
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            var appliedMode = pragma.ExecuteScalar() as string ?? "<unknown>";
            if (!string.Equals(appliedMode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                AppTraceLogger.Log(
                    "MediaDbSchema",
                    $"WAL journal mode was not enabled; continuing in degraded mode. AppliedMode='{appliedMode}'.");
            }
            else
            {
                AppTraceLogger.Log("MediaDbSchema", "WAL journal mode enabled.");
            }
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("MediaDbSchema", "Failed to configure WAL journal mode; continuing with default journaling.", ex);
        }
    }

    private static void EnsureMediaCoreColumns(SqliteConnection connection, SqliteTransaction transaction)
    {
        var columns = GetColumns(connection, transaction, "media");
        var columnDefinitions = new (string Name, string Definition)[]
        {
            ("path", "TEXT NOT NULL DEFAULT ''"),
            ("folderPath", "TEXT NOT NULL DEFAULT ''"),
            ("filename", "TEXT NOT NULL DEFAULT ''"),
            ("type", "TEXT NOT NULL DEFAULT 'image'"),
            ("size", "INTEGER NOT NULL DEFAULT 0"),
            ("width", "INTEGER"),
            ("height", "INTEGER"),
            ("duration", "REAL"),
            ("thumbnail", "TEXT"),
            ("createdAt", "INTEGER NOT NULL DEFAULT 0"),
            ("modifiedAt", "INTEGER NOT NULL DEFAULT 0"),
            ("lastPlayed", "INTEGER"),
            ("playCount", "INTEGER"),
            ("rotation", "INTEGER NOT NULL DEFAULT 0")
        };

        foreach (var (name, definition) in columnDefinitions)
        {
            if (columns.Contains(name))
            {
                continue;
            }

            using var alterCmd = CreateCommand(connection, transaction);
            alterCmd.CommandText = $"ALTER TABLE media ADD COLUMN {name} {definition};";
            alterCmd.ExecuteNonQuery();
            AppTraceLogger.Log("MediaDbSchema", $"Added missing media {name} column.");
        }

        using var indexCmd = CreateCommand(connection, transaction);
        indexCmd.CommandText = @"
CREATE INDEX IF NOT EXISTS idx_media_modifiedAt ON media(modifiedAt DESC);
CREATE INDEX IF NOT EXISTS idx_media_filename_nocase ON media(filename COLLATE NOCASE);
CREATE INDEX IF NOT EXISTS idx_media_path_nocase ON media(path COLLATE NOCASE);
CREATE INDEX IF NOT EXISTS idx_media_folderPath_nocase ON media(folderPath COLLATE NOCASE);
CREATE INDEX IF NOT EXISTS idx_media_folderPath_id ON media(folderPath COLLATE NOCASE, id);
CREATE INDEX IF NOT EXISTS idx_media_type_nocase ON media(type COLLATE NOCASE);
";
        indexCmd.ExecuteNonQuery();

        using var backfillCmd = CreateCommand(connection, transaction);
        backfillCmd.CommandText = @"
UPDATE media
SET folderPath = substr(path, 1, max(length(path) - length(filename) - 1, 0))
WHERE folderPath IS NULL OR folderPath = '';
";
        var updated = backfillCmd.ExecuteNonQuery();
        if (updated > 0)
        {
            AppTraceLogger.Log("MediaDbSchema", $"Backfilled media folderPath column. Updated={updated}.");
        }
    }

    /// <summary>
    /// WP-C: composite indexes matching the page ORDER BY tuple
    /// (folderPath COLLATE NOCASE ASC, &lt;sortField&gt;, id ASC) so page queries do
    /// not re-sort the whole result set per page. Created during startup
    /// maintenance (background) because index builds on large libraries are slow.
    /// </summary>
    private static void EnsureMediaSortIndexes(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
CREATE INDEX IF NOT EXISTS idx_media_folder_filename_id ON media(folderPath COLLATE NOCASE, filename COLLATE NOCASE, id);
CREATE INDEX IF NOT EXISTS idx_media_folder_modifiedAt_id ON media(folderPath COLLATE NOCASE, modifiedAt, id);
CREATE INDEX IF NOT EXISTS idx_media_folder_type_id ON media(folderPath COLLATE NOCASE, type COLLATE NOCASE, id);
CREATE INDEX IF NOT EXISTS idx_media_folder_size_id ON media(folderPath COLLATE NOCASE, size, id);
CREATE INDEX IF NOT EXISTS idx_media_folder_duration_id ON media(folderPath COLLATE NOCASE, COALESCE(duration, 0), id);
";
        cmd.ExecuteNonQuery();
        AppTraceLogger.Log("MediaDbSchema", "Ensured media sort-tuple indexes.");
    }

    private static void EnsurePlaylistColorColumn(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var columnCmd = CreateCommand(connection, transaction);
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

        using var alterCmd = CreateCommand(connection, transaction);
        alterCmd.CommandText = "ALTER TABLE playlists ADD COLUMN color TEXT;";
        alterCmd.ExecuteNonQuery();
        AppTraceLogger.Log("MediaDbSchema", "Added missing playlist color column.");
    }

    private static void EnsurePlaylistCoreColumns(SqliteConnection connection, SqliteTransaction transaction)
    {
        var columns = GetColumns(connection, transaction, "playlists");
        var columnDefinitions = new (string Name, string Definition)[]
        {
            ("name", "TEXT NOT NULL DEFAULT ''"),
            ("sortOrder", "INTEGER NOT NULL DEFAULT 0"),
            ("createdAt", "INTEGER NOT NULL DEFAULT 0")
        };

        foreach (var (name, definition) in columnDefinitions)
        {
            if (columns.Contains(name))
            {
                continue;
            }

            using var alterCmd = CreateCommand(connection, transaction);
            alterCmd.CommandText = $"ALTER TABLE playlists ADD COLUMN {name} {definition};";
            alterCmd.ExecuteNonQuery();
            AppTraceLogger.Log("MediaDbSchema", $"Added missing playlists {name} column.");
        }
    }

    private static void EnsurePlaylistMediaCoreColumns(SqliteConnection connection, SqliteTransaction transaction)
    {
        var columns = GetColumns(connection, transaction, "playlist_media");
        var columnDefinitions = new (string Name, string Definition)[]
        {
            ("playlistId", "TEXT NOT NULL DEFAULT ''"),
            ("mediaId", "TEXT NOT NULL DEFAULT ''"),
            ("sortOrder", "INTEGER NOT NULL DEFAULT 0"),
            ("addedAt", "INTEGER NOT NULL DEFAULT 0")
        };

        foreach (var (name, definition) in columnDefinitions)
        {
            if (columns.Contains(name))
            {
                continue;
            }

            using var alterCmd = CreateCommand(connection, transaction);
            alterCmd.CommandText = $"ALTER TABLE playlist_media ADD COLUMN {name} {definition};";
            alterCmd.ExecuteNonQuery();
            AppTraceLogger.Log("MediaDbSchema", $"Added missing playlist_media {name} column.");
        }
    }

    private static void EnsureTagIndexes(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var indexCmd = CreateCommand(connection, transaction);
        indexCmd.CommandText = @"
CREATE INDEX IF NOT EXISTS idx_tags_mediaId ON tags(mediaId);
CREATE INDEX IF NOT EXISTS idx_tags_name ON tags(name);
CREATE INDEX IF NOT EXISTS idx_tags_name_nocase ON tags(name COLLATE NOCASE);
CREATE INDEX IF NOT EXISTS idx_tags_mediaId_name_nocase ON tags(mediaId, name COLLATE NOCASE);
";
        indexCmd.ExecuteNonQuery();
    }

    private static void EnsureMediaFingerprintColumns(SqliteConnection connection, SqliteTransaction transaction)
    {
        var columns = GetColumns(connection, transaction, "media");

        if (!columns.Contains("contentHash"))
        {
            using var alterHashCmd = CreateCommand(connection, transaction);
            alterHashCmd.CommandText = "ALTER TABLE media ADD COLUMN contentHash TEXT;";
            alterHashCmd.ExecuteNonQuery();
            AppTraceLogger.Log("MediaDbSchema", "Added missing media contentHash column.");
        }

        if (!columns.Contains("fingerprintVersion"))
        {
            using var alterVersionCmd = CreateCommand(connection, transaction);
            alterVersionCmd.CommandText = "ALTER TABLE media ADD COLUMN fingerprintVersion INTEGER NOT NULL DEFAULT 0;";
            alterVersionCmd.ExecuteNonQuery();
            AppTraceLogger.Log("MediaDbSchema", "Added missing media fingerprintVersion column.");
        }

        using var indexCmd = CreateCommand(connection, transaction);
        indexCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_media_contentHash ON media(contentHash);";
        indexCmd.ExecuteNonQuery();
    }

    private static void EnsureMediaRelocationColumns(SqliteConnection connection, SqliteTransaction transaction)
    {
        var columns = GetColumns(connection, transaction, "media_relocations");
        var columnDefinitions = new (string Name, string Definition)[]
        {
            ("path", "TEXT"),
            ("filename", "TEXT"),
            ("type", "TEXT"),
            ("size", "INTEGER"),
            ("modifiedAt", "INTEGER"),
            ("contentHash", "TEXT"),
            ("fingerprintVersion", "INTEGER NOT NULL DEFAULT 0"),
            ("createdAt", "INTEGER NOT NULL DEFAULT 0")
        };

        foreach (var (name, definition) in columnDefinitions)
        {
            if (columns.Contains(name))
            {
                continue;
            }

            using var alterCmd = CreateCommand(connection, transaction);
            alterCmd.CommandText = $"ALTER TABLE media_relocations ADD COLUMN {name} {definition};";
            alterCmd.ExecuteNonQuery();
            AppTraceLogger.Log("MediaDbSchema", $"Added missing media_relocations {name} column.");
        }

        using var indexCmd = CreateCommand(connection, transaction);
        indexCmd.CommandText = @"
CREATE INDEX IF NOT EXISTS idx_media_relocations_contentHash ON media_relocations(contentHash, fingerprintVersion);
CREATE INDEX IF NOT EXISTS idx_media_relocations_weak ON media_relocations(size, modifiedAt, type, filename COLLATE NOCASE);
";
        indexCmd.ExecuteNonQuery();
    }

    private static void EnsurePlaylistIndexes(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var indexCmd = CreateCommand(connection, transaction);
        indexCmd.CommandText = @"
CREATE INDEX IF NOT EXISTS idx_playlists_sortOrder ON playlists(sortOrder);
CREATE INDEX IF NOT EXISTS idx_playlist_media_playlistId ON playlist_media(playlistId);
CREATE INDEX IF NOT EXISTS idx_playlist_media_mediaId ON playlist_media(mediaId);
";
        indexCmd.ExecuteNonQuery();
    }

    private static bool MergeDuplicateMediaPaths(SqliteConnection connection)
    {
        using var findCmd = connection.CreateCommand();
        findCmd.CommandText = @"
SELECT lower(path) AS normalizedPath,
       group_concat(id, char(31)) AS ids
FROM media
GROUP BY normalizedPath
HAVING COUNT(*) > 1;
";

        var duplicateGroups = new List<List<string>>();
        using (var reader = findCmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var ids = reader.IsDBNull(1)
                    ? string.Empty
                    : reader.GetString(1);
                var group = ids
                    .Split((char)31, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (group.Count > 1)
                {
                    duplicateGroups.Add(group);
                }
            }
        }

        if (duplicateGroups.Count == 0)
        {
            return false;
        }

        var hasMediaSearchTable = TableExists(connection, "media_search");
        using var tx = connection.BeginTransaction();
        using var chooseCmd = connection.CreateCommand();
        chooseCmd.Transaction = tx;
        chooseCmd.CommandText = @"
SELECT id
FROM media
WHERE id IN ({0})
ORDER BY modifiedAt DESC, createdAt DESC, id ASC
LIMIT 1;
";

        using var mergeTagsCmd = connection.CreateCommand();
        mergeTagsCmd.Transaction = tx;
        mergeTagsCmd.CommandText = @"
INSERT OR IGNORE INTO tags(mediaId, name, createdAt)
SELECT $keepId, name, createdAt
FROM tags
WHERE mediaId = $dropId;
";
        var mergeTagsKeepParam = mergeTagsCmd.Parameters.Add("$keepId", SqliteType.Text);
        var mergeTagsDropParam = mergeTagsCmd.Parameters.Add("$dropId", SqliteType.Text);

        using var mergePlaylistCmd = connection.CreateCommand();
        mergePlaylistCmd.Transaction = tx;
        mergePlaylistCmd.CommandText = @"
INSERT OR IGNORE INTO playlist_media(playlistId, mediaId, sortOrder, addedAt)
SELECT playlistId, $keepId, sortOrder, addedAt
FROM playlist_media
WHERE mediaId = $dropId;
";
        var mergePlaylistKeepParam = mergePlaylistCmd.Parameters.Add("$keepId", SqliteType.Text);
        var mergePlaylistDropParam = mergePlaylistCmd.Parameters.Add("$dropId", SqliteType.Text);

        using var deleteTagsCmd = connection.CreateCommand();
        deleteTagsCmd.Transaction = tx;
        deleteTagsCmd.CommandText = "DELETE FROM tags WHERE mediaId = $dropId;";
        var deleteTagsDropParam = deleteTagsCmd.Parameters.Add("$dropId", SqliteType.Text);

        using var deletePlaylistCmd = connection.CreateCommand();
        deletePlaylistCmd.Transaction = tx;
        deletePlaylistCmd.CommandText = "DELETE FROM playlist_media WHERE mediaId = $dropId;";
        var deletePlaylistDropParam = deletePlaylistCmd.Parameters.Add("$dropId", SqliteType.Text);

        using var deleteSearchCmd = connection.CreateCommand();
        deleteSearchCmd.Transaction = tx;
        deleteSearchCmd.CommandText = "DELETE FROM media_search WHERE mediaId = $dropId;";
        var deleteSearchDropParam = deleteSearchCmd.Parameters.Add("$dropId", SqliteType.Text);

        using var deleteRelocationCmd = connection.CreateCommand();
        deleteRelocationCmd.Transaction = tx;
        deleteRelocationCmd.CommandText = "DELETE FROM media_relocations WHERE mediaId = $dropId;";
        var deleteRelocationDropParam = deleteRelocationCmd.Parameters.Add("$dropId", SqliteType.Text);

        using var deleteMediaCmd = connection.CreateCommand();
        deleteMediaCmd.Transaction = tx;
        deleteMediaCmd.CommandText = "DELETE FROM media WHERE id = $dropId;";
        var deleteMediaDropParam = deleteMediaCmd.Parameters.Add("$dropId", SqliteType.Text);

        var droppedCount = 0;
        foreach (var group in duplicateGroups)
        {
            var parameterNames = new List<string>();
            chooseCmd.Parameters.Clear();
            for (var index = 0; index < group.Count; index++)
            {
                var parameterName = $"$id{index}";
                parameterNames.Add(parameterName);
                chooseCmd.Parameters.AddWithValue(parameterName, group[index]);
            }

            chooseCmd.CommandText = string.Format(
                @"SELECT id
FROM media
WHERE id IN ({0})
ORDER BY modifiedAt DESC, createdAt DESC, id ASC
LIMIT 1;",
                string.Join(", ", parameterNames));

            var keepId = chooseCmd.ExecuteScalar() as string;
            if (string.IsNullOrWhiteSpace(keepId))
            {
                continue;
            }

            foreach (var dropId in group.Where(id => !string.Equals(id, keepId, StringComparison.Ordinal)))
            {
                mergeTagsKeepParam.Value = keepId;
                mergeTagsDropParam.Value = dropId;
                mergeTagsCmd.ExecuteNonQuery();

                mergePlaylistKeepParam.Value = keepId;
                mergePlaylistDropParam.Value = dropId;
                mergePlaylistCmd.ExecuteNonQuery();

                deleteTagsDropParam.Value = dropId;
                deleteTagsCmd.ExecuteNonQuery();

                deletePlaylistDropParam.Value = dropId;
                deletePlaylistCmd.ExecuteNonQuery();

                if (hasMediaSearchTable)
                {
                    deleteSearchDropParam.Value = dropId;
                    deleteSearchCmd.ExecuteNonQuery();
                }

                deleteRelocationDropParam.Value = dropId;
                deleteRelocationCmd.ExecuteNonQuery();

                deleteMediaDropParam.Value = dropId;
                deleteMediaCmd.ExecuteNonQuery();
                droppedCount++;
            }
        }

        tx.Commit();
        AppTraceLogger.Log("MediaDbSchema", $"Merged duplicate media paths. Groups={duplicateGroups.Count}, Dropped={droppedCount}.");
        return droppedCount > 0;
    }

    private static void EnsureMediaSearchIndex(SqliteConnection connection, bool forceRebuild = false)
    {
        try
        {
            using var createCmd = connection.CreateCommand();
            createCmd.CommandText = @"
CREATE VIRTUAL TABLE IF NOT EXISTS media_search USING fts5(
  mediaId UNINDEXED,
  filename,
  tags,
  tokenize = 'unicode61'
);
";
            createCmd.ExecuteNonQuery();

            using var countCmd = connection.CreateCommand();
            countCmd.CommandText = "SELECT (SELECT COUNT(*) FROM media_search), (SELECT COUNT(DISTINCT mediaId) FROM media_search), (SELECT COUNT(*) FROM media);";
            using var reader = countCmd.ExecuteReader();
            if (!reader.Read())
            {
                return;
            }

            var searchRowCount = reader.GetInt64(0);
            var indexedMediaCount = reader.GetInt64(1);
            var mediaCount = reader.GetInt64(2);
            if (!forceRebuild && searchRowCount == mediaCount && indexedMediaCount == mediaCount)
            {
                return;
            }

            using var rebuildCmd = connection.CreateCommand();
            rebuildCmd.CommandText = @"
DELETE FROM media_search;
INSERT INTO media_search(mediaId, filename, tags)
SELECT m.id, m.filename, COALESCE(group_concat(t.name, ' '), '')
FROM media m
LEFT JOIN tags t ON t.mediaId = m.id
GROUP BY m.id, m.filename;
";
            rebuildCmd.ExecuteNonQuery();
            AppTraceLogger.Log("MediaDbSchema", "Rebuilt media_search FTS index.");
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("MediaDbSchema", "Media search FTS index initialization failed; falling back to LIKE search.", ex);
        }
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type IN ('table', 'view') AND name = $name LIMIT 1;";
        cmd.Parameters.AddWithValue("$name", tableName);
        return cmd.ExecuteScalar() != null;
    }

    private static int GetUserVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    private static void SetUserVersion(SqliteConnection connection, SqliteTransaction transaction, int version)
    {
        using var cmd = CreateCommand(connection, transaction);
        cmd.CommandText = $"PRAGMA user_version = {version};";
        cmd.ExecuteNonQuery();
    }

    private static SqliteCommand CreateCommand(SqliteConnection connection, SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        return command;
    }

    private static HashSet<string> GetColumns(SqliteConnection connection, SqliteTransaction transaction, string tableName)
    {
        using var columnCmd = CreateCommand(connection, transaction);
        columnCmd.CommandText = $"PRAGMA table_info({tableName});";

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = columnCmd.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }
}
