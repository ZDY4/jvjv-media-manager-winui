using JvJvMediaManager.Models;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.Data;

public sealed class MediaDb : IDisposable
{
    public readonly record struct MediaEntryRef(string Id, string Path);
    public readonly record struct MediaFingerprintRef(
        string Id,
        string Path,
        long Size,
        long ModifiedAt,
        string? ContentHash,
        int FingerprintVersion);

    private readonly MediaDbConnectionFactory _connections;
    private readonly MediaRepository _media;
    private readonly MediaTagRepository _tags;
    private readonly PlaylistRepository _playlists;
    private static readonly SemaphoreSlim StartupMaintenanceGate = new(1, 1);
    private readonly CancellationTokenSource _maintenanceCancellation = new();
    private int _isDisposed;

    public MediaDb(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var dbPath = Path.Combine(dataDir, "media.db");
        AppTraceLogger.Log("MediaDb", $"Constructing MediaDb. DataDir='{dataDir}', DbPath='{dbPath}'.");
        _connections = new MediaDbConnectionFactory(dbPath);
        _tags = new MediaTagRepository(_connections);
        _media = new MediaRepository(_connections, _tags);
        _playlists = new PlaylistRepository(_connections);
    }

    public void Initialize()
    {
        AppTraceLogger.Log("MediaDb", "Initialize start.");
        using var connection = _connections.OpenConnection();
        MediaDbSchema.Initialize(connection);
        AppTraceLogger.Log("MediaDb", "Initialize completed (schema only).");

        // WP-A: heavy one-time maintenance (duplicate path merge, FTS rebuild,
        // sort-tuple indexes, orphan audit) runs on a background thread so it
        // cannot block first paint on large libraries. LibraryShellViewModel
        // shows the first cached page immediately, then awaits this task
        // before arming folder watchers (and silently refreshes if maintenance
        // was still running during that first query).
        StartupMaintenanceTask = Task.Run(() => RunStartupMaintenanceAsync(_maintenanceCancellation.Token));
    }

    /// <summary>
    /// Completes after the background startup maintenance finishes (or fails).
    /// Failure is logged and swallowed: the schema itself is already in place,
    /// so the app remains usable without dedup/FTS/index maintenance.
    /// </summary>
    public Task StartupMaintenanceTask { get; private set; } = Task.CompletedTask;

    private async Task RunStartupMaintenanceAsync(CancellationToken cancellationToken)
    {
        await StartupMaintenanceGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
            MediaDbSchema.RunStartupMaintenance(connection);
            AppTraceLogger.Log("MediaDb", "Startup maintenance completed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AppTraceLogger.Log("MediaDb", "Startup maintenance cancelled during shutdown.");
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("MediaDb", "Startup maintenance failed.", ex);
        }
        finally
        {
            StartupMaintenanceGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        _maintenanceCancellation.Cancel();
        try
        {
            StartupMaintenanceTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is AggregateException or OperationCanceledException)
        {
            AppTraceLogger.LogSampled(
                "MediaDb",
                "startup-maintenance-dispose-wait-failed",
                $"Startup maintenance did not finish during database disposal. ErrorType={ex.GetType().Name}, Message='{ex.Message}'.",
                TimeSpan.FromSeconds(5));
        }
        finally
        {
            _maintenanceCancellation.Dispose();
        }
    }

    public Task AddMediaAsync(MediaFile media, CancellationToken cancellationToken = default)
    {
        return _media.AddMediaAsync(media, cancellationToken);
    }

    public Task UpsertMediaBatchAsync(IEnumerable<MediaFile> mediaItems, CancellationToken cancellationToken = default)
    {
        return _media.UpsertMediaBatchAsync(mediaItems, cancellationToken);
    }

    public Task<IReadOnlyList<MediaFile>> GetAllMediaAsync(CancellationToken cancellationToken = default)
    {
        return _media.GetAllMediaAsync(cancellationToken);
    }

    /// <summary>
    /// H2: streams all media in bounded pages instead of materializing the
    /// whole library at once.
    /// </summary>
    public IAsyncEnumerable<MediaFile> StreamAllMediaAsync(CancellationToken cancellationToken = default)
    {
        return _media.StreamAllMediaAsync(cancellationToken);
    }

    public Task<MediaPageResult> QueryMediaPageAsync(MediaQuery query, CancellationToken cancellationToken = default)
    {
        return _media.QueryMediaPageAsync(query, cancellationToken);
    }

    /// <summary>
    /// H3: 0-based position of the target row in the query's ordered result, or
    /// null when the sort is not positionable.
    /// </summary>
    public Task<int?> QueryMediaPositionAsync(MediaQuery query, MediaFile target, CancellationToken cancellationToken = default)
    {
        return _media.QueryMediaPositionAsync(query, target, cancellationToken);
    }

    public Task<IReadOnlyList<MediaFolderSummary>> QueryMediaFolderSummariesAsync(MediaQuery query, CancellationToken cancellationToken = default)
    {
        return _media.QueryMediaFolderSummariesAsync(query, cancellationToken);
    }

    public Task<MediaFile?> GetMediaByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        return _media.GetMediaByIdAsync(id, cancellationToken);
    }

    public Task<MediaFile?> GetMediaByPathAsync(string path, CancellationToken cancellationToken = default)
    {
        return _media.GetMediaByPathAsync(path, cancellationToken);
    }

    public Task<MediaFingerprintRef?> GetMediaFingerprintByPathAsync(string path, CancellationToken cancellationToken = default)
    {
        return _media.GetMediaFingerprintByPathAsync(path, cancellationToken);
    }

    public Task<bool> HasReusableFingerprintCandidateAsync(string path, long size, CancellationToken cancellationToken = default)
    {
        return _media.HasReusableFingerprintCandidateAsync(path, size, cancellationToken);
    }

