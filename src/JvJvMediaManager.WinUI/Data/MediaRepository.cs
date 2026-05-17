using System.Text;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using JvJvMediaManager.Models;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.Data;

internal sealed class MediaRepository
{
    private readonly MediaDbConnectionFactory _connections;
    private readonly MediaTagRepository _tags;

    public MediaRepository(MediaDbConnectionFactory connections, MediaTagRepository tags)
    {
        _connections = connections;
        _tags = tags;
    }

    public void AddMedia(MediaFile media)
    {
        UpsertMediaBatch(new[] { media });
    }

    public void UpsertMediaBatch(IEnumerable<MediaFile> mediaItems)
    {
        var items = mediaItems.ToList();
        if (items.Count == 0)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        using var connection = _connections.OpenConnection();

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

        foreach (var media in items)
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
        AppTraceLogger.Log("MediaRepository", $"UpsertMediaBatch completed. Count={items.Count}, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
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
        var stopwatch = Stopwatch.StartNew();
        using var connection = _connections.OpenConnection();

        var normalizedQuery = query.SearchText.Trim();
        var normalizedTags = query.SelectedTags
            .Select(tag => tag.Trim())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var includedFolders = query.IncludedFolderPaths?
            .Select(path => path.Trim())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var excludedFolders = query.ExcludedFolderPaths
            .Select(path => path.Trim())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (includedFolders is { Count: 0 })
        {
            AppTraceLogger.LogSampled(
                "MediaRepository",
                "query-empty-included-folders",
                $"QueryMediaPage short-circuited. IncludedFolders=0, PlaylistId='{query.PlaylistId ?? "<null>"}'.",
                TimeSpan.FromSeconds(2));
            return new MediaPageResult
            {
                Items = Array.Empty<MediaFile>(),
                HasMore = false
            };
        }

        var limit = query.Limit <= 0 ? 200 : query.Limit;
        var fetchLimit = limit == int.MaxValue ? limit : limit + 1;

        using var cmd = connection.CreateCommand();
        BuildQueryCommand(cmd, normalizedQuery, normalizedTags, includedFolders, excludedFolders, query.PlaylistId, query.SortField, query.SortOrder, query.Offset, fetchLimit);

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

        _tags.AttachTags(connection, items);
        AppTraceLogger.LogSampled(
            "MediaRepository",
            "query-media-page",
            $"QueryMediaPage completed. Offset={query.Offset}, Limit={limit}, Returned={items.Count}, HasMore={hasMore}, Tags={normalizedTags.Count}, IncludedFolders={includedFolders?.Count ?? 0}, ExcludedFolders={excludedFolders.Count}, PlaylistId='{query.PlaylistId ?? "<null>"}', ElapsedMs={stopwatch.ElapsedMilliseconds}.",
            TimeSpan.FromSeconds(1));

        return new MediaPageResult
        {
            Items = items,
            HasMore = hasMore
        };
    }

    public IReadOnlyList<MediaFolderSummary> QueryMediaFolderSummaries(MediaQuery query)
    {
        var stopwatch = Stopwatch.StartNew();
        using var connection = _connections.OpenConnection();

        var normalizedQuery = query.SearchText.Trim();
        var normalizedTags = query.SelectedTags
            .Select(tag => tag.Trim())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var includedFolders = query.IncludedFolderPaths?
            .Select(path => path.Trim())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var excludedFolders = query.ExcludedFolderPaths
            .Select(path => path.Trim())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (includedFolders is { Count: 0 })
        {
            return Array.Empty<MediaFolderSummary>();
        }

        using var cmd = connection.CreateCommand();
        BuildFolderSummaryCommand(cmd, normalizedQuery, normalizedTags, includedFolders, excludedFolders, query.PlaylistId);

        using var reader = cmd.ExecuteReader();
        var folders = new List<MediaFolderSummary>();
        while (reader.Read())
        {
            folders.Add(new MediaFolderSummary
            {
                FolderPath = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                Count = reader.GetInt32(1)
            });
        }

        AppTraceLogger.LogSampled(
            "MediaRepository",
            "query-media-folder-summaries",
            $"QueryMediaFolderSummaries completed. FolderCount={folders.Count}, Tags={normalizedTags.Count}, IncludedFolders={includedFolders?.Count ?? 0}, ExcludedFolders={excludedFolders.Count}, PlaylistId='{query.PlaylistId ?? "<null>"}', ElapsedMs={stopwatch.ElapsedMilliseconds}.",
            TimeSpan.FromSeconds(1));

        return folders;
    }

    public MediaFile? GetMediaById(string id)
    {
        var stopwatch = Stopwatch.StartNew();
        using var connection = _connections.OpenConnection();

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
            AppTraceLogger.Log("MediaRepository", $"GetMediaById missed. MediaId='{id}', ElapsedMs={stopwatch.ElapsedMilliseconds}.");
            return null;
        }

        var media = ReadMedia(reader);
        media.Tags = _tags.GetTags(media.Id).ToList();
        AppTraceLogger.LogSampled(
            "MediaRepository",
            "get-media-by-id",
            $"GetMediaById completed. MediaId='{id}', TagCount={media.Tags.Count}, ElapsedMs={stopwatch.ElapsedMilliseconds}.",
            TimeSpan.FromSeconds(1));
        return media;
    }

    public void DeleteMedia(string id)
    {
        DeleteMedia(new[] { id });
    }

