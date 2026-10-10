using System.Text;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using JvJvMediaManager.Models;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.Data;

internal sealed partial class MediaRepository
{
    private sealed record NormalizedMediaQuery(
        string SearchText,
        List<string> SelectedTags,
        List<string>? IncludedFolders,
        List<string>? ExactFolders,
        List<string> ExcludedFolders,
        List<string> ExcludedMediaIds,
        string? FtsSearch);

    private readonly MediaDbConnectionFactory _connections;
    private readonly MediaTagRepository _tags;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public MediaRepository(MediaDbConnectionFactory connections, MediaTagRepository tags)
    {
        _connections = connections;
        _tags = tags;
    }

    private static SqliteCommand CreateCommand(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        return command;
    }

    public Task AddMediaAsync(MediaFile media, CancellationToken cancellationToken = default)
    {
        return UpsertMediaBatchAsync(new[] { media }, cancellationToken);
    }

    public async Task UpsertMediaBatchAsync(IEnumerable<MediaFile> mediaItems, CancellationToken cancellationToken = default)
    {
        var items = mediaItems.ToList();
        if (items.Count == 0)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await _connections.OpenConnectionAsync(cancellationToken);

            await using var tx = await connection.BeginTransactionAsync(cancellationToken);
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = @"
INSERT INTO media (
  id, path, folderPath, filename, type, size, width, height, duration, thumbnail, createdAt, modifiedAt, lastPlayed, playCount, contentHash, fingerprintVersion, rotation
) VALUES (
  $id, $path, $folderPath, $filename, $type, $size, $width, $height, $duration, $thumbnail, $createdAt, $modifiedAt, $lastPlayed, $playCount, $contentHash, $fingerprintVersion, $rotation
)
ON CONFLICT(id) DO UPDATE SET
  path = excluded.path,
  folderPath = excluded.folderPath,
  filename = excluded.filename,
  type = excluded.type,
  size = excluded.size,
  width = excluded.width,
  height = excluded.height,
  duration = excluded.duration,
  thumbnail = COALESCE(excluded.thumbnail, media.thumbnail),
  createdAt = excluded.createdAt,
  modifiedAt = excluded.modifiedAt,
  lastPlayed = COALESCE(media.lastPlayed, excluded.lastPlayed),
  playCount = COALESCE(media.playCount, excluded.playCount),
  contentHash = COALESCE(excluded.contentHash, media.contentHash),
  fingerprintVersion = CASE
    WHEN excluded.contentHash IS NULL THEN media.fingerprintVersion
    ELSE excluded.fingerprintVersion
  END,
  rotation = media.rotation;
";

            var idParam = cmd.Parameters.Add("$id", SqliteType.Text);
            var pathParam = cmd.Parameters.Add("$path", SqliteType.Text);
            var folderPathParam = cmd.Parameters.Add("$folderPath", SqliteType.Text);
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
            var contentHashParam = cmd.Parameters.Add("$contentHash", SqliteType.Text);
            var fingerprintVersionParam = cmd.Parameters.Add("$fingerprintVersion", SqliteType.Integer);
            var rotationParam = cmd.Parameters.Add("$rotation", SqliteType.Integer);

            foreach (var media in items)
            {
                media.Id = await ResolveReusableMediaIdAsync(connection, (SqliteTransaction)tx, media, cancellationToken);
                idParam.Value = media.Id;
                pathParam.Value = media.Path;
                folderPathParam.Value = GetFolderPath(media);
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
                contentHashParam.Value = string.IsNullOrWhiteSpace(media.ContentHash) ? DBNull.Value : media.ContentHash;
                fingerprintVersionParam.Value = media.FingerprintVersion;
                rotationParam.Value = media.Rotation;
                await cmd.ExecuteNonQueryAsync(cancellationToken);

                await MediaTagRepository.AddTagsAsync(connection, (SqliteTransaction)tx, media.Id, media.Tags, cancellationToken);
                await RefreshSearchIndexAsync(connection, (SqliteTransaction)tx, media.Id, cancellationToken);
                await RemoveRelocationAsync(connection, (SqliteTransaction)tx, media.Id, cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
            AppTraceLogger.Log("MediaRepository", $"UpsertMediaBatchAsync completed. Count={items.Count}, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task<string> ResolveReusableMediaIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        MediaFile media,
        CancellationToken cancellationToken = default)
    {
        if (await MediaIdExistsAsync(connection, transaction, media.Id, cancellationToken))
        {
            return media.Id;
        }

        var existingPathId = await ResolveExistingMediaIdByPathAsync(connection, transaction, media.Path, cancellationToken);
        if (!string.IsNullOrWhiteSpace(existingPathId))
        {
            AppTraceLogger.LogSampled(
                "MediaRepository",
                "reuse-media-id-by-path",
                $"Reusing media id by path. ExistingId='{existingPathId}', Path='{media.Path}'.",
                TimeSpan.FromSeconds(2));
            return existingPathId;
        }

        if (!string.IsNullOrWhiteSpace(media.ContentHash))
        {
            await using var cmd = CreateCommand(connection, transaction);
            cmd.CommandText = @"
SELECT id, path
FROM media
WHERE contentHash = $contentHash
  AND fingerprintVersion = $fingerprintVersion
ORDER BY modifiedAt DESC;
";
            cmd.Parameters.AddWithValue("$contentHash", media.ContentHash);
            cmd.Parameters.AddWithValue("$fingerprintVersion", media.FingerprintVersion);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var existingId = reader.GetString(0);
                var existingPath = reader.GetString(1);
                if (!File.Exists(PathHelpers.ToNativePath(existingPath)))
                {
                    AppTraceLogger.Log(
                        "MediaRepository",
                        $"Reusing media id by content fingerprint. ExistingId='{existingId}', OldPath='{existingPath}', NewPath='{media.Path}'.");
                    return existingId;
                }
            }

            var relocatedId = await ResolveRelocatedMediaIdAsync(connection, transaction, media, cancellationToken);
            if (!string.IsNullOrWhiteSpace(relocatedId))
            {
                AppTraceLogger.Log(
                    "MediaRepository",
                    $"Reusing media id by relocation fingerprint. ExistingId='{relocatedId}', NewPath='{media.Path}'.");
                return relocatedId;
            }
        }

        var weakRelocatedId = await ResolveWeakRelocatedMediaIdAsync(connection, transaction, media, cancellationToken);
        if (!string.IsNullOrWhiteSpace(weakRelocatedId))
        {
            AppTraceLogger.Log(
                "MediaRepository",
                $"Reusing media id by weak relocation fingerprint. ExistingId='{weakRelocatedId}', NewPath='{media.Path}', Size={media.Size}, ModifiedAt={media.ModifiedAt}.");
            return weakRelocatedId;
        }

        return media.Id;
    }

    private static async Task<string?> ResolveExistingMediaIdByPathAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        await using var cmd = CreateCommand(connection, transaction);
        cmd.CommandText = @"
SELECT id
FROM media
WHERE path = $path COLLATE NOCASE
ORDER BY modifiedAt DESC
LIMIT 1;
";
        cmd.Parameters.AddWithValue("$path", PathHelpers.NormalizePath(path));
        return await cmd.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<string?> ResolveRelocatedMediaIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        MediaFile media,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand(connection, transaction);
        cmd.CommandText = @"
SELECT mediaId
FROM media_relocations
WHERE contentHash = $contentHash
  AND fingerprintVersion = $fingerprintVersion
ORDER BY createdAt DESC
LIMIT 1;
";
        cmd.Parameters.AddWithValue("$contentHash", media.ContentHash);
        cmd.Parameters.AddWithValue("$fingerprintVersion", media.FingerprintVersion);
        return await cmd.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<string?> ResolveWeakRelocatedMediaIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        MediaFile media,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand(connection, transaction);
        cmd.CommandText = @"
SELECT mediaId, filename
FROM media_relocations
WHERE size = $size
  AND modifiedAt = $modifiedAt
  AND type = $type COLLATE NOCASE
ORDER BY
  CASE WHEN filename = $filename COLLATE NOCASE THEN 0 ELSE 1 END,
  createdAt DESC;
";
        cmd.Parameters.AddWithValue("$size", media.Size);
        cmd.Parameters.AddWithValue("$modifiedAt", media.ModifiedAt);
        cmd.Parameters.AddWithValue("$type", media.Type.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$filename", media.FileName);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var candidates = new List<(string MediaId, string? FileName)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add((
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        var exactFileName = candidates.FirstOrDefault(candidate =>
            string.Equals(candidate.FileName, media.FileName, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(exactFileName.MediaId))
        {
            return exactFileName.MediaId;
        }

        return candidates.Count == 1 ? candidates[0].MediaId : null;
    }

    private static async Task<bool> MediaIdExistsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string mediaId,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand(connection, transaction);
        cmd.CommandText = "SELECT 1 FROM media WHERE id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", mediaId);
        return await cmd.ExecuteScalarAsync(cancellationToken) != null;
    }

    public async Task<IReadOnlyList<MediaFile>> GetAllMediaAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<MediaFile>();
        await foreach (var media in StreamAllMediaAsync(cancellationToken))
        {
            items.Add(media);
        }

        return items;
    }