    public Task UpdateMediaFingerprintByPathAsync(
        string path,
        long size,
        long modifiedAt,
        string contentHash,
        int fingerprintVersion,
        CancellationToken cancellationToken = default)
    {
        return _media.UpdateMediaFingerprintByPathAsync(path, size, modifiedAt, contentHash, fingerprintVersion, cancellationToken);
    }

    public Task UpdateMediaMetadataByPathAsync(
        string path,
        long size,
        long modifiedAt,
        int? width,
        int? height,
        double? duration,
        CancellationToken cancellationToken = default)
    {
        return _media.UpdateMediaMetadataByPathAsync(path, size, modifiedAt, width, height, duration, cancellationToken);
    }

    public Task DeleteMediaAsync(string id, CancellationToken cancellationToken = default)
    {
        return _media.DeleteMediaAsync(id, cancellationToken);
    }

    public Task DeleteMediaAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        return _media.DeleteMediaAsync(ids, cancellationToken);
    }

    public Task RemoveMediaRecordsOnlyAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        return _media.RemoveMediaRecordsOnlyAsync(ids, cancellationToken);
    }

    public Task ClearAllMediaAsync(bool includePlaylists = false, CancellationToken cancellationToken = default)
    {
        return _media.ClearAllMediaAsync(includePlaylists, cancellationToken);
    }

    public Task DeleteMediaUnderFoldersAsync(
        IReadOnlyList<string> removedFolderPaths,
        IReadOnlyList<string> keepFolderPaths,
        CancellationToken cancellationToken = default)
    {
        return _media.DeleteMediaUnderFoldersAsync(removedFolderPaths, keepFolderPaths, cancellationToken);
    }

    public Task<IReadOnlyList<MediaEntryRef>> GetMediaEntriesUnderFoldersAsync(
        IReadOnlyList<string> folderPaths,
        CancellationToken cancellationToken = default)
    {
        return _media.GetMediaEntriesUnderFoldersAsync(folderPaths, cancellationToken);
    }

    public async Task AddTagAsync(string mediaId, string tag, CancellationToken cancellationToken = default)
    {
        await _media.AddTagAsync(mediaId, tag, cancellationToken);
    }

    public async Task RemoveTagAsync(string mediaId, string tag, CancellationToken cancellationToken = default)
    {
        await _media.RemoveTagAsync(mediaId, tag, cancellationToken);
    }

    /// <summary>
    /// WP-E: replaces each media's tag set in a single transaction with one
    /// search-index refresh per media id. Use for bulk tagging; per-tag calls
    /// are only kept for single-item paths.
    /// </summary>
    public Task SetTagsForMediaAsync(
        IReadOnlyList<(string MediaId, IReadOnlyList<string> Tags)> updates,
        CancellationToken cancellationToken = default)
    {
        return _media.SetTagsForMediaAsync(updates, cancellationToken);
    }

    public Task<IReadOnlyList<string>> GetTagsAsync(string mediaId, CancellationToken cancellationToken = default)
    {
        return _tags.GetTagsAsync(mediaId, cancellationToken);
    }

    /// <summary>
    /// P-rotation: persists the per-media display rotation (0/90/180/270).
    /// </summary>
    public Task SetMediaRotationAsync(string mediaId, int rotation, CancellationToken cancellationToken = default)
    {
        return _media.SetMediaRotationAsync(mediaId, rotation, cancellationToken);
    }

    public Task<IReadOnlyList<string>> GetAllTagsAsync(CancellationToken cancellationToken = default)
    {
        return _tags.GetAllTagsAsync(cancellationToken);
    }

    public Task<IReadOnlyList<Playlist>> GetPlaylistsAsync(CancellationToken cancellationToken = default)
    {
        return _playlists.GetPlaylistsAsync(cancellationToken);
    }

    public Task<Playlist> CreatePlaylistAsync(string name, CancellationToken cancellationToken = default)
    {
        return _playlists.CreatePlaylistAsync(name, cancellationToken);
    }

    public Task RenamePlaylistAsync(string playlistId, string name, CancellationToken cancellationToken = default)
    {
        return _playlists.RenamePlaylistAsync(playlistId, name, cancellationToken);
    }

    public Task SetPlaylistColorAsync(string playlistId, string? colorHex, CancellationToken cancellationToken = default)
    {
        return _playlists.SetPlaylistColorAsync(playlistId, colorHex, cancellationToken);
    }

    public Task DeletePlaylistAsync(string playlistId, CancellationToken cancellationToken = default)
    {
        return _playlists.DeletePlaylistAsync(playlistId, cancellationToken);
    }

    public Task UpdatePlaylistOrderAsync(IReadOnlyList<string> playlistIds, CancellationToken cancellationToken = default)
    {
        return _playlists.UpdatePlaylistOrderAsync(playlistIds, cancellationToken);
    }

    public Task AddMediaToPlaylistAsync(string playlistId, IEnumerable<string> mediaIds, CancellationToken cancellationToken = default)
    {
        return _playlists.AddMediaToPlaylistAsync(playlistId, mediaIds, cancellationToken);
    }

    public Task RemoveMediaFromPlaylistAsync(string playlistId, IEnumerable<string> mediaIds, CancellationToken cancellationToken = default)
    {
        return _playlists.RemoveMediaFromPlaylistAsync(playlistId, mediaIds, cancellationToken);
    }

    public Task<bool> AreAllMediaInPlaylistAsync(string playlistId, IEnumerable<string> mediaIds, CancellationToken cancellationToken = default)
    {
        return _playlists.AreAllMediaInPlaylistAsync(playlistId, mediaIds, cancellationToken);
    }
}