    public void DeleteMedia(IEnumerable<string> ids)
    {
        var mediaIds = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (mediaIds.Count == 0)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        using var connection = _connections.OpenConnection();

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

        foreach (var id in mediaIds)
        {
            playlistIdParam.Value = id;
            tagIdParam.Value = id;
            mediaIdParam.Value = id;
            playlistCmd.ExecuteNonQuery();
            tagCmd.ExecuteNonQuery();
            mediaCmd.ExecuteNonQuery();
        }

        tx.Commit();
        AppTraceLogger.Log("MediaRepository", $"DeleteMedia completed. Count={mediaIds.Count}, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
    }

    public void ClearAllMedia(bool includePlaylists = false)
    {
        var stopwatch = Stopwatch.StartNew();
        using var connection = _connections.OpenConnection();

        using var tx = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = includePlaylists
            ? "DELETE FROM playlist_media; DELETE FROM playlists; DELETE FROM tags; DELETE FROM media;"
            : "DELETE FROM playlist_media; DELETE FROM tags; DELETE FROM media;";
        cmd.ExecuteNonQuery();
        tx.Commit();
        AppTraceLogger.Log("MediaRepository", $"ClearAllMedia completed. IncludePlaylists={includePlaylists}, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
    }

    public IReadOnlyList<MediaDb.MediaEntryRef> GetMediaEntriesUnderFolders(IReadOnlyList<string> folderPaths)
    {
        if (folderPaths.Count == 0)
        {
            return Array.Empty<MediaDb.MediaEntryRef>();
        }

        var stopwatch = Stopwatch.StartNew();
        using var connection = _connections.OpenConnection();

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
        var results = new List<MediaDb.MediaEntryRef>();
        while (reader.Read())
        {
            results.Add(new MediaDb.MediaEntryRef(reader.GetString(0), reader.GetString(1)));
        }

        AppTraceLogger.Log("MediaRepository", $"GetMediaEntriesUnderFolders completed. FolderCount={folderPaths.Count}, ResultCount={results.Count}, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
        return results;
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

    private static void BuildQueryCommand(
        SqliteCommand cmd,
        string searchText,
        IReadOnlyList<string> selectedTags,
        IReadOnlyList<string>? includedFolders,
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

        if (includedFolders is { Count: > 0 })
        {
            sql.Append("  AND (\n");
            for (var i = 0; i < includedFolders.Count; i++)
            {
                var parameterName = $"$includedFolder{i}";
                if (i > 0)
                {
                    sql.Append("   OR\n");
                }

                sql.Append($"    m.path LIKE {parameterName} COLLATE NOCASE\n");
                cmd.Parameters.AddWithValue(parameterName, $"{includedFolders[i].TrimEnd('/')}/%");
            }

            sql.Append("  )\n");
        }

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

        var orderDirection = sortOrder == MediaSortOrder.Asc ? "ASC" : "DESC";
        sql.Append("ORDER BY ");
        sql.Append("substr(m.path, 1, max(length(m.path) - length(m.filename) - 1, 0)) COLLATE NOCASE ASC, ");
        sql.Append(sortField switch
        {
            MediaSortField.FileName => $"m.filename COLLATE NOCASE {orderDirection}",
            MediaSortField.ModifiedAt => $"m.modifiedAt {orderDirection}",
            MediaSortField.Type => $"m.type COLLATE NOCASE {orderDirection}",
            MediaSortField.Size => $"COALESCE(m.size, 0) {orderDirection}",
            MediaSortField.Duration => $"COALESCE(m.duration, 0) {orderDirection}",
            MediaSortField.Resolution =>
                $"(COALESCE(m.width, 0) * COALESCE(m.height, 0)) {orderDirection}, " +
                $"COALESCE(m.width, 0) {orderDirection}, " +
                $"COALESCE(m.height, 0) {orderDirection}",
            _ => $"m.modifiedAt {orderDirection}"
        });
        sql.Append(", m.id ASC ");
        sql.Append("LIMIT $limit OFFSET $offset;");

        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        cmd.CommandText = sql.ToString();
    }

    private static void BuildFolderSummaryCommand(
        SqliteCommand cmd,
        string searchText,
        IReadOnlyList<string> selectedTags,
        IReadOnlyList<string>? includedFolders,
        IReadOnlyList<string> excludedFolders,
        string? playlistId)
    {
        const string folderPathExpression = "substr(m.path, 1, max(length(m.path) - length(m.filename) - 1, 0))";
        var sql = new StringBuilder($@"
SELECT {folderPathExpression} AS folderPath, COUNT(*) AS mediaCount
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

        if (includedFolders is { Count: > 0 })
        {
            sql.Append("  AND (\n");
            for (var i = 0; i < includedFolders.Count; i++)
            {
                var parameterName = $"$includedFolder{i}";
                if (i > 0)
                {
                    sql.Append("   OR\n");
                }

                sql.Append($"    m.path LIKE {parameterName} COLLATE NOCASE\n");
                cmd.Parameters.AddWithValue(parameterName, $"{includedFolders[i].TrimEnd('/')}/%");
            }

            sql.Append("  )\n");
        }

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

        sql.Append("GROUP BY folderPath\n");
        sql.Append("ORDER BY folderPath COLLATE NOCASE ASC;");
        cmd.CommandText = sql.ToString();
    }
}
