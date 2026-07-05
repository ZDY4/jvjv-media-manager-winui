using JvJvMediaManager.Models;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.Data;

public sealed class MediaDb
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
        AppTraceLogger.Log("MediaDb", "Initialize completed.");
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

    public Task<MediaPageResult> QueryMediaPageAsync(MediaQuery query, CancellationToken cancellationToken = default)
    {
        return _media.QueryMediaPageAsync(query, cancellationToken);
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

    public Task<IReadOnlyList<MediaEntryRef>> GetMediaEntriesUnderFoldersAsync(
        IReadOnlyList<string> folderPaths,
        CancellationToken cancellationToken = default)
    {
        return _media.GetMediaEntriesUnderFoldersAsync(folderPaths, cancellationToken);
    }

    public async Task AddTagAsync(string mediaId, string tag, CancellationToken cancellationToken = default)
    {
        await _tags.AddTagAsync(mediaId, tag, cancellationToken);
        await _media.RefreshSearchIndexAsync(mediaId, cancellationToken);
    }

    public async Task RemoveTagAsync(string mediaId, string tag, CancellationToken cancellationToken = default)
    {
        await _tags.RemoveTagAsync(mediaId, tag, cancellationToken);
        await _media.RefreshSearchIndexAsync(mediaId, cancellationToken);
    }

    public Task<IReadOnlyList<string>> GetTagsAsync(string mediaId, CancellationToken cancellationToken = default)
    {
        return _tags.GetTagsAsync(mediaId, cancellationToken);
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