    // H2: pages through the whole table in bounded chunks so bulk maintenance
    // (e.g. the invalid thumbnail cache sweep) never materializes the full
    // library in memory at once.
    public async IAsyncEnumerable<MediaFile> StreamAllMediaAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        const int pageSize = 1000;
        var offset = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await QueryMediaPageAsync(new MediaQuery
            {
                Offset = offset,
                Limit = pageSize,
                SortField = MediaSortField.ModifiedAt,
                SortOrder = MediaSortOrder.Desc
            }, cancellationToken);
            foreach (var media in page.Items)
            {
                yield return media;
            }

            offset += page.Items.Count;
            if (!page.HasMore || page.Items.Count == 0)
            {
                yield break;
            }
        }
    }

    public async Task<MediaPageResult> QueryMediaPageAsync(MediaQuery query, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);

        var normalized = NormalizeQuery(query);
        var useFtsSearch = !string.IsNullOrWhiteSpace(normalized.FtsSearch)
            && await IsSearchIndexAvailableAsync(connection, null, cancellationToken);

        if (normalized.IncludedFolders is { Count: 0 })
        {
            AppTraceLogger.LogSampled(
                "MediaRepository",
                "query-empty-included-folders",
                $"QueryMediaPageAsync short-circuited. IncludedFolders=0, PlaylistId='{query.PlaylistId ?? "<null>"}'.",
                TimeSpan.FromSeconds(2));
            return new MediaPageResult
            {
                Items = Array.Empty<MediaFile>(),
                HasMore = false
            };
        }

        var limit = query.Limit <= 0 ? 200 : query.Limit;
        var fetchLimit = limit == int.MaxValue ? limit : limit + 1;

        await using var cmd = connection.CreateCommand();
        BuildQueryCommand(
            cmd,
            normalized.SearchText,
            normalized.SelectedTags,
            normalized.IncludedFolders,
            normalized.ExactFolders,
            normalized.ExcludedFolders,
            query.PlaylistId,
            query.TypeFilter,
            normalized.ExcludedMediaIds,
            query.RandomOrder,
            useFtsSearch ? normalized.FtsSearch : null,
            query.SortField,
            query.SortOrder,
            query.AfterMediaId,
            query.Cursor,
            query.Offset,
            fetchLimit);

        var items = new List<MediaFile>();
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(ReadMedia(reader));
            }
        }
        catch (SqliteException ex) when (useFtsSearch)
        {
            AppTraceLogger.LogException(
                "MediaRepository",
                $"QueryMediaPageAsync FTS search failed; retrying with LIKE fallback. SearchLength={normalized.SearchText.Length}.",
                ex);
            items.Clear();
            await using var fallbackCmd = connection.CreateCommand();
            BuildQueryCommand(
                fallbackCmd,
                normalized.SearchText,
                normalized.SelectedTags,
                normalized.IncludedFolders,
                normalized.ExactFolders,
                normalized.ExcludedFolders,
                query.PlaylistId,
                query.TypeFilter,
                normalized.ExcludedMediaIds,
                query.RandomOrder,
                null,
                query.SortField,
                query.SortOrder,
                query.AfterMediaId,
                query.Cursor,
                query.Offset,
                fetchLimit);
            await using var fallbackReader = await fallbackCmd.ExecuteReaderAsync(cancellationToken);
            while (await fallbackReader.ReadAsync(cancellationToken))
            {
                items.Add(ReadMedia(fallbackReader));
            }

            useFtsSearch = false;
        }

        var hasMore = items.Count > limit;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        await _tags.AttachTagsAsync(connection, items, cancellationToken);

        var nextCursor = BuildNextCursor(query, items, hasMore);
        AppTraceLogger.LogSampled(
            "MediaRepository",
            "query-media-page",
            $"QueryMediaPageAsync completed. Offset={query.Offset}, Limit={limit}, Returned={items.Count}, HasMore={hasMore}, Keyset={(query.Cursor != null && !query.RandomOrder && IsKeysetSupported(query.SortField))}, Tags={normalized.SelectedTags.Count}, IncludedFolders={normalized.IncludedFolders?.Count ?? 0}, ExcludedFolders={normalized.ExcludedFolders.Count}, ExcludedMediaIds={normalized.ExcludedMediaIds.Count}, TypeFilter='{query.TypeFilter?.ToString() ?? "<null>"}', RandomOrder={query.RandomOrder}, PlaylistId='{query.PlaylistId ?? "<null>"}', FtsSearch={useFtsSearch}, ElapsedMs={stopwatch.ElapsedMilliseconds}.",
            TimeSpan.FromSeconds(1));

        return new MediaPageResult
        {
            Items = items,
            HasMore = hasMore,
            NextCursor = nextCursor
        };
    }

    public async Task<IReadOnlyList<MediaFolderSummary>> QueryMediaFolderSummariesAsync(MediaQuery query, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);

        var normalized = NormalizeQuery(query);
        var useFtsSearch = !string.IsNullOrWhiteSpace(normalized.FtsSearch)
            && await IsSearchIndexAvailableAsync(connection, null, cancellationToken);

        if (normalized.IncludedFolders is { Count: 0 })
        {
            return Array.Empty<MediaFolderSummary>();
        }

        await using var cmd = connection.CreateCommand();
        BuildFolderSummaryCommand(
            cmd,
            normalized.SearchText,
            normalized.SelectedTags,
            normalized.IncludedFolders,
            normalized.ExcludedFolders,
            query.PlaylistId,
            normalized.ExcludedMediaIds,
            useFtsSearch ? normalized.FtsSearch : null);

        var folders = new List<MediaFolderSummary>();
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                folders.Add(new MediaFolderSummary
                {
                    FolderPath = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                    Count = reader.GetInt32(1)
                });
            }
        }
        catch (SqliteException ex) when (useFtsSearch)
        {
            AppTraceLogger.LogException(
                "MediaRepository",
                $"QueryMediaFolderSummariesAsync FTS search failed; retrying with LIKE fallback. SearchLength={normalized.SearchText.Length}.",
                ex);
            folders.Clear();
            await using var fallbackCmd = connection.CreateCommand();
            BuildFolderSummaryCommand(
                fallbackCmd,
                normalized.SearchText,
                normalized.SelectedTags,
                normalized.IncludedFolders,
                normalized.ExcludedFolders,
                query.PlaylistId,
                normalized.ExcludedMediaIds,
                null);
            await using var fallbackReader = await fallbackCmd.ExecuteReaderAsync(cancellationToken);
            while (await fallbackReader.ReadAsync(cancellationToken))
            {
                folders.Add(new MediaFolderSummary
                {
                    FolderPath = fallbackReader.IsDBNull(0) ? string.Empty : fallbackReader.GetString(0),
                    Count = fallbackReader.GetInt32(1)
                });
            }

            useFtsSearch = false;
        }

        AppTraceLogger.LogSampled(
            "MediaRepository",
            "query-media-folder-summaries",
            $"QueryMediaFolderSummariesAsync completed. FolderCount={folders.Count}, Tags={normalized.SelectedTags.Count}, IncludedFolders={normalized.IncludedFolders?.Count ?? 0}, ExcludedFolders={normalized.ExcludedFolders.Count}, ExcludedMediaIds={normalized.ExcludedMediaIds.Count}, PlaylistId='{query.PlaylistId ?? "<null>"}', FtsSearch={useFtsSearch}, ElapsedMs={stopwatch.ElapsedMilliseconds}.",
            TimeSpan.FromSeconds(1));

        return folders;
    }

    public async Task<MediaFile?> GetMediaByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, path, filename, type, size, width, height, duration, thumbnail, createdAt, modifiedAt, lastPlayed, playCount, contentHash, fingerprintVersion, rotation
