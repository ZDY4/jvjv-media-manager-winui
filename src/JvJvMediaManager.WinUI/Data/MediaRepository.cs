using System.Text;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using JvJvMediaManager.Models;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.Data;

internal sealed class MediaRepository
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

    private void AddMedia(MediaFile media)
    {
        UpsertMediaBatch(new[] { media });
    }

    public Task AddMediaAsync(MediaFile media, CancellationToken cancellationToken = default)
    {
        return UpsertMediaBatchAsync(new[] { media }, cancellationToken);
    }

    private void RunWrite(Action action)
    {
        _writeGate.Wait();
        try
        {
            action();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private T RunWrite<T>(Func<T> action)
    {
        _writeGate.Wait();
        try
        {
            return action();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void UpsertMediaBatch(IEnumerable<MediaFile> mediaItems)
    {
        var items = mediaItems.ToList();
        if (items.Count == 0)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        _writeGate.Wait();
        try
        {
        using var connection = _connections.OpenConnection();

        using var tx = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO media (
  id, path, folderPath, filename, type, size, width, height, duration, thumbnail, createdAt, modifiedAt, lastPlayed, playCount, contentHash, fingerprintVersion
) VALUES (
  $id, $path, $folderPath, $filename, $type, $size, $width, $height, $duration, $thumbnail, $createdAt, $modifiedAt, $lastPlayed, $playCount, $contentHash, $fingerprintVersion
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
  END;
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

        foreach (var media in items)
        {
            media.Id = ResolveReusableMediaId(connection, media);
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
            cmd.ExecuteNonQuery();

            _tags.AddTags(connection, media.Id, media.Tags);
            RefreshSearchIndex(connection, media.Id);
            RemoveRelocation(connection, media.Id);
        }

        tx.Commit();
        AppTraceLogger.Log("MediaRepository", $"UpsertMediaBatch completed. Count={items.Count}, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
        }
        finally
        {
            _writeGate.Release();
        }
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
  id, path, folderPath, filename, type, size, width, height, duration, thumbnail, createdAt, modifiedAt, lastPlayed, playCount, contentHash, fingerprintVersion
) VALUES (
  $id, $path, $folderPath, $filename, $type, $size, $width, $height, $duration, $thumbnail, $createdAt, $modifiedAt, $lastPlayed, $playCount, $contentHash, $fingerprintVersion
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
  END;
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

            foreach (var media in items)
            {
                media.Id = await ResolveReusableMediaIdAsync(connection, media, cancellationToken);
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
                await cmd.ExecuteNonQueryAsync(cancellationToken);

                await MediaTagRepository.AddTagsAsync(connection, (SqliteTransaction)tx, media.Id, media.Tags, cancellationToken);
                await RefreshSearchIndexAsync(connection, media.Id, cancellationToken);
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

    private static string ResolveReusableMediaId(SqliteConnection connection, MediaFile media)
    {
        if (MediaIdExists(connection, media.Id))
        {
            return media.Id;
        }

        var existingPathId = ResolveExistingMediaIdByPath(connection, media.Path);
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
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT id, path
FROM media
WHERE contentHash = $contentHash
  AND fingerprintVersion = $fingerprintVersion
ORDER BY modifiedAt DESC;
";
            cmd.Parameters.AddWithValue("$contentHash", media.ContentHash);
            cmd.Parameters.AddWithValue("$fingerprintVersion", media.FingerprintVersion);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
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

            var relocatedId = ResolveRelocatedMediaId(connection, media);
            if (!string.IsNullOrWhiteSpace(relocatedId))
            {
                AppTraceLogger.Log(
                    "MediaRepository",
                    $"Reusing media id by relocation fingerprint. ExistingId='{relocatedId}', NewPath='{media.Path}'.");
                return relocatedId;
            }
        }

        var weakRelocatedId = ResolveWeakRelocatedMediaId(connection, media);
        if (!string.IsNullOrWhiteSpace(weakRelocatedId))
        {
            AppTraceLogger.Log(
                "MediaRepository",
                $"Reusing media id by weak relocation fingerprint. ExistingId='{weakRelocatedId}', NewPath='{media.Path}', Size={media.Size}, ModifiedAt={media.ModifiedAt}.");
            return weakRelocatedId;
        }

        return media.Id;
    }

    private static async Task<string> ResolveReusableMediaIdAsync(
        SqliteConnection connection,
        MediaFile media,
        CancellationToken cancellationToken = default)
    {
        if (await MediaIdExistsAsync(connection, media.Id, cancellationToken))
        {
            return media.Id;
        }

        var existingPathId = await ResolveExistingMediaIdByPathAsync(connection, media.Path, cancellationToken);
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
            await using var cmd = connection.CreateCommand();
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

            var relocatedId = await ResolveRelocatedMediaIdAsync(connection, media, cancellationToken);
            if (!string.IsNullOrWhiteSpace(relocatedId))
            {
                AppTraceLogger.Log(
                    "MediaRepository",
                    $"Reusing media id by relocation fingerprint. ExistingId='{relocatedId}', NewPath='{media.Path}'.");
                return relocatedId;
            }
        }

        var weakRelocatedId = await ResolveWeakRelocatedMediaIdAsync(connection, media, cancellationToken);
        if (!string.IsNullOrWhiteSpace(weakRelocatedId))
        {
            AppTraceLogger.Log(
                "MediaRepository",
                $"Reusing media id by weak relocation fingerprint. ExistingId='{weakRelocatedId}', NewPath='{media.Path}', Size={media.Size}, ModifiedAt={media.ModifiedAt}.");
            return weakRelocatedId;
        }

        return media.Id;
    }

    private static string? ResolveExistingMediaIdByPath(SqliteConnection connection, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT id
FROM media
WHERE path = $path COLLATE NOCASE
ORDER BY modifiedAt DESC
LIMIT 1;
";
        cmd.Parameters.AddWithValue("$path", PathHelpers.NormalizePath(path));
        return cmd.ExecuteScalar() as string;
    }

    private static async Task<string?> ResolveExistingMediaIdByPathAsync(
        SqliteConnection connection,
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        await using var cmd = connection.CreateCommand();
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

    private static string? ResolveRelocatedMediaId(SqliteConnection connection, MediaFile media)
    {
        using var cmd = connection.CreateCommand();
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
        return cmd.ExecuteScalar() as string;
    }

    private static async Task<string?> ResolveRelocatedMediaIdAsync(
        SqliteConnection connection,
        MediaFile media,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
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

    private static string? ResolveWeakRelocatedMediaId(SqliteConnection connection, MediaFile media)
    {
        using var cmd = connection.CreateCommand();
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

        using var reader = cmd.ExecuteReader();
        var candidates = new List<(string MediaId, string? FileName)>();
        while (reader.Read())
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

    private static async Task<string?> ResolveWeakRelocatedMediaIdAsync(
        SqliteConnection connection,
        MediaFile media,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
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

    private static bool MediaIdExists(SqliteConnection connection, string mediaId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM media WHERE id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", mediaId);
        return cmd.ExecuteScalar() != null;
    }

    private static async Task<bool> MediaIdExistsAsync(
        SqliteConnection connection,
        string mediaId,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM media WHERE id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", mediaId);
        return await cmd.ExecuteScalarAsync(cancellationToken) != null;
    }

    private IReadOnlyList<MediaFile> GetAllMedia()
    {
        return QueryMediaPage(new MediaQuery
        {
            Offset = 0,
            Limit = int.MaxValue,
            SortField = MediaSortField.ModifiedAt,
            SortOrder = MediaSortOrder.Desc
        }).Items;
    }

    public async Task<IReadOnlyList<MediaFile>> GetAllMediaAsync(CancellationToken cancellationToken = default)
    {
        var result = await QueryMediaPageAsync(new MediaQuery
        {
            Offset = 0,
            Limit = int.MaxValue,
            SortField = MediaSortField.ModifiedAt,
            SortOrder = MediaSortOrder.Desc
        }, cancellationToken);
        return result.Items;
    }

    private MediaPageResult QueryMediaPage(MediaQuery query)
    {
        var stopwatch = Stopwatch.StartNew();
        using var connection = _connections.OpenConnection();

        var normalized = NormalizeQuery(query);
        var useFtsSearch = !string.IsNullOrWhiteSpace(normalized.FtsSearch) && IsSearchIndexAvailable(connection);

        if (normalized.IncludedFolders is { Count: 0 })
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
            query.Offset,
            fetchLimit);

        var items = new List<MediaFile>();
        try
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadMedia(reader));
            }
        }
        catch (SqliteException ex) when (useFtsSearch)
        {
            AppTraceLogger.LogException(
                "MediaRepository",
                $"QueryMediaPage FTS search failed; retrying with LIKE fallback. SearchLength={normalized.SearchText.Length}.",
                ex);
            items.Clear();
            using var fallbackCmd = connection.CreateCommand();
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
                query.Offset,
                fetchLimit);
            using var fallbackReader = fallbackCmd.ExecuteReader();
            while (fallbackReader.Read())
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

        _tags.AttachTags(connection, items);
        AppTraceLogger.LogSampled(
            "MediaRepository",
            "query-media-page",
            $"QueryMediaPage completed. Offset={query.Offset}, Limit={limit}, Returned={items.Count}, HasMore={hasMore}, Tags={normalized.SelectedTags.Count}, IncludedFolders={normalized.IncludedFolders?.Count ?? 0}, ExcludedFolders={normalized.ExcludedFolders.Count}, ExcludedMediaIds={normalized.ExcludedMediaIds.Count}, TypeFilter='{query.TypeFilter?.ToString() ?? "<null>"}', RandomOrder={query.RandomOrder}, PlaylistId='{query.PlaylistId ?? "<null>"}', FtsSearch={useFtsSearch}, ElapsedMs={stopwatch.ElapsedMilliseconds}.",
            TimeSpan.FromSeconds(1));

        return new MediaPageResult
        {
            Items = items,
            HasMore = hasMore
        };
    }

    public async Task<MediaPageResult> QueryMediaPageAsync(MediaQuery query, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);

        var normalized = NormalizeQuery(query);
        var useFtsSearch = !string.IsNullOrWhiteSpace(normalized.FtsSearch)
            && await IsSearchIndexAvailableAsync(connection, cancellationToken);

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
        AppTraceLogger.LogSampled(
            "MediaRepository",
            "query-media-page",
            $"QueryMediaPageAsync completed. Offset={query.Offset}, Limit={limit}, Returned={items.Count}, HasMore={hasMore}, Tags={normalized.SelectedTags.Count}, IncludedFolders={normalized.IncludedFolders?.Count ?? 0}, ExcludedFolders={normalized.ExcludedFolders.Count}, ExcludedMediaIds={normalized.ExcludedMediaIds.Count}, TypeFilter='{query.TypeFilter?.ToString() ?? "<null>"}', RandomOrder={query.RandomOrder}, PlaylistId='{query.PlaylistId ?? "<null>"}', FtsSearch={useFtsSearch}, ElapsedMs={stopwatch.ElapsedMilliseconds}.",
            TimeSpan.FromSeconds(1));

        return new MediaPageResult
        {
            Items = items,
            HasMore = hasMore
        };
    }

    private IReadOnlyList<MediaFolderSummary> QueryMediaFolderSummaries(MediaQuery query)
    {
        var stopwatch = Stopwatch.StartNew();
        using var connection = _connections.OpenConnection();

        var normalized = NormalizeQuery(query);
        var useFtsSearch = !string.IsNullOrWhiteSpace(normalized.FtsSearch) && IsSearchIndexAvailable(connection);

        if (normalized.IncludedFolders is { Count: 0 })
        {
            return Array.Empty<MediaFolderSummary>();
        }

        using var cmd = connection.CreateCommand();
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
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
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
                $"QueryMediaFolderSummaries FTS search failed; retrying with LIKE fallback. SearchLength={normalized.SearchText.Length}.",
                ex);
            folders.Clear();
            using var fallbackCmd = connection.CreateCommand();
            BuildFolderSummaryCommand(
                fallbackCmd,
                normalized.SearchText,
                normalized.SelectedTags,
                normalized.IncludedFolders,
                normalized.ExcludedFolders,
                query.PlaylistId,
                normalized.ExcludedMediaIds,
                null);
            using var fallbackReader = fallbackCmd.ExecuteReader();
            while (fallbackReader.Read())
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
            $"QueryMediaFolderSummaries completed. FolderCount={folders.Count}, Tags={normalized.SelectedTags.Count}, IncludedFolders={normalized.IncludedFolders?.Count ?? 0}, ExcludedFolders={normalized.ExcludedFolders.Count}, ExcludedMediaIds={normalized.ExcludedMediaIds.Count}, PlaylistId='{query.PlaylistId ?? "<null>"}', FtsSearch={useFtsSearch}, ElapsedMs={stopwatch.ElapsedMilliseconds}.",
            TimeSpan.FromSeconds(1));

        return folders;
    }

    public async Task<IReadOnlyList<MediaFolderSummary>> QueryMediaFolderSummariesAsync(MediaQuery query, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);

        var normalized = NormalizeQuery(query);
        var useFtsSearch = !string.IsNullOrWhiteSpace(normalized.FtsSearch)
            && await IsSearchIndexAvailableAsync(connection, cancellationToken);

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

    private MediaFile? GetMediaById(string id)
    {
        var stopwatch = Stopwatch.StartNew();
        using var connection = _connections.OpenConnection();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, path, filename, type, size, width, height, duration, thumbnail, createdAt, modifiedAt, lastPlayed, playCount, contentHash, fingerprintVersion
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
        media.Tags = MediaTagRepository.GetTags(connection, media.Id).ToList();
        AppTraceLogger.LogSampled(
            "MediaRepository",
            "get-media-by-id",
            $"GetMediaById completed. MediaId='{id}', TagCount={media.Tags.Count}, ElapsedMs={stopwatch.ElapsedMilliseconds}.",
            TimeSpan.FromSeconds(1));
        return media;
    }

    public async Task<MediaFile?> GetMediaByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, path, filename, type, size, width, height, duration, thumbnail, createdAt, modifiedAt, lastPlayed, playCount, contentHash, fingerprintVersion
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

    private MediaFile? GetMediaByPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalizedPath = PathHelpers.NormalizePath(path);
        using var connection = _connections.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, path, filename, type, size, width, height, duration, thumbnail, createdAt, modifiedAt, lastPlayed, playCount, contentHash, fingerprintVersion
FROM media
WHERE path = $path COLLATE NOCASE
LIMIT 1;
";
        cmd.Parameters.AddWithValue("$path", normalizedPath);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadMedia(reader) : null;
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
SELECT id, path, filename, type, size, width, height, duration, thumbnail, createdAt, modifiedAt, lastPlayed, playCount, contentHash, fingerprintVersion
FROM media
WHERE path = $path COLLATE NOCASE
LIMIT 1;
";
        cmd.Parameters.AddWithValue("$path", normalizedPath);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMedia(reader) : null;
    }

    private MediaDb.MediaFingerprintRef? GetMediaFingerprintByPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalizedPath = PathHelpers.NormalizePath(path);
        using var connection = _connections.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, path, size, modifiedAt, contentHash, fingerprintVersion
FROM media
WHERE path = $path COLLATE NOCASE
LIMIT 1;
";
        cmd.Parameters.AddWithValue("$path", normalizedPath);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
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

    private bool HasReusableFingerprintCandidate(string path, long size)
    {
        if (string.IsNullOrWhiteSpace(path) || size < 0)
        {
            return false;
        }

        var normalizedPath = PathHelpers.NormalizePath(path);
        using var connection = _connections.OpenConnection();

        using var mediaCmd = connection.CreateCommand();
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
        if (mediaCmd.ExecuteScalar() != null)
        {
            return true;
        }

        using var relocationCmd = connection.CreateCommand();
        relocationCmd.CommandText = @"
SELECT 1
FROM media_relocations
WHERE size = $size
  AND contentHash IS NOT NULL
  AND contentHash <> ''
LIMIT 1;
";
        relocationCmd.Parameters.AddWithValue("$size", size);
        return relocationCmd.ExecuteScalar() != null;
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

    private void UpdateMediaFingerprintByPath(string path, long size, long modifiedAt, string contentHash, int fingerprintVersion)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(contentHash))
        {
            return;
        }

        using var connection = _connections.OpenConnection();
        using var cmd = connection.CreateCommand();
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
        var updated = cmd.ExecuteNonQuery();
        if (updated > 0)
        {
            AppTraceLogger.LogSampled(
                "MediaRepository",
                "fingerprint-backfill-updated",
                $"UpdateMediaFingerprintByPath completed. Updated={updated}, Path='{path}'.",
                TimeSpan.FromSeconds(2));
        }
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

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
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

    private void UpdateMediaMetadataByPath(string path, long size, long modifiedAt, int? width, int? height, double? duration)
    {
        if (string.IsNullOrWhiteSpace(path) || (!width.HasValue && !height.HasValue && !duration.HasValue))
        {
            return;
        }

        using var connection = _connections.OpenConnection();
        using var cmd = connection.CreateCommand();
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
        var updated = cmd.ExecuteNonQuery();
        if (updated > 0)
        {
            AppTraceLogger.LogSampled(
                "MediaRepository",
                "metadata-backfill-updated",
                $"UpdateMediaMetadataByPath completed. Updated={updated}, Path='{path}', HasSize={width.HasValue && height.HasValue}, HasDuration={duration.HasValue}.",
                TimeSpan.FromSeconds(2));
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

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
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

    private void DeleteMedia(string id)
    {
        DeleteMedia(new[] { id });
    }

    public Task DeleteMediaAsync(string id, CancellationToken cancellationToken = default)
    {
        return DeleteMediaAsync(new[] { id }, cancellationToken);
    }

    private IReadOnlyList<string> DeleteMediaByPaths(IEnumerable<string> paths)
    {
        var normalizedPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(PathHelpers.NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedPaths.Count == 0)
        {
            return Array.Empty<string>();
        }

        var ids = new List<string>();
        using (var connection = _connections.OpenConnection())
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT id FROM media WHERE path = $path COLLATE NOCASE;";
            var pathParam = cmd.Parameters.Add("$path", SqliteType.Text);
            foreach (var path in normalizedPaths)
            {
                pathParam.Value = path;
                var id = cmd.ExecuteScalar() as string;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    ids.Add(id);
                }
            }
        }

        DeleteMedia(ids);
        return ids;
    }

    private void DeleteMedia(IEnumerable<string> ids)
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
        using var relocationCmd = connection.CreateCommand();
        using var mediaCmd = connection.CreateCommand();

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
            playlistCmd.ExecuteNonQuery();
            tagCmd.ExecuteNonQuery();
            DeleteSearchIndex(connection, id);
            relocationCmd.ExecuteNonQuery();
            mediaCmd.ExecuteNonQuery();
        }

        tx.Commit();
        AppTraceLogger.Log("MediaRepository", $"DeleteMedia completed. Count={mediaIds.Count}, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
    }

    public async Task DeleteMediaAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var mediaIds = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (mediaIds.Count == 0)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
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
            await DeleteSearchIndexAsync(connection, id, cancellationToken);
            await relocationCmd.ExecuteNonQueryAsync(cancellationToken);
            await mediaCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        AppTraceLogger.Log("MediaRepository", $"DeleteMediaAsync completed. Count={mediaIds.Count}, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
    }

    private void RemoveMediaRecordsOnly(IEnumerable<string> ids)
    {
        var mediaIds = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (mediaIds.Count == 0)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        using var connection = _connections.OpenConnection();
        using var tx = connection.BeginTransaction();
        using var relocationCmd = connection.CreateCommand();
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

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM media WHERE id = $id;";
        var mediaIdParam = cmd.Parameters.Add("$id", SqliteType.Text);
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var id in mediaIds)
        {
            relocationCreatedAtParam.Value = createdAt;
            relocationIdParam.Value = id;
            relocationCmd.ExecuteNonQuery();
            DeleteSearchIndex(connection, id);
            mediaIdParam.Value = id;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        AppTraceLogger.Log("MediaRepository", $"RemoveMediaRecordsOnly completed. Count={mediaIds.Count}, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
    }

    public async Task RemoveMediaRecordsOnlyAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var mediaIds = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (mediaIds.Count == 0)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
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
            await DeleteSearchIndexAsync(connection, id, cancellationToken);
            mediaIdParam.Value = id;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        AppTraceLogger.Log("MediaRepository", $"RemoveMediaRecordsOnlyAsync completed. Count={mediaIds.Count}, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
    }

    private void ClearAllMedia(bool includePlaylists = false)
    {
        var stopwatch = Stopwatch.StartNew();
        using var connection = _connections.OpenConnection();

        using var tx = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = includePlaylists
            ? "DELETE FROM playlist_media; DELETE FROM playlists; DELETE FROM tags; DELETE FROM media_relocations; DELETE FROM media;"
            : "DELETE FROM playlist_media; DELETE FROM tags; DELETE FROM media_relocations; DELETE FROM media;";
        cmd.ExecuteNonQuery();
        ClearSearchIndex(connection);
        tx.Commit();
        AppTraceLogger.Log("MediaRepository", $"ClearAllMedia completed. IncludePlaylists={includePlaylists}, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
    }

    public async Task ClearAllMediaAsync(bool includePlaylists = false, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);

        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = includePlaylists
            ? "DELETE FROM playlist_media; DELETE FROM playlists; DELETE FROM tags; DELETE FROM media_relocations; DELETE FROM media;"
            : "DELETE FROM playlist_media; DELETE FROM tags; DELETE FROM media_relocations; DELETE FROM media;";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        await ClearSearchIndexAsync(connection, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        AppTraceLogger.Log("MediaRepository", $"ClearAllMediaAsync completed. IncludePlaylists={includePlaylists}, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
    }

    private void RefreshSearchIndex(string mediaId)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return;
        }

        using var connection = _connections.OpenConnection();
        RefreshSearchIndex(connection, mediaId);
    }

    public async Task RefreshSearchIndexAsync(string mediaId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return;
        }

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await RefreshSearchIndexAsync(connection, mediaId, cancellationToken);
    }

    private static void RefreshSearchIndex(SqliteConnection connection, string mediaId)
    {
        if (!IsSearchIndexAvailable(connection))
        {
            return;
        }

        using var mediaCmd = connection.CreateCommand();
        mediaCmd.CommandText = "SELECT filename FROM media WHERE id = $id;";
        mediaCmd.Parameters.AddWithValue("$id", mediaId);
        var fileName = mediaCmd.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            DeleteSearchIndex(connection, mediaId);
            return;
        }

        var tags = string.Join(' ', MediaTagRepository.GetTags(connection, mediaId));
        DeleteSearchIndex(connection, mediaId);

        using var insertCmd = connection.CreateCommand();
        insertCmd.CommandText = "INSERT INTO media_search(mediaId, filename, tags) VALUES ($mediaId, $filename, $tags);";
        insertCmd.Parameters.AddWithValue("$mediaId", mediaId);
        insertCmd.Parameters.AddWithValue("$filename", fileName);
        insertCmd.Parameters.AddWithValue("$tags", tags);
        insertCmd.ExecuteNonQuery();
    }

    private static void DeleteSearchIndex(SqliteConnection connection, string mediaId)
    {
        if (!IsSearchIndexAvailable(connection))
        {
            return;
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM media_search WHERE mediaId = $id;";
        cmd.Parameters.AddWithValue("$id", mediaId);
        cmd.ExecuteNonQuery();
    }

    private static async Task DeleteSearchIndexAsync(SqliteConnection connection, string mediaId, CancellationToken cancellationToken = default)
    {
        if (!await IsSearchIndexAvailableAsync(connection, cancellationToken))
        {
            return;
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM media_search WHERE mediaId = $id;";
        cmd.Parameters.AddWithValue("$id", mediaId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RefreshSearchIndexAsync(SqliteConnection connection, string mediaId, CancellationToken cancellationToken = default)
    {
        if (!await IsSearchIndexAvailableAsync(connection, cancellationToken))
        {
            return;
        }

        await using var mediaCmd = connection.CreateCommand();
        mediaCmd.CommandText = "SELECT filename FROM media WHERE id = $id;";
        mediaCmd.Parameters.AddWithValue("$id", mediaId);
        var fileName = await mediaCmd.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            await DeleteSearchIndexAsync(connection, mediaId, cancellationToken);
            return;
        }

        var tags = string.Join(' ', await MediaTagRepository.GetTagsAsync(connection, mediaId, cancellationToken));
        await DeleteSearchIndexAsync(connection, mediaId, cancellationToken);

        await using var insertCmd = connection.CreateCommand();
        insertCmd.CommandText = "INSERT INTO media_search(mediaId, filename, tags) VALUES ($mediaId, $filename, $tags);";
        insertCmd.Parameters.AddWithValue("$mediaId", mediaId);
        insertCmd.Parameters.AddWithValue("$filename", fileName);
        insertCmd.Parameters.AddWithValue("$tags", tags);
        await insertCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ClearSearchIndex(SqliteConnection connection)
    {
        if (!IsSearchIndexAvailable(connection))
        {
            return;
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM media_search;";
        cmd.ExecuteNonQuery();
    }

    private static async Task ClearSearchIndexAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        if (!await IsSearchIndexAvailableAsync(connection, cancellationToken))
        {
            return;
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM media_search;";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void RemoveRelocation(SqliteConnection connection, string mediaId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM media_relocations WHERE mediaId = $mediaId;";
        cmd.Parameters.AddWithValue("$mediaId", mediaId);
        cmd.ExecuteNonQuery();
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

    private static bool IsSearchIndexAvailable(SqliteConnection connection)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'media_search' LIMIT 1;";
            return cmd.ExecuteScalar() != null;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> IsSearchIndexAvailableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'media_search' LIMIT 1;";
            return await cmd.ExecuteScalarAsync(cancellationToken) != null;
        }
        catch
        {
            return false;
        }
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

    private static string BuildFolderLikePattern(string folderPath)
    {
        return $"{EscapeLikePattern(folderPath.TrimEnd('/'))}/%";
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

    private IReadOnlyList<MediaDb.MediaEntryRef> GetMediaEntriesUnderFolders(IReadOnlyList<string> folderPaths)
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
            filters.Add($"(folderPath = {parameterName}Exact COLLATE NOCASE OR folderPath LIKE {parameterName} ESCAPE '\\' COLLATE NOCASE)");
            cmd.Parameters.AddWithValue($"{parameterName}Exact", folderPaths[i]);
            cmd.Parameters.AddWithValue(parameterName, BuildFolderLikePattern(folderPaths[i]));
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
        var filters = new List<string>(folderPaths.Count);
        for (var i = 0; i < folderPaths.Count; i++)
        {
            var parameterName = $"$folder{i}";
            filters.Add($"(folderPath = {parameterName}Exact COLLATE NOCASE OR folderPath LIKE {parameterName} ESCAPE '\\' COLLATE NOCASE)");
            cmd.Parameters.AddWithValue($"{parameterName}Exact", folderPaths[i]);
            cmd.Parameters.AddWithValue(parameterName, BuildFolderLikePattern(folderPaths[i]));
        }

        cmd.CommandText = $@"
SELECT id, path
FROM media
WHERE {string.Join(" OR ", filters)};
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
            FingerprintVersion = reader.IsDBNull(14) ? 0 : reader.GetInt32(14)
        };
    }

    private static void BuildQueryCommand(
        SqliteCommand cmd,
        string searchText,
        IReadOnlyList<string> selectedTags,
        IReadOnlyList<string>? includedFolders,
        IReadOnlyList<string>? exactFolders,
        IReadOnlyList<string> excludedFolders,
        string? playlistId,
        MediaType? typeFilter,
        IReadOnlyList<string> excludedMediaIds,
        bool randomOrder,
        string? ftsSearch,
        MediaSortField sortField,
        MediaSortOrder sortOrder,
        string? afterMediaId,
        int offset,
        int limit)
    {
        var sql = new StringBuilder(@"
SELECT id, path, filename, type, size, width, height, duration, thumbnail, createdAt, modifiedAt, lastPlayed, playCount, contentHash, fingerprintVersion
FROM media m
");

        if (!string.IsNullOrWhiteSpace(ftsSearch))
        {
            sql.Append(@"
INNER JOIN (
  SELECT DISTINCT mediaId
  FROM media_search
  WHERE media_search MATCH $ftsSearch
) fts ON fts.mediaId = m.id
");
            cmd.Parameters.AddWithValue("$ftsSearch", ftsSearch);
        }

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

                sql.Append($"    (m.folderPath = {parameterName}Exact COLLATE NOCASE OR m.folderPath LIKE {parameterName} ESCAPE '\\' COLLATE NOCASE)\n");
                cmd.Parameters.AddWithValue($"{parameterName}Exact", includedFolders[i]);
                cmd.Parameters.AddWithValue(parameterName, BuildFolderLikePattern(includedFolders[i]));
            }

            sql.Append("  )\n");
        }

        if (exactFolders is { Count: > 0 })
        {
            sql.Append("  AND (\n");
            for (var i = 0; i < exactFolders.Count; i++)
            {
                var parameterName = $"$exactFolder{i}";
                if (i > 0)
                {
                    sql.Append("   OR\n");
                }

                sql.Append($"    m.folderPath = {parameterName} COLLATE NOCASE\n");
                cmd.Parameters.AddWithValue(parameterName, exactFolders[i]);
            }

            sql.Append("  )\n");
        }

        if (!string.IsNullOrWhiteSpace(searchText) && string.IsNullOrWhiteSpace(ftsSearch))
        {
            sql.Append(@"
  AND (
    m.filename LIKE $search ESCAPE '\' COLLATE NOCASE
    OR EXISTS (
      SELECT 1
      FROM tags searchTags
      WHERE searchTags.mediaId = m.id
        AND searchTags.name LIKE $search ESCAPE '\' COLLATE NOCASE
    )
  )
");
            cmd.Parameters.AddWithValue("$search", BuildContainsLikePattern(searchText));
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
  AND m.path NOT LIKE {parameterName} ESCAPE '\' COLLATE NOCASE
");
            cmd.Parameters.AddWithValue(parameterName, BuildFolderLikePattern(excludedFolders[i]));
        }

        if (typeFilter.HasValue)
        {
            sql.Append("\n  AND m.type = $typeFilter COLLATE NOCASE\n");
            cmd.Parameters.AddWithValue("$typeFilter", typeFilter.Value.ToString().ToLowerInvariant());
        }

        if (excludedMediaIds.Count > 0)
        {
            sql.Append("\n  AND m.id NOT IN (");
            for (var i = 0; i < excludedMediaIds.Count; i++)
            {
                if (i > 0)
                {
                    sql.Append(", ");
                }

                var parameterName = $"$excludedMediaId{i}";
                sql.Append(parameterName);
                cmd.Parameters.AddWithValue(parameterName, excludedMediaIds[i]);
            }

            sql.Append(")\n");
        }

        if (!randomOrder && !string.IsNullOrWhiteSpace(afterMediaId))
        {
            sql.Append("\n  AND m.id > $afterMediaId\n");
            cmd.Parameters.AddWithValue("$afterMediaId", afterMediaId);
        }

        if (randomOrder)
        {
            sql.Append("ORDER BY RANDOM(), m.id ASC ");
        }
        else
        {
            var orderDirection = sortOrder == MediaSortOrder.Asc ? "ASC" : "DESC";
            sql.Append("ORDER BY ");
            sql.Append("m.folderPath COLLATE NOCASE ASC, ");
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
        }

        sql.Append("LIMIT $limit");
        if (string.IsNullOrWhiteSpace(afterMediaId))
        {
            sql.Append(" OFFSET $offset");
            cmd.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        }

        sql.Append(';');

        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.CommandText = sql.ToString();
    }

    private static void BuildFolderSummaryCommand(
        SqliteCommand cmd,
        string searchText,
        IReadOnlyList<string> selectedTags,
        IReadOnlyList<string>? includedFolders,
        IReadOnlyList<string> excludedFolders,
        string? playlistId,
        IReadOnlyList<string> excludedMediaIds,
        string? ftsSearch)
    {
        var sql = new StringBuilder(@"
SELECT m.folderPath AS folderPath, COUNT(*) AS mediaCount
FROM media m
");

        if (!string.IsNullOrWhiteSpace(ftsSearch))
        {
            sql.Append(@"
INNER JOIN (
  SELECT DISTINCT mediaId
  FROM media_search
  WHERE media_search MATCH $ftsSearch
) fts ON fts.mediaId = m.id
");
            cmd.Parameters.AddWithValue("$ftsSearch", ftsSearch);
        }

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

                sql.Append($"    (m.folderPath = {parameterName}Exact COLLATE NOCASE OR m.folderPath LIKE {parameterName} ESCAPE '\\' COLLATE NOCASE)\n");
                cmd.Parameters.AddWithValue($"{parameterName}Exact", includedFolders[i]);
                cmd.Parameters.AddWithValue(parameterName, BuildFolderLikePattern(includedFolders[i]));
            }

            sql.Append("  )\n");
        }

        if (!string.IsNullOrWhiteSpace(searchText) && string.IsNullOrWhiteSpace(ftsSearch))
        {
            sql.Append(@"
  AND (
    m.filename LIKE $search ESCAPE '\' COLLATE NOCASE
    OR EXISTS (
      SELECT 1
      FROM tags searchTags
      WHERE searchTags.mediaId = m.id
        AND searchTags.name LIKE $search ESCAPE '\' COLLATE NOCASE
    )
  )
");
            cmd.Parameters.AddWithValue("$search", BuildContainsLikePattern(searchText));
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
  AND m.folderPath <> {parameterName}Exact COLLATE NOCASE
  AND m.folderPath NOT LIKE {parameterName} ESCAPE '\' COLLATE NOCASE
");
            cmd.Parameters.AddWithValue($"{parameterName}Exact", excludedFolders[i]);
            cmd.Parameters.AddWithValue(parameterName, BuildFolderLikePattern(excludedFolders[i]));
        }

        if (excludedMediaIds.Count > 0)
        {
            sql.Append("\n  AND m.id NOT IN (");
            for (var i = 0; i < excludedMediaIds.Count; i++)
            {
                if (i > 0)
                {
                    sql.Append(", ");
                }

                var parameterName = $"$excludedSummaryMediaId{i}";
                sql.Append(parameterName);
                cmd.Parameters.AddWithValue(parameterName, excludedMediaIds[i]);
            }

            sql.Append(")\n");
        }

        sql.Append("GROUP BY folderPath\n");
        sql.Append("ORDER BY folderPath COLLATE NOCASE ASC;");
        cmd.CommandText = sql.ToString();
    }
}
