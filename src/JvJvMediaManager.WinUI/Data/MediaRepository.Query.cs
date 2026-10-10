using System.Text;
using Microsoft.Data.Sqlite;
using JvJvMediaManager.Models;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.Data;

// WP-10: read-side SQL construction for media page and folder-summary queries.
// Pure builders with no connection/transaction ownership; the query entry
// points live in the core repository file.
internal sealed partial class MediaRepository
{
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
        MediaPageCursor? cursor,
        int offset,
        int limit)
    {
        var sql = new StringBuilder(@"
SELECT id, path, filename, type, size, width, height, duration, thumbnail, createdAt, modifiedAt, lastPlayed, playCount, contentHash, fingerprintVersion, rotation
FROM media m
");

        AppendQueryFilters(
            cmd,
            sql,
            searchText,
            selectedTags,
            includedFolders,
            exactFolders,
            excludedFolders,
            playlistId,
            typeFilter,
            excludedMediaIds,
            ftsSearch);

        // WP-08: only apply a cursor that belongs to this exact query. A stale
        // cursor can otherwise silently skip or duplicate rows after a sort or
        // filter change.
        var effectiveCursor = cursor != null && IsCursorCompatible(
            new MediaQuery
            {
                SearchText = searchText,
                SelectedTags = selectedTags,
                IncludedFolderPaths = includedFolders,
                ExactFolderPaths = exactFolders,
                ExcludedFolderPaths = excludedFolders,
                PlaylistId = playlistId,
                TypeFilter = typeFilter,
                ExcludedMediaIds = excludedMediaIds,
                RandomOrder = randomOrder,
                SortField = sortField,
                SortOrder = sortOrder
            },
            cursor)
            ? cursor
            : null;

        // WP-08: keyset predicate takes precedence over the legacy afterMediaId
        // hack. It reproduces the exact ORDER BY tuple comparison so the next
        // page has no gaps or duplicates.
        if (!randomOrder && effectiveCursor != null && IsKeysetSupported(sortField))
        {
            AppendKeysetPredicate(cmd, sql, sortField, sortOrder, effectiveCursor);
        }
        else if (!randomOrder && !string.IsNullOrWhiteSpace(afterMediaId))
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
            sql.Append(GetSortOrderByClause(sortField, orderDirection));
            sql.Append(", m.id ASC ");
        }

        var useKeyset = !randomOrder && effectiveCursor != null && IsKeysetSupported(sortField);
        sql.Append("LIMIT $limit");
        if (!useKeyset && string.IsNullOrWhiteSpace(afterMediaId))
        {
            sql.Append(" OFFSET $offset");
            cmd.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        }

        sql.Append(';');

        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.CommandText = sql.ToString();
    }

    // Shared filter/join construction for page, folder-summary, and position
    // queries so their WHERE clauses can never drift apart.
    private static void AppendQueryFilters(
        SqliteCommand cmd,
        StringBuilder sql,
        string searchText,
        IReadOnlyList<string> selectedTags,
        IReadOnlyList<string>? includedFolders,
        IReadOnlyList<string>? exactFolders,
        IReadOnlyList<string> excludedFolders,
        string? playlistId,
        MediaType? typeFilter,
        IReadOnlyList<string> excludedMediaIds,
        string? ftsSearch)
    {
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
                if (i > 0)
                {
                    sql.Append("   OR\n");
                }

                AppendFolderFilterTerm(cmd, sql, $"$includedFolder{i}", includedFolders[i]);
                sql.Append('\n');
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
            cmd.Parameters.AddWithValue(parameterName, BuildFolderLikePattern(excludedFolders[i]).Pattern);
        }

        if (typeFilter.HasValue)
        {
            sql.Append("\n  AND m.type = $typeFilter COLLATE NOCASE\n");
            cmd.Parameters.AddWithValue("$typeFilter", typeFilter.Value.ToString().ToLowerInvariant());
        }

        if (excludedMediaIds.Count > 0)
        {
            AppendNotInIds(cmd, sql, excludedMediaIds, "excludedMediaId");
        }
    }

    // H3: returns the 0-based position of the target row within the filtered
    // result ordered by the query's sort tuple, or null when the sort is not
    // positionable (random order / multi-column resolution sort).
    public async Task<int?> QueryMediaPositionAsync(MediaQuery query, MediaFile target, CancellationToken cancellationToken = default)
    {
        if (query.RandomOrder || !IsKeysetSupported(query.SortField) || string.IsNullOrWhiteSpace(target.Id))
        {
            return null;
        }

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);

        var normalized = NormalizeQuery(query);
        var useFtsSearch = !string.IsNullOrWhiteSpace(normalized.FtsSearch)
            && await IsSearchIndexAvailableAsync(connection, null, cancellationToken);

        if (normalized.IncludedFolders is { Count: 0 })
        {
            return null;
        }

        await using var cmd = connection.CreateCommand();
        var sql = new StringBuilder("SELECT COUNT(*) FROM media m\n");
        AppendQueryFilters(
            cmd,
            sql,
            normalized.SearchText,
            normalized.SelectedTags,
            normalized.IncludedFolders,
            normalized.ExactFolders,
            normalized.ExcludedFolders,
            query.PlaylistId,
            query.TypeFilter,
            normalized.ExcludedMediaIds,
            useFtsSearch ? normalized.FtsSearch : null);
        AppendBeforePredicate(cmd, sql, query.SortField, query.SortOrder, target);
        cmd.CommandText = sql.ToString();

        try
        {
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
        }
        catch (SqliteException ex) when (useFtsSearch)
        {
            // FTS failure falls back to the LIKE filter, same as page queries.
            AppTraceLogger.LogException(
                "MediaRepository",
                "QueryMediaPositionAsync FTS search failed; retrying with LIKE fallback.",
                ex);
            await using var fallbackCmd = connection.CreateCommand();
            var sql2 = new StringBuilder("SELECT COUNT(*) FROM media m\n");
            AppendQueryFilters(
                fallbackCmd,
                sql2,
                normalized.SearchText,
                normalized.SelectedTags,
                normalized.IncludedFolders,
                normalized.ExactFolders,
                normalized.ExcludedFolders,
                query.PlaylistId,
                query.TypeFilter,
                normalized.ExcludedMediaIds,
                ftsSearch: null);
            AppendBeforePredicate(fallbackCmd, sql2, query.SortField, query.SortOrder, target);
            fallbackCmd.CommandText = sql2.ToString();
            return Convert.ToInt32(await fallbackCmd.ExecuteScalarAsync(cancellationToken));
        }
    }

    // Strictly-before comparison for the sort tuple
    // (folderPath COLLATE NOCASE ASC, sortValue DIR, id ASC); the mirror image
    // of AppendKeysetPredicate's "strictly after".
    private static void AppendBeforePredicate(
        SqliteCommand cmd,
        StringBuilder sql,
        MediaSortField sortField,
        MediaSortOrder sortOrder,
        MediaFile target)
    {
        var sortExpr = GetSortValueExpression(sortField);
        var isText = IsTextSortField(sortField);
        var sortComparand = isText ? "$beforeSort COLLATE NOCASE" : "$beforeSort";
        var dirCmp = sortOrder == MediaSortOrder.Asc ? "<" : ">";

        sql.Append(
            "\n  AND (\n" +
            "    m.folderPath COLLATE NOCASE < $beforeFolder COLLATE NOCASE\n" +
            "    OR (m.folderPath = $beforeFolder COLLATE NOCASE AND " + sortExpr + " " + dirCmp + " " + sortComparand + ")\n" +
            "    OR (m.folderPath = $beforeFolder COLLATE NOCASE AND " + sortExpr + " = " + sortComparand + " AND m.id < $beforeId)\n" +
            "  )\n");

        cmd.Parameters.AddWithValue("$beforeFolder", GetFolderPath(target));
        cmd.Parameters.AddWithValue("$beforeId", target.Id);
        if (isText)
        {
            cmd.Parameters.AddWithValue(
                "$beforeSort",
                sortField == MediaSortField.Type
                    ? target.Type.ToString().ToLowerInvariant()
                    : target.FileName);
        }
        else if (sortField == MediaSortField.Duration)
        {
            cmd.Parameters.AddWithValue("$beforeSort", target.Duration ?? 0d);
        }
        else
        {
            cmd.Parameters.AddWithValue(
                "$beforeSort",
                sortField == MediaSortField.Size ? target.Size : target.ModifiedAt);
        }
    }

    // WP-08: fields whose ORDER BY value is a single comparable expression can be
    // paged with a single-value keyset cursor. Resolution uses a 3-column sort
    // tuple, so it stays on OFFSET.
    internal static bool IsKeysetSupported(MediaSortField sortField)
    {
        return sortField is MediaSortField.FileName
            or MediaSortField.ModifiedAt
            or MediaSortField.Type
            or MediaSortField.Size
            or MediaSortField.Duration;
    }

    // WP-C: SQLite caps variables per statement at 32766. The excluded-id list
    // comes from the pending-deleted snapshot and can be large on big libraries,
    // so emit it as chained NOT IN chunks (union of chunks == full set).
    private const int MaxInClauseParameters = 500;

    private static void AppendNotInIds(SqliteCommand cmd, StringBuilder sql, IReadOnlyList<string> ids, string parameterPrefix)
    {
        if (ids.Count == 0)
        {
            return;
        }

        sql.Append("\n  AND m.id NOT IN (");
        var firstChunk = true;
        for (var chunkStart = 0; chunkStart < ids.Count; chunkStart += MaxInClauseParameters)
        {
            if (!firstChunk)
            {
                sql.Append("\n    AND m.id NOT IN (");
            }

            firstChunk = false;
            var chunkCount = Math.Min(MaxInClauseParameters, ids.Count - chunkStart);
            for (var i = 0; i < chunkCount; i++)
            {
                if (i > 0)
                {
                    sql.Append(", ");
                }

                var parameterName = $"${parameterPrefix}{chunkStart + i}";
                sql.Append(parameterName);
                cmd.Parameters.AddWithValue(parameterName, ids[chunkStart + i]);
            }

            sql.Append(')');
        }

        sql.Append('\n');
    }

    // WP-08: derive the cursor for the next page from the last row of the current
    // page. Returns null when keyset paging does not apply or there is no next page.
    private static MediaPageCursor? BuildNextCursor(MediaQuery query, IReadOnlyList<MediaFile> items, bool hasMore)
    {
        if (!hasMore || items.Count == 0)
        {
            return null;
        }

        return TryCreateCursor(query, items[^1]);
    }

    // Rebuild a forward keyset cursor from an already-loaded row when the page
    // store lost the previous NextCursor (duplicate skip, generation prune, etc.).
    internal static MediaPageCursor? TryCreateCursor(MediaQuery query, MediaFile last)
    {
        if (query.RandomOrder || !IsKeysetSupported(query.SortField))
        {
            return null;
        }

        string? textValue = null;
        long? intValue = null;
        double? realValue = null;

        switch (query.SortField)
        {
            case MediaSortField.FileName:
                textValue = last.FileName;
                break;
            case MediaSortField.Type:
                textValue = last.Type.ToString().ToLowerInvariant();
                break;
            case MediaSortField.ModifiedAt:
                intValue = last.ModifiedAt;
                break;
            case MediaSortField.Size:
                intValue = last.Size;
                break;
            case MediaSortField.Duration:
                realValue = last.Duration ?? 0d;
                break;
        }

        return new MediaPageCursor(
            GetFolderPath(last),
            textValue,
            intValue,
            realValue,
            last.Id,
            query.SortField,
            query.SortOrder,
            BuildFilterFingerprint(query));
    }

    // Stable fingerprint of everything that affects result set/order, so a cursor
    // is never reused across a different query. Callers should compare a stored
    // cursor's fingerprint to the current query before continuing pagination.
    internal static string BuildFilterFingerprint(MediaQuery query)
    {
        var builder = new StringBuilder();
        builder.Append("sf=").Append(query.SortField).Append('|');
        builder.Append("so=").Append(query.SortOrder).Append('|');
        builder.Append("q=").Append(query.SearchText.Trim().ToLowerInvariant()).Append('|');
        builder.Append("pl=").Append(query.PlaylistId ?? string.Empty).Append('|');
        builder.Append("tf=").Append(query.TypeFilter?.ToString() ?? string.Empty).Append('|');
        builder.Append("tags=").Append(string.Join(",", (query.SelectedTags ?? Array.Empty<string>()).OrderBy(t => t, StringComparer.OrdinalIgnoreCase))).Append('|');
        builder.Append("inc=").Append(string.Join(",", (query.IncludedFolderPaths ?? Array.Empty<string>()).OrderBy(t => t, StringComparer.OrdinalIgnoreCase))).Append('|');
        builder.Append("exact=").Append(string.Join(",", (query.ExactFolderPaths ?? Array.Empty<string>()).OrderBy(t => t, StringComparer.OrdinalIgnoreCase))).Append('|');
        builder.Append("exf=").Append(string.Join(",", query.ExcludedFolderPaths.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))).Append('|');
        builder.Append("exid=").Append(string.Join(",", query.ExcludedMediaIds.OrderBy(t => t, StringComparer.OrdinalIgnoreCase)));
        return builder.ToString();
    }

    internal static bool IsCursorCompatible(MediaQuery query, MediaPageCursor cursor)
    {
        return cursor.SortField == query.SortField
            && cursor.SortOrder == query.SortOrder
            && string.Equals(cursor.FilterFingerprint, BuildFilterFingerprint(query), StringComparison.Ordinal);
    }

    private static string GetSortOrderByClause(MediaSortField sortField, string orderDirection)
    {
        return sortField switch
        {
            MediaSortField.FileName => $"m.filename COLLATE NOCASE {orderDirection}",
            MediaSortField.ModifiedAt => $"m.modifiedAt {orderDirection}",
            MediaSortField.Type => $"m.type COLLATE NOCASE {orderDirection}",
            MediaSortField.Size => $"m.size {orderDirection}",
            MediaSortField.Duration => $"COALESCE(m.duration, 0) {orderDirection}",
            MediaSortField.Resolution =>
                $"(COALESCE(m.width, 0) * COALESCE(m.height, 0)) {orderDirection}, " +
                $"COALESCE(m.width, 0) {orderDirection}, " +
                $"COALESCE(m.height, 0) {orderDirection}",
            _ => $"m.modifiedAt {orderDirection}"
        };
    }

    // Sort-value expression for the keyset comparison, matching GetSortOrderByClause.
    private static string GetSortValueExpression(MediaSortField sortField)
    {
        return sortField switch
        {
            MediaSortField.FileName => "m.filename",
            MediaSortField.ModifiedAt => "m.modifiedAt",
            MediaSortField.Type => "m.type",
            MediaSortField.Size => "m.size",
            MediaSortField.Duration => "COALESCE(m.duration, 0)",
            _ => "m.modifiedAt"
        };
    }

    private static bool IsTextSortField(MediaSortField sortField)
        => sortField is MediaSortField.FileName or MediaSortField.Type;

    private static void AppendKeysetPredicate(
        SqliteCommand cmd,
        StringBuilder sql,
        MediaSortField sortField,
        MediaSortOrder sortOrder,
        MediaPageCursor cursor)
    {
        // ORDER BY is (folderPath COLLATE NOCASE ASC, sortValue DIR, id ASC).
        // "Strictly after" the cursor row is the lexicographic comparison:
        //   folderPath > F
        //   OR (folderPath = F AND sortValue <dirCmp> S)
        //   OR (folderPath = F AND sortValue = S AND id > C)
        var sortExpr = GetSortValueExpression(sortField);
        var isText = IsTextSortField(sortField);
        var sortComparand = isText ? "$cursorSort COLLATE NOCASE" : "$cursorSort";
        var dirCmp = sortOrder == MediaSortOrder.Asc ? ">" : "<";

        sql.Append(
            "\n  AND (\n" +
            "    m.folderPath COLLATE NOCASE > $cursorFolder COLLATE NOCASE\n" +
            "    OR (m.folderPath = $cursorFolder COLLATE NOCASE AND " + sortExpr + " " + dirCmp + " " + sortComparand + ")\n" +
            "    OR (m.folderPath = $cursorFolder COLLATE NOCASE AND " + sortExpr + " = " + sortComparand + " AND m.id > $cursorId)\n" +
            "  )\n");

        cmd.Parameters.AddWithValue("$cursorFolder", cursor.FolderPath);
        cmd.Parameters.AddWithValue("$cursorId", cursor.MediaId);
        if (isText)
        {
            cmd.Parameters.AddWithValue("$cursorSort", (object?)cursor.TextSortValue ?? string.Empty);
        }
        else if (sortField == MediaSortField.Duration)
        {
            cmd.Parameters.AddWithValue("$cursorSort", cursor.RealSortValue ?? 0d);
        }
        else
        {
            cmd.Parameters.AddWithValue("$cursorSort", cursor.IntegerSortValue ?? 0L);
        }
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
                if (i > 0)
                {
                    sql.Append("   OR\n");
                }

                AppendFolderFilterTerm(cmd, sql, $"$includedFolder{i}", includedFolders[i]);
                sql.Append('\n');
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
            cmd.Parameters.AddWithValue(parameterName, BuildFolderLikePattern(excludedFolders[i]).Pattern);
        }

        if (excludedMediaIds.Count > 0)
        {
            AppendNotInIds(cmd, sql, excludedMediaIds, "excludedSummaryMediaId");
        }

        sql.Append("GROUP BY folderPath\n");
        sql.Append("ORDER BY folderPath COLLATE NOCASE ASC;");
        cmd.CommandText = sql.ToString();
    }
}