FROM media
WHERE id = $id;
";
        cmd.Parameters.AddWithValue("$id", id);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            AppTraceLogger.Log("MediaRepository", $"GetMediaByIdAsync missed. MediaId='{id}', ElapsedMs={stopwatch.ElapsedMilliseconds}.");
            return null;
        }

        var media = ReadMedia(reader);
        media.Tags = (await MediaTagRepository.GetTagsAsync(connection, media.Id, cancellationToken)).ToList();
        AppTraceLogger.LogSampled(
            "MediaRepository",
            "get-media-by-id-async",
            $"GetMediaByIdAsync completed. MediaId='{id}', TagCount={media.Tags.Count}, ElapsedMs={stopwatch.ElapsedMilliseconds}.",
            TimeSpan.FromSeconds(1));
        return media;
    }

    public async Task<MediaFile?> GetMediaByPathAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalizedPath = PathHelpers.NormalizePath(path);
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, path, filename, type, size, width, height, duration, thumbnail, createdAt, modifiedAt, lastPlayed, playCount, contentHash, fingerprintVersion, rotation
FROM media
WHERE path = $path COLLATE NOCASE
LIMIT 1;
";
        cmd.Parameters.AddWithValue("$path", normalizedPath);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMedia(reader) : null;
    }

    public async Task<MediaDb.MediaFingerprintRef?> GetMediaFingerprintByPathAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalizedPath = PathHelpers.NormalizePath(path);
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, path, size, modifiedAt, contentHash, fingerprintVersion
FROM media
WHERE path = $path COLLATE NOCASE
LIMIT 1;
";
        cmd.Parameters.AddWithValue("$path", normalizedPath);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new MediaDb.MediaFingerprintRef(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? 0 : reader.GetInt32(5));
    }

    public async Task<bool> HasReusableFingerprintCandidateAsync(string path, long size, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || size < 0)
        {
            return false;
        }

        var normalizedPath = PathHelpers.NormalizePath(path);
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);

        await using var mediaCmd = connection.CreateCommand();
        mediaCmd.CommandText = @"
SELECT 1
FROM media
WHERE size = $size
  AND contentHash IS NOT NULL
  AND fingerprintVersion = $fingerprintVersion
  AND path <> $path COLLATE NOCASE
LIMIT 1;
";
        mediaCmd.Parameters.AddWithValue("$size", size);
        mediaCmd.Parameters.AddWithValue("$fingerprintVersion", MediaContentFingerprint.Version);
        mediaCmd.Parameters.AddWithValue("$path", normalizedPath);
        if (await mediaCmd.ExecuteScalarAsync(cancellationToken) != null)
        {
            return true;
        }

        await using var relocationCmd = connection.CreateCommand();
        relocationCmd.CommandText = @"
SELECT 1
FROM media_relocations
WHERE size = $size
  AND contentHash IS NOT NULL
  AND contentHash <> ''
LIMIT 1;
";
        relocationCmd.Parameters.AddWithValue("$size", size);
        return await relocationCmd.ExecuteScalarAsync(cancellationToken) != null;
    }

    public async Task UpdateMediaFingerprintByPathAsync(
        string path,
        long size,
        long modifiedAt,
        string contentHash,
        int fingerprintVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(contentHash))
        {
            return;
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var cmd = CreateCommand(connection, null);
        cmd.CommandText = @"
UPDATE media
SET contentHash = $contentHash,
    fingerprintVersion = $fingerprintVersion
WHERE path = $path COLLATE NOCASE
  AND size = $size
  AND modifiedAt = $modifiedAt;
";
        cmd.Parameters.AddWithValue("$contentHash", contentHash);
        cmd.Parameters.AddWithValue("$fingerprintVersion", fingerprintVersion);
        cmd.Parameters.AddWithValue("$path", PathHelpers.NormalizePath(path));
        cmd.Parameters.AddWithValue("$size", size);
        cmd.Parameters.AddWithValue("$modifiedAt", modifiedAt);
        var updated = await cmd.ExecuteNonQueryAsync(cancellationToken);
        if (updated > 0)
        {
            AppTraceLogger.LogSampled(
                "MediaRepository",
                "fingerprint-backfill-updated",
                $"UpdateMediaFingerprintByPathAsync completed. Updated={updated}, Path='{path}'.",
                TimeSpan.FromSeconds(2));
        }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task UpdateMediaMetadataByPathAsync(
        string path,
        long size,
        long modifiedAt,
        int? width,
        int? height,
        double? duration,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || (!width.HasValue && !height.HasValue && !duration.HasValue))
        {
            return;
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var cmd = CreateCommand(connection, null);
        cmd.CommandText = @"
UPDATE media
SET width = COALESCE($width, width),
    height = COALESCE($height, height),
    duration = COALESCE($duration, duration)
WHERE path = $path COLLATE NOCASE
  AND size = $size
  AND modifiedAt = $modifiedAt;
";
        cmd.Parameters.AddWithValue("$width", (object?)width ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$height", (object?)height ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$duration", (object?)duration ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$path", PathHelpers.NormalizePath(path));
        cmd.Parameters.AddWithValue("$size", size);
        cmd.Parameters.AddWithValue("$modifiedAt", modifiedAt);
        var updated = await cmd.ExecuteNonQueryAsync(cancellationToken);
        if (updated > 0)
        {
            AppTraceLogger.LogSampled(
                "MediaRepository",
                "metadata-backfill-updated",
                $"UpdateMediaMetadataByPathAsync completed. Updated={updated}, Path='{path}', HasSize={width.HasValue && height.HasValue}, HasDuration={duration.HasValue}.",
                TimeSpan.FromSeconds(2));
        }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task DeleteMediaAsync(string id, CancellationToken cancellationToken = default)
    {
        return DeleteMediaAsync(new[] { id }, cancellationToken);
    }

    public async Task DeleteMediaAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var mediaIds = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (mediaIds.Count == 0)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);

        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await using var playlistCmd = connection.CreateCommand();
        await using var tagCmd = connection.CreateCommand();
        await using var relocationCmd = connection.CreateCommand();
        await using var mediaCmd = connection.CreateCommand();

        playlistCmd.Transaction = (SqliteTransaction)tx;
        tagCmd.Transaction = (SqliteTransaction)tx;
        relocationCmd.Transaction = (SqliteTransaction)tx;
        mediaCmd.Transaction = (SqliteTransaction)tx;

        playlistCmd.CommandText = "DELETE FROM playlist_media WHERE mediaId = $id;";
        tagCmd.CommandText = "DELETE FROM tags WHERE mediaId = $id;";
        relocationCmd.CommandText = "DELETE FROM media_relocations WHERE mediaId = $id;";
        mediaCmd.CommandText = "DELETE FROM media WHERE id = $id;";

        var playlistIdParam = playlistCmd.Parameters.Add("$id", SqliteType.Text);
        var tagIdParam = tagCmd.Parameters.Add("$id", SqliteType.Text);
        var relocationIdParam = relocationCmd.Parameters.Add("$id", SqliteType.Text);
        var mediaIdParam = mediaCmd.Parameters.Add("$id", SqliteType.Text);

        foreach (var id in mediaIds)
        {
            playlistIdParam.Value = id;
            tagIdParam.Value = id;
            relocationIdParam.Value = id;
            mediaIdParam.Value = id;
            await playlistCmd.ExecuteNonQueryAsync(cancellationToken);
            await tagCmd.ExecuteNonQueryAsync(cancellationToken);
            await DeleteSearchIndexAsync(connection, (SqliteTransaction)tx, id, cancellationToken);
            await relocationCmd.ExecuteNonQueryAsync(cancellationToken);
            await mediaCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        AppTraceLogger.Log("MediaRepository", $"DeleteMediaAsync completed. Count={mediaIds.Count}, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task RemoveMediaRecordsOnlyAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var mediaIds = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (mediaIds.Count == 0)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await using var relocationCmd = connection.CreateCommand();
        relocationCmd.Transaction = (SqliteTransaction)tx;
        relocationCmd.CommandText = @"
INSERT OR REPLACE INTO media_relocations(mediaId, path, filename, type, size, modifiedAt, contentHash, fingerprintVersion, createdAt)
SELECT
  id,
  path,
  filename,
  type,
  size,
  modifiedAt,
  COALESCE(contentHash, ''),
  fingerprintVersion,
  $createdAt
FROM media
WHERE id = $id;
";
        var relocationCreatedAtParam = relocationCmd.Parameters.Add("$createdAt", SqliteType.Integer);
        var relocationIdParam = relocationCmd.Parameters.Add("$id", SqliteType.Text);

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = "DELETE FROM media WHERE id = $id;";
        var mediaIdParam = cmd.Parameters.Add("$id", SqliteType.Text);
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var id in mediaIds)
        {
            relocationCreatedAtParam.Value = createdAt;
            relocationIdParam.Value = id;
            await relocationCmd.ExecuteNonQueryAsync(cancellationToken);
            await DeleteSearchIndexAsync(connection, (SqliteTransaction)tx, id, cancellationToken);
            mediaIdParam.Value = id;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        AppTraceLogger.Log("MediaRepository", $"RemoveMediaRecordsOnlyAsync completed. Count={mediaIds.Count}, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task ClearAllMediaAsync(bool includePlaylists = false, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);

        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = includePlaylists
            ? "DELETE FROM playlist_media; DELETE FROM playlists; DELETE FROM tags; DELETE FROM media_relocations; DELETE FROM media;"
            : "DELETE FROM playlist_media; DELETE FROM tags; DELETE FROM media_relocations; DELETE FROM media;";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        await ClearSearchIndexAsync(connection, (SqliteTransaction)tx, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        AppTraceLogger.Log("MediaRepository", $"ClearAllMediaAsync completed. IncludePlaylists={includePlaylists}, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task DeleteMediaUnderFoldersAsync(
        IReadOnlyList<string> removedFolderPaths,
        IReadOnlyList<string> keepFolderPaths,
        CancellationToken cancellationToken = default)
    {
        if (removedFolderPaths.Count == 0)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
            await using var tx = await connection.BeginTransactionAsync(cancellationToken);
            await using var selectCmd = connection.CreateCommand();
            selectCmd.Transaction = (SqliteTransaction)tx;

            var whereSql = new StringBuilder();
            AppendFolderPathMatchSql(selectCmd, removedFolderPaths, "removed", whereSql);
            if (keepFolderPaths.Count > 0)
            {
                whereSql.Append(" AND NOT ");
                AppendFolderPathMatchSql(selectCmd, keepFolderPaths, "keep", whereSql);
            }

            selectCmd.CommandText = $@"
DROP TABLE IF EXISTS stale_media_ids;
CREATE TEMP TABLE stale_media_ids (id TEXT PRIMARY KEY);
INSERT INTO stale_media_ids(id)
SELECT id FROM media
WHERE {whereSql};
";
            await selectCmd.ExecuteNonQueryAsync(cancellationToken);

            await using var deleteCmd = connection.CreateCommand();
            deleteCmd.Transaction = (SqliteTransaction)tx;
            deleteCmd.CommandText = @"
DELETE FROM tags WHERE mediaId IN (SELECT id FROM stale_media_ids);
DELETE FROM playlist_media WHERE mediaId IN (SELECT id FROM stale_media_ids);
DELETE FROM media_relocations WHERE mediaId IN (SELECT id FROM stale_media_ids);
DELETE FROM media WHERE id IN (SELECT id FROM stale_media_ids);
";
            await deleteCmd.ExecuteNonQueryAsync(cancellationToken);

            if (await IsSearchIndexAvailableAsync(connection, (SqliteTransaction)tx, cancellationToken))
            {
                await using var ftsCmd = connection.CreateCommand();
                ftsCmd.Transaction = (SqliteTransaction)tx;
                ftsCmd.CommandText = "DELETE FROM media_search WHERE mediaId IN (SELECT id FROM stale_media_ids);";
                await ftsCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var dropCmd = connection.CreateCommand();
            dropCmd.Transaction = (SqliteTransaction)tx;
            dropCmd.CommandText = "DROP TABLE stale_media_ids;";
            await dropCmd.ExecuteNonQueryAsync(cancellationToken);

            await tx.CommitAsync(cancellationToken);
            AppTraceLogger.Log(
                "MediaRepository",
                $"DeleteMediaUnderFoldersAsync completed. RemovedFolders={removedFolderPaths.Count}, KeepFolders={keepFolderPaths.Count}, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task RemoveRelocationAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string mediaId,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "DELETE FROM media_relocations WHERE mediaId = $mediaId;";
        cmd.Parameters.AddWithValue("$mediaId", mediaId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? TryBuildFtsSearchQuery(string searchText)
    {
        var terms = searchText
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(EscapeFtsTerm)
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .ToList();

        return terms.Count == 0
            ? null
            : string.Join(" AND ", terms);
    }

    private static NormalizedMediaQuery NormalizeQuery(MediaQuery query)
    {
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
        var exactFolders = query.ExactFolderPaths?
            .Select(PathHelpers.NormalizeFolderPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var excludedFolders = query.ExcludedFolderPaths
            .Select(path => path.Trim())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var excludedMediaIds = query.ExcludedMediaIds
            .Select(id => id.Trim())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new NormalizedMediaQuery(
            normalizedQuery,
            normalizedTags,
            includedFolders,
            exactFolders,
            excludedFolders,
            excludedMediaIds,
            TryBuildFtsSearchQuery(normalizedQuery));
    }

    private static string? EscapeFtsTerm(string term)
    {
        var normalized = term.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return $"\"{normalized.Replace("\"", "\"\"")}\"*";
    }

    private static string EscapeLikePattern(string value)
    {
        return value
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
    }

    // H1: pattern for "this folder and its subfolders". NeedsEscape is true only
    // when the folder itself contains LIKE wildcards; plain prefixes omit the
    // ESCAPE clause so SQLite can serve the LIKE via idx_media_folderPath_nocase
    // instead of scanning the whole table on every filtered page.
    private static (string Pattern, bool NeedsEscape) BuildFolderLikePattern(string folderPath)
    {
        var trimmed = folderPath.TrimEnd('/');
        var escaped = EscapeLikePattern(trimmed);
        return ($"{escaped}/%", !string.Equals(escaped, trimmed, StringComparison.Ordinal));
    }

    // H1: emits "(folderPath = <p>Exact COLLATE NOCASE OR folderPath LIKE <p> [ESCAPE '\'] COLLATE NOCASE)"
    // with a bare column name so it works both with and without a table alias.
    internal static void AppendFolderFilterTerm(SqliteCommand cmd, StringBuilder sql, string parameterName, string folderPath)
    {
        var trimmed = folderPath.TrimEnd('/');
        var (pattern, needsEscape) = BuildFolderLikePattern(trimmed);
        sql.Append("(folderPath = ")
            .Append(parameterName)
            .Append("Exact COLLATE NOCASE OR folderPath LIKE ")
            .Append(parameterName)
            .Append(needsEscape ? " ESCAPE '\\' COLLATE NOCASE)" : " COLLATE NOCASE)");
        cmd.Parameters.AddWithValue($"{parameterName}Exact", trimmed);
        cmd.Parameters.AddWithValue(parameterName, pattern);
    }

    private static void AppendFolderPathMatchSql(
        SqliteCommand cmd,
        IReadOnlyList<string> folderPaths,
        string parameterPrefix,
        StringBuilder sql)
    {
        sql.Append('(');
        for (var i = 0; i < folderPaths.Count; i++)
        {
            if (i > 0)
            {
                sql.Append(" OR ");
            }

            AppendFolderFilterTerm(cmd, sql, $"${parameterPrefix}{i}", folderPaths[i]);
        }

        sql.Append(')');
    }

    private static string GetFolderPath(MediaFile media)
    {
        if (!string.IsNullOrWhiteSpace(media.Path))
        {
            var normalizedPath = PathHelpers.NormalizePath(media.Path);
            var normalizedFileName = PathHelpers.NormalizePath(media.FileName);
            if (!string.IsNullOrWhiteSpace(normalizedFileName) &&
                normalizedPath.EndsWith($"/{normalizedFileName}", StringComparison.OrdinalIgnoreCase))
            {
                return normalizedPath[..^(normalizedFileName.Length + 1)];
            }

            var separatorIndex = normalizedPath.LastIndexOf('/');
            return separatorIndex > 0 ? normalizedPath[..separatorIndex] : string.Empty;
        }

        return string.Empty;
    }

    private static string BuildContainsLikePattern(string value)
    {
        return $"%{EscapeLikePattern(value)}%";
    }

    public async Task<IReadOnlyList<MediaDb.MediaEntryRef>> GetMediaEntriesUnderFoldersAsync(
        IReadOnlyList<string> folderPaths,
        CancellationToken cancellationToken = default)
    {
        if (folderPaths.Count == 0)
        {
            return Array.Empty<MediaDb.MediaEntryRef>();
        }

        var stopwatch = Stopwatch.StartNew();
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();
        var filters = new StringBuilder(folderPaths.Count * 40);
        for (var i = 0; i < folderPaths.Count; i++)
        {
            if (i > 0)
            {
                filters.Append(" OR ");
            }

            AppendFolderFilterTerm(cmd, filters, $"$folder{i}", folderPaths[i]);
        }

        cmd.CommandText = $@"
SELECT id, path
FROM media
WHERE {filters};
";

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var results = new List<MediaDb.MediaEntryRef>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new MediaDb.MediaEntryRef(reader.GetString(0), reader.GetString(1)));
        }

        AppTraceLogger.Log("MediaRepository", $"GetMediaEntriesUnderFoldersAsync completed. FolderCount={folderPaths.Count}, ResultCount={results.Count}, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
        return results;
    }

    // P-rotation: persists the user's per-media display rotation. Normalized
    // to the nearest 90 degrees so only 0/90/180/270 are ever stored.
    public async Task SetMediaRotationAsync(string mediaId, int rotation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return;
        }

        var normalized = (int)Math.Round(rotation / 90.0) * 90 % 360;
        if (normalized < 0)
        {
            normalized += 360;
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE media SET rotation = $rotation WHERE id = $id;";
            cmd.Parameters.AddWithValue("$rotation", normalized);
            cmd.Parameters.AddWithValue("$id", mediaId);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
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
            PlayCount = reader.IsDBNull(12) ? null : reader.GetInt32(12),
            ContentHash = reader.IsDBNull(13) ? null : reader.GetString(13),
            FingerprintVersion = reader.IsDBNull(14) ? 0 : reader.GetInt32(14),
            Rotation = reader.IsDBNull(15) ? 0 : reader.GetInt32(15)
        };
    }

}
